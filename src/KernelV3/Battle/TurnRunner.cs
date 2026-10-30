using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 回合流转控制器
//
// 最要紧的一条不变量：StartTurn 只操作 ActivePlayer 的状态。
// 「敌方回合给自己涨 PP」就是从这里漏出去的 —— 一旦写错，
// 整局对局的经济系统就全乱了。
// ============================================================================

public sealed class TurnRunner
{
    private readonly BattleState _state;
    private readonly GameSchema _schema;

    public TurnRunner(BattleState state, GameSchema schema = null)
    {
        _state = state;
        _schema = schema ?? state.Schema;
    }

    /// <summary>
    /// 开始某玩家的回合。
    /// 只碰 Players[playerId]，绝不触碰 Players[1 - playerId]。
    /// </summary>
    /// <summary>
    /// 回合开始，严格 8 步（R-3.1）。
    /// 关键：抽牌在第 8 步，不是第 1 步 ——
    /// 这意味着回合初效果造成的加手牌发生在抽牌之前，
    /// 手牌 8 张时回合初先加 1 张变 9，随后的抽牌会爆掉。
    /// </summary>
    public void StartTurn(int playerId, CardVm vm)
    {
        if (playerId is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(playerId), "回合玩家必须是 0 或 1");

        _state.ActivePlayer = playerId;
        if (playerId == 0) _state.TurnNumber++;

        vm?.CurrentTurnOwner = playerId;
        vm?.ResetTriggerCounts();

        var p = _state.Players[playerId];

        // ── 第 1 步：涨费与回费、重置本回合计数、奥义槽 +1 ──
        var maxPpKey = _schema.Counter("max_pp");
        var ppKey = _schema.Counter("pp");
        int maxPp = Math.Min(10, _state.ReadCounter(playerId, maxPpKey) + 1);
        _state.WriteCounter(playerId, maxPpKey, maxPp);
        _state.WriteCounter(playerId, ppKey, maxPp);

        var comboKey = _schema.Counter("combo");
        _state.WriteCounter(playerId, comboKey, 0);

        p.ManualEvolvedThisTurn = false;
        p.UsedExtraPpThisTurn = false;
        p.EngagedEntityIdsThisTurn.Clear();
        p.FusedEntityIdsThisTurn.Clear();
        p.DestroyedThisTurn.Clear();

        foreach (var e in _state.EnumerateZone(ZoneRef.Board, -1))
        {
            e.EngagedThisTurn = false;
            e.FusedThisTurn = false;
        }
        foreach (var e in _state.EnumerateZone(ZoneRef.Hand, -1))
            e.FusedThisTurn = false;

        foreach (var e in p.Board)
        {
            e.AttacksThisTurn = 0;
            e.AttackedThisTurn = false;
        }

        BumpSkyboundGauge(playerId);

        // ── 第 2 步：我方纹章（回合初能力 + 吟唱推进） ──
        StepCrestArea(playerId, vm, ownTurn: true);

        // ── 第 3 步：我方战场（回合初能力 + 护符吟唱推进） ──
        StepBoardCountdown(playerId, vm);

        // ── 第 4 步：对手纹章响应（“对手回合开始时”） ──
        StepCrestArea(1 - playerId, vm, ownTurn: false);

        // ── 第 5 步：对手战场响应 ──
        // （对手护符不推进吟唱 —— 吟唱只在持有者自己的回合推进）
        // （当前无独立的“对手回合开始”事件，留待下一步扩展）

        // ── 第 6 步：直接召唤（Invoke）（R-6.2） ──
        // 满场时不发动；同一时机同名卡只发动 1 张。
        if (p.Board.Count < PlayerState.MaxBoard)
        {
            var fired = new HashSet<int>();
            foreach (var card in p.Deck.ToList())
            {
                if (card.Def?.Abilities == null) continue;
                if (!fired.Add(card.Def.CardId)) continue;   // 同名卡同一时机只跳 1 张

                // 它是否声明了「在牌组中监听的回合开始时能力」
                bool hasInvoke = false;
                foreach (var ab in card.Def.Abilities)
                {
                    if (ab?.Trigger == null) continue;
                    if (ab.Trigger.ActiveZone != ActiveZoneScope.Deck) continue;
                    if (ab.Trigger.Event != CoreEvent.TurnStart) continue;
                    hasInvoke = true;
                    break;
                }
                if (!hasInvoke) continue;

                // 条件由 VM 在触发时判定；这里只负责把满足条件的卡拉上场。
                // 具体做法：把它移到战场，再发 OnEnterField；
                // 入场曲（OnPlay）不发动——直接召唤不是打出。
                _state.MoveEntity(card, ZoneRef.Board, playerId);
                vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnSummon, Subject = EntityRef.From(card) });
                if (card.Def.Kind == Ast.CardKind.Follower)
                {
                    var rallyKey = _schema.Counter("rally");
                    _state.WriteCounter(playerId, rallyKey,
                        _state.ReadCounter(playerId, rallyKey) + 1);
                }
                vm?.StepUntilBlocked();
                if (p.Board.Count >= PlayerState.MaxBoard) break;
            }
        }

        // ── 第 7 步：清空待机队列 ──
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnStart });
        vm?.StepUntilBlocked();

        // ── 第 8 步：常规抽牌（先手第一回合也抽） ──
        _state.DrawCards(playerId, 1);
        vm?.StepUntilBlocked();
    }

    /// <summary>纹章区：回合初能力排入队列 + 吟唱推进。</summary>
    private void StepCrestArea(int playerId, CardVm vm, bool ownTurn)
    {
        var cdKey = _schema.Counter("countdown");
        foreach (var e in _state.Players[playerId].CrestArea.ToList())
        {
            if (!e.Counters.TryGetValue(cdKey, out int cd) || cd <= 0) continue;
            if (!ownTurn) continue;          // 吟唱只在持有者回合推进
            cd--;
            e.Counters[cdKey] = cd;
            if (cd == 0)
            {
                vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnCountdownExpire, Subject = EntityRef.From(e) });
            }
        }
    }

    /// <summary>战场：护符吟唱推进，归零被破坏（会触发谢幕曲）。</summary>
    private void StepBoardCountdown(int playerId, CardVm vm)
    {
        var cdKey = _schema.Counter("countdown");
        foreach (var e in _state.Players[playerId].Board.ToList())
        {
            if (!e.Counters.TryGetValue(cdKey, out int cd) || cd <= 0) continue;
            cd--;
            e.Counters[cdKey] = cd;
            if (cd == 0)
            {
                vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnCountdownExpire, Subject = EntityRef.From(e) });
                _state.DestroyEntity(e);
            }
        }
    }

    /// <summary>
    /// 回合结束，严格 7 步（R-3.2） + 收尾清理。
    /// 清理阶段必须放在所有触发之后 ——
    /// “直到回合结束”的效果在回合结束时仍然要生效。
    /// </summary>
    public void EndTurn(int playerId, CardVm vm)
    {
        if (playerId != _state.ActivePlayer)
            throw new InvalidOperationException("只能结束当前回合玩家的回合");

        // ── 第 1〜6 步：三类“回合结束时”能力（自己纹章 / 自己战场 / 对手纹章 / 对手战场）──
        // 实现上统一由 TurnEnd 事件驱动，VM 会按触发器的 DuringTurn 限定逐个对号入座。
        // 不再手写四次循环 —— 那样反而会把同一条能力反复排队。
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnEnd });
        vm?.StepUntilBlocked();

        // ── 第 7 步：清空待机队列 ──
        vm?.StepUntilBlocked();

        // ── 收尾：撤销到期的限时修正 ──
        // ThisTurn 一律到期；UntilEndOfOpponentTurn 要等到对手回合结束才撤。
        _state.ExpireTemporaryModifiers(playerId);
    }

    // ---------- 出牌 ----------

    /// <summary>
    /// 从手牌打出一张卡。
    /// Fanfare(OnPlay) 只有这条路径才触发 —— 召唤/复活拉进来的随从不会触发。
    /// </summary>
    /// <summary>
    /// 从手牌打出一张牌（R-2.3 操作 A）。
    /// </summary>
    public bool PlayCard(int playerId, RuntimeEntity card, CardVm vm)
    {
        if (playerId != _state.ActivePlayer) return false;
        if (card == null || card.Def == null) return false;

        var p = _state.Players[playerId];
        if (!p.Hand.Contains(card)) return false;

        // ── 1. 限制与费用 ──
        // "Can't be played"（融合专用配件卡）禁止打出
        if (card.Keywords.Contains(_schema.Keyword("cannot_be_played"))) return false;

        // 随从与护符：战场已满 5 张时禁止打出
        bool isFollowerOrAmulet = card.Def.Kind == Ast.CardKind.Follower
                                  || card.Def.Kind == Ast.CardKind.Amulet;
        if (isFollowerOrAmulet && p.Board.Count >= PlayerState.MaxBoard) return false;

        var ppKey = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, ppKey);

        // ── 爆能强化强制铁律 ──
        // pp >= N 时必须按最高可支付档付费，不能选择只付原费。
        int pay = card.Cost;
        int bestTier = 0;
        foreach (var tier in EnhanceTiers(card)) if (pp >= tier && tier > bestTier) bestTier = tier;
        if (bestTier > pay) pay = bestTier;

        if (pp < pay) return false;

        // ── 2. 法术的手选目标预检（随从/护符不受限）──
        if (card.Def.Kind == Ast.CardKind.Spell && !HasLegalTargetForSpell(card)) return false;

        // ── 扣费──
        // pp_at_play 记录打出前的 PP，供 Enhance 分支判定能走哪一档
        _state.WriteCounter(playerId, _schema.Counter("pp_at_play"), pp);
        _state.WriteCounter(playerId, ppKey, pp - pay);
        card.EnteredTurn = _state.TurnNumber;

        // ── 3. 连击 +1（包含当前这张）──
        var comboKey = _schema.Counter("combo");
        _state.WriteCounter(playerId, comboKey, _state.ReadCounter(playerId, comboKey) + 1);

        // ── 4. 入场 ──
        if (isFollowerOrAmulet)
        {
            _state.MoveEntity(card, ZoneRef.Board, playerId);

            // 「当随从进入战场时」全场监听
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(card) });

            // 自身【入场曲】（优先级 10）
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });
            vm?.StepUntilBlocked();

            // 协作（Rally）：入场曲结算完之后才 +1，
            // 所以当前这张在它自己的入场曲里不算进 rally。
            if (card.Def.Kind == Ast.CardKind.Follower)
            {
                var rallyKey = _schema.Counter("rally");
                _state.WriteCounter(playerId, rallyKey, _state.ReadCounter(playerId, rallyKey) + 1);
            }
        }
        else
        {
            // 法术：结算效果 → 进墓场 → shadow +1
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });
            vm?.StepUntilBlocked();

            _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
            var shadowKey = _schema.Counter("shadow");
            _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);

            // 手牌里带魔力增幅的卡计数 +1 并触发 OnSpellboost
            BumpSpellboost(playerId, vm);
        }

        return true;
    }

    /// <summary>
    /// 法术是否有合法目标。
    /// 只检查「需要玩家手选（Choose）」的目标：
    /// 自动取（All / Random）永远合法，只有 Choose 在池子为空时才会卡住。
    /// </summary>
    private bool HasLegalTargetForSpell(RuntimeEntity card)
    {
        if (card.Def?.Abilities == null) return true;

        // 关键：「没有 Choose 目标」与「Choose 目标全部无效」必须区分开。
        // 之前循环结束后无条件 return true，
        // 于是敌方空场时「Select an enemy follower」的法术照样能打出。
        bool sawChoose = false;
        foreach (var ab in card.Def.Abilities)
        {
            if (ab?.Body == null) continue;
            foreach (var node in ab.Body)
            {
                var spec = GetChooseTarget(node);
                if (spec == null || spec.Mode != SelectMode.Choose) continue;
                sawChoose = true;
                if (CountLegalTargets(spec, card.PlayerId) > 0) return true;
            }
        }
        return !sawChoose;
    }

    private static Ast.TargetSpec GetChooseTarget(Ast.EffectNode n) => n switch
    {
        Ast.SelectTargetNode st => st.Target,
        Ast.DealDamageNode d => d.Target,
        Ast.MoveZoneNode m => m.Target,
        Ast.ModifyStatsNode ms => ms.Target,
        Ast.ModifyKeywordNode mk => mk.Target,
        _ => null,
    };

    private int CountLegalTargets(Ast.TargetSpec spec, int playerId)
    {
        if (spec == null || spec.Mode != SelectMode.Choose) return 1;
        int owner = spec.Owner switch
        {
            ZoneScope.Self => playerId,
            ZoneScope.Opponent => 1 - playerId,
            _ => -1,
        };
        var pool = _state.EnumerateZone(spec.Zone, owner).ToList();
        var ambush = _schema.Keyword("ambush");
        var aura = _schema.Keyword("aura");
        pool = pool.Where(e =>
        {
            if (e.Zone != ZoneRef.Board) return true;
            if (e.PlayerId == playerId) return true;
            return !e.Keywords.Contains(ambush) && !e.Keywords.Contains(aura);
        }).ToList();
        return pool.Count;
    }

    /// <summary>该卡声明了哪些 Enhance (N) 档位。</summary>
    private static IEnumerable<int> EnhanceTiers(RuntimeEntity card)
    {
        if (card.Def?.Abilities == null) yield break;
        foreach (var ab in card.Def.Abilities)
        {
            if (ab?.Costs == null) continue;
            foreach (var c in ab.Costs)
            {
                if (c.Kind != Ast.CostKind.UsePp) continue;
                if (c.Label == null || !c.Label.Equals("enhance", StringComparison.OrdinalIgnoreCase)) continue;
                if (c.Amount?.Op == Ast.NumOp.Const) yield return c.Amount.Value;
            }
        }
    }

    /// <summary>法术打出后：手牌中带魔力增幅的卡计数 +1，并触发 OnSpellboost。</summary>
    private void BumpSpellboost(int playerId, CardVm vm)
    {
        var sbKey = _schema.Counter("spellboost");
        bool any = false;
        foreach (var e in _state.Players[playerId].Hand)
        {
            if (e.Def?.Abilities == null) continue;
            bool has = false;
            foreach (var ab in e.Def.Abilities)
                if (ab?.Trigger != null && ab.Trigger.Event == CoreEvent.OnSpellboost) { has = true; break; }
            if (!has) continue;

            e.Counters.TryGetValue(sbKey, out var cur);
            e.Counters[sbKey] = cur + 1;
            any = true;
        }
        if (any) vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnSpellboost });
    }


    /// <summary>
    /// 手牌中所有带【奥义】的卡，奥义槽 +1。
    /// 规则：奥义槽 = 当前回合数 + 本卡在手时我方随从进化/超进化的次数。
    /// 每张卡各自维护自己的槽位，所以槽位存在实体计数器上。
    /// </summary>
    private void BumpSkyboundGauge(int playerId)
    {
        var gaugeKey = _schema.Counter("skybound_gauge");
        foreach (var e in _state.EnumerateZone(ZoneRef.Hand, playerId))
        {
            if (e.Def == null) continue;
            bool hasSkybound = false;
            foreach (var ab in e.Def.Abilities)
            {
                if (ab?.Condition == null) continue;
                if (ContainsOnlySkyboundGauge(ab.Condition)) { hasSkybound = true; break; }
            }
            if (!hasSkybound) continue;
            e.Counters.TryGetValue(gaugeKey, out var cur);
            e.Counters[gaugeKey] = cur + 1;
        }
    }

    private static bool ContainsOnlySkyboundGauge(Ast.BoolExpr e)
    {
        if (e is Ast.CompareExpr c)
            return c.Left != null && c.Left.Op == Ast.NumOp.Counter
                   && c.Left.Counter.Name == "skybound_gauge";
        if (e is Ast.AndExpr a)
            foreach (var x in a.Items) if (ContainsOnlySkyboundGauge(x)) return true;
        return false;
    }

    // ---------- 启动护符 / 融合 ----------

    /// <summary>
    /// 启动护符（Engage）：支付 Engage (N) 的 PP，发 OnEngage。
    /// 每回合每个护符只能启动 1 次。
    /// </summary>
    public bool EngageAmulet(int playerId, RuntimeEntity amulet, int cost, CardVm vm)
    {
        if (playerId != _state.ActivePlayer) return false;
        if (amulet == null || amulet.Zone != ZoneRef.Board || amulet.PlayerId != playerId) return false;
        if (amulet.EngagedThisTurn) return false;

        var ppKey = _schema.Counter("pp");
        if (_state.ReadCounter(playerId, ppKey) < cost) return false;
        _state.WriteCounter(playerId, ppKey, _state.ReadCounter(playerId, ppKey) - cost);

        amulet.EngagedThisTurn = true;
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEngage, Subject = EntityRef.From(amulet) });
        vm?.StepUntilBlocked();
        return true;
    }

    /// <summary>
    /// 融合（Fuse）：把关卡作为材料融合进 baseCard，累计 fuse_count / fuse_cost，发 OnFuse。
    /// 每个融合目标每回合只能融合 1 次。
    /// </summary>
    public bool FuseCards(int playerId, RuntimeEntity baseCard,
                          IReadOnlyList<RuntimeEntity> materials, CardVm vm)
    {
        if (playerId != _state.ActivePlayer) return false;
        if (baseCard == null || baseCard.Zone != ZoneRef.Hand || baseCard.PlayerId != playerId) return false;
        if (baseCard.FusedThisTurn) return false;
        if (materials == null || materials.Count == 0) return false;

        int totalCost = 0;
        foreach (var m in materials)
        {
            if (m == null || m.Zone != ZoneRef.Hand || m.PlayerId != playerId) return false;
            totalCost += m.Cost;
        }

        var fuseCount = _schema.Counter("fuse_count");
        var fuseCost = _schema.Counter("fuse_cost");
        _state.WriteCounter(playerId, fuseCount, _state.ReadCounter(playerId, fuseCount) + 1);
        _state.WriteCounter(playerId, fuseCost, _state.ReadCounter(playerId, fuseCost) + totalCost);

        foreach (var m in materials)
            _state.MoveEntity(m, ZoneRef.Cemetery, playerId);

        baseCard.FusedThisTurn = true;
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnFuse, Subject = EntityRef.From(baseCard) });
        vm?.StepUntilBlocked();
        return true;
    }

    // ---------- 后手额外能量点 ----------

    /// <summary>
    /// 后手玩家使用额外能量点：本回合临时 pp + 1（不增加 max_pp）。
    /// 一局两次，第 1~5 回合一次、第 6 回合起一次，每回合最多一次。
    /// </summary>
    public bool UseExtraPp(int playerId)
    {
        if (playerId != 1) return false;                       // 只有后手有
        if (playerId != _state.ActivePlayer) return false;      // 只能在自己回合
        var p = _state.Players[playerId];
        if (p.UsedExtraPpThisTurn) return false;                // 每回合最多一次

        bool early = _state.TurnNumber <= 5;
        if (early && p.UsedEarlyExtraPp) return false;
        if (!early && p.UsedLateExtraPp) return false;

        if (early) p.UsedEarlyExtraPp = true; else p.UsedLateExtraPp = true;
        p.UsedExtraPpThisTurn = true;

        var ppKey = _schema.Counter("pp");
        _state.WriteCounter(playerId, ppKey, _state.ReadCounter(playerId, ppKey) + 1);
        return true;
    }

    // ---------- 进化 ----------

    /// <summary>普通进化。先手第 5 回合 / 后手第 4 回合解锁，消耗 1 ep。</summary>
    public bool CanEvolve(int playerId)
    {
        if (playerId != _state.ActivePlayer) return false;
        var p = _state.Players[playerId];
        if (p.ManualEvolvedThisTurn) return false;

        int unlock = playerId == 0 ? 5 : 4;
        if (_state.TurnNumber < unlock) return false;

        var epKey = _schema.Counter("ep");
        return _state.ReadCounter(playerId, epKey) > 0;
    }

    /// <summary>超进化。先手第 7 回合 / 后手第 6 回合解锁，消耗 1 sep。</summary>
    public bool CanSuperEvolve(int playerId)
    {
        if (playerId != _state.ActivePlayer) return false;
        var p = _state.Players[playerId];
        if (p.ManualEvolvedThisTurn) return false;

        int unlock = playerId == 0 ? 7 : 6;
        if (_state.TurnNumber < unlock) return false;

        var sepKey = _schema.Counter("sep");
        return _state.ReadCounter(playerId, sepKey) > 0;
    }

    public bool Evolve(int playerId, RuntimeEntity e, bool super, CardVm vm)
    {
        bool ok = super ? CanSuperEvolve(playerId) : CanEvolve(playerId);
        if (!ok) return false;
        if (e == null || e.Zone != ZoneRef.Board || e.PlayerId != playerId) return false;
        if (e.IsEvolved) return false;

        var p = _state.Players[playerId];
        p.ManualEvolvedThisTurn = true;

        if (super)
        {
            var sepKey = _schema.Counter("sep");
            _state.WriteCounter(playerId, sepKey, _state.ReadCounter(playerId, sepKey) - 1);
            e.IsSuperEvolved = true;
            e.IsEvolved = true;          // 超进化包含普通进化状态
            e.Attack += 3;
            e.Life += 3;
            e.MaxLife += 3;
        }
        else
        {
            var epKey = _schema.Counter("ep");
            _state.WriteCounter(playerId, epKey, _state.ReadCounter(playerId, epKey) - 1);
            e.IsEvolved = true;
            e.Attack += 2;
            e.Life += 2;
            e.MaxLife += 2;
        }

        // 超进化同时触发 Evolve 与 Super-Evolve 两种能力（官方铁律）。
        // 之前无论普通还是超进化都只发 OnEvolve，
        // 导致所有「Super-Evolve:」能力在手动超进化时永远不触发。
        var evoKey = _schema.Counter("evolve_count");
        _state.WriteCounter(playerId, evoKey, _state.ReadCounter(playerId, evoKey) + 1);

        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEvolve, Subject = EntityRef.From(e), IsManualEvolve = true });
        if (super)
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnSuperEvolve, Subject = EntityRef.From(e), IsManualEvolve = true });
        vm?.StepUntilBlocked();
        return true;
    }

    // ---------- 胜负 ----------

    /// <summary>返回获胜方，-1 表示未结束，-2 表示平局（双方同时到 0）。</summary>
    /// <summary>
    /// 胜负判定：返回获胜方（0/1），-1 未结束，-2 平局。
    ///
    /// 三条判负来源：
    ///   1. 主战者生命归零
    ///   2. 牌组抽空（且牌组底部没有「胜利的卡牌」）
    ///   3. 抽空时持有「胜利的卡牌」-> 反而获胜
    /// </summary>
    public int Winner()
    {
        var p0 = _state.Players[0];
        var p1 = _state.Players[1];

        bool p0Lose = IsLoser(p0);
        bool p1Lose = IsLoser(p1);

        // 「胜利的卡牌」优先于抽空判负
        if (p0.WonByVictoryCard) p1Lose = true;
        if (p1.WonByVictoryCard) p0Lose = true;

        if (p0Lose && p1Lose) return -2;
        if (p1Lose) return 0;
        if (p0Lose) return 1;
        return -1;
    }

    private static bool IsLoser(PlayerState p)
        => (p.Leader != null && p.Leader.Life <= 0) || p.LostByDeckOut;
}

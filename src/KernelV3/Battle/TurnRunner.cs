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
        //
        // 这里**不再手写搬卡**。Invoke 卡的能力体本身就是 `move $self to Board`，
        // 第 7 步的 TurnStart 事件会让它以 ActiveZone=Deck 的身份入列并自行上场。
        // 两处都做会把同名卡抬上场两次（实测牌组 2 张同名 → 上场 2 张）。
        //
        // 「满场不发动」由 MoveEntity 的满场拦截负责（牌库→战场被拒时原地不动）；
        // 「同一时机同名只 1 张」由 CardVm.FlushNextTrigger 的按 (CardId, 事件) 去重负责。

        // ── 第 7 步：清空待机队列 ──
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnStart });

        // ── 第 8 步：常规抽牌（先手第一回合也抽） ──
        // 必须等第 2～7 步的回合初能力**彻底结算完**（含玩家完成所有选择）再抽。
        // 直接写在 StepUntilBlocked() 后面会让抽牌抢在挂起的回合初能力之前。
        if (vm != null)
        {
            vm.EnqueueContinuation(() =>
            {
                _state.DrawCards(playerId, 1);
                vm.StepUntilBlocked();
            });
            vm.StepUntilBlocked();
        }
        else
        {
            _state.DrawCards(playerId, 1);
        }
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
                // 吟唱归 0 的纹章**必须离开纹章区**（76-c 2.2 第 2 步）。
                // 之前只发了个事件，纹章永远留在场上，纹章区越堆越多。
                //
                // 「失去纹章」与「被破坏」不同：不进墓场、不加 shadow，
                // 但若它带谢幕曲，仍要在墓地收到 OnDestroy —— 所以走
                // DestroyEntity(byRule) 而不是静默移除。
                _state.ExpireCrest(e);
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

        // ── 第 7 步：清空待机队列 → 收尾撤销限时修正 ──
        // 撤销必须排在本回合增益真正用完之后：若回合末能力挂起等选择，
        // 这时候把 Buff 清掉，玩家选完才发现「加成已经没了」。
        // ThisTurn 一律到期；UntilEndOfOpponentTurn 要等到对手回合结束才撤。
        if (vm != null)
        {
            vm.EnqueueContinuation(() => _state.ExpireTemporaryModifiers(playerId));
            vm.StepUntilBlocked();
        }
        else
        {
            _state.ExpireTemporaryModifiers(playerId);
        }
    }

    // ---------- 出牌 ----------

    /// <summary>
    /// 从手牌打出一张卡。
    /// Fanfare(OnPlay) 只有这条路径才触发 —— 召唤/复活拉进来的随从不会触发。
    /// </summary>
    /// <summary>
    /// 从手牌打出一张牌（R-2.3 操作 A）。
    /// </summary>
    /// <summary>
    /// 这张牌现在能不能打出。纯查询，不改状态。
    ///
    /// 供 GetLegalActions 枚举与 UI 置灰使用。它与 PlayCard 的前置判定
    /// 必须逐条对齐 —— 否则「界面显示可打」和「实际打不出去」会打架。
    /// </summary>
    public bool CanPlayCard(int playerId, RuntimeEntity card, out string reason)
    {
        reason = "";
        if (playerId != _state.ActivePlayer) { reason = "不是你的回合"; return false; }
        if (card == null || card.Def == null) { reason = "卡面缺失"; return false; }
        var pp0 = _state.Players[playerId];
        if (!pp0.Hand.Contains(card)) { reason = "不在手牌"; return false; }
        if (card.Keywords.Contains(_schema.Keyword("cannot_be_played")))
        { reason = "这张卡不能打出"; return false; }

        bool folOrAmu = card.Def.Kind == Ast.CardKind.Follower || card.Def.Kind == Ast.CardKind.Amulet;
        bool full = pp0.Board.Count >= PlayerState.MaxBoard;

        var k = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, k);
        int pay = card.Cost;
        int best = 0;
        foreach (var tier in EnhanceTiers(card)) if (pp >= tier && tier > best) best = tier;
        if (best > pay) pay = best;

        if (pp < pay)
        {
            // 激奏与结晶都要做**法术形态的目标预检**：
            // 之前这里直接 return true，于是「敌方空场 + 只够激奏费」时
            // GetLegalActions 说能打，真打时又被 PlayAsAltMode 拦下 ——
            // 玩家看到的是一个点了没反应的动作。
            if (card.Def.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
            {
                var accProbe = card.Def;
                card.Def = card.Def.AccelerateDef;
                bool accLegal = HasLegalTargetForSpell(card);
                card.Def = accProbe;
                if (accLegal) return true;
            }

            if (card.Def.CrystallizeDef != null && pp >= card.Def.CrystallizeDef.BaseCost && !full)
            {
                var cryProbe = card.Def;
                card.Def = card.Def.CrystallizeDef;
                bool cryLegal = HasLegalTargetForSpell(card);
                card.Def = cryProbe;
                if (cryLegal) return true;
            }

            reason = "费用不足"; return false;
        }

        // 满场时本体进不来，但激奏是法术、不占格子 ——
        // 只要激奏费够、法术目标也合法，就该允许按激奏打出。
        // 否则「8 费手里拿着 2 费激奏的 6 费随从、场上已满」会被直接封死。
        if (folOrAmu && full)
        {
            if (card.Def.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
            {
                var probe = card.Def;
                card.Def = card.Def.AccelerateDef;
                bool legal = HasLegalTargetForSpell(card);
                card.Def = probe;
                if (legal) return true;
            }
            reason = "战场已满"; return false;
        }
        if (card.Def.Kind == Ast.CardKind.Spell && !HasLegalTargetForSpell(card))
        { reason = "没有合法目标"; return false; }
        return true;
    }

    public bool PlayCard(int playerId, RuntimeEntity card, CardVm vm)
    {
        if (playerId != _state.ActivePlayer) return false;
        if (card == null || card.Def == null) return false;

        var p = _state.Players[playerId];
        if (!p.Hand.Contains(card)) return false;

        // ── 1. 限制与费用 ──
        // "Can't be played"（融合专用配件卡）禁止打出
        if (card.Keywords.Contains(_schema.Keyword("cannot_be_played"))) return false;

        // 随从与护符：战场已满 5 张时禁止按**本体**打出。
        // 但「满场」不该拦住激奏 —— 激奏按法术结算，根本不占格子。
        // 所以这里只记下事实，真正的拦截放到确认走本体路径之后再判。
        bool isFollowerOrAmulet = card.Def.Kind == Ast.CardKind.Follower
                                  || card.Def.Kind == Ast.CardKind.Amulet;
        bool boardFull = p.Board.Count >= PlayerState.MaxBoard;

        var ppKey = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, ppKey);

        // ── 爆能强化强制铁律 ──
        // pp >= N 时必须按最高可支付档付费，不能选择只付原费。
        int pay = card.Cost;
        int bestTier = 0;
        foreach (var tier in EnhanceTiers(card)) if (pp >= tier && tier > bestTier) bestTier = tier;
        if (bestTier > pay) pay = bestTier;

        // 本体费用不够时，激奏 / 结晶是**合法的另一条出牌路径**。
        // 规则的顺序是：能按本体打出就必须按本体，只有不够时才允许这两者。
        if (pp < pay)
        {
            // 激奏：按法术结算，不占战场格子，满场也允许。
            if (card.Def?.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
                return PlayAsAltMode(playerId, card, card.Def.AccelerateDef, isAccelerate: true, vm);
            // 结晶：按护符进场，占格子，满场不允许。
            if (card.Def?.CrystallizeDef != null && pp >= card.Def.CrystallizeDef.BaseCost
                && !boardFull)
                return PlayAsAltMode(playerId, card, card.Def.CrystallizeDef, isAccelerate: false, vm);
            return false;
        }

        // 能按本体打出时的满场拦截。
        // 但满场不等于不能出牌：激奏是法术、不占格子，只要费够就允许。
        if (isFollowerOrAmulet && boardFull)
        {
            if (card.Def?.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
                return PlayAsAltMode(playerId, card, card.Def.AccelerateDef, isAccelerate: true, vm);
            return false;
        }

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

            // OnEnterField / OnSummon / 连携 已由 MoveEntity → NotifySummoned 发出，
            // 这里**不能再调一次**，否则连携会被记两次。
            //
            // 打出的卡只有一个时序例外：官方的连携在**入场曲结算之后**才 +1，
            // 所以当前这张在它自己的入场曲里不该已经算进去。
            // 把 NotifySummoned 加的那一次先扣回来，等 OnPlay 结算完再补上——
            // 净效果仍是 +1，只是生效时点推迟到入场曲之后。
            bool isFollower = card.Def.Kind == Ast.CardKind.Follower;
            var rallyKey = _schema.Counter("rally");
            if (isFollower)
                _state.WriteCounter(playerId, rallyKey, _state.ReadCounter(playerId, rallyKey) - 1);

            // 自身【入场曲】（优先级 10）
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });

            // 协作（Rally）：入场曲**彻底结算完**之后才 +1。
            // 入场曲里含 Select 时会挂起等玩家选 —— 直接写在 StepUntilBlocked() 后面
            // 会让 rally 在玩家点选之前就 +1，而官方的时点是入场曲结算完毕。
            if (isFollower)
            {
                if (vm != null)
                    vm.EnqueueContinuation(() => _state.WriteCounter(playerId, rallyKey,
                        _state.ReadCounter(playerId, rallyKey) + 1));
                else
                    _state.WriteCounter(playerId, rallyKey, _state.ReadCounter(playerId, rallyKey) + 1);
            }
            vm?.StepUntilBlocked();
        }
        else
        {
            // 法术：结算效果 → 进墓场 → shadow +1 → 魔力增幅。
            //
            // 这四步全部必须排在「效果彻底结算完」之后：法术若含 Select 或选模式，
            // VM 会挂起等玩家输入。直接写在 StepUntilBlocked() 后面意味着
            // **玩家还没选，法术已经进墓地、shadow 已 +1、魔力增幅已触发**，
            // 甚至手牌里其它卡的魔力增幅会插到这次选择之前结算。
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });

            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                    var shadowKey = _schema.Counter("shadow");
                    _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
                    BumpSpellboost(playerId, vm);
                });
                vm.StepUntilBlocked();
            }
            else
            {
                _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                var shadowKey = _schema.Counter("shadow");
                _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
                BumpSpellboost(playerId, vm);
            }
        }

        return true;
    }

    /// <summary>
    /// 以激奏（法术）或结晶（护符）模式打出一张本体费用不够的卡。
    ///
    /// 两者都是「同一张卡的另一张脸」：临时把实体 Def 换成模式定义，
    /// 走各自的结算路径，结算完把 Def 还原 —— 这张卡若之后被回手，
    /// 手牌里仍然应当是本体卡面。
    /// </summary>
    private bool PlayAsAltMode(int playerId, RuntimeEntity card, Ast.CardDef altDef,
                               bool isAccelerate, CardVm vm)
    {
        var p = _state.Players[playerId];
        var ppKey = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, ppKey);
        int cost = altDef.BaseCost;
        if (pp < cost) return false;

        var original = card.Def;

        // 激奏按法术结算，必须先在**法术形态下**做目标预检 ——
        // 否则一张需要指定目标的激奏在没有合法目标时照样能打出去。
        if (isAccelerate)
        {
            var probe = card.Def;
            card.Def = altDef;
            bool legal = HasLegalTargetForSpell(card);
            card.Def = probe;
            if (!legal) return false;
        }

        _state.WriteCounter(playerId, _schema.Counter("pp_at_play"), pp);
        _state.WriteCounter(playerId, ppKey, pp - cost);
        _state.WriteCounter(playerId, _schema.Counter("combo"),
            _state.ReadCounter(playerId, _schema.Counter("combo")) + 1);

        card.OriginalDef = original;
        card.Def = altDef;
        card.Cost = cost;

        if (isAccelerate)
        {
            // 激奏：按法术结算 —— 效果 → 进墓地 → shadow +1 → 魔力增幅 → 还原。
            // 与普通法术同理，全部排进续体：效果挂了起等选择时，
            // 卡不该提前进墓地，也不该提前把 Def 还原成本体随从 ——
            // 那会让后续的「这张卡是什么」读错。
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });

            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                    var shadowKey = _schema.Counter("shadow");
                    _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
                    BumpSpellboost(playerId, vm);
                    // 进墓地后还原：手牌 / 墓地里的这张卡必须显示本体卡面。
                    card.Def = original;
                    card.OriginalDef = null;
                });
                vm.StepUntilBlocked();
            }
            else
            {
                _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                var shadowKey = _schema.Counter("shadow");
                _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
                BumpSpellboost(playerId, vm);
                card.Def = original;
                card.OriginalDef = null;
            }
        }
        else
        {
            // 结晶：按护符进场。
            //
            // 必须先洗掉本体随从的一切：手牌里它已经带着本体的身材与关键词。
            // 不清就变成「带守护和必杀的结晶护符」。
            card.Attack = 0;
            card.Life = 0;
            card.MaxLife = 0;
            card.Keywords.Clear();
            card.KeywordValues.Clear();
            card.Counters.Clear();
            card.TempModifiers.Clear();
            _state.MountInnateProperties(card);

            // MoveEntity 内部已通过 NotifySummoned 发过 OnEnterField，这里只补 OnPlay。
            _state.MoveEntity(card, ZoneRef.Board, playerId);
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });
            vm?.StepUntilBlocked();
        }

        // 结晶路径**不能**在这里还原本体 Def —— 它现在是一张场上的护符，
        // 吟唱与谢幕曲都挂在 CrystallizeDef 上，还原等于把它变回随从，
        // 吟唱到期时也找不到谢幕曲了。
        // 还原交给 ResetToPrintedValues（回手 / 回收时读 OriginalDef）。
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
        // 语义：**每一个**顶层必选 Choose 目标都必须有候选，法术才能打出。
        //
        // 两处曾经写错：
        //   · 只匹配 5 种节点，漏了 RestoreLife / Transform / Evolve /
        //     ModifyCost / GrantAbility —— 「Restore 2 defense to an allied
        //     follower」在己方空场时也能空放。
        //   · 写成「任意一个 > 0 就 true」，于是一张要连选两个目标
        //     （先友方再敌方）的法术，只要友方有候选就能打出，敌方空场也放。
        bool sawChoose = false;
        foreach (var ab in card.Def.Abilities)
        {
            if (ab?.Body == null) continue;
            foreach (var node in ab.Body)
            {
                var spec = GetChooseTarget(node);
                if (spec == null || spec.Mode != SelectMode.Choose) continue;
                sawChoose = true;
                if (CountLegalTargets(spec, card) <= 0) return false;
            }
        }
        return !sawChoose;
    }

    /// <summary>
    /// 该节点作用的目标。
    ///
    /// 必须覆盖**全部**带目标的节点：漏一种，那类法术就永远被判成
    /// 「没有必选目标」，空场也能放出去。早先只列了 5 种，
    /// Restore / Transform / Evolve / ModifyCost / GrantAbility 全在漏网之列。
    /// </summary>
    private static Ast.TargetSpec GetChooseTarget(Ast.EffectNode n) => n switch
    {
        Ast.SelectTargetNode st => st.Target,
        Ast.DealDamageNode d => d.Target,
        Ast.RestoreLifeNode r => r.Target,
        Ast.MoveZoneNode m => m.Target,
        Ast.ModifyStatsNode ms => ms.Target,
        Ast.ModifyKeywordNode mk => mk.Target,
        Ast.ModifyCostNode mc => mc.Target,
        Ast.GrantAbilityNode ga => ga.Target,
        Ast.TransformNode tn => tn.Target,
        Ast.EvolveNode ev => ev.Target,
        _ => null,
    };

    private int CountLegalTargets(Ast.TargetSpec spec, RuntimeEntity source)
    {
        int playerId = source?.PlayerId ?? _state.ActivePlayer;
        if (spec == null || spec.Mode != SelectMode.Choose) return 1;
        int owner = spec.Owner switch
        {
            ZoneScope.Self => playerId,
            ZoneScope.Opponent => 1 - playerId,
            _ => -1,
        };
        // 必须走 VmEval.ResolveRef 而不是裸 EnumerateZone：
        // spec.Where（「带 Fuse」「攻击力 3 以上」等）与历史池都在那里生效，
        // 只按区域枚举会把所有不符合条件的卡也算成合法目标，
        // 于是「没有合法目标就不能打出」这道预检形同虚设。
        var eval = new Vm.VmEval(_state);
        // Source 必须绑定：像「Select another card in your hand and discard it」
        // 这类带 other-than-itself 的目标，靠 Source 才能排除法术卡自身；
        // 不绑的话，手里只剩这一张法术也会被判成「有合法目标」。
        var sc = new Vm.VmScope
        {
            Owner = spec.Owner,
            Controller = playerId,
            Source = source != null ? Vm.EntityRef.From(source) : null,
        };
        var pool = new List<RuntimeEntity>();
        foreach (var r in eval.ResolveRef(spec, sc))
        {
            var live = _state.GetLive(r.EntityId, r.EntrySerial);
            if (live != null) pool.Add(live);
        }

        var ambush = _schema.Keyword("ambush");
        var aura = _schema.Keyword("aura");
        pool = pool.Where(e =>
        {
            if (e.Zone != ZoneRef.Board) return true;
            // 土之印 / Clue 不可被能力点选，预检也要排除。
            if (_state.IsProtectedAmulet(e)) return false;
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
    private void BumpSkyboundGauge(int playerId) => BumpSkyboundGauge(_state, _schema, playerId);

    /// <summary>
    /// 奥义槽 +1 的唯一实现。手动进化（本类）与能力自动进化（CardVm.DoEvolve）
    /// 都走这里 —— 两份实现必然漂移。
    /// </summary>
    internal static void BumpSkyboundGauge(IVmWorld world, GameSchema schema, int playerId)
    {
        var gaugeKey = schema.Counter("skybound_gauge");
        foreach (var e in world.EnumerateZone(ZoneRef.Hand, playerId))
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
    /// <summary>
    /// 这个护符现在能不能启动。纯查询，供 UI 与 GetLegalActions 使用。
    ///
    /// 早先没有这道检查：任何白板护符都能被启动，被沉默的也能，
    /// 而且费用要外部手填 —— 卡面上印的启动费完全没被读过。
    /// </summary>
    public bool CanEngageAmulet(int playerId, RuntimeEntity amulet, out int engageCost)
    {
        engageCost = 0;
        if (playerId != _state.ActivePlayer) return false;
        if (amulet == null || amulet.Zone != ZoneRef.Board || amulet.PlayerId != playerId) return false;
        if (amulet.EngagedThisTurn) return false;
        // 被沉默的实体没有能力，自然也不能启动。
        if (amulet.IsSilenced) return false;

        // 必须真的声明了 Engage —— 没有启动能力的护符是白板。
        var engage = FindEngageAbility(amulet);
        if (engage == null) return false;

        // 启动费写在能力的 Costs 里（CostKind.UsePp + label=engage）。
        foreach (var c in engage.Costs ?? Array.Empty<Ast.CostSpec>())
        {
            if (c.Kind != Ast.CostKind.UsePp) continue;
            if (c.Amount?.Op == Ast.NumOp.Const) engageCost = c.Amount.Value;
        }

        var ppKey = _schema.Counter("pp");
        return _state.ReadCounter(playerId, ppKey) >= engageCost;
    }

    /// <summary>实体（含运行时授予）身上声明的 OnEngage 能力。</summary>
    private Ast.AbilityDef FindEngageAbility(RuntimeEntity e)
    {
        if (e?.Def?.Abilities != null)
            foreach (var ab in e.Def.Abilities)
                if (ab?.Trigger != null && ab.Trigger.Event == CoreEvent.OnEngage) return ab;
        foreach (var (ge, gab) in _state.GrantedAbilities)
            if (ge != null && ge.EntityId == e?.EntityId
                && gab?.Trigger != null && gab.Trigger.Event == CoreEvent.OnEngage) return gab;
        return null;
    }

    /// <summary>以卡面声明的启动费启动护符。</summary>
    public bool EngageAmulet(int playerId, RuntimeEntity amulet, CardVm vm)
    {
        if (!CanEngageAmulet(playerId, amulet, out int autoCost)) return false;
        return EngageAmuletCore(playerId, amulet, autoCost, vm);
    }

    public bool EngageAmulet(int playerId, RuntimeEntity amulet, int cost, CardVm vm)
    {
        // 显式传费用时也要过能力检查：能启动的前提是「它真有 Engage 能力」。
        if (!CanEngageAmulet(playerId, amulet, out int cardCost)) return false;
        // 卡面写了启动费就以卡面为准；卡面没写（0）才采用调用方给的值。
        int pay = cardCost > 0 ? cardCost : Math.Max(0, cost);
        return EngageAmuletCore(playerId, amulet, pay, vm);
    }

    private bool EngageAmuletCore(int playerId, RuntimeEntity amulet, int cost, CardVm vm)
    {
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
    /// <summary>
    /// 这组卡能不能融合。纯查询，不改状态 —— 供 UI 判定与 GetLegalActions 枚举。
    ///
    /// 四条必须同时成立：主体可融合、材料在手、材料不是主体、材料满足声明条件。
    /// 早先 FuseCards 只检查了「都在手牌」，于是任意手牌都能喂进任意一张卡。
    /// </summary>
    public bool CanFuseCards(int playerId, RuntimeEntity baseCard,
                             IReadOnlyList<RuntimeEntity> materials, out string reason)
    {
        reason = "";
        if (playerId != _state.ActivePlayer) { reason = "不是你的回合"; return false; }
        if (baseCard == null || baseCard.Zone != ZoneRef.Hand || baseCard.PlayerId != playerId)
        { reason = "主体卡不在你的手牌"; return false; }
        if (baseCard.FusedThisTurn) { reason = "本回合已经融合过"; return false; }

        // 主体必须能融合 —— 否则任何一张手牌都能当融合台。
        // 两个判据任一成立即可：
        //   · HasFuse：卡面有 `Fuse: <材料>` 声明（编译器置位）
        //   · 存在 OnFuse 能力：手工构造的 CardDef 可能只写了能力
        if (baseCard.Def == null) { reason = "这张卡不能融合"; return false; }
        bool canFuse = baseCard.Def.HasFuse;
        if (!canFuse && baseCard.Def.Abilities != null)
            foreach (var ab in baseCard.Def.Abilities)
                if (ab?.Trigger != null && ab.Trigger.Event == CoreEvent.OnFuse) { canFuse = true; break; }
        if (!canFuse) { reason = "这张卡不能融合"; return false; }

        if (materials == null || materials.Count == 0)
        { reason = "没有融合材料"; return false; }

        var eval = new Vm.VmEval(_state);
        var seen = new HashSet<int>();
        foreach (var m in materials)
        {
            if (m == null || m.Zone != ZoneRef.Hand || m.PlayerId != playerId)
            { reason = "材料不在你的手牌"; return false; }
            if (m.EntityId == baseCard.EntityId)
            { reason = "不能把主体自己当材料"; return false; }
            if (!seen.Add(m.EntityId))
            { reason = "同一张材料出现了两次"; return false; }

            // `Fuse: Loot cards` / `Fuse: Artifact amulets` 的材料条件。
            var filter = baseCard.Def.FuseMaterialFilter;
            if (filter != null && filter.Where != null
                && !(filter.Where is Ast.ConstBoolExpr cb && cb.Value))
            {
                var sc = new Vm.VmScope { Owner = Core.ZoneScope.Self, Controller = playerId };
                sc.BindRefs(Ast.TargetSpec.SlotItem, new[] { Vm.EntityRef.From(m) });
                if (!eval.EvalBool(filter.Where, sc))
                { reason = "材料不满足融合条件"; return false; }
            }
        }
        return true;
    }

    public bool FuseCards(int playerId, RuntimeEntity baseCard,
                          IReadOnlyList<RuntimeEntity> materials, CardVm vm)
    {
        if (!CanFuseCards(playerId, baseCard, materials, out _)) return false;

        int totalCost = 0;
        foreach (var m in materials) totalCost += m.Cost;

        // 三个计数器全部挂在**这张卡**上，不是玩家级 —— 卡面写的是
        // 「Fused to **this card**」，是这张卡各自的累计。
        //   fuse_count：融合了几次
        //   fuse_cost ：材料总费用（Striker / Fortifier 的分档判据）
        //   fuse_kinds：不同卡号的数量（Ominous Artifact α 的 both / Sinciro 的 X）
        var fuseCountKey = _schema.Counter("fuse_count");
        var fuseCostKey = _schema.Counter("fuse_cost");
        var fuseKindsKey = _schema.Counter("fuse_kinds");

        baseCard.Counters.TryGetValue(fuseCountKey, out int fc);
        baseCard.Counters.TryGetValue(fuseCostKey, out int fcost);
        baseCard.Counters[fuseCountKey] = fc + 1;
        baseCard.Counters[fuseCostKey] = fcost + totalCost;

        // 种类数按「这张卡历史融合过的不同卡号」计。
        // 记录放在 FusedKinds 集合里而不是只留一个整数，
        // 否则同名材料重复喂会把 kinds 也算上去。
        foreach (var m in materials)
        {
            int mid = m.Def?.CardId ?? 0;
            if (mid != 0) baseCard.FusedKinds.Add(mid);
        }
        baseCard.Counters[fuseKindsKey] = baseCard.FusedKinds.Count;

        // 被融合吞掉的材料卡「离场但不在任何区域」（76-c 2.3-C）：
        // 不进墓场、不加 shadow、不触发 OnDiscard / OnLeaveField / 谢幕曲。
        // 之前走 Cemetery，于是融合反而给亡者召还喂了肥料，
        // 还顺带把墓场计数与 shadow 数都算高了。
        foreach (var m in materials)
        {
            // 材料进**融合素材区**，实体保留（不再销毁）。
            //
            // 之前是 MoveEntity(None) + RemoveEntity，材料彻底消失：
            // 既没法按卡号去重数 fuse_kinds，UI 也展示不了这张卡吃了什么。
            // 迁移仍走 MoveEntity，但目标区是 FuseMaterial —— 那里不在
            // 「离开战场」的判定范围内（材料本来就在手牌），所以不会误发
            // OnLeaveField；下方再显式挡掉 OnDiscard。
            bool wasInHand = m.Zone == ZoneRef.Hand;
            m.FusedIntoId = baseCard.EntityId;
            _state.MoveEntity(m, ZoneRef.FuseMaterial, playerId);
            if (wasInHand && m.Zone != ZoneRef.FuseMaterial)
            {
                // 极端兜底：迁移被拒时至少别让它留在手牌里当双份材料。
                m.Zone = ZoneRef.None;
                _state.RemoveEntity(m);
            }
        }

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
    /// <summary>现在能不能按额外能量点。纯查询，与 UseExtraPp 的前置判定对齐。</summary>
    public bool CanUseExtraPp(int playerId)
    {
        if (playerId != 1) return false;                       // 只有后手有
        if (playerId != _state.ActivePlayer) return false;      // 只能在自己回合
        var pl = _state.Players[playerId];
        if (pl.UsedExtraPpThisTurn) return false;
        bool earlyTurn = _state.TurnNumber <= 5;
        return earlyTurn ? !pl.UsedEarlyExtraPp : !pl.UsedLateExtraPp;
    }

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

        // 奥义槽 = TurnNumber + 进化次数 + 超进化次数。
        // 之前只有**自动**进化会调 BumpSkyboundGauge（CardVm.Nodes.cs:604），
        // 手动进化反而从不调用 —— 于是靠进化攒奥义的卡永远攒不起来。
        BumpSkyboundGauge(playerId);

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

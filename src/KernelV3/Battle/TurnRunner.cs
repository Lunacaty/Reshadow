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

    /// <summary>
    /// 驱动卡池级主战者计数器的回合生命周期。
    ///
    /// 这些计数器属于**赛制**而非某张卡：连击每回合归零、某些自定义资源
    /// 每回合自动 +1 —— 在能力节点里写不出来，因为那时候还没有实体承载它们。
    /// </summary>
    private void DrivePoolCounters(int playerId)
    {
        var defs = _state.PoolCounterDefs;
        if (defs == null || defs.Count == 0) return;

        foreach (var def in defs)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) continue;
            var key = _state.Schema.Counter(def.Name);
            if (!key.IsValid) continue;

            switch (def.Lifecycle)
            {
                case ShadowverseFan.KernelV3.Packs.CounterLifecycle.ResetToMinOnTurnStart:
                    _state.WriteCounter(playerId, key, def.Min);
                    break;
                case ShadowverseFan.KernelV3.Packs.CounterLifecycle.IncrementOnTurnStart:
                    _state.WriteCounter(playerId, key, _state.ReadCounter(playerId, key) + 1);
                    break;
                default:
                    break;
            }
        }
    }

    public void StartTurn(int playerId, CardVm vm)
    {
        if (playerId is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(playerId), "回合玩家必须是 0 或 1");

        _state.ActivePlayer = playerId;
        // 用 FirstPlayer 而不是写死 0：玩家 1 先手时，他的第 1 回合也该让 TurnNumber 前进。
        if (_state.TurnNumber == 0 || playerId == _state.FirstPlayer) _state.TurnNumber++;

        // 回合数推进后立刻判一次解锁：解锁前该玩家的 EP/SEP 恒为 0。
        _state.CheckAndUnlockEvolution(playerId);

        // 卡池级主战者计数器的回合生命周期（164 专题二）。
        // 放在 EP/SEP 解锁之后：解锁本身会写 ep/sep，
        // 先跑计数器再解锁会把刚重置的值覆盖掉。
        DrivePoolCounters(playerId);

        vm?.CurrentTurnOwner = playerId;
        vm?.ResetTriggerCounts();

        var p = _state.Players[playerId];

        // ── 第 1 步：涨费与回费、重置本回合计数、奥义槽 +1 ──
        var maxPpKey = _schema.Counter("max_pp");
        var ppKey = _schema.Counter("pp");
        // 涨费量与上限都走规则：DIY 卡池可以设「每回合 +2、上限 12」。
        var rules = _state.Rules;
        int maxPp = Math.Min(rules.MaxPpCap, _state.ReadCounter(playerId, maxPpKey) + rules.PpGainPerTurn);
        _state.WriteCounter(playerId, maxPpKey, maxPp);
        _state.WriteCounter(playerId, ppKey, maxPp);

        var comboKey = _schema.Counter("combo");
        _state.WriteCounter(playerId, comboKey, 0);

        // 本回合我方随从攻击次数清零 —— 「本回合我方随从没攻击过」的判据。
        _state.WriteCounter(playerId, _schema.Counter("turn_ally_attacks"), 0);

        // 「我方上回合是否有随从攻击过主战者」（《利牙》的替代条件）：
        // 把上一回合的 this 转存进 last，再清零 this。
        var attackedThis = _schema.Counter("ally_attacked_leader_this_turn");
        _state.WriteCounter(playerId, _schema.Counter("ally_attacked_leader_last_turn"),
                            _state.ReadCounter(playerId, attackedThis));
        _state.WriteCounter(playerId, attackedThis, 0);

        p.ManualEvolvedThisTurn = false;
        p.UsedExtraPpThisTurn = false;
        // 额外槽位是**本回合**的临时资源；激活后未动用就清零，
        // 但 Early/Late 次数未消耗，后续回合仍可再激活。
        p.BonusPp = 0;
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
        EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.TurnStart, PlayerId = playerId });

        // ── 第 8 步：常规抽牌（先手第一回合也抽） ──
        // 必须等第 2～7 步的回合初能力**彻底结算完**（含玩家完成所有选择）再抽。
        // 直接写在 StepUntilBlocked() 后面会让抽牌抢在挂起的回合初能力之前。
        int drawCount = ShouldDrawThisTurn(playerId) ? rules.CardsDrawnPerTurn : 0;
        if (drawCount > 0)
        {
            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    _state.DrawCards(playerId, drawCount);
                    vm.StepUntilBlocked();
                });
                vm.StepUntilBlocked();
            }
            else
            {
                _state.DrawCards(playerId, drawCount);
            }
        }
    }

    /// <summary>本回合该不该常规抽牌（先手第 1 回合按规则可开关）。</summary>
    private bool ShouldDrawThisTurn(int playerId)
    {
        var rules = _state.Rules;
        if (_state.TurnNumber == 1 && playerId == _state.FirstPlayer && !rules.FirstPlayerDrawsOnTurn1) return false;
        return true;
    }

    /// <summary>纹章区：回合初能力排入队列 + 吟唱推进。</summary>
    private void StepCrestArea(int playerId, CardVm vm, bool ownTurn)
    {
        var cdKey = _schema.Counter("countdown");
        foreach (var e in _state.Players[playerId].CrestArea.ToList())
        {
            // 铁律：信仰（Faith）是整局永驻资源，没有吟唱倒计时，
            // 不随回合递减，也绝不到期消失 —— 哪怕信仰值被消耗回 0 也要留在纹章区。
            if (e == null || e.IsFaith || e.Def?.Kind == Ast.CardKind.Faith) continue;

            if (!e.Counters.TryGetValue(cdKey, out int cd) || cd <= 0) continue;
            if (!ownTurn) continue;          // 吟唱只在持有者回合推进
            cd--;
            e.Counters[cdKey] = cd;
            if (cd == 0)
            {
                EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnCountdownExpire, Subject = EntityRef.From(e) });
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
                EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnCountdownExpire, Subject = EntityRef.From(e) });
                _state.DestroyEntity(e);
            }
        }
    }

    /// <summary>
    /// 回合结束，严格 7 步（R-3.2） + 收尾清理。
    /// 清理阶段必须放在所有触发之后 ——
    /// “直到回合结束”的效果在回合结束时仍然要生效。
    /// </summary>

    /// <summary>
    /// 回合结束时的卡池级计数器生命周期。
    ///
    /// 目前只有 ResetToMinOnTurnEnd 一种（连击类的镜像场景）。
    /// 与 StartTurn 的驱动分开写：两边触发时机和顺序都不同，
    /// 合在一起会让「回合开始归零」和「回合结束归零」互相覆盖。
    /// </summary>
    private void DrivePoolCountersOnTurnEnd(int playerId)
    {
        var defs = _state.PoolCounterDefs;
        if (defs == null || defs.Count == 0) return;

        foreach (var def in defs)
        {
            if (def == null || string.IsNullOrWhiteSpace(def.Name)) continue;
            if (def.Lifecycle != ShadowverseFan.KernelV3.Packs.CounterLifecycle.ResetToMinOnTurnEnd) continue;
            var key = _state.Schema.Counter(def.Name);
            if (!key.IsValid) continue;
            _state.WriteCounter(playerId, key, def.Min);
        }
    }

    public void EndTurn(int playerId, CardVm vm)
    {
        if (playerId != _state.ActivePlayer)
            throw new InvalidOperationException("只能结束当前回合玩家的回合");

        // 卡池级计数器的「回合结束」生命周期（164 专题二）。
        // 放在能力结算之前：这些是赛制级资源，不该被本回合最后一条能力影响。
        DrivePoolCountersOnTurnEnd(playerId);

        // 额外能量点独立槽位只在本回合有效；回合末能力结算前就撤掉，
        // 免得「回合结束时」的效果白嫖到这一格。次数是否消耗已在借用时记账。
        _state.Players[playerId].BonusPp = 0;

        // ── 第 1〜6 步：三类“回合结束时”能力（自己纹章 / 自己战场 / 对手纹章 / 对手战场）──
        // 实现上统一由 TurnEnd 事件驱动，VM 会按触发器的 DuringTurn 限定逐个对号入座。
        // 不再手写四次循环 —— 那样反而会把同一条能力反复排队。
        EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.TurnEnd, PlayerId = playerId });

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

    // ---------- 能量点支付（普通 PP 优先，额外槽位兜底）----------

    /// <summary>
    /// 当前可用于支付费用的能量点总数 = 普通 pp + 独立额外槽位 BonusPp。
    /// 只用于「够不够付 / 爆能档位」的判定；真实扣费必须走 SpendPp。
    /// </summary>
    public int AvailablePp(int playerId)
        => _state.ReadCounter(playerId, _schema.Counter("pp"))
           + _state.Players[playerId].BonusPp;

    /// <summary>
    /// 扣费：普通 PP 优先，只有普通 PP 不够时才动用额外槽位。
    /// 一旦真正借用了额外槽位，立刻把该次（Early/Late）标记为已消耗；
    /// 之后即使卡牌效果恢复 PP，也不会把这次消耗退还（判定点是扣费那一刻）。
    /// </summary>
    public bool SpendPp(int playerId, int cost)
    {
        if (cost < 0) cost = 0;
        var ppKey = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, ppKey);
        if (pp >= cost)
        {
            _state.WriteCounter(playerId, ppKey, pp - cost);
            return true;
        }

        int need = cost - pp;
        var p = _state.Players[playerId];
        if (p.BonusPp < need) return false;

        _state.WriteCounter(playerId, ppKey, 0);
        p.BonusPp -= need;
        MarkExtraPpConsumed(playerId);
        return true;
    }

    /// <summary>把当前阶段的额外能量点次数记为「真正使用掉」。</summary>
    private void MarkExtraPpConsumed(int playerId)
    {
        var p = _state.Players[playerId];
        bool early = _state.TurnNumber <= _state.Rules.ExtraPpEarlyPhaseLastTurn;
        if (early) p.UsedEarlyExtraPp = true; else p.UsedLateExtraPp = true;
        p.UsedExtraPpThisTurn = true;
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
        bool full = pp0.Board.Count >= _state.Rules.MaxBoardSize;

        int pp = AvailablePp(playerId);
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

            // 结晶打出后是**护符**，按 76-c 2.3-A-2：护符没合法手选目标也能拍上场。
            // 套用法术的目标预检会让结晶卡在敌方空场时打不出去。
            if (card.Def.CrystallizeDef != null && pp >= card.Def.CrystallizeDef.BaseCost && !full)
                return true;

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
        bool boardFull = p.Board.Count >= _state.Rules.MaxBoardSize;

        int pp = AvailablePp(playerId);

        // ── 爆能强化强制铁律 ──
        // pp >= N 时必须按最高可支付档付费，不能选择只付原费。
        int pay = card.Cost;
        int bestTier = 0;
        foreach (var tier in EnhanceTiers(card)) if (pp >= tier && tier > bestTier) bestTier = tier;

        // 只要命中合法档位（bestTier > 0）且不低于当前牌面费用，就算发动了爆能。
        // 旧判据 `bestTier > card.Cost` 会漏掉「手牌被加费到与档位同额」的情形：
        // 4 费卡带 Enhance(5) 在手里被加费成 5 费，5 PP 打出时 5 > 5 为假，
        // 于是一档都不算发动。
        bool isEnhanced = bestTier > 0 && bestTier >= card.Cost;
        if (isEnhanced) pay = bestTier;

        // 记录本卡本次是以【爆能强化】打出（供 "Whenever you play an Enhanced card" 判定）。
        // 必须在扣费前记：进墓地/被回手的路径都不会再回来改这个标志。
        card.PlayedWithEnhance = isEnhanced;

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
        // pp_at_play 是**本次生效的爆能档位**，不是玩家当前 PP。
        //
        // 旧实现无条件写 pp：4 费卡在手握 7 PP 时打出（正常出，没有爆能），
        // pp_at_play 也被写成 7，VM 里的 `if pp_at_play >= 7` 分支被白嫖执行；
        // 场上有《米路缇欧》封印爆能时更是直接绕过封印。
        // 未发动爆能就必须写 0，让所有爆能分支走 else。
        _state.WriteCounter(playerId, _schema.Counter("pp_at_play"), isEnhanced ? bestTier : 0);
        SpendPp(playerId, pay);

        // 记入本局已使用卡牌（书页「使用」页签按它统计）。
        _state.Players[playerId].PlayedThisMatch.Add(card.Def);
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
            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnPlay,
                Subject = EntityRef.From(card),
                // PlayerId 必须带上：它不参与触发判定（OnPlay 的判据是
                // 「打出的这张牌就是本卡」），但前端要靠它区分敌我，
                // 敌方出牌才播「从敌方手牌飞出一张」的动画。
                PlayerId = playerId,
            });

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
            // 法术：魔力增幅（效果生效前）→ 结算效果 → 进墓场 → shadow +1。
            //
            // 魔力增幅必须在**效果生效前**发动，所以排在 OnPlay 之前。
            // 效果若含 Select 或选模式，VM 会挂起等玩家输入；进墓地与 shadow 仍排在
            // 效果彻底结算完之后，避免「玩家还没选，法术已经进墓地」。
            BumpSpellboost(playerId, card, vm);

            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnPlay,
                Subject = EntityRef.From(card),
                // PlayerId 必须带上：它不参与触发判定（OnPlay 的判据是
                // 「打出的这张牌就是本卡」），但前端要靠它区分敌我，
                // 敌方出牌才播「从敌方手牌飞出一张」的动画。
                PlayerId = playerId,
            });

            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                    var shadowKey = _schema.Counter("shadow");
                    _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
                });
                vm.StepUntilBlocked();
            }
            else
            {
                _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                var shadowKey = _schema.Counter("shadow");
                _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
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

    // ---------------------------------------------------------------- 手牌呈现状态

    /// <summary>手牌在当前 PP 下的可打出形态（决定手牌光圈颜色与显示费用）。</summary>
    public enum HandPlayMode
    {
        Unplayable = 0,
        Normal = 1,      // 可按本体打出：翠绿光圈
        Enhance = 2,     // PP 已达爆能档位：灿金黄光圈，费用球显示爆能费
        Accelerate = 3,  // 本体费不够但可激奏：天青蓝光圈
        Crystallize = 4, // 本体费不够但可结晶：天青蓝光圈
    }

    /// <summary>
    /// 计算一张手牌在当前 PP 下的显示费用与可打出模式。
    /// PP 变化时由前端逐张调用，刷新费用球颜色与光圈。
    /// </summary>
    public (HandPlayMode Mode, int DisplayCost) EvaluateHandCardPresentation(
        int playerId, RuntimeEntity card, bool isMyActiveTurn)
    {
        if (card?.Def == null) return (HandPlayMode.Unplayable, 0);

        var p = _state.Players[playerId];
        int pp = AvailablePp(playerId);
        bool folOrAmu = card.Def.Kind is Ast.CardKind.Follower or Ast.CardKind.Amulet;
        bool boardFull = p.Board.Count >= _state.Rules.MaxBoardSize;

        // 1. 当前 PP 能支付的最高爆能档位。
        int bestEnhance = 0;
        foreach (var tier in EnhanceTiers(card))
            if (pp >= tier && tier > bestEnhance) bestEnhance = tier;

        int bodyPay = bestEnhance > card.Cost ? bestEnhance : card.Cost;
        bool canPlayNow = isMyActiveTurn && p.Hand.Contains(card)
                          && !card.Keywords.Contains(_schema.Keyword("cannot_be_played"));

        // 2. PP 够付本体（或爆能档位）。
        if (pp >= bodyPay)
        {
            // 满场下不了随从/护符本体；激奏不占场，能付就切激奏。
            if (folOrAmu && boardFull)
            {
                if (card.Def.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
                {
                    bool accOk = canPlayNow && CheckAltSpellTarget(card, card.Def.AccelerateDef);
                    return (accOk ? HandPlayMode.Accelerate : HandPlayMode.Unplayable,
                            card.Def.AccelerateDef.BaseCost);
                }
                return (HandPlayMode.Unplayable, bodyPay);
            }

            bool spellOk = card.Def.Kind != Ast.CardKind.Spell || HasLegalTargetForSpell(card);
            if (!canPlayNow || !spellOk)
                return (HandPlayMode.Unplayable, bodyPay);

            return (bestEnhance > card.Cost ? HandPlayMode.Enhance : HandPlayMode.Normal, bodyPay);
        }

        // 3. 本体费不够 -> 激奏 / 结晶是另一条合法路径。
        if (card.Def.AccelerateDef != null && pp >= card.Def.AccelerateDef.BaseCost)
        {
            bool accOk = canPlayNow && CheckAltSpellTarget(card, card.Def.AccelerateDef);
            return (accOk ? HandPlayMode.Accelerate : HandPlayMode.Unplayable,
                    card.Def.AccelerateDef.BaseCost);
        }

        if (card.Def.CrystallizeDef != null && pp >= card.Def.CrystallizeDef.BaseCost && !boardFull)
        {
            return (canPlayNow ? HandPlayMode.Crystallize : HandPlayMode.Unplayable,
                    card.Def.CrystallizeDef.BaseCost);
        }

        return (HandPlayMode.Unplayable, card.Cost);
    }

    private bool CheckAltSpellTarget(RuntimeEntity card, Ast.CardDef altDef)
    {
        var saved = card.Def;
        card.Def = altDef;
        bool ok = HasLegalTargetForSpell(card);
        card.Def = saved;
        return ok;
    }

    private bool PlayAsAltMode(int playerId, RuntimeEntity card, Ast.CardDef altDef,
                               bool isAccelerate, CardVm vm)
    {
        var p = _state.Players[playerId];
        int pp = AvailablePp(playerId);
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
        SpendPp(playerId, cost);

        // 激奏/结晶同样算「使用过这张卡」，记的仍是本体定义 ——
        // 统计页要显示「你打了几张 X」，而不是「几张激奏版 X」。
        p.PlayedThisMatch.Add(card.Def);
        _state.WriteCounter(playerId, _schema.Counter("combo"),
            _state.ReadCounter(playerId, _schema.Counter("combo")) + 1);

        card.OriginalDef = original;
        card.Def = altDef;
        card.Cost = cost;

        if (isAccelerate)
        {
            // 激奏：魔力增幅（效果生效前）→ 按法术结算 → 进墓地 → shadow +1 → 还原。
            // 与普通法术同理：增幅排在 OnPlay 之前；效果挂了起等选择时，
            // 卡不该提前进墓地，也不该提前把 Def 还原成本体随从 ——
            // 那会让后续的「这张卡是什么」读错。
            BumpSpellboost(playerId, card, vm);

            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnPlay,
                Subject = EntityRef.From(card),
                // PlayerId 必须带上：它不参与触发判定（OnPlay 的判据是
                // 「打出的这张牌就是本卡」），但前端要靠它区分敌我，
                // 敌方出牌才播「从敌方手牌飞出一张」的动画。
                PlayerId = playerId,
            });

            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    _state.MoveEntity(card, ZoneRef.Cemetery, playerId);
                    var shadowKey = _schema.Counter("shadow");
                    _state.WriteCounter(playerId, shadowKey, _state.ReadCounter(playerId, shadowKey) + 1);
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
            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnPlay,
                Subject = EntityRef.From(card),
                // PlayerId 必须带上：它不参与触发判定（OnPlay 的判据是
                // 「打出的这张牌就是本卡」），但前端要靠它区分敌我，
                // 敌方出牌才播「从敌方手牌飞出一张」的动画。
                PlayerId = playerId,
            });
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
        // 带爆能 / 连击替代的指向性法术，Body 在编织后是顶层 ConditionalNode。
        // 只遍历顶层会让它绕过目标合法性预检 —— 敌方空场也能空放出去。
        // 必须按当前 PP / 连击走进**实际会走**的那个分支。
        // 出牌瞬间这张法术仍留在手牌；法术本体的能力在结算完才进墓地。
        // 预检必须把 $self 明确绑成这张卡，否则 other-than-itself 类目标
        // 会因为 $self 槽位为空而退化成「不排除任何人」。
        var eval = new Vm.VmEval(_state);
        var scope = new Vm.VmScope
        {
            Owner = Core.ZoneScope.Self,
            Controller = card.PlayerId,
            Source = Vm.EntityRef.From(card),
        };
        scope.Bind(Ast.TargetSpec.SlotSelf, new[] { card });

        bool sawChoose = false;
        bool CheckNodes(IReadOnlyList<Ast.EffectNode> nodes)
        {
            if (nodes == null) return true;
            foreach (var node in nodes)
            {
                if (node is Ast.ConditionalNode cond)
                {
                    bool take = true;
                    try { take = cond.Condition == null || eval.EvalBool(cond.Condition, scope); }
                    catch { take = true; }
                    if (!CheckNodes(take ? cond.Then : cond.Else)) return false;
                    continue;
                }

                var spec0 = GetChooseTarget(node);
                if (spec0 == null || spec0.Mode != SelectMode.Choose) continue;
                if (spec0.SourceKind == Ast.TargetSourceKind.Slot) continue;

                sawChoose = true;
                var cands = LegalTargetsOf(spec0, card, scope);
                if (cands.Count <= 0) return false;

                // 把本次的选择结果推进作用域，供后面的 Select 引用：
                //   "选择 1 个敌方随从，再选择另 1 个不同于第 1 个所选随从的敌方随从"
                // 敌方只有 1 个随从时，第 2 个 Select 带着 other than $choose1，
                // 候选池应当为空，法术因此不可打出。
                // 每次都新建空 scope 会让 $choose1 永远是空的，
                // 那个唯一随从会被误判成「第 2 个目标也能选中」。
                //
                // 只 bind 一次：BindSelection 每调一次都会 ++SelectSequence，
                // 连调两次会把编号推到 2，$choose1 与 $choose2 双双错位。
                // 真实打出时玩家只选 1 个，这里取第一个候选作为代表。
                scope.BindSelection(Ast.TargetSpec.SlotSelected,
                                    new[] { Vm.EntityRef.From(cands[0]) }, append: false);
            }
            return true;
        }

        foreach (var ab in card.Def.Abilities)
        {
            if (ab?.Body == null) continue;
            if (!CheckNodes(ab.Body)) return false;
        }

        // 走到这里说明「所有 Choose 目标都至少有一个合法候选」。
        // 这里必须返回 true ——
        // 早先写的是 return !sawChoose，于是只要卡带手选目标就恒返回 false，
        // 全游戏的点杀/指定法术一张都打不出去（而单卡测试只测 Choose 不合法的那一面，测不出来）。
        return true;
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
        => LegalTargetsOf(spec, source, null).Count;

    /// <summary>
    /// 该目标声明的**全部**合法候选实体。
    ///
    /// 与 CountLegalTargets 的区别是多带一个可累加的作用域：
    /// HasLegalTargetForSpell 要在检查过程中把前一次 Select 的结果写进 $chooseK，
    /// 好让「other than $choose1」这类排除条件有对象可比。
    /// </summary>
    private List<RuntimeEntity> LegalTargetsOf(Ast.TargetSpec spec, RuntimeEntity source, Vm.VmScope outer)
    {
        // 与 CardVm 执行时同一套归一化：PyAST 把 <Follower> / other 提权成区域修饰词，
        // 解析回来的 Where 默认主语是 $self；不归一化会让「敌方随从」候选恒空。
        spec = Vm.CardVm.NormalizeZoneWhere(spec);
        int playerId = source?.PlayerId ?? _state.ActivePlayer;
        if (spec == null || spec.Mode != SelectMode.Choose) return new List<RuntimeEntity>();
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
        // 有外层作用域就沿用它，把此前 Select 已经写入的 $selected / $chooseK 带过来；
        // 没有才新建。这样多目标法术的第 2 个 Select 才能看见第 1 个选了什么。
        var sc = outer ?? new Vm.VmScope
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
            // 光环授予的潜行/灵气同样算数（与 ResolveOrBlock / DoSelect 保持一致）。
            return !_state.HasEffectiveKeyword(e, ambush)
                && !_state.HasEffectiveKeyword(e, aura);
        }).ToList();
        return pool;
    }

    /// <summary>该卡声明了哪些 Enhance (N) 档位。</summary>
    private IEnumerable<int> EnhanceTiers(RuntimeEntity card)
    {
        if (card.Def?.Abilities == null) yield break;

        // 被「爆能强化无法发动」光环覆盖时，一档都不给 ——
        // 于是调用方算出来的 bestTier 等于基础费用，不会进入爆能分支。
        if (_state != null && _state.HasEffectiveKeyword(card, _schema.Keyword("suppress_enhance")))
            yield break;
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

    /// <summary>
    /// 打出法术时：**默认发动一次魔力增幅**，且必须发生在该法术效果生效之前。
    ///
    /// 规则：打出任何法术都会发动一次魔力增幅，与该法术自身是否写了增幅无关；
    /// 但「不包括给自己」—— 正在打出的这张法术不获得增幅。
    ///
    /// 手牌里所有带【魔力增幅时】能力的卡 +1；事件带 PlayerId 供能力按归属过滤。
    /// </summary>
    private void BumpSpellboost(int playerId, RuntimeEntity spellCard, CardVm vm)
    {
        var sbKey = _schema.Counter("spellboost");
        foreach (var e in _state.Players[playerId].Hand)
        {
            if (ReferenceEquals(e, spellCard)) continue;   // 不包括给自己
            if (e.Def?.Abilities == null) continue;
            bool has = false;
            foreach (var ab in e.Def.Abilities)
                if (ab?.Trigger != null && ab.Trigger.Event == CoreEvent.OnSpellboost) { has = true; break; }
            if (!has) continue;

            e.Counters.TryGetValue(sbKey, out var cur);
            e.Counters[sbKey] = cur + 1;
        }
        // 任何法术都发一次事件（带 PlayerId）；没有增幅卡时事件无接收者，无害。
        EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnSpellboost, PlayerId = playerId });
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

        return AvailablePp(playerId) >= engageCost;
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
        if (!SpendPp(playerId, cost)) return false;

        amulet.EngagedThisTurn = true;
        EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnEngage, Subject = EntityRef.From(amulet) });
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
        EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnFuse, Subject = EntityRef.From(baseCard) });
        vm?.StepUntilBlocked();
        return true;
    }

    // ---------- 后手额外能量点 ----------

    /// <summary>
    /// 后手玩家「激活」额外能量点：只写独立槽位 BonusPp，不改 pp / max_pp。
    /// 一局两次，第 1~5 回合一次、第 6 回合起一次，每回合最多激活一次。
    /// 是否算用掉，取决于扣费那一刻有没有真正借用到这个槽位。
    /// </summary>
    /// <summary>现在能不能按额外能量点。纯查询，与 UseExtraPp 的前置判定对齐。</summary>
    public bool CanUseExtraPp(int playerId)
    {
        if (!_state.Rules.EnableExtraPpForSecondPlayer) return false;
        if (playerId == _state.FirstPlayer) return false;      // 只有后手有（先手由本局决定）
        if (playerId != _state.ActivePlayer) return false;      // 只能在自己回合
        var pl = _state.Players[playerId];
        if (pl.UsedExtraPpThisTurn) return false;
        bool earlyTurn = _state.TurnNumber <= _state.Rules.ExtraPpEarlyPhaseLastTurn;
        return earlyTurn ? !pl.UsedEarlyExtraPp : !pl.UsedLateExtraPp;
    }

    public bool UseExtraPp(int playerId)
    {
        // 直接复用 CanUseExtraPp：两份判定各写一遍必然漂移 ——
        // 早先 CanUseExtraPp 读了规则，UseExtraPp 却写死 5 且不检查开关，
        // 于是关掉 EnableExtraPpForSecondPlayer 后仍能直接调用加成。
        if (!CanUseExtraPp(playerId)) return false;
        var p = _state.Players[playerId];
        // 这里只记「本回合已激活」，绝不记 Early/Late 已消耗。
        // Early/Late 的消耗发生在 SpendPp 真正借用槽位的那一刻。
        p.UsedExtraPpThisTurn = true;
        p.BonusPp = 1;
        return true;
    }

    // ---------- 进化 ----------

    /// <summary>普通进化。先手第 5 回合 / 后手第 4 回合解锁，消耗 1 ep。</summary>
    public bool CanEvolve(int playerId) => CanEvolve(playerId, null);

    /// <summary>
    /// 能否手动进化。
    ///
    /// 传入 unit 时会检查「无需进化点即可进化」：持有该关键词的随从
    /// 即使 EP 为 0 也能进化，且进化时不扣点。
    /// </summary>
    public bool CanEvolve(int playerId, RuntimeEntity unit)
    {
        if (playerId != _state.ActivePlayer) return false;
        var p = _state.Players[playerId];
        if (p.ManualEvolvedThisTurn) return false;

        if (_state.TurnNumber < _state.Rules.EvolveUnlockTurn(playerId, _state.FirstPlayer)) return false;

        // 走 HasEffectiveKeyword：光环授予的「无需进化点进化」同样要生效。
        if (unit != null && _state.HasEffectiveKeyword(unit, _schema.Keyword("free_evolve")))
            return true;

        var epKey = _schema.Counter("ep");
        return _state.ReadCounter(playerId, epKey) > 0;
    }

    /// <summary>超进化。先手第 7 回合 / 后手第 6 回合解锁，消耗 1 sep。</summary>
    public bool CanSuperEvolve(int playerId) => CanSuperEvolve(playerId, null);

    /// <summary>能否手动超进化；持有 free_super_evolve 时即使是 0 点也允许。</summary>
    public bool CanSuperEvolve(int playerId, RuntimeEntity unit)
    {
        if (playerId != _state.ActivePlayer) return false;
        var p = _state.Players[playerId];
        if (p.ManualEvolvedThisTurn) return false;

        if (_state.TurnNumber < _state.Rules.SuperEvolveUnlockTurn(playerId, _state.FirstPlayer)) return false;

        if (unit != null && _state.HasEffectiveKeyword(unit, _schema.Keyword("free_super_evolve")))
            return true;

        var sepKey = _schema.Counter("sep");
        return _state.ReadCounter(playerId, sepKey) > 0;
    }

    /// <summary>
    /// 事件出口：优先走 _state.EmitEvent（会同时写入 EventLog 并转发给 VM），
    /// 只有在没有 TriggerSink 且有 vm 时才退化为直接入列。
    ///
    /// 早先这里直接 vm?.EnqueueEvent —— 绕过了 EventLog，
    /// 前端 ConsumeNewEventsToVfx 读不到任何记录，
    /// 于是「手动进化」「回合开始/结束」的演出全是空气。
    /// </summary>
    private void EmitOrEnqueue(CardVm vm, VmEvent ev)
    {
        if (ev == null) return;
        if (_state.TriggerSink != null || vm == null) _state.EmitEvent(ev);
        else { _state.EventLog.Add(ev); vm.EnqueueEvent(ev); }
    }

    public bool Evolve(int playerId, RuntimeEntity e, bool super, CardVm vm)
    {
        // 必须把 e 传进去：否则持有 free_evolve / free_super_evolve 的随从
        // 在 EP/SEP 为 0 时会被这道守卫挡掉，关键词形同虚设。
        bool ok = super ? CanSuperEvolve(playerId, e) : CanEvolve(playerId, e);
        if (!ok) return false;
        if (e == null || e.Zone != ZoneRef.Board || e.PlayerId != playerId) return false;
        if (e.IsEvolved) return false;

        var p = _state.Players[playerId];
        p.ManualEvolvedThisTurn = true;

        if (super)
        {
            // 「无需超进化点即可超进化」：不扣点，其余流程完全不变。
            if (!_state.HasEffectiveKeyword(e, _schema.Keyword("free_super_evolve")))
            {
                var sepKey = _schema.Counter("sep");
                _state.WriteCounter(playerId, sepKey, _state.ReadCounter(playerId, sepKey) - 1);
            }
            e.IsSuperEvolved = true;
            e.IsEvolved = true;          // 超进化包含普通进化状态
            e.Attack += _state.Rules.SuperEvolveBonusAttack;
            e.Life += _state.Rules.SuperEvolveBonusLife;
            e.MaxLife += _state.Rules.SuperEvolveBonusLife;
        }
        else
        {
            // 「无需进化点即可进化」：不扣点。
            if (!_state.HasEffectiveKeyword(e, _schema.Keyword("free_evolve")))
            {
                var epKey = _schema.Counter("ep");
                _state.WriteCounter(playerId, epKey, _state.ReadCounter(playerId, epKey) - 1);
            }
            e.IsEvolved = true;
            e.Attack += _state.Rules.EvolveBonusAttack;
            e.Life += _state.Rules.EvolveBonusLife;
            e.MaxLife += _state.Rules.EvolveBonusLife;
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

        EmitOrEnqueue(vm, new VmEvent
        {
            Kind = CoreEvent.OnEvolve,
            Subject = EntityRef.From(e),
            PlayerId = playerId,
            IsManualEvolve = true,
        });
        if (super)
        {
            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnSuperEvolve,
                Subject = EntityRef.From(e),
                PlayerId = playerId,
                IsManualEvolve = true,
            });
        }
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

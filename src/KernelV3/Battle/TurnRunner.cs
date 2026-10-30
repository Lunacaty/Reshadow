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
    public void StartTurn(int playerId, CardVm vm)
    {
        if (playerId is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(playerId), "回合玩家必须是 0 或 1");

        _state.ActivePlayer = playerId;
        if (playerId == 0) _state.TurnNumber++;

        // 触发器的回合归属与每回合计数必须跟着换
        vm?.CurrentTurnOwner = playerId;
        vm?.ResetTriggerCounts();

        var p = _state.Players[playerId];

        // 奥义槽 += 1（回合数增加了 1）。规则：奥义槽 = 当前回合数 + 本卡在手时我方进化次数。
        // 每张手牌各自维护自己的槽位，所以这里给手牌里每张「带奥义能力」的卡累加。
        BumpSkyboundGauge(playerId);

        // 1. 只涨当前玩家的 MaxPP 与 PP（上限 10）
        var maxPpKey = _schema.Counter("max_pp");
        var ppKey = _schema.Counter("pp");
        int maxPp = Math.Min(10, _state.ReadCounter(playerId, maxPpKey) + 1);
        _state.WriteCounter(playerId, maxPpKey, maxPp);
        _state.WriteCounter(playerId, ppKey, maxPp);

        // 2. 重置本回合的限额与统计
        var comboKey = _schema.Counter("combo");
        _state.WriteCounter(playerId, comboKey, 0);

        p.ManualEvolvedThisTurn = false;
        p.UsedExtraPpThisTurn = false;

        // 每回合重置实体的「本回合已启动 / 已融合」标记。
        // 不重置的话，一个护符在第一次 Engage 之后就永远不能再启动。
        foreach (var e in _state.EnumerateZone(ZoneRef.Board, -1))
        {
            e.EngagedThisTurn = false;
            e.FusedThisTurn = false;
        }
        foreach (var e in _state.EnumerateZone(ZoneRef.Hand, -1))
        {
            e.FusedThisTurn = false;
        }
        p.EngagedEntityIdsThisTurn.Clear();
        p.FusedEntityIdsThisTurn.Clear();
        p.DestroyedThisTurn.Clear();

        // 3. 重置本方的攻击次数
        foreach (var e in p.Board)
        {
            e.AttacksThisTurn = 0;
            e.AttackedThisTurn = false;
        }

        // 4. 推进本方护符/纹章的倒计时
        var cdKey = _schema.Counter("countdown");
        foreach (var e in p.Board.Concat(p.CrestArea).ToList())
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

        // 5. 回合开始事件
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnStart });

        // 6. 常规抽 1 张。
        // 统一口径：每个回合开始时当前回合玩家都抽 1 张，
        // 先手第一回合也不例外。之前按「先手不抽」写的分支会少抽一张。
        _state.DrawCards(playerId, 1);

        vm?.StepUntilBlocked();
    }

    /// <summary>结束当前玩家回合，发出回合结束事件。</summary>
    public void EndTurn(int playerId, CardVm vm)
    {
        if (playerId != _state.ActivePlayer)
            throw new InvalidOperationException("只能结束当前回合玩家的回合");

        // 先跑完回合结束触发（"At the end of your turn" 效果仍在本回合生效）
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnEnd });
        vm?.StepUntilBlocked();

        // 再撤销到期的限时修正（官方：until the end of the turn）
        _state.ExpireTemporaryModifiers(playerId);
    }

    // ---------- 出牌 ----------

    /// <summary>
    /// 从手牌打出一张卡。
    /// Fanfare(OnPlay) 只有这条路径才触发 —— 召唤/复活拉进来的随从不会触发。
    /// </summary>
    public bool PlayCard(int playerId, RuntimeEntity card, CardVm vm)
    {
        if (playerId != _state.ActivePlayer) return false;

        var p = _state.Players[playerId];
        if (!p.Hand.Contains(card)) return false;

        var ppKey = _schema.Counter("pp");
        int pp = _state.ReadCounter(playerId, ppKey);
        if (pp < card.Cost) return false;

        // 爆能强化：先用**本次打出前**的 PP 判定能不能走高档，
        // 再一次性扣陣。之前先扣基础费再让 EnhanceWeaver 判 pp >= N，
        // 已经扣掉基础费的 pp 永远达不到高档。
        // 这里只记录“本次打出时可用 PP”，供 VM 内的分支判定使用。
        // 记下打出前的 PP：Enhance 分支靠它判定能走哪一档。
        _state.WriteCounter(playerId, _schema.Counter("pp_at_play"), pp);
        _state.WriteCounter(playerId, ppKey, pp - card.Cost);
        card.EnteredTurn = _state.TurnNumber;

        bool isFollowerOrAmulet = card.Def != null
            && (card.Def.Kind == Ast.CardKind.Follower || card.Def.Kind == Ast.CardKind.Amulet);

        if (isFollowerOrAmulet)
            _state.MoveEntity(card, ZoneRef.Board, playerId);
        else
            _state.MoveEntity(card, ZoneRef.Cemetery, playerId);   // 法术结算后进墓地

        // 连击计数
        var comboKey = _schema.Counter("combo");
        _state.WriteCounter(playerId, comboKey, _state.ReadCounter(playerId, comboKey) + 1);

        // 只有主动打出才触发 OnPlay
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(card) });
        vm?.StepUntilBlocked();
        return true;
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

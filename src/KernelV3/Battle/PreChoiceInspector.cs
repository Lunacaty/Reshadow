using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

/// <summary>
/// 发动前预扫描器：把一张卡的自身能力 Body 从头走一遍，
/// 找出「在扣费之前就必须问玩家」的目标与模式。
///
/// 它**只读**：不修改 BattleState，不执行任何效果，
/// 只回答一个问题 —— 这次操作还需要玩家选什么。
///
/// 必须与运行时的挂起点严格对齐：
///   运行时走 Block() 的地方（DoSelect / ResolveOrBlock / DoBranch），
///   这里就要产出对应 Prompt；少了会让预选卡住，多了会让答案错位。
/// </summary>
public static class PreChoiceInspector
{
    public sealed class Result
    {
        /// <summary>需要玩家先做的选择；null 表示没有前置必选，可直接提交。</summary>
        public PendingChoice Prompt { get; init; }

        /// <summary>本次已消费掉的答案个数。</summary>
        public int Consumed { get; init; }
    }

    public static Result Inspect(
        BattleState state,
        RuntimeEntity card,
        PlayerActionKind kind,
        IReadOnlyList<IReadOnlyList<int>> answers)
    {
        if (state == null || card?.Def?.Abilities == null) return new Result();

        // 模拟出牌 / 进化**那一刻**的上下文，否则条件分支一律判错：
        //   · PlayCard：pp_at_play 尚未写入、combo 尚未 +1
        //     → Enhance / Combo 分支预扫时恒为假，该问的不问
        //   · Evolve / SuperEvolve：随从的 IsEvolved 还是 false
        //     → HasStateExpr(Evolved, Self) 恒假，进化分支被整段跳过
        // try/finally 严格还原，保证本方法只读、无副作用。
        var ppKey = state.Schema.Counter("pp");
        var ppAtPlayKey = state.Schema.Counter("pp_at_play");
        var comboKey = state.Schema.Counter("combo");
        int savedPpAtPlay = state.ReadCounter(card.PlayerId, ppAtPlayKey);
        int savedCombo = state.ReadCounter(card.PlayerId, comboKey);
        bool savedEv = card.IsEvolved, savedSep = card.IsSuperEvolved;

        try
        {
            if (kind == PlayerActionKind.PlayCard)
            {
                // 与 TurnRunner.PlayCard 保持同一真相：被封印爆能时 pp_at_play 写 0，
                // 否则预扫会把爆能分支当成可走，弹出根本不该出现的选目标框。
                int ppNow = state.ReadCounter(card.PlayerId, ppKey);
                bool suppressed = state.HasEffectiveKeyword(
                    card, state.Schema.Keyword("suppress_enhance"));
                int synthetic = suppressed ? 0 : ppNow;
                state.WriteCounter(card.PlayerId, ppAtPlayKey, synthetic);
                state.WriteCounter(card.PlayerId, comboKey, savedCombo + 1);
            }
            else if (kind == PlayerActionKind.Evolve) card.IsEvolved = true;
            else if (kind == PlayerActionKind.SuperEvolve)
            {
                card.IsEvolved = true;
                card.IsSuperEvolved = true;
            }

            var scope = ScopeOf(card);
            int consumed = 0;

            // 激奏 / 结晶：当前 PP 不够本体费用时，运行时走的是子卡定义。
            // 预扫必须跟着切，否则会按本体入场曲去问目标 ——
            // 玩家被问了、选了，真正打出的却是激奏版，答案全错位。
            var defToScan = card.Def;
            if (kind == PlayerActionKind.PlayCard && defToScan != null)
            {
                int ppNow = state.ReadCounter(card.PlayerId, ppKey);
                int bodyCost = defToScan.BaseCost;
                bool boardFull = state.Players[card.PlayerId].Board.Count
                                 (x => x?.Def != null
                                    && (x.Def.Kind == Ast.CardKind.Follower
                                        || x.Def.Kind == Ast.CardKind.Amulet))
                                 >= state.Rules.MaxBoardSize;

                bool bodyPlayable = ppNow >= bodyCost
                    && !(boardFull
                         && (defToScan.Kind == Ast.CardKind.Follower
                             || defToScan.Kind == Ast.CardKind.Amulet));

                if (!bodyPlayable)
                {
                    if (defToScan.AccelerateDef != null
                        && ppNow >= defToScan.AccelerateDef.BaseCost)
                        defToScan = defToScan.AccelerateDef;
                    else if (defToScan.CrystallizeDef != null
                             && ppNow >= defToScan.CrystallizeDef.BaseCost && !boardFull)
                        defToScan = defToScan.CrystallizeDef;
                }
            }

            foreach (var ab in defToScan.Abilities)
            {
                if (ab?.Trigger == null || ab.Body == null) continue;

                // 只认**自身能力**：SubjectFilter 非空的是旁观监听
                // （「每当你打出战利品卡时…」的 Trigger.Event 也是 OnPlay），
                // 把它当自身入场曲预扫会凭空多问一次。
                if (ab.Trigger.SubjectFilter != null
                    && !(ab.Trigger.SubjectFilter.SourceKind == TargetSourceKind.Slot
                         && ab.Trigger.SubjectFilter.Slot == TargetSpec.SlotSelf))
                    continue;

                bool trigMatch = kind switch
                {
                    PlayerActionKind.Evolve => ab.Trigger.Event == CoreEvent.OnEvolve,
                    // 超进化同时触发 OnEvolve 与 OnSuperEvolve
                    PlayerActionKind.SuperEvolve => ab.Trigger.Event == CoreEvent.OnEvolve
                                                 || ab.Trigger.Event == CoreEvent.OnSuperEvolve,
                    PlayerActionKind.EngageAmulet => ab.Trigger.Event == CoreEvent.OnEngage,
                    _ => ab.Trigger.Event == CoreEvent.OnPlay,
                };
                if (!trigMatch) continue;

                if (!EvalCond(state, card, ab.Condition)) continue;

                var prompt = Walk(state, card, scope, ab.Body, answers, ref consumed);
                if (prompt != null) return new Result { Prompt = prompt, Consumed = consumed };
            }

            return new Result { Consumed = consumed };
        }
        finally
        {
            state.Players[card.PlayerId].Counters[ppAtPlayKey.Id] = savedPpAtPlay;
            state.Players[card.PlayerId].Counters[comboKey.Id] = savedCombo;
            card.IsEvolved = savedEv;
            card.IsSuperEvolved = savedSep;
        }
    }

    private static PendingChoice Walk(
        BattleState state, RuntimeEntity card, VmScope scope,
        IReadOnlyList<EffectNode> body,
        IReadOnlyList<IReadOnlyList<int>> answers,
        ref int consumed)
    {
        foreach (var node in body)
        {
            if (IsIrreversible(node)) return null;

            switch (node)
            {
                case ConditionalNode cond:
                {
                    bool take = EvalCond(state, card, cond.Condition);
                    var branch = take ? cond.Then : cond.Else;
                    if (branch == null || branch.Count == 0) continue;
                    var sub = Walk(state, card, scope, branch, answers, ref consumed);
                    if (sub != null) return sub;
                    continue;
                }

                case BranchModeNode bm when bm.Kind == ModeDispatchKind.Choose:
                {
                    int n = bm.Options?.Count ?? 0;
                    if (n == 0) continue;

                    var cands = Enumerable.Range(0, n).ToList();
                    if (consumed < answers.Count)
                    {
                        consumed++;
                        var picked = answers[consumed - 1];
                        foreach (var idx in picked)
                        {
                            if (idx < 0 || idx >= n) continue;
                            var sub = Walk(state, card, scope, bm.Options[idx], answers, ref consumed);
                            if (sub != null) return sub;
                        }
                        continue;
                    }

                    int pick = 1;
                    if (bm.PickCount != null)
                    {
                        try { pick = new VmEval(state).EvalNum(bm.PickCount, ScopeOf(card)); }
                        catch { pick = 1; }
                    }
                    return NewPrompt(card, ChoiceKind.SelectMode, cands, 1,
                                     Math.Min(Math.Max(1, pick), n), "$mode", "选择模式");
                }

                default:
                {
                    // 与运行时完全对齐：所有带 Choose 目标的动作节点都要问。
                    // 只认 SelectTargetNode 会漏掉「对 1 个敌方随从造成 2 点伤害」
                    // 这类 —— 它们编译成单条 DealDamageNode，走 ResolveOrBlock。
                    var spec = ChooseTargetOf(node);
                    if (spec == null || spec.Mode != SelectMode.Choose) continue;
                    if (spec.SourceKind == TargetSourceKind.Slot) continue;

                    var ids = ResolveCands(state, card, spec);
                    if (ids.Count == 0) continue;   // 无合法目标：运行时也直接跳过

                    // SelectTargetNode 的槽位在 OutSlot 上，不在 spec.Slot 上；
                    // 最大可选数量取 spec.Count（钳到实际候选数），不能写死 1。
                    string slotName = node is SelectTargetNode stn && !string.IsNullOrEmpty(stn.OutSlot)
                        ? stn.OutSlot
                        : spec.Slot;
                    int maxPick = 1;
                    if (spec.Count != null)
                    {
                        try { maxPick = new VmEval(state).EvalNum(spec.Count, scope); }
                        catch { maxPick = 1; }
                    }
                    maxPick = Math.Clamp(maxPick, 1, ids.Count);

                    if (consumed < answers.Count)
                    {
                        consumed++;
                        BindAnswer(state, scope, slotName, answers[consumed - 1]);
                        continue;
                    }

                    return NewPrompt(card, ChoiceKind.SelectTarget, ids, 1, maxPick,
                                     slotName, "选择目标");
                }
            }
        }

        return null;
    }

    /// <summary>把玩家选好的实体 id 绑定进探针作用域，供后续条件求值使用。</summary>
    private static void BindAnswer(BattleState state, VmScope scope, string slot,
                                   IReadOnlyList<int> picked)
    {
        if (scope == null || picked == null || picked.Count == 0) return;
        var list = new List<EntityRef>();
        foreach (var id in picked)
        {
            var live = state.GetLive(id, 0);
            if (live == null)
            {
                // EntrySerial 可能不匹配，按 id 扫一遍在场实体
                foreach (var p in new[] { state.Players[0], state.Players[1] })
                {
                    live = p.Board.FirstOrDefault(x => x.EntityId == id)
                        ?? p.Hand.FirstOrDefault(x => x.EntityId == id);
                    if (live != null) break;
                }
            }
            if (live != null) list.Add(EntityRef.From(live));
        }
        if (list.Count > 0) scope.BindRefs(slot ?? TargetSpec.SlotSelected, list);
    }

    private static VmScope ScopeOf(RuntimeEntity card) => new()
    {
        Owner = ZoneScope.Self,
        Controller = card.PlayerId,
        Source = EntityRef.From(card),
    };

    private static PendingChoice NewPrompt(
        RuntimeEntity card, ChoiceKind kind,
        IReadOnlyList<int> cands, int min, int max, string slot, string text)
        => new()
        {
            Player = ZoneScope.Self,
            PlayerId = card.PlayerId,
            Kind = kind,
            Candidates = cands,
            MinSelect = min,
            MaxSelect = max,
            Slot = slot ?? TargetSpec.SlotSelected,
            CanCancel = true,
            Prompt = text,
        };

    /// <summary>该节点是否会让玩家看到不可逆信息。</summary>
    private static bool IsIrreversible(EffectNode node)
    {
        switch (node)
        {
            case LoopNode:
                return true;

            // 随机选模式：玩家看不到具体选了哪些，之后的选择不能反悔
            case BranchModeNode bm:
                return bm.Kind == ModeDispatchKind.Random
                    || bm.Kind == ModeDispatchKind.RandomUnused;

            // 抽牌是「牌库 → 手牌」的移区，不是 MacroNode("draw")
            case MoveZoneNode mv:
                return mv.ToZone == ZoneRef.Hand && mv.Target != null
                       && mv.Target.SourceKind == TargetSourceKind.Zone
                       && mv.Target.Zone == ZoneRef.Deck;

            // 「发现」是带 PoolQuery 的生成，玩家已看到候选
            case SpawnCardNode sp:
                return sp.SourceKind == CardSourceKind.PoolQuery;

            default:
                // 随机目标（"deal 3 damage to a random enemy follower"）
                // 也是不可逆信息：结果一旦定下就不能装作没问过。
                var t = ChooseTargetOf(node);
                if (t != null && t.Mode == SelectMode.Random) return true;
                return false;
        }
    }

    /// <summary>取出该节点作用的目标（与 TurnRunner.GetChooseTarget 保持一致）。</summary>
    private static TargetSpec ChooseTargetOf(EffectNode n) => n switch
    {
        SelectTargetNode st => st.Target,
        DealDamageNode d => d.Target,
        RestoreLifeNode r => r.Target,
        MoveZoneNode m => m.Target,
        ModifyStatsNode s => s.Target,
        ModifyKeywordNode k => k.Target,
        ModifyCostNode c => c.Target,
        TransformNode t => t.Target,
        EvolveNode e => e.Target,
        GrantAbilityNode g => g.Target,
        _ => null,
    };

    private static List<int> ResolveCands(BattleState state, RuntimeEntity card, TargetSpec spec)
    {
        var eval = new VmEval(state);
        var sc = ScopeOf(card);

        var ambush = state.Schema.Keyword("ambush");
        var aura = state.Schema.Keyword("aura");
        var ids = new List<int>();

        foreach (var r in eval.ResolveRef(spec, sc))
        {
            var live = state.GetLive(r.EntityId, r.EntrySerial);
            if (live == null) continue;
            if (live.Zone == ZoneRef.Board)
            {
                if (state.IsProtectedAmulet(live)) continue;
                // 必须走 HasEffectiveKeyword：潜行/灵气可能是光环授予的。
                if (live.PlayerId != card.PlayerId
                    && (state.HasEffectiveKeyword(live, ambush)
                        || state.HasEffectiveKeyword(live, aura)))
                    continue;
            }
            ids.Add(live.EntityId);
        }
        return ids;
    }

    private static bool EvalCond(BattleState state, RuntimeEntity card, BoolExpr cond)
    {
        if (cond == null) return true;
        try { return new VmEval(state).EvalBool(cond, ScopeOf(card)); }
        catch { return true; }
    }
}


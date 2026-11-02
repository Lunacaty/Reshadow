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

        var trigger = kind switch
        {
            PlayerActionKind.Evolve => CoreEvent.OnEvolve,
            PlayerActionKind.SuperEvolve => CoreEvent.OnSuperEvolve,
            PlayerActionKind.EngageAmulet => CoreEvent.OnEngage,
            _ => CoreEvent.OnPlay,
        };

        int consumed = 0;

        foreach (var ab in card.Def.Abilities)
        {
            if (ab?.Trigger == null || ab.Trigger.Event != trigger) continue;
            if (ab.Body == null) continue;

            var prompt = Walk(state, card, ab.Body, answers, ref consumed);
            if (prompt != null) return new Result { Prompt = prompt, Consumed = consumed };
        }

        return new Result { Consumed = consumed };
    }

    private static PendingChoice Walk(
        BattleState state, RuntimeEntity card,
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
                    var sub = Walk(state, card, branch, answers, ref consumed);
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
                            var sub = Walk(state, card, bm.Options[idx], answers, ref consumed);
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

                    if (consumed < answers.Count) { consumed++; continue; }

                    return NewPrompt(card, ChoiceKind.SelectTarget, ids, 1, 1,
                                     spec.Slot, "选择目标");
                }
            }
        }

        return null;
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
    private static bool IsIrreversible(EffectNode node) => node switch
    {
        LoopNode => true,
        BranchModeNode bm => bm.Kind == ModeDispatchKind.Random
                          || bm.Kind == ModeDispatchKind.RandomUnused,
        MacroNode m => m.Macro.IsValid && m.Macro.Name != null
                       && m.Macro.Name.Contains("draw", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

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
                if (live.PlayerId != card.PlayerId
                    && (live.Keywords.Contains(ambush) || live.Keywords.Contains(aura)))
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


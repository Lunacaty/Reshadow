using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 元模式缝合器（Meta-Mode Weaver）
//
// 对照《英文引擎设计3》§5.1 形态 C：
//   "Reduce your faith's value by 10 to give it
//      "Increase the number of Modes you can select by 1.""
//   "Enhance (8): Activate all of them instead."
//
// 这类句子**不是动作**，而是对前一个模式块的元操作 —— 改它的选取数量。
// 之前被当成「启动已选中的护符」，于是整张卡的模式抉择语义丢失。
//
// 本 Pass 的职责：把
//     [ BranchMode(pick = N, options = O) ]  +  [ Activate all of them instead ]
// 缝合为
//     [ Conditional(cond, Then: BranchMode(pick = |O|), Else: BranchMode(pick = N)) ]
// ============================================================================

public static class MetaModeWeaver
{
    /// <summary>该能力是否只是「Activate/Use all of them」的元模式声明。</summary>
    private static bool IsMetaMode(AbilityDef a)
    {
        if (a?.Body == null) return false;

        // "Activate all of them instead" 末尾的 instead 会追加一个占位宏，
        // 先把它滤掉再判形态 —— 否则整句认不出来，元模式永远合不进模式块。
        var real = a.Body
            .Where(n => !(n is MacroNode m && m.Macro.Name == "instead_marker"))
            .ToList();
        if (real.Count != 1) return false;
        if (real[0] is not InvokeAbilityNode inv) return false;
        return inv.Event == Core.CoreEvent.OnEngage
            && inv.Target != null
            && inv.Target.SourceKind == TargetSourceKind.Slot
            && inv.Target.Slot == TargetSpec.SlotSelected;
    }

    public static List<AbilityDef> Weave(List<AbilityDef> abilities, Lexicon lex)
    {
        if (abilities == null || abilities.Count == 0) return abilities;

        // 用「工作副本 + consumed 集合」而不是「边遍历边补 null」：
        // 早先实现在往后找到模式块时，用 null 占位把 result 补齐到 idx，
        // 紧接着又判 `if (prev == null) continue;` —— 自己刚塞的 null 把自己挡回去，
        // 元模式永远合不上（10912310 / 10771310 / 10823310 / 10962310 / 10413310 全部如此）。
        var working = new List<AbilityDef>(abilities);
        var consumed = new HashSet<int>();

        for (int i = 0; i < working.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            var cur = working[i];
            if (cur == null || !IsMetaMode(cur)) continue;

            // 先往前找最近的分支块
            int idx = -1;
            for (int k = i - 1; k >= 0; k--)
            {
                if (consumed.Contains(k)) continue;
                if (working[k]?.Body != null && working[k].Body.Any(n => n is BranchModeNode))
                { idx = k; break; }
            }

            // 往前找不到就往后找：
            //   "Select a Mode to activate. If ..., activate all of them instead. 1. ... 2. ..."
            // instead 句在选项之**前**，此时模式块还没落盘。
            if (idx < 0)
            {
                for (int k = i + 1; k < working.Count; k++)
                {
                    if (consumed.Contains(k)) continue;
                    if (working[k]?.Body != null && working[k].Body.Any(n => n is BranchModeNode))
                    { idx = k; break; }
                }
            }
            if (idx < 0) continue;

            var prev = working[idx];
            var cond = BuildMetaCondition(cur, lex);
            if (cond == null) continue;

            var widened = new List<EffectNode>();
            foreach (var node in prev.Body ?? Array.Empty<EffectNode>())
            {
                if (node is BranchModeNode bm)
                {
                    widened.Add(new BranchModeNode
                    {
                        Kind = bm.Kind,
                        // 「all of them」= 选项数全取，运行时按 opts.Count 钳制
                        PickCount = NumExpr.Const(Math.Max(bm.Options.Count, 1)),
                        Options = bm.Options,
                        GrantTarget = bm.GrantTarget,
                        If = bm.If,
                    });
                }
                else widened.Add(node);
            }

            var mergedCosts = (prev.Costs ?? Array.Empty<CostSpec>())
                .Concat((cur.Costs ?? Array.Empty<CostSpec>()).Where(c => c.Kind == CostKind.UsePp))
                .ToList();

            working[idx] = new AbilityDef
            {
                Trigger = prev.Trigger,
                Costs = mergedCosts,
                Condition = prev.Condition,
                Body = new List<EffectNode>
                {
                    new ConditionalNode
                    {
                        Condition = cond,
                        Then = widened,
                        Else = prev.Body,
                    },
                },
            };
            consumed.Add(i);
        }

        var result = new List<AbilityDef>();
        for (int i = 0; i < working.Count; i++)
            if (!consumed.Contains(i) && working[i] != null) result.Add(working[i]);
        return result;
    }

    private static BoolExpr BuildMetaCondition(AbilityDef cur, Lexicon lex)
    {
        // CompileSegment 已经把句首 If / 「Super Skybound Art」等条件折进了 cur.Condition。
        // 优先用它 —— 只从 Costs 构造的话，这几类条件全部算不出来，
        // 元模式要么不合并，要么合成了却无条件全开。
        if (cur.Condition != null && !(cur.Condition is ConstBoolExpr cb0 && cb0.Value))
            return cur.Condition;

        BoolExpr acc = null;

        foreach (var c in cur.Costs ?? Array.Empty<CostSpec>())
        {
            var counter = c.Kind switch
            {
                CostKind.TurnPlayCount => "combo",
                CostKind.MatchPlayCount => "match_play",
                CostKind.Necromance => "shadow",
                // 爆能：必须判 pp_at_play >= N。
                // 以前落进下面的「有代价就算成立」兜底并返回 BoolExpr.True，
                // 于是 5 费打出《真王之刃》也会把 3 个模式全开。
                CostKind.UsePp when string.Equals(c.Label, "enhance", StringComparison.OrdinalIgnoreCase)
                    => "pp_at_play",
                _ => null,
            };
            if (counter == null) continue;
            var expr = new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter(counter)),
                Op = Core.CompareOp.Ge,
                Right = c.Amount,
            };
            acc = acc == null ? expr : BoolExpr.And(acc, expr);
        }

        var ev = cur.Trigger?.Event ?? Core.CoreEvent.OnPlay;
        if (ev == Core.CoreEvent.OnSuperEvolve)
        {
            var e = new HasStateExpr { State = Core.UnitState.SuperEvolved, Of = TargetSpec.Self };
            acc = acc == null ? e : BoolExpr.And(acc, e);
        }
        else if (ev == Core.CoreEvent.OnEvolve)
        {
            var e = new HasStateExpr { State = Core.UnitState.Evolved, Of = TargetSpec.Self };
            acc = acc == null ? e : BoolExpr.And(acc, e);
        }

        if (acc != null) return acc;

        // Enhance 这类真代价：只要带了代价项就认为「支付后成立」。
        // 运行期由 Battle 层在结算前扣 PP，这里不再重复判定。
        if (cur.Costs != null && cur.Costs.Count > 0) return BoolExpr.True;

        return null;
    }
}

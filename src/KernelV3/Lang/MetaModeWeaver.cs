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
        if (a?.Body == null || a.Body.Count != 1) return false;
        if (a.Body[0] is not InvokeAbilityNode inv) return false;
        return inv.Event == Core.CoreEvent.OnEngage
            && inv.Target != null
            && inv.Target.SourceKind == TargetSourceKind.Slot
            && inv.Target.Slot == TargetSpec.SlotSelected;
    }

    public static List<AbilityDef> Weave(List<AbilityDef> abilities, Lexicon lex)
    {
        if (abilities == null || abilities.Count == 0) return abilities;

        var result = new List<AbilityDef>();
        foreach (var cur in abilities)
        {
            if (!IsMetaMode(cur))
            {
                result.Add(cur);
                continue;
            }

            // 往前找最近一个带 BranchModeNode 的能力
            int idx = -1;
            for (int i = result.Count - 1; i >= 0; i--)
            {
                if (result[i].Body != null && result[i].Body.Any(n => n is BranchModeNode))
                {
                    idx = i;
                    break;
                }
            }

            // 往前找不到时，必须往后找。
            // 卡面可以写成：
            //   "Select a Mode to activate. If ..., activate all of them instead. 1. ... 2. ..."
            // 这里 instead 句出现在选项 1./2. 之**前**，若只往前找，
            // 永远找不到那个還没被落盘的模式块。
            if (idx < 0)
            {
                for (int i = result.Count; i < abilities.Count; i++)
                {
                    var a2 = abilities[i];
                    if (a2?.Body != null && a2.Body.Any(n => n is BranchModeNode)) { idx = i; break; }
                }
                if (idx >= 0)
                {
                    // 后面的模式块还没循环到：先把它们原样放进 result，
                    // 再按下面的通常路径改写。
                    while (result.Count <= idx) result.Add(abilities[result.Count]);
                }
            }

            if (idx < 0)
            {
                // 找不到可改写的模式块：保留原动作，不让效果凭空消失
                result.Add(cur);
                continue;
            }

            var prev = result[idx];
            var cond = BuildMetaCondition(cur, lex);
            if (cond == null)
            {
                result.Add(cur);
                continue;
            }

            var widened = new List<EffectNode>();
            foreach (var node in prev.Body)
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
                else
                {
                    widened.Add(node);
                }
            }

            // 元模式句自己的代价（Enhance (8)）必须保留在合并后的能力上，
            // 否则代价数值整条蒸发，卡面「支付 8 PP」的语义丢失。
            var mergedCosts = (prev.Costs ?? Array.Empty<CostSpec>())
                .Concat(cur.Costs ?? Array.Empty<CostSpec>())
                .ToList();

            result[idx] = new AbilityDef
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
        }

        return result;
    }

    /// <summary>
    /// 元模式句自身的成立条件：
    ///   Enhance (3)      → 有 Enhance 代价，运行期按 PP 判定，这里用代价项的条件化形式
    ///   Combo (3)        → Combo &gt;= 3
    ///   Super-Evolve:    → 已超进化
    /// 找不到可判定的条件时不缝合，留给下游按普通动作处理。
    /// </summary>
    private static BoolExpr BuildMetaCondition(AbilityDef cur, Lexicon lex)
    {
        BoolExpr acc = null;

        foreach (var c in cur.Costs ?? Array.Empty<CostSpec>())
        {
            var counter = c.Kind switch
            {
                CostKind.TurnPlayCount => "combo",
                CostKind.MatchPlayCount => "match_play",
                CostKind.Necromance => "shadow",
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

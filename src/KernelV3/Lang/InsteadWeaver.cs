using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;

namespace ShadowverseFan.KernelV3.Lang;

public static class InsteadWeaver
{
    /// <summary>
    /// 把「instead」语义合成条件分支。
    ///
    /// 卡面写法：
    ///   Deal 2 damage to a random enemy follower. Combo (3) - Deal 4 damage instead.
    /// 期望语义（而不是两句都执行）：
    ///   if (Combo >= 3) Deal 4 damage; else Deal 2 damage;
    ///
    /// 识别方式：后一句的 Body 里出现 macro「instead_marker」占位，
    /// 且该句带有 Condition（Combo/Rally/Necromancy 等 CostSpec 会转成条件）。
    /// </summary>
    public static List<AbilityDef> Weave(List<AbilityDef> abilities, Lexicon lex)
    {
        if (abilities == null || abilities.Count < 2) return abilities;

        var result = new List<AbilityDef>();
        for (int i = 0; i < abilities.Count; i++)
        {
            var cur = abilities[i];

            // 当前这句带 instead 标记 → 与前一句合成
            if (HasInsteadMarker(cur) && result.Count > 0)
            {
                var prev = result[^1];
                result.RemoveAt(result.Count - 1);
                result.Add(Merge(prev, cur, lex));
                continue;
            }

            result.Add(cur);
        }
        return result;
    }

    /// <summary>把条件型代价转成布尔条件。UsePp / DestroyAlly 属于真代价，不转。</summary>
    private static BoolExpr BuildCostCondition(IReadOnlyList<CostSpec> costs, Lexicon lex)
    {
        if (costs == null || costs.Count == 0) return null;
        BoolExpr acc = null;
        foreach (var c in costs)
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
                Left = NumExpr.Ctr(lex.Counter(counter)),   // 必须是 lexicon 注册过的真实 Id
                Op = Core.CompareOp.Ge,
                Right = c.Amount,
            };
            acc = acc == null ? expr : BoolExpr.And(acc, expr);
        }
        return acc;
    }

    private static bool HasInsteadMarker(AbilityDef a)
        => a.Body != null && a.Body.Any(n => n is MacroNode m && m.Macro.Name == "instead_marker");

    /// <summary>
    /// prev 是「否则」分支，cur 是「条件成立」分支。
    /// 两者的动作不能同时执行。
    /// </summary>
    private static AbilityDef Merge(AbilityDef prev, AbilityDef cur, Lexicon lex)
    {
        // 去掉占位宏
        var thenBody = cur.Body
            .Where(n => !(n is MacroNode m && m.Macro.Name == "instead_marker"))
            .ToList();

        // 条件型代价（Combo / Rally / Necromancy）本身就是替代的判定条件。
        // 卡面："Deal 2 damage. Combo (3) - Deal 4 damage instead."
        // 这里把 CostSpec 转成 Condition，否则退化成两句都执行。
        var costCond = BuildCostCondition(cur.Costs, lex);
        var cond = costCond != null
            ? (cur.Condition != null && !ReferenceEquals(cur.Condition, BoolExpr.True)
                ? BoolExpr.And(cur.Condition, costCond)
                : costCond)
            : cur.Condition;

        if (cond == null || ReferenceEquals(cond, BoolExpr.True))
        {
            // 没有触发条件就无法构成替代 —— 退化为顺序执行
            return new AbilityDef
            {
                Trigger = prev.Trigger,
                Condition = prev.Condition,
                Costs = prev.Costs,
                Body = prev.Body.Concat(thenBody).ToList(),
            };
        }

        var branch = new ConditionalNode
        {
            Condition = cond,
            Then = thenBody,
            Else = prev.Body,
        };

        return new AbilityDef
        {
            Trigger = cur.Trigger,
            Condition = cond,
            Costs = prev.Costs,
            Body = new List<EffectNode> { branch },
        };
    }
}

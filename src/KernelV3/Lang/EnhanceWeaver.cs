using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Enhance 展开（Enhance Weaver）
//
// 官方规则：满足 Enhance (N) 的 PP 时，**累加** 生效：
//   · 基础能力（无条件那条）
//   · 所有阈值 ≤ N 的 Enhance 档
//
// 例：Noel IV（基础 Fanfare + Enhance(7) + Enhance(8)）
//   6 费打出  → 只执行基础（1 个随从）
//   7 费打出  → 基础 + 7 档（2 个随从：Bane / Drain）
//   8 费打出  → 基础 + 7 档 + 8 档（3 个随从）
//
// 现状：三条 OnPlay 能力各自带条件，运行时全部尝试触发，
// 而且 CostSpec 根本不参与执行（VM/Battle 层无任何引用）。
// 本 Pass 把它们合成一条带分支的能力，让 AST 自包含。
// ============================================================================

public static class EnhanceWeaver
{
    /// <summary>
    /// 合并同一触发时机下的 Enhance 档与基础能力。
    /// </summary>
    public static List<AbilityDef> Weave(List<AbilityDef> abilities, Lexicon lex)
    {
        if (abilities == null || abilities.Count < 2) return abilities;

        var result = new List<AbilityDef>();
        var consumed = new HashSet<int>();

        for (int i = 0; i < abilities.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            var a = abilities[i];

            // 入口判定：这条能力所在的触发时机下是否存在 Enhance 档。
            // 不能只看自己是不是 Enhance——基础 Fanfare 通常排在第一条，
            // 只看自己会让基础能力先被单独收进结果，导致重复执行。
            var myEv = a.Trigger?.Event ?? CoreEvent.OnPlay;
            bool groupHasEnhance = false;
            for (int k = 0; k < abilities.Count; k++)
            {
                if (consumed.Contains(k)) continue;
                var c = abilities[k];
                if ((c.Trigger?.Event ?? CoreEvent.OnPlay) != myEv) continue;
                if (IsEnhanceTier(c, out _)) { groupHasEnhance = true; break; }
            }
            if (!groupHasEnhance) { result.Add(a); continue; }

            // 同一触发时机下已处理过的就跳过
            bool alreadyGrouped = false;
            for (int k = 0; k < i; k++)
                if (consumed.Contains(k) && (abilities[k].Trigger?.Event ?? CoreEvent.OnPlay) == myEv) { alreadyGrouped = true; break; }
            if (alreadyGrouped) continue;

            // 收集同一触发时机的全部 Enhance 档 + 基础能力
            var tiers = new List<(int amount, AbilityDef ab, int idx)>();
            var baseAb = default(AbilityDef);
            int baseIdx = -1;

            var ev = a.Trigger?.Event ?? CoreEvent.OnPlay;
            for (int j = 0; j < abilities.Count; j++)
            {
                if (consumed.Contains(j)) continue;
                var b = abilities[j];
                var bev = b.Trigger?.Event ?? CoreEvent.OnPlay;
                if (bev != ev) continue;
                if (IsEnhanceTier(b, out var amt)) tiers.Add((amt, b, j));
                else if (baseIdx < 0 && !HasAnyCost(b)) { baseAb = b; baseIdx = j; }
            }

            if (tiers.Count == 0) { result.Add(a); continue; }

            tiers.Sort((x, y) => x.amount.CompareTo(y.amount));

            // 构造嵌套分支：从高到低
            //   if pp >= 8 then (基础 + 7档 + 8档)
            //   else if pp >= 7 then (基础 + 7档)
            //   else 基础
            IReadOnlyList<EffectNode> Chain(int maxIdx)
            {
                var body = new List<EffectNode>();

                // 爆能费已由 TurnRunner.PlayCard 统一扣除（它会取最高可支付档）。
                // 这里不能再补扣，否则同一笔费扣两次。
                if (baseAb != null) body.AddRange(baseAb.Body);
                for (int k = 0; k <= maxIdx; k++) body.AddRange(tiers[k].ab.Body);
                return body;
            }

            var elseBody = baseAb?.Body ?? Array.Empty<EffectNode>();
            for (int k = 0; k < tiers.Count; k++)
            {
                var cond = new CompareExpr
                {
                    // 用「打出前」的 PP，不是当前 PP。
                    // TurnRunner.PlayCard 会先扣掉基础费，若读当前 pp，
                    // 4 费打「2 费 + Enhance (4)」的卡时已剩 2，永远进不了 Enhance 分支。
                    Left = NumExpr.Ctr(lex.Counter("pp_at_play")),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(tiers[k].amount),
                };
                elseBody = new List<EffectNode>
                {
                    new ConditionalNode
                    {
                        Condition = cond,
                        Then = Chain(k),
                        Else = elseBody,
                    },
                };
            }

            result.Add(new AbilityDef
            {
                Trigger = a.Trigger,
                // 分支已含条件，外层不再挂条件（否则 pp<7 时整条能力不生效）
                Condition = BoolExpr.True,
                // 把各个 Enhance 档位写回 Costs。
                // 运行时 TurnRunner 靠它算出本次实付最高档；
                // 清成空的话付费永远停在基础费，爆能变成白孪。
                Costs = tiers
                    .Select(t => new CostSpec
                    {
                        Kind = CostKind.UsePp,
                        Label = "enhance",
                        Amount = NumExpr.Const(t.amount),
                    })
                    .ToArray(),
                Body = new List<EffectNode> { new ConditionalNode { Condition = BoolExpr.True, Then = elseBody } },
            });

            foreach (var t in tiers) consumed.Add(t.idx);
            if (baseIdx >= 0) consumed.Add(baseIdx);
        }

        return result;
    }

    private static bool IsEnhanceTier(AbilityDef a, out int amount)
    {
        amount = 0;
        if (a?.Costs == null) return false;
        foreach (var c in a.Costs)
        {
            if (c.Kind != CostKind.UsePp) continue;
            if (c.Label == null || !c.Label.Equals("enhance", StringComparison.OrdinalIgnoreCase)) continue;
            amount = c.Amount?.Op == NumOp.Const ? c.Amount.Value : 0;
            return true;
        }
        return false;
    }

    private static bool HasAnyCost(AbilityDef a) => a?.Costs != null && a.Costs.Count > 0;
}

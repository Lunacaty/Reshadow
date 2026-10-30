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
        if (abilities == null || abilities.Count == 0) return abilities;

        // 单条能力也可能残留 marker（"Super-Evolve: Restore 4 instead." 没有前句可替代），
        // 此时清掉标记保留动作，避免运行时撞到 ExpandMacro 空实现
        if (abilities.Count == 1 && HasInsteadMarker(abilities[0]))
            return new List<AbilityDef> { StripMarker(abilities[0]) };

        var result = new List<AbilityDef>();
        for (int i = 0; i < abilities.Count; i++)
        {
            var cur = abilities[i];

            // 当前这句带 instead 标记 → 找一个「同触发时机」的前句合成。
            // 卡面："Fanfare: Restore 3. Super-Evolve: Restore 4 instead."
            // 前一句是 Fanfare，代替句是 Super-Evolve —— 它们本就该合成一个条件分支，
            // 所以配对键是 Trigger.Event，不是「紧邻」。
            if (HasInsteadMarker(cur))
            {
                int idx = FindPartner(result, cur);
                if (idx >= 0)
                {
                    var prev = result[idx];
                    result.RemoveAt(idx);
                    result.Insert(idx, Merge(prev, cur, lex));
                    continue;
                }
                // 找不到配对：把占位标记删掉，保留动作本体，至少不会漏效果
                result.Add(StripMarker(cur));
                continue;
            }

            result.Add(cur);
        }
        return result;
    }

    /// <summary>替代句是进化变体时，替代条件是「已进化 / 已超进化」。</summary>
    private static BoolExpr BuildEvoCondition(AbilityDef prev, AbilityDef cur)
    {
        var pev = prev.Trigger?.Event ?? Core.CoreEvent.OnPlay;
        var cev = cur.Trigger?.Event ?? Core.CoreEvent.OnPlay;
        if (pev == cev) return null;
        return cev switch
        {
            Core.CoreEvent.OnSuperEvolve => new HasStateExpr { State = Core.UnitState.SuperEvolved, Of = TargetSpec.Self },
            Core.CoreEvent.OnEvolve => new HasStateExpr { State = Core.UnitState.Evolved, Of = TargetSpec.Self },
            _ => null,
        };
    }

    /// <summary>
    /// 透镜覆写：把前一句的“卡身信息”补到替代句上。
    /// 只补替代句自己没写的部分，不覆盖它已有的值。
    /// </summary>
    private static List<EffectNode> ApplyLens(
        IReadOnlyList<EffectNode> prevBody, List<EffectNode> thenBody)
    {
        if (prevBody == null || prevBody.Count == 0 || thenBody == null) return thenBody;

        // 从前一句里找“可被继承”的动作（召唤 / 加手牌 / 伤害）
        var prevSpawn = prevBody.OfType<SpawnCardNode>().FirstOrDefault();
        var prevMove = prevBody.OfType<MoveZoneNode>().FirstOrDefault();
        var prevDmg = prevBody.OfType<DealDamageNode>().FirstOrDefault();

        var result = new List<EffectNode>();
        foreach (var n in thenBody)
        {
            switch (n)
            {
                // 替代句的召唤通常读不出卡名（写成 CopyOfPrevious），
                // 这里直接用前一句的卡身信息覆写，只保留替代句自己的数量与区域。
                case SpawnCardNode sp when (string.IsNullOrEmpty(sp.CardName) && sp.CardId == 0
                                            || sp.SourceKind == CardSourceKind.CopyOfPrevious)
                                          && prevSpawn != null
                                          && prevSpawn.SourceKind != CardSourceKind.CopyOfPrevious:
                    result.Add(new SpawnCardNode
                    {
                        SourceKind = prevSpawn.SourceKind,
                        CardId = prevSpawn.CardId,
                        CardName = prevSpawn.CardName,
                        CopyFrom = prevSpawn.CopyFrom,
                        ToOwner = sp.ToOwner,
                        ToZone = sp.ToZone,
                        Count = sp.Count,          // 数量用替代句的
                        OutSlot = sp.OutSlot,
                    });
                    break;

                case MoveZoneNode mv when mv.Target == null && prevMove != null:
                    result.Add(new MoveZoneNode
                    {
                        Target = prevMove.Target,
                        ToZone = mv.ToZone,
                        IsDestroy = mv.IsDestroy,
                        OutSlot = mv.OutSlot,
                    });
                    break;

                case DealDamageNode d when prevDmg != null && d.Target == null:
                    result.Add(new DealDamageNode
                    {
                        Target = prevDmg.Target,
                        Amount = d.Amount,
                        IsAbility = d.IsAbility,
                        IsSplit = d.IsSplit,
                    });
                    break;

                default:
                    result.Add(n);
                    break;
            }
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

    /// <summary>
    /// 找代替句的配对前句。
    ///
    /// 两种情况：
    ///   1. 同事件 —— "Deal 2. Combo (3) - Deal 4 instead."
    ///   2. 替代句是进化变体 —— "Fanfare: Restore 3. Super-Evolve: Restore 4 instead."
    ///      此时前句是 OnPlay，替代句是 OnSuperEvolve，配对键是「进化形态」。
    /// </summary>
    private static int FindPartner(List<AbilityDef> list, AbilityDef cur)
    {
        var ev = cur.Trigger?.Event ?? Core.CoreEvent.OnPlay;

        // 进化变体：优先找最近的 OnPlay / OnEvolve 句
        Core.CoreEvent[] fallback = ev switch
        {
            Core.CoreEvent.OnSuperEvolve => new[] { Core.CoreEvent.OnSuperEvolve, Core.CoreEvent.OnEvolve, Core.CoreEvent.OnPlay },
            Core.CoreEvent.OnEvolve => new[] { Core.CoreEvent.OnEvolve, Core.CoreEvent.OnPlay },
            _ => new[] { ev },
        };

        foreach (var target in fallback)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (HasInsteadMarker(list[i])) continue;
                var pev = list[i].Trigger?.Event ?? Core.CoreEvent.OnPlay;
                if (pev == target) return i;
            }
        }
        return -1;
    }

    /// <summary>去掉占位标记，保留动作，避免整句效果丢失。</summary>
    private static AbilityDef StripMarker(AbilityDef a) => new AbilityDef
    {
        Trigger = a.Trigger,
        Condition = a.Condition,
        Costs = a.Costs,
        Body = a.Body.Where(n => !(n is MacroNode m && m.Macro.Name == "instead_marker")).ToList(),
    };

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
        // 进化变体替代："Super-Evolve: Restore 4 instead" 的条件是「已超进化」
        var evoCond = BuildEvoCondition(prev, cur);
        var costCond = BuildCostCondition(cur.Costs, lex);
        if (evoCond != null) costCond = costCond == null ? evoCond : BoolExpr.And(evoCond, costCond);
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

        // 透镜覆写：替代句往往写得很短，缺卡名 / 缺目标。
        //   "Summon a Megalorca. If you're in Overflow, summon 2 instead."
        // 第二句只说了数量，卡名要从前一句透镜继承，
        // 否则结果就是“召唤 2 张空卡”。
        thenBody = ApplyLens(prev.Body, thenBody);

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

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
                    result.InsertRange(idx, Merge(prev, cur, lex));
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

    /// <summary>
    /// 透镜覆写：把前一句的“卡身信息”补到替代句上。
    /// 只补替代句自己没写的部分，不覆盖它已有的值。
    /// </summary>
    private static List<EffectNode> ApplyLens(
        IReadOnlyList<EffectNode> prevBody, List<EffectNode> thenBody)
    {
        if (prevBody == null || prevBody.Count == 0 || thenBody == null) return thenBody;

        // 从前一句里找“可被继承”的动作（召唤 / 加手牌 / 伤害 / 回复）
        var prevSpawn = prevBody.OfType<SpawnCardNode>().FirstOrDefault();
        var prevMove = prevBody.OfType<MoveZoneNode>().FirstOrDefault();
        var prevDmg = prevBody.OfType<DealDamageNode>().FirstOrDefault();
        var prevRestore = prevBody.OfType<RestoreLifeNode>().FirstOrDefault();

        // 进化透镜：'Super-evolve them instead' 必须继承前一句的群体目标。
        //
        //   "Fanfare: Skybound Art - Evolve all unevolved allied followers on the field."
        //   "Super Skybound Art - Super-evolve them instead."
        //
        // 没有这一支，替代句的 EvolveNode.Target 是空的（them 解析不出群体），
        // 超奥义分支就变成「只超进化本卡」。
        var prevEvolve = prevBody.OfType<EvolveNode>().FirstOrDefault();

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

                // 替代句的 EvolveNode 若目标为空 / 省略 / 仍是槽位引用，
                // 就用前一句的目标（通常是 Select all ... 得出的 $selected）。
                case EvolveNode ev when prevEvolve != null
                    && (ev.Target == null
                        || (ev.Target.SourceKind == TargetSourceKind.Slot
                            && ev.Target.Slot == TargetSpec.SlotSelf)
                        || ev.Target.SourceKind == TargetSourceKind.Slot):
                    result.Add(new EvolveNode
                    {
                        Target = prevEvolve.Target,
                        IsSuperEvolve = ev.IsSuperEvolve,
                        If = ev.If,
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

                case DealDamageNode d when prevDmg != null:
                    result.Add(new DealDamageNode
                    {
                        // 透镜只补替代句自己没写的字段，逐字段判断。
                        // "Fanfare: Deal 2 damage to an enemy follower. If you're in Overflow,
                        //  deal 4 damage to all enemy followers instead."
                        // 替代句显式写了 all enemy followers —— Mode / Count / Where 都必须
                        // 保留它自己的值；只有 "deal 4 damage" 这种压根没写目标、
                        // 由 ParseDeal 兜底的 TargetSpec 才整段继承前句。
                        Target = InheritTarget(d.Target, prevDmg.Target),
                        Amount = d.Amount,
                        IsAbility = d.IsAbility,
                        IsSplit = d.IsSplit,
                    });
                    break;

                case RestoreLifeNode r when prevRestore != null:
                    result.Add(new RestoreLifeNode
                    {
                        Target = InheritTarget(r.Target, prevRestore.Target),
                        Amount = r.Amount,
                        FullRestore = r.FullRestore,
                    });
                    break;

                default:
                    result.Add(n);
                    break;
            }
        }

        // 前一句里「修饰替代句产出物」的节点必须一并继承。
        //
        //   "Add a Steelclad Knight to your hand and give it Rush."
        //   "Rally (10) - Add 2 copies instead."
        //
        // 替代句只写了数量，`and give it Rush` 挂在前一句、作用在 $created 上。
        // 替代句把召唤动作整体换掉了，但「给召出来的东西某某」这条修饰仍然有效。
        // 早先只处理 thenBody 里出现的节点，这类「只修饰、不产出」的节点直接丢失，
        // 结果「加 2 张并给突进」变成「加 2 张，没有突进」。
        if (prevSpawn != null && result.OfType<SpawnCardNode>().Any())
        {
            bool thenHasCreated = result.Any(IsCreatedModifier);
            if (!thenHasCreated)
            {
                foreach (var n in prevBody)
                {
                    if (n is SpawnCardNode) continue;
                    if (!IsCreatedModifier(n)) continue;
                    result.Add(n);
                }
            }
        }

        return result;
    }

    /// <summary>该节点是否在修饰「刚刚产出的那个东西」（$created / $self 槽位）。</summary>
    private static bool IsCreatedModifier(EffectNode n)
    {
        TargetSpec t = n switch
        {
            ModifyKeywordNode mk => mk.Target,
            ModifyStatsNode ms => ms.Target,
            ModifyCostNode mc => mc.Target,
            _ => null,
        };
        if (t == null || t.SourceKind != TargetSourceKind.Slot) return false;
        return t.Slot == TargetSpec.SlotCreated || t.Slot == TargetSpec.SlotTrigger;
    }

    /// <summary>
    /// 逐字段透镜：只把替代句「没写」的字段换成前句的值。
    ///
    /// 判据是 TargetSpec.OmittedFields（解析器兜底时打的位集），不是值判等：
    /// "deal 4 damage" 兜底出的 All/Board/Opponent 与显式写出的
    /// "deal 4 damage to all enemy followers" 在 Owner/Zone/Mode/Count 上完全一致，
    /// 值判等一定会把显式目标压成前句的 Choose/1。
    ///
    /// 未写目标（整段兜底）时返回 prev；写了目标时，未置位的字段一律保留 own 的值。
    /// </summary>
    private static TargetSpec InheritTarget(TargetSpec own, TargetSpec prev)
    {
        if (own == null) return prev;
        if (prev == null || !own.IsOmitted) return own;

        var f = own.OmittedFields;
        return new TargetSpec
        {
            SourceKind = f.HasFlag(TargetField.SourceKind) ? prev.SourceKind : own.SourceKind,
            Owner = f.HasFlag(TargetField.Owner) ? prev.Owner : own.Owner,
            Zone = f.HasFlag(TargetField.Zone) ? prev.Zone : own.Zone,
            Slot = f.HasFlag(TargetField.Slot) ? prev.Slot : own.Slot,
            CardId = f.HasFlag(TargetField.Card) ? prev.CardId : own.CardId,
            CardName = f.HasFlag(TargetField.Card) ? prev.CardName : own.CardName,
            Where = f.HasFlag(TargetField.Where) ? prev.Where : own.Where,
            Sort = f.HasFlag(TargetField.Sort) ? prev.Sort : own.Sort,
            SortDescending = f.HasFlag(TargetField.Sort) ? prev.SortDescending : own.SortDescending,
            TopN = f.HasFlag(TargetField.TopN) ? prev.TopN : own.TopN,
            DistinctByName = f.HasFlag(TargetField.DistinctByName) ? prev.DistinctByName : own.DistinctByName,
            // DuplicatesOnly 没有自己的 TargetField 位，与 DistinctByName 同属「去重族」，
            // 随它一起继承：透镜不该把「只要重复份」悄悄变成「全都要」。
            DuplicatesOnly = f.HasFlag(TargetField.DistinctByName) ? prev.DuplicatesOnly : own.DuplicatesOnly,
            Mode = f.HasFlag(TargetField.Mode) ? prev.Mode : own.Mode,
            Count = f.HasFlag(TargetField.Count) ? prev.Count : own.Count,
            History = f.HasFlag(TargetField.History) ? prev.History : own.History,
            IncludeLeader = f.HasFlag(TargetField.IncludeLeader) ? prev.IncludeLeader : own.IncludeLeader,
            // 继承完成，新目标不再有「兜底」成分。
            OmittedFields = TargetField.None,
        };
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

    /// <summary>
    /// 能力体里是否含 instead 占位宏。
    ///
    /// 必须**递归穿透 ConditionalNode**：Enhance/Combo 之类的代价句会被
    /// 包进「if true then if pp_at_play >= N then ...」两层分支，
    /// 只看顶层会让整句认不出是替代句，替代语义静默失效。
    /// </summary>
    private static bool HasInsteadMarker(AbilityDef a)
        => a?.Body != null && ContainsMarker(a.Body);

    private static bool ContainsMarker(IReadOnlyList<EffectNode> nodes)
    {
        if (nodes == null) return false;
        foreach (var n in nodes)
        {
            if (n is MacroNode m && m.Macro.Name == "instead_marker") return true;
            if (n is ConditionalNode c && (ContainsMarker(c.Then) || ContainsMarker(c.Else))) return true;
            if (n is LoopNode lp && ContainsMarker(lp.Body)) return true;
            if (n is BranchModeNode bm && bm.Options != null
                && bm.Options.Any(ContainsMarker)) return true;
        }
        return false;
    }

    /// <summary>
    /// prev 是「否则」分支，cur 是「条件成立」分支。两者的动作不能同时执行。
    ///
    /// 返回 1 条或 2 条能力，取决于两句是不是同一个触发时机：
    ///
    ///   · 同时机（prev.Event == cur.Event）—— 可以合成一条：
    ///       Trigger / Condition 一律沿用 prev 的外层值，cond 只挂在内层 ConditionalNode 上。
    ///       之前的写法把 cur 的 Trigger / 外层 Condition 搬到能力顶层，
    ///       于是「Fanfare: Deal 2. If you're in Overflow, deal 4 instead.」的外层条件
    ///       变成 max_pp >= 7，未觉醒时整条能力在 FlushNextTrigger 就被跳过，
    ///       Else 分支（打 2）成了死代码。
    ///
    ///   · 跨时机（OnPlay / OnEvolve / OnSuperEvolve 之间）—— 不能合成一条：
    ///       两个事件本来就各自独立发生，塞进同一条能力只会让其中一个永远不触发。
    ///       此时拆成两条独立能力：prev 原样保留，cur 经 ApplyLens 补全卡身后独立成条。
    ///       若 prev 是 OnEvolve、cur 是 OnSuperEvolve，手动超进化会同时发两个事件，
    ///       两条都留着就是 1+2=3 次 —— 于是给 prev 那条加「未超进化」的门。
    /// </summary>
    private static List<AbilityDef> Merge(AbilityDef prev, AbilityDef cur, Lexicon lex)
    {
        // 去掉占位宏
        var thenBody = cur.Body
            .Where(n => !(n is MacroNode m && m.Macro.Name == "instead_marker"))
            .ToList();

        // 替代句往往写得很短，缺卡名 / 缺目标。
        //   "Summon a Megalorca. If you're in Overflow, summon 2 instead."
        // 第二句只说了数量，卡名要从前一句透镜继承，
        // 否则结果就是“召唤 2 张空卡”。
        thenBody = ApplyLens(prev.Body, thenBody);

        var pev = prev.Trigger?.Event ?? Core.CoreEvent.OnPlay;
        var cev = cur.Trigger?.Event ?? Core.CoreEvent.OnPlay;
        if (pev != cev) return MergeAcrossTimings(prev, cur, thenBody, pev, cev);

        // 条件型代价（Combo / Rally / Necromancy / Enhance）就是替代的判定条件。
        //
        // 但 CompileSegment 的 AndCosts 已经把 cur.Costs 合进 cur.Condition 了，
        // 再调一次 BuildCostCondition 就会得到
        //   match_play >= 10 and match_play >= 10
        // 这种重复条件。只在 cur.Condition 缺失/恒真时才自己构造。
        bool curCondWeak = cur.Condition == null
            || (cur.Condition is ConstBoolExpr cb0 && cb0.Value);
        var cond = curCondWeak ? BuildCostCondition(cur.Costs, lex) : cur.Condition;

        if (cond == null || ReferenceEquals(cond, BoolExpr.True))
        {
            // 没有触发条件就无法构成替代 —— 退化为顺序执行
            return new List<AbilityDef>
            {
                new()
                {
                    Trigger = prev.Trigger,
                    Condition = prev.Condition,
                    Costs = prev.Costs,
                    Body = prev.Body.Concat(thenBody).ToList(),
                },
            };
        }

        // 「Select 一个目标，再对它造成伤害」这类两段式基础句：
        // 替代句通常写 "Deal 3 damage instead"，靠 it 指回选中的目标。
        // 若把 SelectTargetNode 留在 else 分支里，爆能分支就没有可选目标，
        // 目标退化成了对手全场。把它提到分支外作为公共前置。
        var prefixNodes = new List<EffectNode>();
        IReadOnlyList<EffectNode> elseBody = prev.Body ?? Array.Empty<EffectNode>();
        if (elseBody.Count > 1 && elseBody[0] is SelectTargetNode
            && !thenBody.Any(n => n is SelectTargetNode))
        {
            prefixNodes.Add(elseBody[0]);
            elseBody = elseBody.Skip(1).ToList();
        }

        // 合并后的 CostSpec 必须保留 Enhance 档位：
        // TurnRunner.EnhanceTiers 靠它算爆能档并扣 PP。丢了就永远只扣基础费用。
        var mergedCosts = (prev.Costs ?? Array.Empty<CostSpec>())
            .Concat((cur.Costs ?? Array.Empty<CostSpec>())
                .Where(c => c.Kind == CostKind.UsePp))
            .ToList();

        var branch = new ConditionalNode
        {
            Condition = cond,
            Then = thenBody,
            Else = elseBody,
        };

        prefixNodes.Add(branch);

        // 外层 Trigger / Condition 必须是 prev 的：条件只在内层分支上生效。
        return new List<AbilityDef>
        {
            new()
            {
                Trigger = prev.Trigger,
                Condition = prev.Condition,
                Costs = mergedCosts,
                Body = prefixNodes,
            },
        };
    }

    /// <summary>
    /// 跨时机替代（OnPlay / OnEvolve / OnSuperEvolve 之间）：拆成两条独立能力。
    ///
    ///   "Fanfare: Restore 2 defense to your leader. Super-Evolve: Restore 4 defense instead."
    ///      -> [OnPlay: Restore 2] + [OnSuperEvolve: Restore 4]
    ///   "Evolve: Summon 1 X. Super-Evolve: Summon 2 X instead."
    ///      -> [OnEvolve: Summon 1  if not SuperEvolved] + [OnSuperEvolve: Summon 2]
    ///
    /// 后一种情况必须给 prev 加门：手动超进化会同时发 OnEvolve 与 OnSuperEvolve，
    /// 两条都无条件留着就是 1+2=3 次。普通进化不发 OnSuperEvolve，
    /// 所以这一门只会挡住「超进化顺手再触发一次基础句」，不会误伤普通进化。
    /// </summary>
    private static List<AbilityDef> MergeAcrossTimings(
        AbilityDef prev, AbilityDef cur, List<EffectNode> thenBody,
        Core.CoreEvent pev, Core.CoreEvent cev)
    {
        var result = new List<AbilityDef>();

        // 超进化会连带触发 Evolve 头，基础句必须让位给替代句。
        var notSuperEvolved = BoolExpr.Not(
            new HasStateExpr { State = Core.UnitState.SuperEvolved, Of = TargetSpec.Self });
        bool needsGate = pev == Core.CoreEvent.OnEvolve && cev == Core.CoreEvent.OnSuperEvolve;

        result.Add(new AbilityDef
        {
            Trigger = prev.Trigger,
            Condition = needsGate ? Combine(prev.Condition, notSuperEvolved) : prev.Condition,
            Costs = prev.Costs,
            // 门放两处，缺一不可：
            //   能力级 Condition —— CardVm.FlushNextTrigger 求值它时作用域里没有 Source，
            //                       $self 解析为空集，否定式恒为真，挡不住任何东西；
            //   能力内 ConditionalNode —— DoConditional 拿到的是带 Source 的作用域，
            //                       这一层才真正拦得住「超进化顺手再跑一遍基础句」。
            Body = needsGate ? GateBody(prev.Body, notSuperEvolved) : prev.Body,
        });

        result.Add(new AbilityDef
        {
            Trigger = cur.Trigger,
            Condition = cur.Condition,
            Costs = cur.Costs,
            Body = thenBody,
        });
        return result;
    }

    /// <summary>把额外条件并进一个可能是 true / null 的条件里。</summary>
    private static BoolExpr Combine(BoolExpr existing, BoolExpr extra)
        => existing == null || ReferenceEquals(existing, BoolExpr.True)
            ? extra
            : BoolExpr.And(existing, extra);

    /// <summary>用一道条件门包住整段动作（条件不成立就整段不执行）。</summary>
    private static IReadOnlyList<EffectNode> GateBody(IReadOnlyList<EffectNode> body, BoolExpr gate)
    {
        if (body == null || body.Count == 0) return body;
        return new List<EffectNode>
        {
            new ConditionalNode
            {
                Condition = gate,
                Then = body,
                Else = Array.Empty<EffectNode>(),
            },
        };
    }
}

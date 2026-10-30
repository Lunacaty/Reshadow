using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 条件从句编译器 —— If <BoolExpr> 部分
//
// 只处理真正的布尔条件；事件触发已由 PrefixParser 接管（这是关键修正）。
// 无法建模的条件必须抛异常，绝不静默丢弃。
// ============================================================================

public static class ConditionCompiler
{
    public static BoolExpr Build(IReadOnlyList<Token> toks, Lexicon lex)
    {
        if (toks == null || toks.Count == 0) return BoolExpr.True;

        var words = toks.Select(t => t.Text.ToLowerInvariant()).ToList();
        var joined = string.Join(" ", words);

        if (joined.Contains("overflow"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(7),
            };

        if (joined.Contains("super-evolved"))
            return new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Self };
        if (joined.Contains("unevolved"))
            return BoolExpr.Not(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self });
        if (joined.Contains("evolved"))
            return new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self };

        if (joined.Contains("unlocked") && joined.Contains("super-evolution"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_sep")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };

        if (joined.Contains("no duplicates"))
            return new DeckRuleExpr { Rule = DeckRule.NoDuplicates, Owner = ZoneScope.Self };

        if (joined.Contains("selected"))
            return new IsPreviousExpr { Slot = TargetSpec.SlotSelected };

        int n = FindFirstNumber(toks);

        if (joined.Contains("at least") && n > 0)
        {
            var owner = joined.Contains("enemy") ? ZoneScope.Opponent : ZoneScope.Self;
            return new CompareExpr
            {
                Left = NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = owner,
                    Zone = ZoneRef.Board,
                    Mode = SelectMode.All,
                }),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };
        }

        if (joined.Contains("play point") && n > 0)
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };

        if (joined.Contains("defense is") && n > 0)
        {
            var op = joined.Contains("or less") ? CompareOp.Le : CompareOp.Eq;
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
                Op = op,
                Right = NumExpr.Const(n),
            };
        }

        if (joined.Contains("attack is lower"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Self),
                Op = CompareOp.Lt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
            };

        if (joined.Contains("attack is higher"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Self),
                Op = CompareOp.Gt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
            };

        // you've unlocked super-evolution（被拆成 super - evolution）
        if (joined.Contains("unlocked") && joined.Contains("evol"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_sep")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };

        // you've fused to this card
        if (joined.Contains("fused") || joined.Contains("fuse"))
            return new IsPreviousExpr { Slot = "fuse_material" };

        // you've performed Earth Rite
        if (joined.Contains("earth rite") || joined.Contains("perform earth"))
            return new IsPreviousExpr { Slot = "earth_rite" };

        // an allied follower attacked a leader last turn
        if (joined.Contains("attacked") && joined.Contains("leader"))
            return new IsPreviousExpr { Slot = "leader_attacked" };

        // you've played a card this turn
        if (joined.Contains("played") && joined.Contains("this turn"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("turn_play")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };

        // "this card's cost isn't N"
        if (joined.Contains("cost isn't") || joined.Contains("cost is not"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Self),
                Op = CompareOp.Ne,
                Right = NumExpr.Const(n > 0 ? n : 0),
            };

        // "it's evolved" / "it is evolved"
        if ((joined.StartsWith("it's") || joined.StartsWith("it is")) && joined.Contains("evolved"))
            return new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Trigger };

        // "you've unlocked"
        if (joined.Contains("unlocked"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_sep")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };

        // "you've fused"
        if (joined.Contains("fused"))
            return new IsPreviousExpr { Slot = "fuse_material" };

        // "allied followers have evolved at least N times"
        if (joined.Contains("evolved") && joined.Contains("at least") && n > 0)
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("evolve_count")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };

        // "an allied follower attacked a leader"
        if (joined.Contains("attacked") && joined.Contains("leader"))
            return new HasStateExpr { State = UnitState.AttackedLeaderLastTurn, Of = TargetSpec.Self };

        // "there's / there are ... on the field"
        if (joined.Contains("there's") || joined.Contains("there are") || joined.Contains("there is"))
        {
            var own = joined.Contains("enemy") ? ZoneScope.Opponent : ZoneScope.Self;
            var c = n > 0 ? n : 1;
            return new CompareExpr
            {
                Left = NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = own,
                    Zone = ZoneRef.Board,
                    Mode = SelectMode.All,
                }),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(c),
            };
        }

        // "X is at least N"
        if (joined.StartsWith("x is at least") && n > 0)
            return new CompareExpr
            {
                Left = NumExpr.Var("X"),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };

        // "you have a clue amulet"
        if (joined.Contains("clue") && (joined.Contains("have") || joined.Contains("control")))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("clues")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };

        // "you've engaged a clue"
        if (joined.Contains("engaged"))
            return new IsPreviousExpr { Slot = "engaged" };

        // "you've played cards with base costs of ..."
        if (joined.Contains("played") && joined.Contains("base cost"))
            return new IsPreviousExpr { Slot = "played_all_costs" };

        throw new LangException("条件无法建模: [" + joined + "]");
    }

    private static int FindFirstNumber(IReadOnlyList<Token> toks)
    {
        foreach (var t in toks) if (t.Kind == TokKind.Number) return t.Value;
        return -1;
    }
}

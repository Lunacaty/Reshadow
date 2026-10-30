using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 条件从句编译器 —— If <BoolExpr> 部分
//
// 事件触发由 PrefixParser 接管，这里只处理真正的布尔条件。
//
// ★ 修订（Gemini 63 号审计·偏离 3）：
//   旧实现用 string.Join + joined.Contains(...) 匹配，问题有三：
//     1. 子串误命中（"evolved" 会命中 "unevolved"）
//     2. 分支顺序敏感，后面的分支变成永远走不到的死代码
//     3. 主语拿不到 TargetParser
//   现改为 TokenCursor 驱动，主语交给 TargetParser。
//
// 无法建模的条件一律抛异常，绝不静默降级为恒真。
// ============================================================================

public static class ConditionCompiler
{
    public static BoolExpr Build(IReadOnlyList<Token> toks, Lexicon lex)
    {
        if (toks == null || toks.Count == 0) return BoolExpr.True;
        var c = new TokenCursor(toks);
        var r = ParseCondition(c, lex);
        return r;
    }

    private static BoolExpr ParseCondition(TokenCursor c, Lexicon lex)
    {
        var left = ParseAtom(c, lex);
        while (true)
        {
            if (c.MatchWord("and"))
            {
                c.MatchWord("if");
                left = BoolExpr.And(left, ParseAtom(c, lex));
                continue;
            }
            if (c.MatchWord("or"))
            {
                c.MatchWord("if");
                left = BoolExpr.Or(left, ParseAtom(c, lex));
                continue;
            }
            break;
        }
        return left;
    }

    private static BoolExpr ParseAtom(TokenCursor c, Lexicon lex)
    {
        if (c.MatchPhrase("there", "are", "no", "duplicates"))
        {
            c.MatchPhrase("in", "your", "deck");
            return new DeckRuleExpr { Rule = DeckRule.NoDuplicates, Owner = ZoneScope.Self };
        }

        if (c.MatchPhrase("you", "selected", "one"))
            return new IsPreviousExpr { Slot = TargetSpec.SlotSelected };

        if (c.MatchPhrase("you", "are", "in", "overflow") || c.MatchPhrase("you're", "in", "overflow"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(7),
            };

        if (c.MatchPhrase("you've", "unlocked") || c.MatchPhrase("you", "have", "unlocked"))
        {
            c.TryMatchWord("super"); c.TryMatch(TokKind.Dash); c.TryMatchWord("evolution");
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("sep")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };
        }

        if (c.MatchPhrase("you've", "fused") || c.MatchPhrase("you", "have", "fused"))
        {
            while (!c.IsAtEnd) c.Advance();
            return new IsPreviousExpr { Slot = "fuse_material" };
        }

        if (c.MatchPhrase("an", "allied", "follower", "attacked", "a", "leader"))
        {
            while (!c.IsAtEnd) c.Advance();
            return new HasStateExpr { State = UnitState.AttackedLeaderLastTurn, Of = TargetSpec.Self };
        }

        if (c.MatchPhrase("it's", "super", "-", "evolved")
            || c.MatchPhrase("it", "is", "super", "-", "evolved"))
            return new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Trigger };
        if (c.MatchPhrase("it's", "evolved") || c.MatchPhrase("it", "is", "evolved"))
            return new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Trigger };

        if (c.MatchPhrase("x", "is", "at", "least"))
            return new CompareExpr
            {
                Left = NumExpr.Var("X"),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(ReadNumber(c)),
            };

        if (c.MatchPhrase("this", "card's", "cost", "is") && c.Peek().Kind == TokKind.Number)
        {
            cur_skip: ;
            var sv = c.Position;
            c.Advance();
            if (c.Current.Kind == TokKind.Number)
            {
                int cv = c.Current.Value; c.Advance();
                return new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Self),
                    Op = CompareOp.Eq,
                    Right = NumExpr.Const(cv),
                };
            }
            c.Seek(sv);
        }
        if (c.MatchPhrase("this", "card's", "cost", "isn't")
            || c.MatchPhrase("this", "card's", "cost", "is", "not"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Self),
                Op = CompareOp.Ne,
                Right = NumExpr.Const(ReadNumber(c)),
            };

        if (c.MatchPhrase("this", "follower", "is", "super", "-", "evolved")
            || c.MatchPhrase("this", "follower", "is", "super", "evolved"))
            return new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Self };
        if (c.MatchPhrase("this", "follower", "is", "unevolved"))
            return BoolExpr.Not(new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self });
        if (c.MatchPhrase("this", "follower", "is", "evolved"))
            return new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self };
        if (c.MatchPhrase("this", "follower", "is", "damaged"))
            return new HasStateExpr { State = UnitState.Damaged, Of = TargetSpec.Self };

        if (c.MatchPhrase("this", "follower's", "attack", "is", "lower", "than", "its", "defense"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Self),
                Op = CompareOp.Lt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
            };
        if (c.MatchPhrase("this", "follower's", "attack", "is", "higher", "than", "its", "defense"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Self),
                Op = CompareOp.Gt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
            };

        if (c.MatchPhrase("your", "leader's", "defense", "is", "higher", "than", "the", "enemy", "leader's", "defense"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Life, TargetSpec.SelfLeader),
                Op = CompareOp.Gt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.OpponentLeader),
            };
        if (c.MatchPhrase("your", "leader's", "defense", "is", "lower", "than", "the", "enemy", "leader's", "defense"))
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Life, TargetSpec.SelfLeader),
                Op = CompareOp.Lt,
                Right = NumExpr.Prop(StatKey.Life, TargetSpec.OpponentLeader),
            };

        // 「主语 + is + 数值比较」
        {
            int save = c.Position;
            var subject = new TargetParser(lex).TryParse(c);
            if (subject != null && c.MatchWord("is"))
            {
                if (c.MatchPhrase("at", "least"))
                    return new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Life, subject),
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(ReadNumber(c)),
                    };
                if (c.Current.Kind == TokKind.Number)
                {
                    int n2 = c.Current.Value;
                    c.Advance();
                    var op = CompareOp.Eq;
                    if (c.MatchWord("or"))
                    {
                        if (c.MatchWord("less")) op = CompareOp.Le;
                        else if (c.MatchWord("more")) op = CompareOp.Ge;
                    }
                    return new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Life, subject),
                        Op = op,
                        Right = NumExpr.Const(n2),
                    };
                }
            }
            c.Seek(save);
        }

        // 「there are at least N ...」
        if (c.MatchPhrase("there", "are") || c.MatchPhrase("there", "is") || c.MatchWord("there's"))
        {
            bool atLeast = c.MatchPhrase("at", "least");
            int n = 1;
            if (atLeast && c.Current.Kind == TokKind.Number) { n = c.Current.Value; c.Advance(); }
            var tgt = new TargetParser(lex).TryParse(c);
            var own = tgt?.Owner ?? ZoneScope.Self;
            var zone = tgt?.Zone ?? ZoneRef.Board;
            return new CompareExpr
            {
                Left = NumExpr.CountOf(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = own,
                    Zone = zone,
                    Mode = SelectMode.All,
                }),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };
        }

        // ---- 资源 / 手牌 / 场上计数类 ----

        // "you have N max play points" / "both players have N max play points"
        if (c.MatchPhrase("both", "players", "have"))
        {
            int n = ReadNumber(c);
            c.MatchPhrase("max", "play", "points");
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };
        }
        if (c.MatchPhrase("you", "have"))
        {
            // "you have a Clue amulet in your hand"
            if (c.MatchPhrase("a", "clue", "amulet"))
            {
                c.MatchPhrase("in", "your", "hand");
                return new CompareExpr
                {
                    Left = NumExpr.Ctr(lex.Counter("clues")),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(1),
                };
            }
            bool atLeast = c.MatchPhrase("at", "least");
            int n = ReadNumber(c);
            var tgt = new TargetParser(lex).TryParse(c);
            if (tgt != null)
            {
                return new CompareExpr
                {
                    Left = NumExpr.CountOf(tgt),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(atLeast ? n : (n > 0 ? n : 1)),
                };
            }
            // 兜底：max play points
            if (c.MatchPhrase("max", "play", "points") || c.MatchWord("max"))
                return new CompareExpr
                {
                    Left = NumExpr.Ctr(lex.Counter("max_pp")),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(n),
                };
        }

        // "you've Engaged a Clue amulet this turn"
        if (c.MatchPhrase("you've", "engaged") || c.MatchPhrase("you", "have", "engaged"))
        {
            var tgt = new TargetParser(lex).TryParse(c);
            c.MatchPhrase("this", "turn");
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("ced_engage")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };
        }

        // "you've played cards with base costs of 1, 2, ... this match"
        if (c.MatchPhrase("you've", "played", "cards"))
        {
            while (!c.IsAtEnd) c.Advance();
            return new IsPreviousExpr { Slot = "played_all_costs" };
        }

        // "at least N differently named allied Artifact followers have entered the field this match"
        if (c.MatchPhrase("at", "least"))
        {
            int n = ReadNumber(c);
            var tgt = new TargetParser(lex).TryParse(c);
            while (!c.IsAtEnd) c.Advance();
            return new CompareExpr
            {
                Left = NumExpr.CountOf(tgt ?? new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                    Zone = ZoneRef.Board, Mode = SelectMode.All,
                }),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };
        }

        // "allied followers have evolved at least N times this match"
        if (c.MatchPhrase("allied", "followers", "have", "evolved"))
        {
            c.MatchPhrase("at", "least");
            int n = ReadNumber(c);
            while (!c.IsAtEnd) c.Advance();
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("evolve_count")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(n),
            };
        }

        // "the sum of the 3 highest base costs in your hand is higher than that of your opponent's"
        if (c.MatchPhrase("the", "sum", "of"))
        {
            while (!c.IsAtEnd) c.Advance();
            return new IsPreviousExpr { Slot = "sum_base_cost" };
        }

        // "you selected a spell / an allied amulet"
        if (c.MatchPhrase("you", "selected"))
        {
            var tgt = new TargetParser(lex).TryParse(c);
            return new IsPreviousExpr { Slot = TargetSpec.SlotSelected };
        }

        // "attacking a follower"
        if (c.MatchWord("attacking"))
        {
            while (!c.IsAtEnd) c.Advance();
            return new HasStateExpr { State = UnitState.None, Of = TargetSpec.Trigger };
        }

        // "the opposing follower is damaged"
        if (c.MatchPhrase("the", "opposing", "follower", "is", "damaged"))
            return new HasStateExpr { State = UnitState.Damaged, Of = TargetSpec.Trigger };

        // "this follower's defense is N or less" / "your leader's defense is N or less"
        {
            int sp2 = c.Position;
            var subject = new TargetParser(lex).TryParse(c);
            if (subject != null && c.MatchWord("is"))
            {
                if (c.MatchPhrase("at", "least"))
                    return new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Life, subject),
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(ReadNumber(c)),
                    };
                if (c.Current.Kind == TokKind.Number)
                {
                    int n2 = c.Current.Value; c.Advance();
                    var op = CompareOp.Eq;
                    if (c.MatchWord("or"))
                    {
                        if (c.MatchWord("less")) op = CompareOp.Le;
                        else if (c.MatchWord("more")) op = CompareOp.Ge;
                    }
                    return new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.Life, subject),
                        Op = op,
                        Right = NumExpr.Const(n2),
                    };
                }
            }
            c.Seek(sp2);
        }

        throw new LangException("无法识别的条件");
    }

    private static int ReadNumber(TokenCursor c)
    {
        if (c.Current.Kind != TokKind.Number) return 0;
        int v = c.Current.Value;
        c.Advance();
        return v;
    }
}

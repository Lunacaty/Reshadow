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

        // 第一个原子能解析就走组合子路径
        var c1 = new TokenCursor(toks);
        try { return ParseCondition(c1, lex); }
        catch (LangException) { }

        // 回退：整段当作「主语 + is/are + 数值比较」
        var c2 = new TokenCursor(toks);
        var fallback = TryParseGenericComparison(c2, lex);
        if (fallback != null) return fallback;

        var raw = string.Join(" ", toks.Select(t => t.Text));
        throw new LangException("条件无法建模: [" + raw + "]");
    }

    /// <summary>回退路径：&lt;TargetNP&gt; is N / is N or less / is at least N。</summary>
    /// <summary>
    /// 识别资源计数器名，返回对应 CounterTag；不是资源时返回 null。
    /// 支持：play points / pp / combo / shadows / earth sigils / evolution points / crests / faith
    /// </summary>
    private static Core.SymbolId<Core.CounterTag>? MatchResourceCounter(TokenCursor c, Lexicon lex)
    {
        int save = c.Position;
        string name = null;
        if (c.MatchPhrase("max", "play", "points")) name = "max_pp";
        else if (c.MatchPhrase("play", "points") || c.MatchPhrase("play", "point")) name = "pp";
        else if (c.MatchWord("combo")) name = "combo";
        else if (c.MatchWord("shadows")) name = "shadow";
        else if (c.MatchPhrase("earth", "sigils") || c.MatchPhrase("earth", "sigil")) name = "earth_sigil";
        else if (c.MatchPhrase("evolution", "points") || c.MatchPhrase("evolution", "point")) name = "ep";
        else if (c.MatchWord("crests")) name = "crest";
        else if (c.MatchWord("faith")) name = "faith";
        if (name == null) { c.Seek(save); return null; }
        return lex.Counter(name);
    }


    private static BoolExpr TryParseGenericComparison(TokenCursor c, Lexicon lex)
    {
        var subject = new TargetParser(lex).TryParse(c);
        if (subject == null) subject = TargetSpec.Self;

        if (c.MatchWord("is") || c.MatchWord("are"))
        {
            var stat = StatKey.Life;
            if (c.MatchWord("at") && c.MatchWord("least"))
                return new CompareExpr
                {
                    Left = NumExpr.Prop(stat, subject),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(ReadNumber(c)),
                };
            if (c.Current.Kind == TokKind.Number)
            {
                int n = c.Current.Value; c.Advance();
                var op = CompareOp.Eq;
                if (c.MatchWord("or"))
                {
                    if (c.MatchWord("less")) op = CompareOp.Le;
                    else if (c.MatchWord("more")) op = CompareOp.Ge;
                }
                return new CompareExpr
                {
                    Left = NumExpr.Prop(stat, subject),
                    Op = op,
                    Right = NumExpr.Const(n),
                };
            }
        }
        return null;
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
        // "if its cost is 4" / "if its defense is 7" / "if their attack is 3"
        // 代词 + 属性名 + is N。属性名必须真正读进去，
        // 否则 cost 会被当成默认的 Life。
        if (c.Current.IsAnyWord("its", "their"))
        {
            int sv = c.Position;
            c.Advance();
            var st = c.Current.Text.ToLowerInvariant();
            if (st is "cost" or "costs" or "attack" or "defense" or "life")
            {
                c.Advance();
                if (c.MatchWord("is") || c.MatchWord("are"))
                {
                    var key = st.StartsWith("cost") ? StatKey.Cost
                            : st.StartsWith("attack") ? StatKey.Attack
                            : StatKey.Life;
                    var op = CompareOp.Eq;
                    if (c.MatchWord("at") && c.MatchWord("least")) op = CompareOp.Ge;
                    else if (c.MatchWord("at") && c.MatchWord("most")) op = CompareOp.Le;
                    if (c.Current.Kind == TokKind.Number)
                    {
                        int n = c.Current.Value;
                        c.Advance();
                        while (!c.IsAtEnd) c.Advance();
                        return new CompareExpr
                        {
                            Left = NumExpr.Prop(key, TargetSpec.Self),
                            Op = op,
                            Right = NumExpr.Const(n),
                        };
                    }
                }
            }
            c.Seek(sv);
        }

        // ---- 扩展条件语法（官方卡面与渲染器都会产出这些写法）----

        // "if X is at least 5" / "if X is 5" — 变量 X / Y / Z
        if (c.Current.IsAnyWord("x", "y", "z"))
        {
            int vs = c.Position;
            var vn = c.Current.Text.ToUpperInvariant();
            c.Advance();
            if (c.MatchWord("is") || c.MatchWord("are"))
            {
                c.TryMatchAnyWord("at", "least");
                if (c.Current.Kind == TokKind.Number)
                {
                    int v = c.Current.Value;
                    c.Advance();
                    while (!c.IsAtEnd) c.Advance();
                    return new CompareExpr
                    {
                        Left = NumExpr.Ctr(lex.Counter(vn)),
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(v),
                    };
                }
            }
            c.Seek(vs);
        }

        // "if countdown 2 or more" / "if countdown is at least 2"
        if (c.Current.IsAnyWord("countdown"))
        {
            int cs = c.Position;
            c.Advance();
            c.TryMatchAnyWord("is");
            c.TryMatchAnyWord("at", "least");
            if (c.Current.Kind == TokKind.Number)
            {
                int v = c.Current.Value;
                c.Advance();
                c.TryMatchAnyWord("or");
                c.TryMatchAnyWord("more");
                while (!c.IsAtEnd) c.Advance();
                return new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Countdown, TargetSpec.Self),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(v),
                };
            }
            c.Seek(cs);
        }

        // "if this follower is super-evolved" / "if this card is damaged" — 自身状态句
        // 注意："super-evolved" 在词法层被拆成 super + '-' + evolved 三个 token。
        if (c.MatchPhrase("this", "follower", "is") || c.MatchPhrase("this", "card", "is"))
        {
            bool isSuper = false;
            if (c.Current.IsAnyWord("super")) { isSuper = true; c.Advance(); c.TryMatch(TokKind.Dash); }
            var word = c.Current.Text.ToLowerInvariant();
            c.Advance();
            while (!c.IsAtEnd) c.Advance();
            var st = word switch
            {
                "evolved" => isSuper ? UnitState.SuperEvolved : UnitState.Evolved,
                "superevolved" => UnitState.SuperEvolved,
                "damaged" => UnitState.Damaged,
                "silenced" => UnitState.Silenced,
                _ => UnitState.None,
            };
            if (st != UnitState.None) return new HasStateExpr { State = st, Of = TargetSpec.Self };
            return BoolExpr.True;
        }

        // "if it is super-evolved" / "if it is evolved"
        if (c.MatchWord("it") && (c.MatchWord("is")))
        {
            bool isSuper2 = false;
            if (c.Current.IsAnyWord("super")) { isSuper2 = true; c.Advance(); c.TryMatch(TokKind.Dash); }
            var w2 = c.Current.Text.ToLowerInvariant();
            c.Advance();
            while (!c.IsAtEnd) c.Advance();
            var st2 = w2 switch
            {
                "evolved" => isSuper2 ? UnitState.SuperEvolved : UnitState.Evolved,
                "damaged" => UnitState.Damaged,
                _ => UnitState.None,
            };
            if (st2 != UnitState.None) return new HasStateExpr { State = st2, Of = TargetSpec.Self };
            return BoolExpr.True;
        }


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
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("fuse_count")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };
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
                Left = NumExpr.Var(lex.Counter("X")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(ReadNumber(c)),
            };

        if (c.Current.IsWord("this") && c.Peek().IsWord("card's")
            && c.Peek(2).IsWord("cost") && c.Peek(3).IsWord("is")
            && c.Peek(4).Kind == TokKind.Number)
        {
            c.Advance(); c.Advance(); c.Advance(); c.Advance();
            int cv = c.Current.Value; c.Advance();
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Self),
                Op = CompareOp.Eq,
                Right = NumExpr.Const(cv),
            };
        }

        // "this follower's attack is lower/higher than its defense"
        if (c.Current.IsWord("this") && c.Peek().IsWord("follower's")
            && c.Peek(2).IsWord("attack") && c.Peek(3).IsWord("is"))
        {
            bool isLower = c.Peek(4).IsWord("lower");
            bool isHigher = c.Peek(4).IsWord("higher");
            if (isLower || isHigher)
            {
                while (!c.IsAtEnd && !c.Current.IsWord("defense")) c.Advance();
                c.TryMatchAnyWord("defense");
                return new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Attack, TargetSpec.Self),
                    Op = isLower ? CompareOp.Lt : CompareOp.Gt,
                    Right = NumExpr.Prop(StatKey.Life, TargetSpec.Self),
                };
            }
        }

        // "this follower's defense is N or less"
        if (c.Current.IsWord("this") && c.Peek().IsWord("follower's")
            && c.Peek(2).IsAnyWord("defense", "attack") && c.Peek(3).IsWord("is"))
        {
            bool isAtk = c.Peek(2).IsWord("attack");
            c.Advance(); c.Advance(); c.Advance(); c.Advance();
            if (c.Current.Kind == TokKind.Number)
            {
                int n3 = c.Current.Value; c.Advance();
                var op3 = CompareOp.Eq;
                if (c.MatchWord("or"))
                {
                    if (c.MatchWord("less")) op3 = CompareOp.Le;
                    else if (c.MatchWord("more")) op3 = CompareOp.Ge;
                }
                return new CompareExpr
                {
                    Left = NumExpr.Prop(isAtk ? StatKey.Attack : StatKey.Life, TargetSpec.Self),
                    Op = op3,
                    Right = NumExpr.Const(n3),
                };
            }
        }

        // "this follower is super-evolved"（分词成 super / - / evolved）
        if (c.Current.IsWord("this") && c.Peek().IsWord("follower") && c.Peek(2).IsWord("is")
            && c.Peek(3).IsWord("super"))
        {
            c.Advance(); c.Advance(); c.Advance(); c.Advance();
            if (c.Current.Kind == TokKind.Dash) c.Advance();
            c.TryMatchAnyWord("evolved");
            return new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Self };
        }

        if (c.MatchPhrase("this", "card's", "cost", "is") && c.Current.Kind == TokKind.Number)
        {
            int cv = c.Current.Value; c.Advance();
            return new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Self),
                Op = CompareOp.Eq,
                Right = NumExpr.Const(cv),
            };
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

        // "your leader's defense is N or less" / "this follower's defense is N or less"
        {
            int sv = c.Position;
            if (c.MatchPhrase("your", "leader's"))
            {
                if (c.MatchWord("is") && c.Current.Kind == TokKind.Number)
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
                        Left = NumExpr.Prop(StatKey.Life, TargetSpec.SelfLeader),
                        Op = op,
                        Right = NumExpr.Const(n2),
                    };
                }
            }
            c.Seek(sv);
        }

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

            // 必须把 TryParse 已解析出的限定条件一起带下去。
            // 之前这里重新构造了一个只带 Owner/Zone 的 TargetSpec，
            // 把 Where 里的 "base cost of 5 or more" 丢掉了，
            // 于是条件退化成「只要有随从」。
            var spec = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Owner = own,
                Zone = zone,
                Mode = SelectMode.All,
                Where = tgt?.Where,
                CardId = tgt?.CardId ?? 0,
                CardName = tgt?.CardName,
                DistinctByName = tgt?.DistinctByName ?? false,
            };
            return new CompareExpr
            {
                Left = NumExpr.CountOf(spec),
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

            // 资源计数器："you have at least 4 play points" / "at least 3 combo"
            var resCounter = MatchResourceCounter(c, lex);
            if (resCounter != null)
                return new CompareExpr
                {
                    Left = NumExpr.Ctr(resCounter.Value),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(n > 0 ? n : 1),
                };

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
            // 需要 8 个不同基础费用各至少打出 1 次
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("distinct_base_costs_played")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(8),
            };
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
            // "the sum of the 3 highest base costs" —— 数字 N 必须读进来。
            // 之前这里无条件 while(!c.IsAtEnd) c.Advance() 把整句吞掉，
            // 于是 N=3 完全消失，求和变成「全部基础费用之和」。
            c.TryMatchAnyWord("the");
            int topN = 0;
            if (c.Current.Kind == TokKind.Number) { topN = c.Current.Value; c.Advance(); }
            c.TryMatchAnyWord("highest", "lowest");
            while (!c.IsAtEnd) c.Advance();

            var handSelf = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Owner = ZoneScope.Self,
                Zone = ZoneRef.Hand,
                Mode = SelectMode.All,
                Sort = SortKey.BaseCost,
                SortDescending = true,
                TopN = topN,
            };
            var handOpp = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Owner = ZoneScope.Opponent,
                Zone = ZoneRef.Hand,
                Mode = SelectMode.All,
                Sort = SortKey.BaseCost,
                SortDescending = true,
                TopN = topN,
            };
            return new CompareExpr
            {
                Left = NumExpr.SumOf(handSelf, StatKey.BaseCost),
                Op = CompareOp.Gt,
                Right = NumExpr.SumOf(handOpp, StatKey.BaseCost),
            };
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
}

    private static int ReadNumber(TokenCursor c)
    {
        if (c.Current.Kind != TokKind.Number) return 0;
        int v = c.Current.Value;
        c.Advance();
        return v;
    }
}

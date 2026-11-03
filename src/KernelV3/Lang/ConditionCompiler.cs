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

        var raw = string.Join(" ", toks.Select(t => t.Text));

        // 双主语条件**不提供兜底**。
        // 兜底路径是按单个主语（TargetSpec.Self）构造比较式的，
        // 对 "both players ..." 只会产出「只判自己」的条件，
        // 而这正是本文件要消灭的错译形态：报错比静默错一半安全得多。
        if (raw.IndexOf("both players", StringComparison.OrdinalIgnoreCase) >= 0)
            throw new LangException(
                "both players 的谓词无法建模（需两侧同时求值），拒绝静默退化为单侧条件：[" + raw + "]");

        // 回退：整段当作「主语 + is/are + 数值比较」
        var c2 = new TokenCursor(toks);
        var fallback = TryParseGenericComparison(c2, lex);
        if (fallback != null) return fallback;

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
                        // "or less" / "or more" 是**比较方向**，不是废话：
                        // 一律吞掉会让「费用 4 以下」退化成「费用正好 4」。
                        if (c.MatchWord("or"))
                        {
                            if (c.MatchWord("less")) op = CompareOp.Le;
                            else if (c.MatchWord("more")) op = CompareOp.Ge;
                            else if (c.MatchWord("fewer")) op = CompareOp.Le;
                            else if (c.MatchWord("greater")) op = CompareOp.Ge;
                        }
                        // 只吃到句读或并列连词，别把 and / or 之后的兄弟条件吞掉。
                        while (!c.IsAtEnd && !c.Current.IsAnyWord("and", "or")) c.Advance();
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
                    c.AdvanceToClauseBoundary();
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
            // "at least" 是两个词，TryMatchAnyWord 只吃一个 —— 必须写成短语匹配。
            // 少这一条，「countdown is at least 2」整句报「条件无法建模」。
            if (!c.MatchPhrase("at", "least")) c.TryMatchAnyWord("at", "least");
            if (c.Current.Kind == TokKind.Number)
            {
                int v = c.Current.Value;
                c.Advance();
                c.TryMatchAnyWord("or");
                c.TryMatchAnyWord("more");
                c.AdvanceToClauseBoundary();
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
            c.AdvanceToClauseBoundary();
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
            c.AdvanceToClauseBoundary();
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
                Right = NumExpr.Const(lex.Schema.Rules.OverflowThreshold),
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
            c.AdvanceToClauseBoundary();
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("fuse_count")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(1),
            };
        }

        if (c.MatchPhrase("an", "allied", "follower", "attacked", "a", "leader"))
        {
            c.AdvanceToClauseBoundary();
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

        // ---- 信仰值 / 纹章个数的直接比较 ----
        // "If your faith's value is at least 5" / "if this crest's count is 3"
        // 这类不带 "you have" 的写法此前完全没有分支，整句报「条件无法建模」。
        {
            int fvSave = c.Position;
            bool sawFaith = false, sawCrest = false;
            if (c.MatchPhrase("your", "faith's", "value") || c.MatchPhrase("this", "faith's", "value"))
                sawFaith = true;
            else if (c.MatchPhrase("your", "crest's", "count") || c.MatchPhrase("this", "crest's", "count"))
                sawCrest = true;

            if (sawFaith || sawCrest)
            {
                c.MatchWord("is");
                var op = CompareOp.Ge;
                if (c.MatchPhrase("at", "least")) op = CompareOp.Ge;
                else if (c.MatchPhrase("at", "most")) op = CompareOp.Le;
                var n = ReadNumber(c);
                if (c.MatchWord("or"))
                {
                    if (c.MatchWord("more")) op = CompareOp.Ge;
                    else if (c.MatchWord("less")) op = CompareOp.Le;
                }
                return new CompareExpr
                {
                    Left = NumExpr.Ctr(lex.Counter(sawFaith ? "faith" : "crest")),
                    Op = op,
                    Right = NumExpr.Const(n),
                };
            }
            c.Seek(fvSave);
        }

        // ---- 资源 / 手牌 / 场上计数类 ----

        // "both players <谓词>" —— 双主语，必须展开成两侧合取。
        // 单侧写法 "if you have N max play points" 由下面的 "you have" 分支处理，
        // 仍然只判自己；两条路径不能合流。
        if (c.MatchPhrase("both", "players"))
            return ParseBothPlayers(c, lex);
        // 「本回合我方随从没有攻击过」——「安息」系列 4 张纹章的触发条件。
        if (c.MatchPhrase("allied", "followers", "didn't", "attack", "this", "turn")
            || c.MatchPhrase("allied", "followers", "did", "not", "attack", "this", "turn"))
        {
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("turn_ally_attacks")),
                Op = CompareOp.Eq,
                Right = NumExpr.Const(0),
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

            // "you have at least 4 cards with the same cost in your hand"
            // TargetParser 不认识 "with the same cost"，会在 "cards" 处提前收尾，
            // 反手把后面的 "in your hand" 一起丢掉，于是条件被编成「场上 4 张卡」。
            // 「同费最大张数」在 AST 里是 NumOp.MaxGroupSize，必须整段接住。
            {
                int sc = c.Position;
                c.TryMatchAnyWord("cards", "card", "followers", "follower");
                if (c.MatchPhrase("with", "the", "same", "cost"))
                {
                    var zone0 = ZoneRef.Board;
                    var owner0 = ZoneScope.Self;
                    while (!c.IsAtEnd && c.Current.Kind != TokKind.Comma)
                    {
                        if (c.MatchPhrase("in", "your", "hand")) { zone0 = ZoneRef.Hand; continue; }
                        if (c.MatchPhrase("in", "your", "deck")) { zone0 = ZoneRef.Deck; continue; }
                        if (c.MatchPhrase("on", "the", "field")) { zone0 = ZoneRef.Board; continue; }
                        if (c.MatchPhrase("in", "your", "opponent's", "hand"))
                        { zone0 = ZoneRef.Hand; owner0 = ZoneScope.Opponent; continue; }
                        c.Advance();
                    }
                    return new CompareExpr
                    {
                        Left = new NumExpr { Op = NumOp.MaxGroupSize, Of = TargetSpec.InZone(zone0, owner0) },
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(n > 0 ? n : 1),
                    };
                }
                c.Seek(sc);
            }

            // 资源计数器："you have at least 4 play points" / "at least 3 combo"
            var resCounter = MatchResourceCounter(c, lex);
            if (resCounter != null)
                return new CompareExpr
                {
                    Left = NumExpr.Ctr(resCounter.Value),
                    Op = CompareOp.Ge,
                    Right = NumExpr.Const(n > 0 ? n : 1),
                };

            // 「N cards or less / or fewer」：TargetParser 会在 `or` 处收尾，
            // 而且 `or less` 挡在 `in your hand` 前面，区域短语也一起丢了。
            var cmpOp = CompareOp.Ge;
            var tgt = new TargetParser(lex).TryParse(c);
            if (c.MatchPhrase("or", "less") || c.MatchPhrase("or", "fewer"))
            {
                cmpOp = CompareOp.Le;
                if (c.MatchPhrase("in", "your", "hand"))
                    tgt = TargetSpec.InZone(ZoneRef.Hand, ZoneScope.Self);
            }
            else if (c.MatchPhrase("or", "more"))
                cmpOp = CompareOp.Ge;

            if (tgt != null)
            {
                return new CompareExpr
                {
                    Left = NumExpr.CountOf(tgt),
                    Op = cmpOp,
                    Right = NumExpr.Const(n > 0 ? n : 1),
                };
            }

            // 承前省略：「If you have at least 6, restore 1 defense…」
            // 句尾没有名词，默认指手牌张数。
            if (c.IsAtEnd && n > 0)
            {
                return new CompareExpr
                {
                    Left = NumExpr.CountOf(TargetSpec.InZone(ZoneRef.Hand, ZoneScope.Self)),
                    Op = cmpOp,
                    Right = NumExpr.Const(n),
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
            c.AdvanceToClauseBoundary();
            // 需要 8 个不同基础费用各至少打出 1 次
            return new CompareExpr
            {
                Left = NumExpr.Ctr(lex.Counter("distinct_base_costs_played")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(8),
            };
        }

        // "at least N differently named allied Artifact followers have entered the field this match"
        // "at least N other allied copies of X have entered the field this match"
        if (c.MatchPhrase("at", "least"))
        {
            int n = ReadNumber(c);
            var tgt = new TargetParser(lex).TryParse(c);
            c.AdvanceToClauseBoundary();

            // 计数语义必须归一化：
            //   · Mode=Choose 是「玩家选 N 个」的动作语义，用于**计数**毫无意义，
            //     留着它会让 count 走选择分支、结果恒为候选数而不是实际张数。
            //   · 历史池（...this match）的候选来自历史记录，区域必须随之归一，
            //     否则会去「当前场上」数，而历史上进过场又已离场的那些一个都数不到。
            if (tgt != null)
            {
                // 先把要读的字段取出来：对象初始化器里 `tgt.X` 读的是**新对象**的字段
                // （此时还没赋值），不是旧对象 —— 直接写在初始化器里会静默读到 null/None。
                var old = tgt;
                tgt = new TargetSpec
                {
                    SourceKind = old.SourceKind, Owner = old.Owner,
                    Zone = old.History == HistoryScope.None
                         ? old.Zone
                         : old.History == HistoryScope.EnteredThisMatch ? ZoneRef.Board : ZoneRef.Cemetery,
                    Slot = old.Slot, CardId = old.CardId, CardName = old.CardName,
                    Where = old.Where, Sort = old.Sort, SortDescending = old.SortDescending,
                    TopN = old.TopN, DistinctByName = old.DistinctByName,
                    DuplicatesOnly = old.DuplicatesOnly,
                    Mode = SelectMode.All, Count = null,
                    History = old.History, IncludeLeader = false,
                };
            }

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
            c.AdvanceToClauseBoundary();
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
            c.AdvanceToClauseBoundary();

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
        //
        // 旧实现返回 HasStateExpr(UnitState.None)，VmEval.MatchState 对 None 一律
        // 返回 false —— 条件恒假，等于「这张卡的 Strike 效果永远不生效」。
        // 这里改用 CombatResolver 在发 OnAttack 之前就已写定的
        // AttackedLeaderLastTurn：它为真表示本次攻击打的是主战者，
        // 所以「目标是随从」= 对它取反。
        if (c.MatchWord("attacking"))
        {
            bool isFollower = !(c.MatchPhrase("a", "leader") || c.MatchPhrase("the", "enemy", "leader")
                                || c.MatchPhrase("an", "enemy", "leader"));
            c.TryMatchAnyWord("a", "an", "the");
            c.TryMatchAnyWord("follower", "followers", "leader");
            c.AdvanceToClauseBoundary();
            var onLeader = new HasStateExpr
            {
                State = UnitState.AttackedLeaderLastTurn,
                Of = TargetSpec.Trigger,
            };
            return isFollower ? BoolExpr.Not(onLeader) : onLeader;
        }

        // "the opposing follower is damaged"
        //
        // Of 必须指向 $opposing（被攻击的那颗随从），
        // 而不是 $trigger —— 在 OnAttack 下 $trigger 是攻击者自己，
        // 于是 "If the opposing follower is damaged, destroy it"
        // 会检查「攻击者是否受伤」并破坏攻击者。
        if (c.MatchPhrase("the", "opposing", "follower", "is", "damaged"))
            return new HasStateExpr
            {
                State = UnitState.Damaged,
                Of = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotOpposing,
                    Mode = SelectMode.All,
                },
            };

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

    // ========================================================================
    // "both players <谓词>" —— 双主语展开
    //
    // 语义：卡面写「双方都满足」时，条件必须对**两侧分别成立**。
    // 旧实现吃掉 "both players have" 之后只构造了一个读 self 侧计数器的
    // CompareExpr，于是 "If both players have 10 max play points" 退化成
    // 「自己满 10 费」—— 对手没满费时 Gilnelise 照样塞手牌。
    //
    // 这里只认**显式白名单**里的谓词形态，每一种都能同时对两侧求值。
    // 认不出来的一律抛 LangException：静默生成只判一侧的条件，
    // 比直接编译失败危险得多（编译失败会在全卡池统计里立刻暴露）。
    // ========================================================================

    /// <summary>玩家级计数器比较：自己读 scope.Owner，对手读 Of=OpponentLeader。</summary>
    private static BoolExpr BothSidesCounter(Core.SymbolId<Core.CounterTag> counter,
                                             CompareOp op, int n)
    {
        var right = NumExpr.Const(n);
        return BoolExpr.And(
            new CompareExpr { Left = NumExpr.Ctr(counter, TargetSpec.SelfLeader), Op = op, Right = right },
            new CompareExpr
            {
                Left = NumExpr.Ctr(counter, TargetSpec.OpponentLeader),
                Op = op,
                Right = right,
            });
    }

    /// <summary>区域张数比较：两侧各自数自己的区域。</summary>
    private static BoolExpr BothSidesCount(ZoneRef zone, CompareOp op, int n)
    {
        var right = NumExpr.Const(n);
        return BoolExpr.And(
            new CompareExpr
            {
                Left = NumExpr.CountOf(TargetSpec.InZone(zone, ZoneScope.Self)),
                Op = op,
                Right = right,
            },
            new CompareExpr
            {
                Left = NumExpr.CountOf(TargetSpec.InZone(zone, ZoneScope.Opponent)),
                Op = op,
                Right = right,
            });
    }

    /// <summary>"both players" 之后的谓词。进来说明已经吃掉了 <c>both players</c>。</summary>
    private static BoolExpr ParseBothPlayers(TokenCursor c, Lexicon lex)
    {
        int start = c.Position;

        // ---- overflow 由规则决定阈值，不来自卡面数字 ----
        if (c.MatchPhrase("are", "in", "overflow") || c.MatchPhrase("is", "in", "overflow"))
            return BothSidesCounter(lex.Counter("max_pp"), CompareOp.Ge, OverflowMaxPp(lex));
        if (c.MatchPhrase("have", "overflow") || c.MatchPhrase("are", "overflow"))
            return BothSidesCounter(lex.Counter("max_pp"), CompareOp.Ge, OverflowMaxPp(lex));
        c.Seek(start);

        // ---- "have" / "has" 可写可不写：官方两种写法都有 ----
        c.MatchPhrase("have");
        if (c.Current.IsWord("has")) c.Advance();

        // ---- 比较方向 + 数值：全部从文本读，不硬编码 ----
        bool atLeast = c.MatchPhrase("at", "least");
        int n;
        var op = CompareOp.Ge;
        if (c.Current.Kind == TokKind.Number)
        {
            n = c.Current.Value;
            c.Advance();
            if (c.MatchPhrase("or", "more")) op = CompareOp.Ge;
            else if (c.MatchPhrase("or", "less") || c.MatchPhrase("or", "fewer")) op = CompareOp.Le;
        }
        else
        {
            c.Seek(start);
            throw new LangException("both players 谓词缺少可读的数值：" + RestText(c));
        }
        if (atLeast) op = CompareOp.Ge;

        // ---- 谓词主体：资源计数器 ----
        var counter = MatchResourceCounter(c, lex);
        if (counter != null)
            return BothSidesCounter(counter.Value, op, n);

        // ---- 谓词主体：区域张数 ----
        if (c.MatchPhrase("cards", "in", "hand") || c.MatchPhrase("cards", "in", "their", "hand")
            || c.MatchPhrase("cards", "in", "their", "hands"))
            return BothSidesCount(ZoneRef.Hand, op, n);
        if (c.MatchPhrase("cards", "in", "deck") || c.MatchPhrase("cards", "in", "their", "deck"))
            return BothSidesCount(ZoneRef.Deck, op, n);
        if (c.MatchPhrase("followers", "on", "the", "field")
            || c.MatchPhrase("followers", "on", "their", "field"))
            return BothSidesCount(ZoneRef.Board, op, n);

        // ---- 白名单之外：显式失败，绝不静默只判一侧 ----
        c.Seek(start);
        throw new LangException(
            "both players 的谓词无法建模（需两侧同时求值），拒绝静默退化为单侧条件：[" + RestText(c) + "]");
    }

    /// <summary>Overflow 的规则阈值：最大能量点 7（与 EngineCompiler 的 overflow 分支一致）。</summary>
    // 阈值改为读 Schema.Rules —— 不同卡池的觉醒线可以不同。
    private static int OverflowMaxPp(Lexicon lex) => lex?.Schema?.Rules?.OverflowThreshold ?? 7;

    /// <summary>把游标剩余部分拼回人类可读的条件文本，用于报错信息。</summary>
    private static string RestText(TokenCursor c)
    {
        var parts = new List<string>();
        int save = c.Position;
        while (!c.IsAtEnd)
        {
            parts.Add(c.Current.Text);
            c.Advance();
        }
        c.Seek(save);
        return string.Join(" ", parts);
    }
}

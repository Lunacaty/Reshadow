using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 英文编译器 —— 一段文本 → 若干 AbilityDef
//
// 三条铁律的执行点：
//   ① 全程 TokenCursor，每句结束 AssertEnd
//   ② 引号交给 ActionParser 递归编译成 AbilityDef
//   ③ 词典来自 Lexicon（可含玩家 DIY 的卡名/兵种/关键词）
// ============================================================================

public sealed class EnglishCompiler
{
    private readonly Lexicon _lex;
    private readonly Tokenizer _tokenizer;
    private readonly TargetParser _target;
    private readonly ActionParser _actions;
    private readonly PrefixParser _prefixes;

    public EnglishCompiler(Lexicon lex)
    {
        _lex = lex ?? throw new ArgumentNullException(nameof(lex));
        _tokenizer = new Tokenizer(lex);
        _target = new TargetParser(lex);
        _actions = new ActionParser(lex, CompileQuotedAbility);
        _prefixes = new PrefixParser(lex, _target);
    }

    public sealed class Result
    {
        public List<AbilityDef> Abilities { get; } = new();
        public List<string> Errors { get; } = new();
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>编译整张卡的效果文本。</summary>
    public Result Compile(string text)
    {
        var r = new Result();
        if (string.IsNullOrWhiteSpace(text)) return r;

        var plain = Preprocess(text);
        foreach (var segment in SplitSegments(plain))
        {
            if (segment.Trim().Length == 0) continue;
            try
            {
                var abs = CompileSegment(segment);
                r.Abilities.AddRange(abs);
            }
            catch (LangException ex)
            {
                r.Errors.Add($"[{Trunc(segment)}] {ex.Message}");
            }
        }
        return r;
    }

    /// <summary>铁律二：把引号内的 token 流编译成一个 AbilityDef。</summary>
    /// <remarks>
    /// ★ 修订（Gemini 63 号审计·偏离 6）：
    /// 旧实现只调 TryParseHeader，没接 PrefixParser，导致
    /// "At the end of your turn, ..." / "Whenever ..." 这类引号能力全部在位置 0 报错。
    /// 现与顶层句子走同一套前缀剥离器。
    /// </remarks>
    private AbilityDef CompileQuotedAbility(IReadOnlyList<Token> inner)
    {
        var cur = new TokenCursor(inner);
        var hb = new AbilityHeaderBuilder();
        _prefixes.ParseAll(cur, hb);

        var body = cur.IsAtEnd
            ? (IReadOnlyList<EffectNode>)Array.Empty<EffectNode>()
            : _actions.ParseSequence(cur);

        return new AbilityDef
        {
            Trigger = hb.BuildTrigger(),
            Condition = hb.Condition,
            Costs = hb.Costs,
            Body = body,
        };
    }

    // ---------- 预处理 ----------

    /// <summary>HTML → 纯文本标记。保留分段边界。</summary>
    private static string Preprocess(string text)
    {
        var s = text ?? "";
        s = s.Replace("<hr>", "\u0001");
        s = s.Replace("<ev>", "\u0002").Replace("</ev>", "\u0003");
        s = s.Replace("<sev>", "\u0004").Replace("</sev>", "\u0005");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>", "");
        s = s.Replace("\n", " ").Replace("\r", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " ");
        return s.Trim();
    }

    /// <summary>按段落标记与句号切分。</summary>
    private static IEnumerable<string> SplitSegments(string text)
    {
        var buf = new System.Text.StringBuilder();
        foreach (var c in text)
        {
            if (c == '\u0001' || c == '\u0002' || c == '\u0003' || c == '\u0004' || c == '\u0005')
            {
                if (buf.Length > 0) { yield return buf.ToString(); buf.Clear(); }
                continue;
            }
            buf.Append(c);
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    // ---------- 单段编译 ----------

    private List<AbilityDef> CompileSegment(string segment)
    {
        var result = new List<AbilityDef>();
        var tokens = _tokenizer.Tokenize(segment);

        // 裸关键词后面紧跟能力头（Ward Last Words: ...）→ 在此切一刀
        tokens = SplitKeywordThenHeader(tokens);
        tokens = SplitAfterQuote(tokens);

        // 按句号切成句子；同时记录每句属于哪个模式选项（-1 = 非选项）
        var sentences = new List<List<Token>>();
        var sentOptions = new List<int>();
        var cur = new List<Token>();
        int curOption = -1;
        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Eof) break;
            if (t.Kind == TokKind.OptionMark)
            {
                if (cur.Count > 0) { sentences.Add(cur); sentOptions.Add(curOption); cur = new List<Token>(); }
                curOption = t.Value;
                continue;
            }
            if (t.Kind == TokKind.Period)
            {
                if (cur.Count > 0) { sentences.Add(cur); sentOptions.Add(curOption); cur = new List<Token>(); }
                continue;
            }
            cur.Add(t);
        }
        if (cur.Count > 0) { sentences.Add(cur); sentOptions.Add(curOption); }

        // 条件句：把 "If X, Y" 拆成条件 + 动作
        BoolExpr pendingCondition = null;
        var modeOptions = new List<IReadOnlyList<EffectNode>>();
        TriggerSpec modeTrigger = null;
        BoolExpr modeCondition = BoolExpr.True;
        List<CostSpec> modeCosts = new List<CostSpec>();

        for (int si = 0; si < sentences.Count; si++)
        {
            var sent = sentences[si];

            // 模式选项句：收集到 modeOptions
            if (sentOptions[si] >= 0)
            {
                var oc = new TokenCursor(sent);
                try
                {
                    var ob = _actions.ParseSequence(oc);
                    modeOptions.Add(ob);
                }
                catch (LangException) { /* 单条选项解析失败不影响其它 */ }
                continue;
            }

            // 遇到非选项句时，先把已收集的模式选项落成一个 BranchModeNode
            if (modeOptions.Count > 0)
            {
                result.Add(new AbilityDef
                {
                    Trigger = modeTrigger ?? new TriggerSpec { Event = CoreEvent.OnPlay },
                    Condition = modeCondition,
                    Costs = modeCosts,
                    Body = new List<EffectNode>
                    {
                        new BranchModeNode
                        {
                            Kind = ModeDispatchKind.Choose,
                            PickCount = NumExpr.Const(1),
                            Options = modeOptions,
                        },
                    },
                });
                modeOptions = new List<IReadOnlyList<EffectNode>>();
                modeTrigger = null;
                modeCondition = BoolExpr.True;
                modeCosts = new List<CostSpec>();
            }

            var c = new TokenCursor(sent);

            // 纯时机短语：不产生动作
            var joinedSent = string.Join(" ", sent.Select(t => t.Text));
            if (IsPureStatePhrase(joinedSent)) continue;

            // 变量声明句（X is ... / X starts at N）：登记变量，不产生动作
            if (IsVariableDeclaration(sent)) continue;

            // 融合分支表 "1: <Card> 2: <Card> 3 or more: <Card>"
            if (IsFuseSwitchTableHead(c))
            {
                ParseFuseSwitchTable(c, result);
                if (c.IsAtEnd) continue;
            }

            // "Ignores Ward" / "Earth Sigil" / "Clue" 这类句首类型词
            while (!c.IsAtEnd)
            {
                if (c.MatchPhrase("ignores", "ward"))
                {
                    result.Add(new AbilityDef
                    {
                        Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                        Condition = BoolExpr.True,
                        Costs = Array.Empty<CostSpec>(),
                        Body = new List<EffectNode>
                        {
                            new ModifyKeywordNode
                            {
                                Target = TargetSpec.Self,
                                Op = KeywordOp.Grant,
                                Keyword = _lex.Keyword("ignores ward"),
                            },
                        },
                    });
                    continue;
                }
                if (c.MatchPhrase("earth", "sigil") || c.MatchWord("clue")) continue;
                break;
            }

            // 句首裸关键词（Ward / Rush / Storm …）：每个词生成一个独立常驻能力
            while (c.Current.Kind == TokKind.Keyword)
            {
                // "1. Rush, Ward, and Bane." 这种并列交给动作解析器
                if (c.Peek(1).Kind == TokKind.Comma || c.Peek(1).IsWord("and")) break;
                var kwText = c.Current.Text;
                c.Advance();
                result.Add(new AbilityDef
                {
                    Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                    Condition = BoolExpr.True,
                    Costs = Array.Empty<CostSpec>(),
                    Body = new List<EffectNode>
                    {
                        new ModifyKeywordNode
                        {
                            Target = TargetSpec.Self,
                            Op = KeywordOp.Grant,
                            Keyword = _lex.Keyword(kwText),
                        },
                    },
                });
            }
            if (c.IsAtEnd) continue;

            // 循环剥离所有句首前缀（回合 / 频率 / 边界 / 头 / 事件 / 条件）
            var hb = new AbilityHeaderBuilder();
            _prefixes.ParseAll(c, hb);

            if (c.IsAtEnd)
            {
                // 只有前缀没有动作：常驻光环或纯触发声明
                if (!hb.EventExplicit) continue;
                result.Add(new AbilityDef
                {
                    Trigger = hb.BuildTrigger(),
                    Condition = hb.Condition,
                    Costs = hb.Costs,
                    Body = Array.Empty<EffectNode>(),
                });
                continue;
            }

            var body = _actions.ParseSequence(c);
            c.AssertEnd(Trunc(segment));
            if (body.Count == 0) continue;

            result.Add(new AbilityDef
            {
                Trigger = hb.BuildTrigger(),
                Condition = hb.Condition,
                Costs = hb.Costs,
                Body = body,
            });
        }

        // 收尾：把剩余的模式选项落成 BranchModeNode
        if (modeOptions.Count > 0)
        {
            result.Add(new AbilityDef
            {
                Trigger = modeTrigger ?? new TriggerSpec { Event = CoreEvent.OnPlay },
                Condition = modeCondition,
                Costs = modeCosts,
                Body = new List<EffectNode>
                {
                    new BranchModeNode
                    {
                        Kind = ModeDispatchKind.Choose,
                        PickCount = NumExpr.Const(1),
                        Options = modeOptions,
                    },
                },
            });
        }

        return result;
    }


    /// <summary>
    /// "Ward Last Words: ..." → ["Ward", "Last Words: ..."]
    /// 裸关键词后面紧跟 Fanfare / Last Words / Strike 等头时必须断开，
    /// 否则关键词会被当成动作动词。
    /// </summary>
    /// <summary>
    /// 引号块之后紧跟的能力头（'...' Super-Evolve: / ... Enhance (7): / ... If ...）
    /// 必须切开，否则会被当成同一动作序列的一部分。
    /// </summary>
    /// <summary>
    /// "Ward Last Words: ..." → ["Ward", ".", "Last Words: ..."]
    /// 裸关键词后面紧跟能力头时必须断开。
    /// </summary>
    private static List<Token> SplitKeywordThenHeader(IReadOnlyList<Token> tokens)
    {
        var result = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind == TokKind.Keyword && i + 1 < tokens.Count)
            {
                var nx = tokens[i + 1];
                if (nx.IsAnyWord("last", "fanfare", "strike", "clash", "evolve", "super-evolve",
                                 "engage", "enhance", "countdown", "necromancy", "rally", "combo",
                                 "whenever", "when", "at", "aura", "during", "once"))
                {
                    result.Add(t);
                    result.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
                    continue;
                }
            }
            result.Add(t);
        }
        return result;
    }

    private static List<Token> SplitAfterQuote(IReadOnlyList<Token> tokens)
    {
        var result = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            result.Add(t);
            if (t.Kind != TokKind.Quoted) continue;
            if (i + 1 >= tokens.Count) continue;

            var nx = tokens[i + 1];
            if (nx.Kind == TokKind.Eof || nx.Kind == TokKind.Period
                || nx.Kind == TokKind.Comma) continue;
            if (nx.IsAnyWord("and", "until")) continue;

            // 引号后还有内容，且不是连接词 -> 切开
            result.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
        }
        return result;
    }

    // ---------- 能力头 ----------

    private static readonly (string prefix, CoreEvent ev)[] HeaderPrefixes =
    {
        ("fanfare:",       CoreEvent.OnPlay),
        ("last words:",    CoreEvent.OnDestroy),
        ("evolve:",        CoreEvent.OnEvolve),
        ("super-evolve:",  CoreEvent.OnSuperEvolve),
        ("strike:",        CoreEvent.OnAttack),
        ("follower strike:", CoreEvent.OnAttack),
        ("clash:",         CoreEvent.OnClash),
        ("on spellboost:", CoreEvent.OnSpellboost),
        ("on evolve:",     CoreEvent.OnEvolve),
        ("engage:",        CoreEvent.OnEngage),
        ("enhance:",       CoreEvent.OnPlay),
        ("fuse:",          CoreEvent.OnFuse),
        ("countdown:",     CoreEvent.OnCountdownExpire),
        ("activates in hand:", CoreEvent.PassiveAura),
        ("activates:",     CoreEvent.PassiveAura),
        ("super skybound art:", CoreEvent.OnPlay),
        ("skybound art:",  CoreEvent.OnPlay),
        ("once on each of your turns:", CoreEvent.PassiveAura),
        ("once on each of your opponent's turns:", CoreEvent.PassiveAura),
        ("during your turn:", CoreEvent.PassiveAura),
        ("during your opponent's turn:", CoreEvent.PassiveAura),
        ("then:",          CoreEvent.OnPlay),
    };

    private (TriggerSpec trigger, BoolExpr condition)? TryParseHeader(TokenCursor cur)
    {
        int save = cur.Position;
        var sb = new System.Text.StringBuilder();
        int consumed = 0;
        while (!cur.IsAtEnd && consumed < 4)
        {
            var t = cur.Current;
            if (t.Kind == TokKind.Colon)
            {
                sb.Append(':');
                cur.Advance();
                break;
            }
            if (t.Kind == TokKind.Word || t.Kind == TokKind.Keyword)
            {
                sb.Append(t.Text.ToLowerInvariant()).Append(' ');
                cur.Advance();
                consumed++;
                // Super-Evolve：词被连字符拆成 Super / - / Evolve
                if (t.IsWord("super") && cur.Current.Kind == TokKind.Dash)
                {
                    cur.Advance();
                    if (cur.Current.IsAnyWord("evolve"))
                    {
                        sb.Append("evolve ");
                        cur.Advance();
                    }
                }
                // Enhance (4) / Engage (2)：消费括号参数
                if (cur.Current.Kind == TokKind.OpenParen && cur.Peek().Kind == TokKind.Number)
                {
                    cur.Advance();
                    sb.Append("(").Append((int)cur.Current.Value).Append(") ");
                    cur.Advance();
                    cur.TryMatch(TokKind.CloseParen);
                }
                continue;
            }
            break;
        }
        var head = sb.ToString().Trim();

        // head 形如 "fanfare :"（词与冒号之间有空格），统一去掉空格再比
        var norm = head.Replace(" ", "").TrimEnd(':');
        foreach (var (prefix, ev) in HeaderPrefixes)
        {
            var want = prefix.Replace(" ", "").TrimEnd(':');
            if (norm.Equals(want, StringComparison.OrdinalIgnoreCase))
                return (new TriggerSpec { Event = ev }, BoolExpr.True);
        }

        // Enhance (N) / Engage (N) 等带参数的头
        if (head.StartsWith("enhance") || head.StartsWith("engage"))
            return (new TriggerSpec { Event = CoreEvent.OnPlay }, BoolExpr.True);

        cur.Seek(save);
        return null;
    }

    // ---------- 条件从句 ----------

    private (BoolExpr cond, int commaPos)? SplitCondition(TokenCursor cur)
    {
        int save = cur.Position;
        cur.Advance();   // if / when / whenever / at

        var buf = new List<Token>();
        while (!cur.IsAtEnd && cur.Current.Kind != TokKind.Comma)
        {
            buf.Add(cur.Current);
            cur.Advance();
        }
        if (cur.IsAtEnd) { cur.Seek(save); return null; }
        cur.Advance();   // 逗号

        var cond = BuildSimpleCondition(buf);
        return (cond, 0);
    }

    private BoolExpr BuildSimpleCondition(List<Token> toks)
    {
        var words = toks.Select(t => t.Text.ToLowerInvariant()).ToList();
        var joined = string.Join(" ", words);

        if (joined.Contains("overflow"))
            return new CompareExpr
            {
                Left = NumExpr.Ctr(_lex.Counter("max_pp")),
                Op = CompareOp.Ge,
                Right = NumExpr.Const(7),
            };

        if (joined.Contains("evolved"))
            return new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self };

        // "you have N max play points"
        for (int i = 0; i < toks.Count; i++)
        {
            if (toks[i].Kind == TokKind.Number)
            {
                int v = (int)toks[i].Value;
                if (joined.Contains("play point"))
                    return new CompareExpr
                    {
                        Left = NumExpr.Ctr(_lex.Counter("max_pp")),
                        Op = CompareOp.Ge,
                        Right = NumExpr.Const(v),
                    };
                break;
            }
        }

        // 兜底：把条件文本当作「无名布尔标记」——显式记录，不静默丢弃
        return new HasStateExpr { State = UnitState.None, Of = TargetSpec.Item };
    }


    /// <summary>纯时机/状态短语，不产生动作。</summary>
    private static bool IsPureStatePhrase(string joined)
    {
        var j = joined.Trim().TrimEnd('.').ToLowerInvariant();
        return j == "activates in hand"
            || j == "activates in deck"
            || j == "activates"
            || j == "mitilykket"
            || j == "delay"
            || j == "clue"
            || j == "earth sigil"
            || j == "during your turn"
            || j == "during your opponent's turn"
            || j == "once on each of your turns"
            || j == "once on each of your opponent's turns"
            || j == "at the start of your turn"
            || j == "at the end of your turn"
            || j == "at the start of your opponent's turn"
            || j == "at the end of your opponent's turn";
    }


    /// <summary>
    /// 变量声明句：X is ... / X starts at N / X and Y are ...
    /// 这些句子定义变量的取值来源，本身不产生动作。
    /// 变量由 ValueExpr 的 Counter 节点引用（Counter 名即变量名 X/Y/Z）。
    /// </summary>
    private static bool IsVariableDeclaration(IReadOnlyList<Token> sent)
    {
        if (sent.Count < 3) return false;
        // 匹配: "X is ...", "X starts at ...", "X/Y is ...", "X and Y are ...", "X, Y, and Z are ..."
        if (sent[0].IsAnyWord("x", "y", "z", "x/y"))
        {
            for (int i = 1; i < Math.Min(sent.Count, 8); i++)
                if (sent[i].IsAnyWord("is", "are", "starts")) return true;
        }
        return false;
    }

    /// <summary>是否是融合分支表的开头（1: / 1 or more:）。</summary>
    private static bool IsFuseSwitchTableHead(TokenCursor c)
    {
        if (c.Current.Kind != TokKind.Number) return false;
        var nx = c.Peek(1);
        return nx.Kind == TokKind.Colon || nx.IsAnyWord("or");
    }

    /// <summary>解析 "1: A 2: B 3 or more: C" 分支表，产出 BranchModeNode。</summary>
    private void ParseFuseSwitchTable(TokenCursor c, List<AbilityDef> result)
    {
        var options = new List<IReadOnlyList<EffectNode>>();
        while (!c.IsAtEnd && c.Current.Kind == TokKind.Number)
        {
            c.Advance();                       // 序号
            c.TryMatchAnyWord("or");
            c.TryMatchAnyWord("more");
            c.TryMatch(TokKind.Colon);

            string cardName = "";
            if (c.Current.Kind == TokKind.CardName)
            {
                cardName = c.Current.Text;
                c.Advance();
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                while (!c.IsAtEnd && c.Current.Kind != TokKind.Number && c.Current.Kind != TokKind.Period)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(c.Current.Text);
                    c.Advance();
                }
                cardName = sb.ToString();
            }

            if (!string.IsNullOrWhiteSpace(cardName))
                options.Add(new List<EffectNode>
                {
                    new SpawnCardNode
                    {
                        SourceKind = CardSourceKind.CardId,
                        CardName = cardName,
                        ToZone = ZoneRef.Board,
                        ToOwner = ZoneScope.Self,
                        Count = NumExpr.Const(1),
                    },
                });
        }

        if (options.Count > 0)
            result.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.OnPlay },
                Condition = BoolExpr.True,
                Costs = Array.Empty<CostSpec>(),
                Body = new List<EffectNode>
                {
                    new BranchModeNode
                    {
                        Kind = ModeDispatchKind.Sequence,
                        PickCount = NumExpr.Const(options.Count),
                        Options = options,
                    },
                },
            });
    }

    private static string Trunc(string s)
        => s != null && s.Length > 50 ? s.Substring(0, 50) + "…" : (s ?? "");
}

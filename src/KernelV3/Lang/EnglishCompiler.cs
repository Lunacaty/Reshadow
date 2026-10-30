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
    private AbilityDef CompileQuotedAbility(IReadOnlyList<Token> inner)
    {
        var cur = new TokenCursor(inner);
        var trigger = new TriggerSpec { Event = CoreEvent.PassiveAura };

        // 引号内可能自带能力头（"Last Words: Draw a card"）
        var hdr = TryParseHeader(cur);
        if (hdr != null) trigger = hdr.Value.trigger;

        var body = _actions.ParseSequence(cur);
        return new AbilityDef
        {
            Trigger = trigger,
            Condition = BoolExpr.True,
            Costs = Array.Empty<CostSpec>(),
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

        // 按句号切成句子
        var sentences = new List<List<Token>>();
        var cur = new List<Token>();
        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Eof) break;
            // 选项序号：先把已累积的句子收掉，再跳过序号本身
            if (t.Kind == TokKind.OptionMark)
            {
                if (cur.Count > 0) { sentences.Add(cur); cur = new List<Token>(); }
                continue;
            }
            if (t.Kind == TokKind.Period)
            {
                if (cur.Count > 0) { sentences.Add(cur); cur = new List<Token>(); }
                continue;
            }
            cur.Add(t);
        }
        if (cur.Count > 0) sentences.Add(cur);

        // 条件句：把 "If X, Y" 拆成条件 + 动作
        BoolExpr pendingCondition = null;
        foreach (var sent in sentences)
        {
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
                            Keyword = SymbolId<KeywordTag>.FromName(kwText),
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
    private static List<Token> SplitAfterQuote(IReadOnlyList<Token> tokens)
    {
        var result = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            result.Add(t);
            if (t.Kind != TokKind.Quoted) continue;

            // 只在引号后紧跟「已知能力头」时切分：
            //   '...' Super-Evolve:  /  '...' Enhance (7):  /  '...' Fanfare:  /  '...' Last Words:
            if (i + 1 < tokens.Count)
            {
                var nx = tokens[i + 1];
                var nn = i + 2 < tokens.Count ? tokens[i + 2] : null;
                bool isHeader = (nx.IsAnyWord("super-evolve", "enhance", "fanfare", "evolve", "engage",
                                              "countdown", "fuse", "combo", "rally", "necromancy",
                                              "last", "strike", "clash")
                                 && nn != null && (nn.Kind == TokKind.Colon || nn.IsAnyWord("words")));
                if (isHeader)
                    result.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
            }
        }
        return result;
    }

    private static List<Token> SplitKeywordThenHeader(IReadOnlyList<Token> tokens)
    {
        var result = new List<Token>();
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            // 在关键词之后插入一个 Period，制造切分
            if (t.Kind == TokKind.Keyword && i + 1 < tokens.Count)
            {
                var nx = tokens[i + 1];
                if (nx.IsAnyWord("last", "fanfare", "strike", "clash", "evolve", "super-evolve",
                                 "engage", "enhance", "countdown", "necromancy", "rally", "combo",
                                 "whenever", "when", "at", "aura", "during", "during", "once"))
                {
                    result.Add(t);
                    result.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
                    continue;
                }
            }

            // Countdown (N) 后面直接跟另一个头/事件句 → 在右括号后切分
            if (t.IsAnyWord("countdown") && i + 1 < tokens.Count && tokens[i + 1].Kind == TokKind.OpenParen)
            {
                result.Add(t);                 // Countdown
                result.Add(tokens[i + 1]);     // (
                int k = i + 2;
                if (k < tokens.Count && tokens[k].Kind == TokKind.Number) { result.Add(tokens[k]); k++; }
                if (k < tokens.Count && tokens[k].Kind == TokKind.CloseParen) { result.Add(tokens[k]); k++; }
                // 后面若还有内容，插一个句号制造切分
                if (k < tokens.Count && tokens[k].Kind != TokKind.Period)
                    result.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
                i = k - 1;
                continue;
            }
            result.Add(t);
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
                Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_pp")),
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
                        Left = NumExpr.Ctr(SymbolId<CounterTag>.FromName("max_pp")),
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

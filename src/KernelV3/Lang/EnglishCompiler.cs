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
        _actions = new ActionParser(lex, CompileQuotedAbility, _target);
        _actions.VariableResolver = ResolveVariable;
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
        _variableBindings.Clear();   // 防止上一张卡的变量跨卡污染
        var r = new Result();
        if (string.IsNullOrWhiteSpace(text)) return r;

        var plain = Preprocess(text);
        var buffer = new List<AbilityDef>();
        foreach (var (zone, segment) in MergeOptionSegments(SplitSegments(plain)))
        {
            if (segment.Trim().Length == 0) continue;
            try
            {
                var abs = CompileSegment(segment, zone);
                buffer.AddRange(abs);
            }
            catch (LangException ex)
            {
                r.Errors.Add($"[{Trunc(segment)}] {ex.Message}");
            }
        }

        // 跨段替代语义：instead 可能落在与前提句不同的段落
        // （"Fanfare: Restore 3. Super-Evolve: Restore 4 instead."），
        // CompileSegment 只见得到自己那一段，接不上，必须在这里再合一次。
        r.Abilities.Clear();
        r.Abilities.AddRange(InsteadWeaver.Weave(buffer, _lex));

        // "X starts at N" 的初值落盘：产出一条 PassiveAura，把初始值写进实体计数器。
        // 不这样做的话，初值只能编译期替换成常量，
        // 后面的 "Increase X by 1" 就没有东西可以加。
        foreach (var kv in _initialCounters)
        {
            r.Abilities.Add(new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Condition = BoolExpr.True,
                Costs = Array.Empty<CostSpec>(),
                Body = new List<EffectNode>
                {
                    new ModifyCounterNode
                    {
                        Counter = _lex.Counter(kv.Key),
                        Op = CounterOp.Set,
                        Delta = NumExpr.Const(kv.Value),
                    },
                },
            });
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

        // 引号内可能包含 "X is the number of ..." 这类变量声明句。
        // 不仅要从动作流里剔除，还必须**先登记绑定**。
        // 之前只剔除不登记，导致引号内的 X 永远解析不出真实取值。
        var kept = new List<Token>();
        var sent = new List<Token>();
        foreach (var t in inner)
        {
            if (t.Kind == TokKind.Period)
            {
                if (sent.Count > 0)
                {
                    if (IsVariableDeclaration(sent)) TryBindVariableDeclaration(sent);
                    else kept.AddRange(sent);
                    kept.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = t.Pos });
                }
                sent = new List<Token>();
                continue;
            }
            sent.Add(t);
        }
        if (sent.Count > 0)
        {
            if (IsVariableDeclaration(sent)) TryBindVariableDeclaration(sent);
            else kept.AddRange(sent);
        }

        var cur2 = new TokenCursor(kept);
        var hb2 = new AbilityHeaderBuilder();
        _prefixes.ParseAll(cur2, hb2);

        // 铁律一：引号内的能力必须真正解析出来，不允许吞异常降级
        var body = cur2.IsAtEnd
            ? (IReadOnlyList<EffectNode>)Array.Empty<EffectNode>()
            : _actions.ParseSequence(cur2);

        // 引号内没有显式能力头时，它是**常驻效果**，不是入场曲。
        //   "Give this follower "Can attack 2 times per turn.""
        //   "Give it "Can't attack followers or leaders.""
        // 旧实现用 BuildTrigger() 的默认值 OnPlay，把常驻限制变成
        // 「被授予后再打出时才触发」，在场上永远不生效。
        var trig = hb2.EventExplicit
            ? hb2.BuildTrigger()
            : new TriggerSpec { Event = CoreEvent.PassiveAura };

        return new AbilityDef
        {
            Trigger = trig,
            Condition = hb2.Condition,
            Costs = hb2.Costs,
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

        // 换行符是**能力级**分隔符，与句号的语义完全不同：
        //   "Countdown (2)\nLast Words: Summon a Regal Falcon."  → 两条独立能力
        //   "Engage: Destroy this card. Select an ally and give it Rush." → 同一条能力的两个动作
        // 之前把 \n 抹成空格，两种情形被迫归成一种，
        // 直接造成 Countdown / Fuse / Ward 与后续能力粘连。
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\n[ \t]+", "\n");
        s = s.Replace("\n", "\u0001");

        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " ");
        return s.Trim();
    }

    /// <summary>段落所属的进化作用域。</summary>
    private enum SectionZone { Normal = 0, Evolved, SuperEvolved }

    /// <summary>
    /// 按段落标记与句号切分。
    /// &lt;ev&gt; / &lt;sev&gt; 是**作用域标记**而非普通分隔符：
    /// 段落必须继承 Evolved / SuperEvolved，否则未进化时会白嫖进化后才有的能力。
    /// </summary>
    /// <summary>该段是不是以编号开头的选项（"1. Storm." / "2. Bane."）。</summary>
    private static bool IsNumberedOption(string seg)
    {
        var t = seg.TrimStart();
        if (t.Length < 3) return false;
        int i = 0;
        while (i < t.Length && char.IsDigit(t[i])) i++;
        if (i == 0) return false;
        if (i >= t.Length || t[i] != '.') return false;
        // "1." 后必须有空格或直接结束，避免误判 "1.5"
        return i + 1 >= t.Length || t[i+1] == ' ';
    }

    private static IEnumerable<(SectionZone zone, string text)> SplitSegments(string text)
    {
        var buf = new System.Text.StringBuilder();
        var zone = SectionZone.Normal;
        foreach (var c in text)
        {
            if (c == '\u0001' || c == '\u0002' || c == '\u0003' || c == '\u0004' || c == '\u0005')
            {
                if (buf.Length > 0) { yield return (zone, buf.ToString()); buf.Clear(); }
                if (c == '\u0002') zone = SectionZone.Evolved;
                else if (c == '\u0003') zone = SectionZone.Normal;
                else if (c == '\u0004') zone = SectionZone.SuperEvolved;
                else if (c == '\u0005') zone = SectionZone.Normal;
                continue;
            }
            buf.Append(c);
        }
        if (buf.Length > 0) yield return (zone, buf.ToString());
    }

    /// <summary>
    /// 把紧跟在模式引导句后面的编号选项段并回前一段。
    /// 官方数据里选项写成多行：
    ///   "...3 random abilities from the following.\n1. Storm.\n2. Bane."
    /// 如果不合并，引导句看不到任何选项，模式节点会是空的。
    /// </summary>
    private static IEnumerable<(SectionZone zone, string text)> MergeOptionSegments(
        IEnumerable<(SectionZone zone, string text)> segs)
    {
        var pending = new System.Text.StringBuilder();
        var pendZone = SectionZone.Normal;
        bool has = false;
        foreach (var (zone, txt) in segs)
        {
            if (IsNumberedOption(txt) && has)
            {
                pending.Append(' ').Append(txt.Trim());
                continue;
            }
            if (has) yield return (pendZone, pending.ToString());
            pending.Clear();
            pending.Append(txt);
            pendZone = zone;
            has = true;
        }
        if (has) yield return (pendZone, pending.ToString());
    }

    // ---------- 单段编译 ----------

    private List<AbilityDef> CompileSegment(string segment, SectionZone zone = SectionZone.Normal)
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

        // ★ 选项块终结：选项只延续到「下一个能力头」出现为止。
        // 少了这一步，"... 2. Deal 2 damage. Enhance (3): Activate all of them instead."
        // 会把 Enhance 那一句也并进选项 2，整句随后撞墙报错。
        // 规则：某个选项之后出现 Fanfare / Enhance / Combo ... 这类带冒号或
        // 短横的能力头，即视为选项块结束，其后所有句子回到 -1。
        CloseOptionBlockOnHeader(sentences, sentOptions);

        // 条件句：把 "If X, Y" 拆成条件 + 动作
        BoolExpr pendingCondition = null;
        var modeOptions = new List<IReadOnlyList<EffectNode>>();
        // 与 modeOptions 平行的序号表：同一个编号的连续句子并进同一个选项。
        var modeOptionIds = new List<int>();
        TriggerSpec modeTrigger = null;
        BoolExpr modeCondition = BoolExpr.True;
        List<CostSpec> modeCosts = new List<CostSpec>();
        SectionZone modeZone = zone;
        // 模式块引导句携带的选取数量（"Select 2 Modes to activate"）。
        NumExpr modePick = NumExpr.Const(1);
        // "Give this follower 3 **random** abilities from the following" —— 随机取，不是玩家挑选。
        bool modeIsRandom = false;

        // 变量声明句先整体摘出：它可能出现在选项块内部
        // （"2. Deal X damage ... X is this follower's attack."），
        // 一旦被当成选项的动作解析，就会撞上「解析器无法推进」。
        // 声明本身只登记绑定、不产出动作，与它在哪个块里无关。
        for (int si = 0; si < sentences.Count; si++)
        {
            if (IsVariableDeclaration(sentences[si]))
            {
                TryBindVariableDeclaration(sentences[si]);
                sentOptions[si] = -2;   // -2 = 已消费，不再参与后续分支
            }
        }

        // 同一段内可以有多句：只有第一句带能力头，
        // 后续没头的句子应当**追加到同一条能力**，而不是另起一条 OnPlay。
        //   "Engage: Destroy this card. Select an ally and give it Rush."（两句同属 OnEngage）
        //   "Return your hand to deck. Draw 5 cards. ..."（三句同属 OnPlay）
        AbilityDef currentAbility = null;

        for (int si = 0; si < sentences.Count; si++)
        {
            var sent = sentences[si];

            if (sentOptions[si] == -2) continue;

            // 模式选项句：收集到 modeOptions。
            // 同一个序号下的多句（"1. A. B."）属于同一个选项，
            // 必须并进同一个 action 列表 —— 之前按句各建一个选项，
            // 4 句的 "1. 2. 3. 4." 会被拆成 8 个选项，编号形同虚设。
            if (sentOptions[si] >= 0)
            {
                var oc = new TokenCursor(sent);

                // ★ 选项内部同样要先剥前缀。
                // "3. Earth Rite (3) - Deal 4 damage ..." 的代价属于这个选项本身，
                // 直接 ParseSequence 会把 "Earth Rite (3) -" 整段丢掉，
                // 于是代价数值凭空消失，AST 里只剩一个无条件的伤害。
                var ohb = new AbilityHeaderBuilder();
                _prefixes.ParseAll(oc, ohb);

                var ob = oc.IsAtEnd
                    ? new List<EffectNode>()
                    : _actions.ParseSequence(oc);
                oc.AssertEnd("模式选项");

                // 选项级条件 / 代价必须随选项一起保留：
                // 有代价就包一层 ConditionalNode，让它在结算时真正被判定。
                if (ohb.Costs.Count > 0 || (ohb.Condition != null && ohb.Condition is not ConstBoolExpr))
                {
                    var optCond = AndCosts(ohb.Condition, ohb.Costs);
                    ob = new List<EffectNode>
                    {
                        new ConditionalNode { Condition = optCond, Then = ob },
                    };
                }

                if (modeOptions.Count > 0 && modeOptionIds[^1] == sentOptions[si])
                {
                    ((List<EffectNode>)modeOptions[^1]).AddRange(ob);
                }
                else
                {
                    modeOptions.Add(ob);
                    modeOptionIds.Add(sentOptions[si]);
                }
                continue;
            }

            // 遇到非选项句时，先把已收集的模式选项落成一个 BranchModeNode。
            // 落盘必须发生在解析本句**之前** —— 本句可能正是一个新的模式块引导句
            // （"1. ... 2. ... Evolve: Select a Mode to activate." 里那个 Evolve 头），
            // 先落盘才能保证两段选项不会被并进同一个 BranchModeNode。
            FlushModeBlock(result, ref modeOptions, ref modeOptionIds, ref modeTrigger,
                           ref modeCondition, ref modeCosts, ref modeZone, ref modePick, ref modeIsRandom);

            var c = new TokenCursor(sent);

            // 纯时机短语：不产生动作
            var joinedSent = string.Join(" ", sent.Select(t => t.Text));
            if (IsPureStatePhrase(joinedSent)) continue;

            // 变量声明句：登记变量绑定，不产生动作
            if (IsVariableDeclaration(sent))
            {
                TryBindVariableDeclaration(sent);
                continue;
            }

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

            // 代词作用域（R-4 / Stage 4）：
            // 处于 "When/Whenever <主语> ..." 触发器下时，本句的 it 应指向触发源 $trigger，
            // 而不是空的 $selected。一旦本句执行了 Select，切回 $selected。
            _target.DefaultPronounSlot = hb.SubjectFilter != null
                ? TargetSpec.SlotTrigger
                : null;

                // Countdown (N) 是护符属性：拍下来时为 N，每回合 -1，归零破坏。
                // 与谢幕曲 / Whenever 等触发完全独立，必须独立成一条能力。
                if (hb.CountdownValue.HasValue)
                {
                    result.Add(new AbilityDef
                    {
                        Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                        Condition = BoolExpr.True,
                        Costs = new[] { new CostSpec
                        {
                            Kind = CostKind.Countdown,
                            Amount = NumExpr.Const(hb.CountdownValue.Value),
                            Label = "countdown",
                        } },
                        Body = Array.Empty<EffectNode>(),
                    });
                    hb.CountdownValue = null;
                }
            if (c.IsAtEnd)
            {
                // 只有前缀没有动作。
                // 若是 Countdown (N) 这类"带数值的头部"，数值本身是卡的
                // 重要属性（护符剩余回合），不能因为这一段没有动作就丢掉：
                // 卡面常写成 "Countdown (4)" 单独一段，<hr> 后才是正文。

                if (hb.Costs.Count > 0 && !hb.EventExplicit)
                {
                    result.Add(new AbilityDef
                    {
                        Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                        Condition = AndZone(zone, hb.Condition),
                        Costs = hb.Costs,
                        Body = Array.Empty<EffectNode>(),
                    });
                    continue;
                }
                if (!hb.EventExplicit) continue;

                // "Fuse: Forestcraft cards" 这类材料声明段本身没有动作，
                // 它只是告诉玩家可以融合什么，不是一条能力。
                // 之前把它落成 body 为空的 OnFuse，运行时会多出一条空触发。
                if (hb.Event == CoreEvent.OnFuse && hb.Costs.Count == 0
                    && (hb.Condition == null || hb.Condition is ConstBoolExpr))
                    continue;

                result.Add(new AbilityDef
                {
                    Trigger = hb.BuildTrigger(),
                    Condition = AndZone(zone, AndCosts(hb.Condition, hb.Costs)),
                    Costs = hb.Costs,
                    Body = Array.Empty<EffectNode>(),
                });
                continue;
            }

            // 模式引导句（"Select a Mode to activate." / "Select 2 Modes..."）
            // 本身不产出动作（ParseSelect 把它消费成空 body），但它确定了
            // 后续 "1. 2. 3." 选项块的选取数量，必须先于空 body 判定接住。
            var pick = ExtractModePick(sent, out var pickIsRandom);
            if (pick != null)
            {
                modeTrigger = hb.BuildTrigger();
                modeCondition = AndCosts(hb.Condition, hb.Costs);
                modeCosts = hb.Costs;
                modeZone = zone;
                modePick = pick;
                modeIsRandom = pickIsRandom;
                continue;
            }

            var body = _actions.ParseSequence(c);
            c.AssertEnd(Trunc(segment));
            if (body.Count == 0) continue;

            // 无新头、无新代价、无新条件，且前面已有能力：
            // 这是同一触发下的后续动作句，并入当前能力的 Body。
            bool noNewHeader = !hb.EventExplicit
                                && hb.Costs.Count == 0
                                && (hb.Condition == null || hb.Condition is ConstBoolExpr);
            if (noNewHeader && currentAbility != null && currentAbility.Trigger != null
                && currentAbility.Trigger.Event != CoreEvent.PassiveAura)
            {
                ((List<EffectNode>)currentAbility.Body).AddRange(body);
                continue;
            }

            var built = new AbilityDef
            {
                Trigger = hb.BuildTrigger(),
                Condition = AndZone(zone, AndCosts(hb.Condition, hb.Costs)),
                Costs = hb.Costs,
                Body = body,
            };
            result.Add(built);
            currentAbility = built;
        }

        // 收尾：把剩余的模式选项落成 BranchModeNode
        FlushModeBlock(result, ref modeOptions, ref modeOptionIds, ref modeTrigger,
                       ref modeCondition, ref modeCosts, ref modeZone, ref modePick, ref modeIsRandom);

        // 元模式（Meta-Mode）先行：把 "... 1. ... 2. ... Enhance (3): Activate all of them instead."
        // 合成「条件成立时把前一个模式块的选取数放宽为全部」。
        // 必须在 InsteadWeaver 之前做，否则 "Activate all of them" 会被
        // 误当成「启动已选中的护符」。
        // Enhance 累加展开：必须在 MetaMode 之前，否则模式块会先被合并。
        result = EnhanceWeaver.Weave(result, _lex);

        result = MetaModeWeaver.Weave(result, _lex);

        // 替代语义：把 "... Deal 2 damage. Combo (3) - Deal 4 damage instead."
        // 合成为 ConditionalNode(cond, Then: 后一句, Else: 前一句)。
        // 之前 instead 被当空气吃掉，导致两句都执行（合打 6 点）。
        result = InsteadWeaver.Weave(result, _lex);

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
    /// 能力头词表 —— 出现在选项块之后即标志选项块结束。
    /// 与 PrefixParser 的头部词表保持同源，但这里只用于「断开选项块」。
    /// </summary>
    /// <summary>
    /// 只有这些词才标志「选项块到此结束」。
    ///
    /// 刻意**不含** Combo / Necromancy / Rally / Earth Rite 这类代价前缀：
    /// 它们是选项自身的修饰（"3. Combo (3) - Deal 4 damage"），
    /// 把它当块终止符会把代价连同选项一起摘出去，代价就静默丢失了。
    /// </summary>
    private static readonly string[] HeaderWords =
    {
        "fanfare", "last", "strike", "clash", "evolve", "super-evolve", "super",
        "enhance", "engage", "countdown",
        "whenever", "when", "aura", "during", "once", "on", "at", "if",
        "skybound", "super skybound", "activates", "fuse",
    };

    /// <summary>
    /// 把「能力头之后的句子」从选项块里摘出来。
    ///
    /// 句子拆分阶段只知道「从哪儿开始是新的 1./2./3.」，
    /// 不知道选项块在哪儿结束。结束判据是**能力头的出现**：
    ///   "1. Draw a card. 2. Deal 2 damage. Enhance (3): Activate all of them instead."
    /// 其中 Enhance 一句不属于选项 2。
    /// </summary>
    private static void CloseOptionBlockOnHeader(List<List<Token>> sentences, List<int> sentOptions)
    {
        bool closed = false;
        for (int i = 0; i < sentences.Count; i++)
        {
            if (sentOptions[i] < 0) { closed = false; continue; }
            if (closed) { sentOptions[i] = -1; continue; }

            // 选项内出现能力头 → 本句**自己**就脱离选项块，其后句子也全部脱离。
            // 只置 closed 不摘掉本句，能力头仍会留在选项 2 里撞墙。
            if (StartsWithHeader(sentences[i]))
            {
                sentOptions[i] = -1;
                closed = true;
            }
        }
    }

    /// <summary>该句是否以能力头开头（Fanfare: / Enhance (3): / Combo (3) - …）。</summary>
    private static bool StartsWithHeader(IReadOnlyList<Token> sent)
    {
        if (sent.Count == 0) return false;

        // 能力头必须以冒号或短横收尾才算数，否则 "On the field" 这类短语会误判
        bool hasDelimiter = false;
        for (int i = 0; i < sent.Count && i < 8; i++)
        {
            if (sent[i].Kind == TokKind.Colon || sent[i].Kind == TokKind.Dash) { hasDelimiter = true; break; }
            if (sent[i].Kind == TokKind.Period || sent[i].Kind == TokKind.Comma) break;
        }
        if (!hasDelimiter) return false;

        var head = sent[0].Text.ToLowerInvariant();
        return Array.IndexOf(HeaderWords, head) >= 0;
    }

    /// <summary>
    /// 识别模式块引导句并取出选取数量。
    ///
    /// 卡面写法：
    ///   "Select a Mode to activate."   → 1
    ///   "Select 2 Modes to activate."  → 2
    ///   "Activate 2 random abilities from the following." → 2
    ///   "Activate an ability in sequence from the following." → 1
    ///
    /// 返回 null 表示这不是模式引导句。
    /// </summary>
    private static NumExpr ExtractModePick(IReadOnlyList<Token> sent, out bool isRandom)
    {
        isRandom = false;
        if (sent.Count == 0) return null;

        // 引导句可能带能力头前缀：
        //   "Super-Evolve: Select a Mode to activate."
        // 前缀在 ParseAll 阶段已从游标剥离，但传进来的 sent 仍是**原始整句**，
        // 所以不能只认句首，要在整句里定位 "select"。
        // 两种引导句：
        //   "Select a Mode to activate." / "Select 2 Modes to activate."
        //   "Give this follower 3 random abilities from the following."（无 select/activate）
        int i = -1;
        for (int k = 0; k < sent.Count; k++)
        {
            if (sent[k].IsAnyWord("select", "activate", "give")) { i = k; break; }
        }
        if (i < 0) return null;
        i++;

        // "give this follower" / "give it" 跳过受予者，直到数字
        if (sent[i - 1].IsWord("give"))
        {
            while (i < sent.Count && sent[i].Kind != TokKind.Number
                   && !sent[i].IsAnyWord("a", "an")) i++;
        }

        int n = 1;
        bool sawDigit = false;
        if (i < sent.Count && sent[i].Kind == TokKind.Number)
        {
            n = (int)sent[i].Value;
            sawDigit = true;
            i++;
        }

        bool isMode = false;
        for (int k = i; k < sent.Count && k < i + 4; k++)
        {
            if (sent[k].IsAnyWord("mode", "modes", "abilities", "ability")) { isMode = true; break; }
        }
        if (!isMode) return null;

        // 必须真的是「引导后续选项块」的形状，否则只是普通 Select
        bool leadsBlock = false;
        for (int k = i; k < sent.Count; k++)
        {
            if (sent[k].IsAnyWord("following", "activate", "activated")) { leadsBlock = true; break; }
        }
        if (!leadsBlock) return null;

        // "3 **random** abilities from the following" —— 随机取，不是玩家挑选
        for (int k = 0; k < sent.Count; k++)
            if (sent[k].IsWord("random")) { isRandom = true; break; }

        return NumExpr.Const(sawDigit ? n : 1);
    }

    /// <summary>
    /// 把已收集的模式选项落成一个 BranchModeNode。
    /// 选项为空时什么也不做，且不改变任何状态。
    ///
    /// ★ 修复（对照设计 3 §5.1.2）：三件事必须一起做，否则嵌套模式会串味——
    ///   1. zone 随块继承：进化段的选项必须带 Evolved 条件
    ///   2. PickCount 来自引导句，不是写死 1
    ///   3. 落盘后把块状态彻底复位，避免上一块的触发头污染下一块
    /// </summary>
    private void FlushModeBlock(
        List<AbilityDef> result,
        ref List<IReadOnlyList<EffectNode>> options,
        ref List<int> optionIds,
        ref TriggerSpec trigger,
        ref BoolExpr condition,
        ref List<CostSpec> costs,
        ref SectionZone zone,
        ref NumExpr pick,
        ref bool isRandom)
    {
        if (options.Count == 0) return;

        result.Add(new AbilityDef
        {
            Trigger = trigger ?? new TriggerSpec { Event = CoreEvent.OnPlay },
            Condition = AndZone(zone, condition),
            Costs = costs,
            Body = new List<EffectNode>
            {
                new BranchModeNode
                {
                    Kind = isRandom ? ModeDispatchKind.Random : ModeDispatchKind.Choose,
                    PickCount = pick,
                    Options = options,
                },
            },
        });

        isRandom = false;
        options = new List<IReadOnlyList<EffectNode>>();
        optionIds = new List<int>();
        trigger = null;
        condition = BoolExpr.True;
        costs = new List<CostSpec>();
        zone = SectionZone.Normal;
        pick = NumExpr.Const(1);
    }

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
                bool isHeader = nx.IsAnyWord("last", "fanfare", "strike", "clash", "evolve",
                                            "super-evolve", "engage", "enhance", "countdown",
                                            "necromancy", "rally", "combo", "whenever", "when",
                                            "aura", "during", "once");

                // "at" 只在跟 the/a 时才可能是句首（"At the end of your turn"），
                // 否则 "Ward at random" 会被误切。
                if (!isHeader && nx.IsWord("at") && i + 2 < tokens.Count
                    && tokens[i + 2].IsAnyWord("the", "a"))
                    isHeader = true;

                if (isHeader)
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
            // OptionMark 本身就是切分边界，不必再插 Period
            if (nx.Kind == TokKind.Eof || nx.Kind == TokKind.Period
                || nx.Kind == TokKind.Comma || nx.Kind == TokKind.OptionMark) continue;
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

    /// <summary>本次编译期间登记的变量绑定：变量名 -> 取值表达式。</summary>
    private readonly Dictionary<string, NumExpr> _variableBindings = new();

    /// <summary>"X starts at N" 的初值，装配到实体初始计数器。</summary>
    private readonly Dictionary<string, int> _initialCounters = new();

    /// <summary>
    /// 解析 "X is the number of ..." / "X, Y, and Z ... add up to your faith's value"。
    /// 后者建立 X/Y/Z -> SplitOf(faith, 0/1/2) 的绑定。
    /// </summary>
    private void TryBindVariableDeclaration(IReadOnlyList<Token> sent)
    {
        var texts = sent.Select(t => t.Text.ToLowerInvariant()).ToList();
        var joined = string.Join(" ", texts);

        // "X, Y, and Z are determined randomly and add up to your faith's value"
        if (joined.Contains("add up to") && joined.Contains("faith"))
        {
            var ctr = _lex.Counter("faith");
            _variableBindings["X"] = NumExpr.SplitOf(ctr, 0);
            _variableBindings["Y"] = NumExpr.SplitOf(ctr, 1);
            _variableBindings["Z"] = NumExpr.SplitOf(ctr, 2);
            return;
        }

        // "X starts at 0"
        if (joined.Contains("starts at") && sent.Count >= 3 && sent[0].IsAnyWord("x", "y", "z"))
        {
            // 注意：分词后它是 X / starts / at / 2，数字在第 3 位而不是第 2 位。
            int numIdx = -1;
            for (int k = 1; k < sent.Count; k++)
                if (sent[k].Kind == TokKind.Number) { numIdx = k; break; }
            if (numIdx > 0)
            {
                // "X starts at N" 不是编译期常量：后面往往跟着
                // "On Spellboost: Increase X by 1" 这类动态修改。
                // 绑成 Const(N) 会让增量全部丢失。
                var vn = sent[0].Text.ToUpperInvariant();
                _variableBindings[vn] = NumExpr.Ctr(_lex.Counter(vn));
                _initialCounters[vn] = sent[numIdx].Value;
            }
            return;
            return;
        }

        // "X/Y is ..." / "X and Y are ..."：多个变量共享同一个描述。
        // 分词后它们是 X / Y is ...，所以要先把变量名收齐。
        if (sent.Count >= 3 && sent[0].IsAnyWord("x", "y", "z"))
        {
            var names = new List<string>();
            int k = 0;
            while (k < sent.Count)
            {
                if (sent[k].IsAnyWord("x", "y", "z"))
                {
                    names.Add(sent[k].Text.ToUpperInvariant());
                    k++;
                    // 跳过分隔：/ 或 and 或 ,
                    while (k < sent.Count && (sent[k].IsWord("/") || sent[k].IsWord("and"))) k++;
                }
                else break;
            }
            if (names.Count > 0)
            {
                // 找 is / are
                int verb = k;
                while (verb < sent.Count && !sent[verb].IsAnyWord("is", "are")) verb++;
                var rest = verb + 1 < sent.Count ? sent.Skip(verb + 1).ToList() : new List<Token>();

                // "X/Y is the total base attack/defense of ..."
                // 这种写法里 X 取攻击部分、Y 取防御部分，不能共用一个表达式。
                bool splitByStat = names.Count >= 2 && rest.Count > 3
                                    && rest.Any(t => t.IsWord("attack"))
                                    && rest.Any(t => t.IsWord("defense"));
                if (splitByStat)
                {
                    // X 取攻击：把 "attack/defense" 整个双写法压成只剩 attack，
                    // 否则 lastIndexOf("defense") 会把它误判成 Life。
                    var atkRest = new List<Token>();
                    for (int q = 0; q < rest.Count; q++)
                    {
                        if (rest[q].IsWord("attack") && q + 2 < rest.Count
                            && rest[q+1].IsWord("/") && rest[q+2].IsWord("defense"))
                        {
                            atkRest.Add(rest[q]);
                            q += 2;
                            continue;
                        }
                        atkRest.Add(rest[q]);
                    }
                    _variableBindings[names[0]] = BuildVariableExpr(atkRest);

                    var defRest = rest.Select(t => t.IsWord("attack")
                                        ? new Token { Kind = TokKind.Word, Text = "defense", Pos = t.Pos } : t).ToList();
                    _variableBindings[names[1]] = BuildVariableExpr(defRest);
                }
                else
                {
                    var expr = BuildVariableExpr(rest);
                    foreach (var nm in names) _variableBindings[nm] = expr;
                }
            }
        }
    }

    /// <summary>
    /// 把 "this follower's attack" / "your Combo" / "the number of X" 这类右侧描述
    /// 编译成真实的 NumExpr，而不是常数。
    /// </summary>
    private NumExpr BuildVariableExpr(IReadOnlyList<Token> rest)
    {
        if (rest.Count == 0) return NumExpr.Const(0);
        var c = new TokenCursor(rest);
        var joined = string.Join(" ", rest.Select(t => t.Text.ToLowerInvariant()));

        // "the number of <TargetNP>"
        if (c.MatchPhrase("the", "number", "of"))
        {
            // "the number of clues you have" / "cards in your hand" 之类：
            // 先试有名资源，再交给目标语法。
            // 目标语法不认识 "clues you have"，会返回 null，
            // 然后整句落进兜底变成一个叫
            // "the_number_of_clues_you_have" 的不存在计数器。
            var saved = c.Position;
            if (!c.IsAtEnd)
            {
                var res0 = c.Current.Text.ToLowerInvariant();
                // 这类资源名在目标语法里解析不出来，
                // 落进兜底就把整句拼成一个不存在的计数器名。
                var resAlias = res0 switch
                {
                    "clue" or "clues" => "clues",
                    "crest" or "crests" => "crest",
                    "sigil" or "sigils" => "earth_sigil",
                    "shadow" or "shadows" => "shadow",
                    "card" or "cards" => null,
                    _ => null,
                };
                if (resAlias != null)
                {
                    c.Advance();
                    while (!c.IsAtEnd) c.Advance();
                    return NumExpr.Ctr(_lex.Counter(resAlias));
                }
            }
            c.Seek(saved);
            var tgt = _target.TryParse(c);
            if (tgt != null) return NumExpr.CountOf(tgt);
            // 还是不行就把剩余词当资源名（如 "cards in your hand"）
            c.Seek(saved);
            while (!c.IsAtEnd) c.Advance();
        }

        // "your Combo" / "your <资源>"
        if (c.Current.IsWord("your"))
        {
            c.Advance();
            if (!c.IsAtEnd)
            {
                var res = c.Current.Text.ToLowerInvariant();
                return NumExpr.Ctr(_lex.Counter(res));
            }
        }

        // "this follower's attack / defense / cost"
        if (c.Current.IsWord("this"))
        {
            c.Advance();
            c.TryMatchAnyWord("follower's", "card's", "follower", "card");
            if (!c.IsAtEnd)
            {
                var statName = c.Current.Text.ToLowerInvariant();
                var key = statName.StartsWith("attack") ? StatKey.Attack
                        : statName.StartsWith("defense") ? StatKey.Life
                        : statName.StartsWith("cost") ? StatKey.Cost
                        : StatKey.Attack;
                return NumExpr.Prop(key, TargetSpec.Self);
            }
        }

        // "the selected follower's attack / defense"
        if (c.Current.IsWord("the") && c.Peek().IsWord("selected"))
        {
            c.Advance(); c.Advance();
            c.TryMatchAnyWord("follower's", "card's");
            if (!c.IsAtEnd)
            {
                var sn = c.Current.Text.ToLowerInvariant();
                var key = sn.StartsWith("attack") ? StatKey.Attack : StatKey.Life;
                return NumExpr.Prop(key, TargetSpec.Previous);
            }
        }

        // "the cost of the selected card" / "the cost of the card you drew"
        if (c.MatchPhrase("the", "cost", "of"))
        {
            var tgt = _target.TryParse(c);
            return NumExpr.Prop(StatKey.Cost, tgt ?? TargetSpec.Previous);
        }

        // 兜底：整段作为计数器名（如 "number of cards returned" 已在前面的分支处理）
        // "the total base attack/defense of allied <Trait> followers destroyed this turn"
        // 这类描述没有现成计数器，但语义明确：统计被破坏的己方某兵种的属性合。
        if (joined.Contains("total") && joined.Contains("base"))
        {
            // 注意：原文常写作 "attack/defense"，两个词都在。
            // 不能用「含 defense 就取 Life」判定，否则 X 与 Y 永远相同。
            // 应取“最后出现的那个词”——分词时 Y 已被换成 defense，
            // 而 X 保留原文的 "attack/defense"。
            int ia = joined.LastIndexOf("attack", StringComparison.Ordinal);
            int idf = joined.LastIndexOf("defense", StringComparison.Ordinal);
            var statKey = idf > ia ? StatKey.Life : StatKey.Attack;
            var filt = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Self);
            foreach (var tr in new[] { "Shikigami", "Artifact", "Golem", "Marine", "Puppetry", "Departed", "Officer", "Pixie", "Clue", "Loot" })
            {
                if (!joined.Contains(tr.ToLowerInvariant())) continue;
                filt = filt.WithWhere(new HasTraitExpr { Trait = _lex.Trait(tr), Of = TargetSpec.Item });
                break;
            }
            return NumExpr.SumOf(filt, statKey);
        }

        return NumExpr.Ctr(_lex.Counter(joined.Replace(" ", "_")));
    }

    /// <summary>查变量绑定；没有则退回计数器读取。</summary>
    public NumExpr ResolveVariable(string name)
    {
        var key = (name ?? "").ToUpperInvariant();
        return _variableBindings.TryGetValue(key, out var e) ? e : NumExpr.Ctr(_lex.Counter(key));
    }

    /// <summary>
    /// 把进化段落的作用域折进条件。
    /// &lt;ev&gt; 段落的能力必须附加 Evolved 状态，否则未进化时就会生效。
    /// OnEvolve 触发器本身已隐含进化，不重复附加。
    /// </summary>
    /// <summary>
    /// 把**条件型代价**折进 Condition。
    ///
    /// Necromancy (6) / Combo (3) / Rally (10) 不是「付费」，而是「满足条件才生效」：
    ///   Necromancy (6): Deal 6 damage  ==  若墓地 >= 6，则打 6 点
    /// 之前它们只进 Costs，而 VM 从不检查 Costs ——
    /// 效果于是在条件不满足时也照常触发。
    ///
    /// 真代价（UsePp / DestroyAlly）不折进来，那是另一个语义。
    /// </summary>
    private BoolExpr AndCosts(BoolExpr cond, IReadOnlyList<CostSpec> costs)
    {
        if (costs == null || costs.Count == 0) return cond;

        BoolExpr acc = cond ?? BoolExpr.True;
        foreach (var c in costs)
        {
            // 代价 → 布尔条件的映射。
            // 之前只映射了 combo / rally / necromancy，其余一律 continue 跳过，
            // 于是 "Earth Rite (3) - ..." 与 "Enhance (8): ..." 的数值整个蒸发。
            var expr = c.Kind switch
            {
                CostKind.TurnPlayCount => Compare("combo", c.Amount),
                CostKind.MatchPlayCount => Compare("match_play", c.Amount),
                CostKind.Necromance => Compare("shadow", c.Amount),
                // Earth Rite (N)：需要破坏自己 N 个护符 —— 用 earth_sigil 计数判定
                CostKind.DestroyAlly => Compare("earth_sigil", c.Amount),
                // Enhance (N) / Engage (N)：需要 N 点 PP
                CostKind.UsePp => Compare("pp", c.Amount),
                // Countdown (N)：护符每回合减 1，减到 0 触发。
                // 它不是"条件"而是"初始计数"，但数值必须留在 AST 里 ——
                // 之前一律 continue 跳过，Countdown (4) 的 4 就整条蒸发了。
                CostKind.Countdown => Compare("countdown", c.Amount),
                _ => null,
            };
            if (expr == null) continue;
            acc = BoolExpr.And(acc, expr);
        }
        return acc;

        CompareExpr Compare(string counter, NumExpr amount) => new()
        {
            Left = NumExpr.Ctr(_lex.Counter(counter)),
            Op = CompareOp.Ge,
            Right = amount ?? NumExpr.Const(0),
        };
    }

    private static BoolExpr AndZone(SectionZone zone, BoolExpr cond)
    {
        if (zone == SectionZone.Normal) return cond;
        var state = zone == SectionZone.Evolved ? UnitState.Evolved : UnitState.SuperEvolved;
        return BoolExpr.And(cond, new HasStateExpr { State = state, Of = TargetSpec.Self });
    }

    private static string Trunc(string s)
        => s != null && s.Length > 50 ? s.Substring(0, 50) + "…" : (s ?? "");
}

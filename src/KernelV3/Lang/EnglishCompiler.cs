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
        _target.VariableResolver = ResolveVariable;
        // 行内属性读取复用 BuildVariableExpr：它已支持
        // "the first/second/Nth selected follower's attack/defense/cost"。
        _actions.StatExprResolver = toks => BuildVariableExpr(toks);
        // 引号内的变量声明也要真正登记（Shakdoh 的 Draw X）。
        _actions.VariableBinder = TryBindVariableDeclaration;
        _prefixes = new PrefixParser(lex, _target);
    }

    /// <summary>
    /// 是否启用设计3 5.3 的代词强制校验。
    /// 整卡编译为 true；单句语料被强行切开、上下文不完整，应设为 false。
    /// </summary>
    public bool StrictPronounCheck
    {
        get => _target.StrictPronounCheck;
        set => _target.StrictPronounCheck = value;
    }

    public sealed class Result
    {
        public List<AbilityDef> Abilities { get; } = new();

        /// <summary>
        /// 卡面句首的裸关键词（Ward / Rush / Storm …）。
        /// 它们是**卡牌定义属性**，不是可求值效果：创建实体时一次性写进
        /// Entity.Keywords 即可，不需要走能力管线。
        /// 早先把它们编成 PassiveAura + ModifyKeywordNode，导致同一张卡在
        /// InnateKeywords 与 Abilities 里各存一份，卡面还会多出一条空的 aura: 段。
        /// </summary>
        public List<Core.SymbolId<Core.KeywordTag>> InnateKeywords { get; } = new();

        public List<string> Errors { get; } = new();
        public bool Ok => Errors.Count == 0;

        /// <summary>
        /// 本卡生成的那个**纹章**的完整定义（含编译好的 Abilities）。
        /// 只有卡面写了「Gain Crest: &lt;同名&gt;」且 alt_modes 里确实有 Crest 段时才非空。
        /// 调用方负责把它挂到 CardDef.CrestDef 上，运行时 GainCrest 才有能力可装。
        /// </summary>
        public Ast.CardDef CrestDef { get; set; }

        /// <summary>本卡生成的那个**信仰**的完整定义。</summary>
        public Ast.CardDef FaithDef { get; set; }

        /// <summary>激奏模式（本体费用不够时按法术打出）。</summary>
        public Ast.CardDef AccelerateDef { get; set; }

        /// <summary>结晶模式（本体费用不够时按护符打出）。</summary>
        public Ast.CardDef CrystallizeDef { get; set; }

        /// <summary>卡面是否有 `Fuse: ...` 材料声明（供「with Fuse」谓词判定）。</summary>
        public bool HasFuse { get; set; }

        /// <summary>`Fuse: <材料>` 的材料过滤条件（null = 不限）。</summary>
        public TargetSpec FuseMaterialFilter { get; set; }
    }

    /// <summary>编译整张卡的效果文本。</summary>
    /// <summary>
    /// 编译一张卡。
    ///
    /// altModes 是这张卡自己的 alt_modes（形如
    /// <c>[("Crest", 0, "Whenever an allied Pixie follower enters the field, give it Storm.")]</c>）。
    /// 纹章/信仰/结晶/激奏的**效果定义**只存在于这里，卡面正文只写「Gain Crest: &lt;同名&gt;」。
    /// 不传的话，纹章实体会按名字现造空壳 —— 名字对、能力全空。
    /// </summary>
    public Result Compile(string text,
                          IReadOnlyList<(string typeEng, int cost, string textEng)> altModes = null,
                          string cardName = null,
                          int cardId = 0)
    {
        _altModes = altModes;
        _currentCardName = cardName;
        _currentCardId = cardId;

        // 让条件编译器也能读到卡面登记的变量表达式。
        // "If X is at least 2" 的 X 在下面 TryBindVariableDeclaration 里绑定，
        // 但条件编译发生在 EnglishCompiler 之外，拿不到 _variableBindings。
        ConditionCompiler.VariableResolver = vn
            => _variableBindings.TryGetValue(vn, out var bound) ? bound : null;

        // ⚠ 这里**不能**调 _lex.AddCard(cardName, cardId)。
        // 测试集与官方包存在同名卡（81000037 Ancient Cannon 的
        // "Gear of Ambition" 与官方的 90071210 同名但不同号）。
        // 强行把当前卡写进 Lexicon 会让**别的卡**引用同一个名字时
        // 解析到本卡的号，30 张测试集卡的脚本往返因此从 30/30 掉到 29/30，
        // 逐卡矩阵也从 32/32 掉到 29/32。
        // 同名卡号覆盖问题应另行在数据层解决，不能在这里动全局词表。

        // 先把 alt_modes 里的纹章/信仰定义编出来，供动作解析器在生成
        // 「Gain Crest: ...」时一并带上。必须早于正文编译。
        var pre = new Result();
        BuildAltModeDefs(pre);
        _actions.CrestDefResolver = () => pre.CrestDef;

        _sawFuseDecl = false;
        // 融合材料筛选必须随「本次编译」一起复位。
        //
        // EnglishCompiler 是可复用的：TestsetLoader 就是用一个实例连编 30 张卡。
        // 只复位 _sawFuseDecl 而不复位 _fuseMaterialFilter，会让
        // 「上一张卡的融合筛选」粘到下一张卡上 —— 于是一张根本不能融合的卡，
        // 其 Def.FuseMaterialFilter 却挂着别人的 has trait Artifact，
        // 而 HasFuse 是 false：两个字段自相矛盾，卡面却完全正常。
        _fuseMaterialFilter = null;
        var r = CompileCore(text);
        r.CrestDef = pre.CrestDef ?? r.CrestDef;
        r.FaithDef = pre.FaithDef ?? r.FaithDef;
        r.HasFuse = _sawFuseDecl;
        r.FuseMaterialFilter = _fuseMaterialFilter;
        // 材料声明段不产出能力，事实位必须贴到已有能力上，运行期才查得到。
        if (_sawFuseDecl && r.Abilities.Count > 0 && !r.Abilities[0].HasFuse)
        {
            var a0 = r.Abilities[0];
            r.Abilities[0] = new Ast.AbilityDef
            {
                Trigger = a0.Trigger, Condition = a0.Condition,
                Costs = a0.Costs, Body = a0.Body, HasFuse = true,
            };
        }
        // 黑洞封堵（主卡级）：原文有实义词，却编译出 0 条能力且 0 报错，
        // 说明有整句被当成状态短语或空分支静默吞掉了。必须报出来。
        // 排除纯融合材料声明（「Fuse: ...」本身不产出能力，是合法情况）。
        if (r.Errors.Count == 0 && r.Abilities.Count == 0 && !_sawFuseDecl
            && !string.IsNullOrWhiteSpace(text))
        {
            string plain = text.Trim();
            r.Errors.Add("[静默丢弃] 原文非空但产出了 0 条能力：["
                         + (plain.Length > 120 ? plain.Substring(0, 120) + "…" : plain) + "]");
        }

        _actions.CrestDefResolver = null;
        return r;
    }

    private IReadOnlyList<(string typeEng, int cost, string textEng)> _altModes;

    private Result CompileCore(string text)
    {
        // 防止上一张卡的状态跨卡污染。
        // 两个字典必须一起清：只清 _variableBindings 而漏了 _initialCounters，
        // 会让「X starts at 2」的初始计数器泄漏给后面每一张卡。
        // 实测：982 张里有 866 张会凭空多出一条 PassiveAura 计数器能力（真实值 7 张）。
        _variableBindings.Clear();
        _initialCounters.Clear();
        _fuseTiers.Clear();
        _fuseTierByCost = false;
        _fuseHostAbility = null;
        _actions.FuseTierByCost = false;
        _lastBuiltAbility = null;
        // 卡级槽位登记：整张卡编译开始时清空一次。
        _target.ResetSlots();
        var r = new Result();
        if (string.IsNullOrWhiteSpace(text)) return r;

        // 静态钩子必须在这里**重新**绑定。
        //
        // Compile() 在调 CompileCore 之前先跑了 BuildAltModeDefs，
        // 那里用 new EnglishCompiler(_lex).Compile(...) 编译关联纹章，
        // 会把 ConditionCompiler.VariableResolver 覆盖指向**那个子编译器**的空绑定表。
        // 主卡随后的 "If X is at least 2" 于是拿不到 X，退化成 ctr:X（10844110）。
        ConditionCompiler.VariableResolver = ResolveVariable;

        // 「an exact copy of each」要按所选张数复制，靠 _lastSelectCount 记录。
        // 必须逐卡清空，否则上一张卡的 Select 数量会泄漏给下一张。
        _actions.ResetSelectCount();

        var plain = Preprocess(text);
        var mergedSegs = MergeOptionSegments(SplitSegments(plain)).ToList();

        // 全卡级变量声明预扫描：
        // "X is the number of ..." 可能写在任意段落（含 <hr> 之后），
        // 而条件编译发生在逐段循环里 —— 声明句若排在后面，
        // 前面段落的 "If X is at least 2" 就绑不上。
        foreach (var (_, segText) in mergedSegs)
        {
            if (string.IsNullOrWhiteSpace(segText)) continue;
            var preToks = _tokenizer.Tokenize(segText);
            var curSent = new List<Token>();
            foreach (var tk in preToks)
            {
                if (tk.Kind == TokKind.Eof || tk.Kind == TokKind.Period || tk.Kind == TokKind.OptionMark)
                {
                    if (curSent.Count > 0 && IsVariableDeclaration(curSent))
                        TryBindVariableDeclaration(curSent);
                    curSent.Clear();
                    continue;
                }
                curSent.Add(tk);
            }
            if (curSent.Count > 0 && IsVariableDeclaration(curSent))
                TryBindVariableDeclaration(curSent);
        }

        var buffer = new List<AbilityDef>();
        foreach (var (zone, segment) in mergedSegs)
        {
            if (segment.Trim().Length == 0) continue;
            try
            {
                var abs = CompileSegment(segment, zone, r.InnateKeywords);
                buffer.AddRange(abs);
            }
            catch (LangException ex)
            {
                r.Errors.Add($"[{Trunc(segment)}] {ex.Message}");
            }
        }

        // 三大编织器在**整卡级**跑一次，顺序必须是 MetaMode → Instead → Enhance。
        //
        // 顺序的理由：
        //   · MetaMode 先跑："Select a Mode ... Enhance (8): Activate all of them instead"
        //     要先把「全部模式」认出来，否则那句会被 Instead 当成替代动作。
        //   · Instead 次之：此时 instead_marker 仍在 Body 顶层、Enhance 的 CostSpec 也还在，
        //     EnhanceWeaver 尚未把它包进两层 ConditionalNode。
        //   · Enhance 最后：累加型多档（基础 + Enhance 7 + Enhance 8）此时才折叠成阶梯。
        var woven = MetaModeWeaver.Weave(buffer, _lex);
        woven = InsteadWeaver.Weave(woven, _lex);
        woven = EnhanceWeaver.Weave(woven, _lex);

        r.Abilities.Clear();
        r.Abilities.AddRange(woven);

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

        // 纹章 / 信仰的**效果定义**只存在于主人卡的 alt_modes 里。
        // 卡面正文只写「Gain Crest: <同名>」，定义在别处 —— 不在这里编译，
        // 运行时只能按名字现造空壳，纹章「拿得到但没有能力」。
        BuildAltModeDefs(r);

        return r;
    }

    /// <summary>当前正在编译的卡名（供 alt_modes 定义命名）。</summary>
    private string _currentCardName;

    /// <summary>本卡是否出现过 Fuse 材料声明（由 CompileSegment 置位）。</summary>
    private bool _sawFuseDecl;

    /// <summary>`Fuse: <材料>` 的材料过滤条件（由 CompileSegment 置位）。</summary>
    private TargetSpec _fuseMaterialFilter;

    /// <summary>当前正在编译的卡号（激奏/结晶子定义要继承它）。</summary>
    private int _currentCardId;

    /// <summary>
    /// 把 alt_modes 里的 Crest / Faith 段各自编译成一个完整 CardDef 挂到 Result 上。
    /// Crystallize / Accelerate 不在这里处理：它们是**出牌模式**，不是独立实体。
    /// </summary>
    private void BuildAltModeDefs(Result r)
    {
        if (_altModes == null || _altModes.Count == 0) return;

        foreach (var (typeEng, cost, textEng) in _altModes)
        {
            if (string.IsNullOrWhiteSpace(textEng)) continue;
            bool isCrest = string.Equals(typeEng, "Crest", StringComparison.OrdinalIgnoreCase);
            bool isFaith = string.Equals(typeEng, "Faith", StringComparison.OrdinalIgnoreCase);
            bool isAccel = string.Equals(typeEng, "Accelerate", StringComparison.OrdinalIgnoreCase);
            bool isCryst = string.Equals(typeEng, "Crystallize", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(typeEng, "Crystallise", StringComparison.OrdinalIgnoreCase);
            if (!isCrest && !isFaith && !isAccel && !isCryst) continue;
            if (isCrest && r.CrestDef != null) continue;
            if (isFaith && r.FaithDef != null) continue;
            if (isAccel && r.AccelerateDef != null) continue;
            if (isCryst && r.CrystallizeDef != null) continue;

            // 官方 alt 段不会自引用，这里不再传 altModes，避免无限递归。
            var sub = new EnglishCompiler(_lex).Compile(textEng);

            // 黑洞封堵：子卡（纹章/信仰/激奏/结晶）的编译错误以前被整段丢弃，
            // 结果卡面上写着效果，游戏里纹章是空壳，且没有任何报警。
            foreach (var subErr in sub.Errors)
                r.Errors.Add("[" + typeEng + "] " + subErr);

            // 原文非空、无报错、却产出 0 条能力 —— 这是「静默丢弃」，必须暴露。
            if (sub.Errors.Count == 0 && sub.Abilities.Count == 0
                && !string.IsNullOrWhiteSpace(textEng))
            {
                r.Errors.Add("[" + typeEng + "] 静默丢弃警告：子卡原文非空但编译出了 0 条能力！原文：["
                             + (textEng.Length > 120 ? textEng.Substring(0, 120) + "…" : textEng) + "]");
            }
            var def = new Ast.CardDef
            {
                // 纹章 / 信仰是**独立实体**，CardId 留 0，运行期按名字合成号段
                // （BattleState.ResolveCrestCardId / FaithEntityIdBase）。
                //
                // 激奏与结晶是**同一张卡的另一张脸**，必须继承本体卡号：
                // 留 0 会让它在墓地/牌库里认不出自己是哪张卡 ——
                // 亡者召还、按卡号检索、以及回手后的还原全部失效。
                CardId = (isAccel || isCryst) ? _currentCardId : 0,
                Name = _currentCardName ?? "",
                // 激奏按法术结算（结算完进墓地、shadow +1）；
                // 结晶按护符进场（带自己的 Countdown 与谢幕曲）。
                Kind = isCrest ? Ast.CardKind.Crest
                     : isFaith ? Ast.CardKind.Faith
                     : isAccel ? Ast.CardKind.Spell
                     : Ast.CardKind.Amulet,
                BaseCost = cost,
                Abilities = sub.Abilities,
                SourceText = textEng,
                // 纹章/信仰是合成号段，没有立绘文件；记下主人卡号，
                // 溯源面板才能加载出主人卡的图而不是黑底。
                ArtCardId = _currentCardId,
            };
            if (isCrest) r.CrestDef = def;
            else if (isFaith) r.FaithDef = def;
            else if (isAccel) r.AccelerateDef = def;
            else r.CrystallizeDef = def;
        }
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
        // 保留行首缩进：DIY 的嵌套模式靠它表层级。
        // 之前用 \n[ \t]+ -> \n 把缩进全删了。
        s = s.Replace("\n", "\u0001");

        // 行首缩进很容易被空白压缩吃掉（上一版 5 个空格被压成 3 个）。
        // 做法：先把「\u0001 + 连续空白」整体换成同长的 \u0000 占位，
        // 压缩完再换回空格。这样不论缩进多少层都不会丢。
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\u0001([ \t]+)",
            m => "\u0001" + new string('\u0000', m.Groups[1].Length));
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]+", " ");
        s = s.Replace('\u0000', ' ');
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

        // 融合分档表用冒号：「1: Ominous Artifact α」/「3 or more: ...」。
        // 它同样必须并回前一段，否则三档被段落符切成三段，
        // 各自编译成独立能力，分档表就永远合不起来。
        if (i < t.Length && t[i] == ':') return true;
        if (i < t.Length && t[i] == ' ')
        {
            var rest = t.Substring(i).TrimStart();
            if (rest.StartsWith("or more:") || rest.StartsWith("or less:")) return true;
        }

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
                // 用 \u0001 连接，并**保留段首缩进**。
                // DIY 的嵌套模式靠缩进表层级；之前用 ' ' + txt.Trim()
                // 把缩进删掉了，层级信息随之丢失。
                pending.Append('').Append(txt);
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

    /// <summary>
    /// 递归检查条件表达式里是否引用了 $opposing 槽位。
    ///
    /// "Follower Strike: If the opposing follower is damaged, destroy it."
    /// 条件本身提到了 $opposing，代词语义就应当默认指向它。
    /// </summary>
    private static bool MentionsOpposingSlot(BoolExpr e)
    {
        switch (e)
        {
            case null:
                return false;

            case AndExpr a:
                foreach (var it in a.Items)
                    if (MentionsOpposingSlot(it)) return true;
                return false;

            case OrExpr o:
                foreach (var it in o.Items)
                    if (MentionsOpposingSlot(it)) return true;
                return false;

            case NotExpr n:
                return MentionsOpposingSlot(n.Inner);

            case HasStateExpr hs:
                return IsOpposing(hs.Of);

            case HasKeywordExpr hk:
                return IsOpposing(hk.Of);

            case HasTraitExpr ht:
                return IsOpposing(ht.Of);

            case HasClanExpr hc:
                return IsOpposing(hc.Of);

            case ExistsExpr ex:
                return IsOpposing(ex.Target);

            case CompareExpr cmp:
                return RefersToOpposing(cmp.Left) || RefersToOpposing(cmp.Right);

            default:
                return false;
        }
    }

    private static bool IsOpposing(TargetSpec t)
        => t != null && t.SourceKind == TargetSourceKind.Slot
           && t.Slot == TargetSpec.SlotOpposing;

    /// <summary>数值表达式是否引用了 $opposing（例如「数量」类比较）。</summary>
    private static bool RefersToOpposing(NumExpr n)
    {
        if (n == null) return false;
        if (IsOpposing(n.Of)) return true;

        foreach (var a in n.Args)
            if (RefersToOpposing(a)) return true;

        return RefersToOpposing(n.Then) || RefersToOpposing(n.Else);
    }

    private List<AbilityDef> CompileSegment(string segment, SectionZone zone = SectionZone.Normal,
                                            List<Core.SymbolId<Core.KeywordTag>> innateSink = null)
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
        // 嵌套层级表：缩进相同的编号是同层兄弟，更深的属于内层块。
        var sentDepth = new List<int>();
        var indentLevels = new List<int>();
        int curDepth = 0;

        void PushSentence()
        {
            if (cur.Count == 0) return;
            sentences.Add(cur);
            sentOptions.Add(curOption);
            sentDepth.Add(curDepth);
            cur = new List<Token>();
        }

        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Eof) break;
            if (t.Kind == TokKind.OptionMark)
            {
                // 先收尾前一句（它属于上一个序号），再更新本句的层级。
                PushSentence();

                int indent = t.Indent;
                if (indentLevels.Count == 0)
                {
                    indentLevels.Add(indent);
                    curDepth = 0;
                }
                else
                {
                    int found = indentLevels.IndexOf(indent);
                    if (found >= 0)
                    {
                        curDepth = found;
                        while (indentLevels.Count > found + 1) indentLevels.RemoveAt(indentLevels.Count - 1);
                    }
                    else if (indent > indentLevels[^1])
                    {
                        indentLevels.Add(indent);
                        curDepth = indentLevels.Count - 1;
                    }
                    else
                    {
                        indentLevels.Clear();
                        indentLevels.Add(indent);
                        curDepth = 0;
                    }
                }

                curOption = t.Value;
                continue;
            }
            if (t.Kind == TokKind.Period)
            {
                PushSentence();
                continue;
            }
            cur.Add(t);
        }
        PushSentence();

        // ★ 选项块终结：选项只延续到「下一个能力头」出现为止。
        // 少了这一步，"... 2. Deal 2 damage. Enhance (3): Activate all of them instead."
        // 会把 Enhance 那一句也并进选项 2，整句随后撞墙报错。
        // 规则：某个选项之后出现 Fanfare / Enhance / Combo ... 这类带冒号或
        // 短横的能力头，即视为选项块结束，其后所有句子回到 -1。
        CloseOptionBlockOnHeader(sentences, sentOptions);

        // "..., Otherwise, <else>." 的 otherwise 常被句号切成独立一句。
        // 它不是一个动作，而是**前一句条件分支的 else**。
        //
        // 不能把两句用逗号拼回去：那样 "If ..." 会从句首变成句中，
        // 而句首的 If 是由 PrefixParser 折成能力 Condition 的，
        // 拼回去之后条件整条丢失，两条分支都无条件执行。
        //
        // 正确做法：保留独立句，稍后在能力落盘时把它接到上一条能力的 Else 上。

        // 条件句：把 "If X, Y" 拆成条件 + 动作
        BoolExpr pendingCondition = null;
        var modeOptions = new List<IReadOnlyList<EffectNode>>();
        // 与 modeOptions 平行的序号表：同一个编号的连续句子并进同一个选项。
        var modeOptionIds = new List<int>();
        // 与 modeOptions 平行的深度表：序列里每项来自第几层嵌套。
        var modeDepth = new List<int>();
        TriggerSpec modeTrigger = null;
        // 模式块要并入的那条已有能力（"Engage: ... Select a Mode to activate."）。
        AbilityDef modeAttachTo = null;
        BoolExpr modeCondition = BoolExpr.True;
        List<CostSpec> modeCosts = new List<CostSpec>();
        SectionZone modeZone = zone;
        // 模式块引导句携带的选取数量（"Select 2 Modes to activate"）。
        NumExpr modePick = NumExpr.Const(1);
        // 模式引导句的选取语义（Choose / Random / Sequence / *Unused）。
        // 它由引导句本身决定，落盘时写进 BranchModeNode.Kind。
        ModeDispatchKind modeKind = ModeDispatchKind.Choose;


        // 句内「and, once on each of your turns,」拆句。
        //
        // 「Whenever an allied Marine follower attacks, give it +1/+0 until the end
        //  of the turn and, once on each of your turns, add a Majestic Megalorca…」
        // 前半句每次攻击都触发，后半句每回合限一次 —— 语义上是共用同一触发头的两条能力。
        // 早先整句直接撞在 `once` 上报「无法推进」。
        for (int si = 0; si < sentences.Count; si++)
        {
            var src = sentences[si];
            int onceIdx = -1, andIdx = -1;
            for (int ti = 0; ti + 3 < src.Count; ti++)
            {
                if (src[ti].IsWord("once") && src[ti + 1].IsWord("on")
                    && src[ti + 2].IsWord("each") && src[ti + 3].IsWord("of"))
                { onceIdx = ti; break; }
            }
            if (onceIdx <= 2) continue;

            // 往前找最近的 and（允许中间隔一个逗号）
            for (int ti = onceIdx - 1; ti >= 0; ti--)
            {
                if (src[ti].Kind == TokKind.Comma) continue;
                if (src[ti].IsWord("and")) { andIdx = ti; }
                break;
            }
            if (andIdx < 0) continue;

            // 后半句：once ... turns, <动作> → 去掉 once 子句，action 保留
            int actionStart = onceIdx;
            while (actionStart < src.Count && src[actionStart].Kind != TokKind.Comma) actionStart++;
            if (actionStart >= src.Count - 1) continue;
            actionStart++;

            // 触发头 = 第一句开头的 Whenever/When 子句（到第一个逗号为止）
            int headEnd = 0;
            while (headEnd < andIdx && src[headEnd].Kind != TokKind.Comma) headEnd++;
            if (headEnd >= andIdx) continue;

            // 频率前缀 "once on each of your turns," 必须留在第二句里。
            // 早先把 onceIdx..actionStart 整段跳过，拆出来的第二句没有
            // MaxTriggersPerTurn=1，一回合攻击 3 次就发 3 次效果。
            // 语序是「频率前缀在前、Whenever 头在后」，两个都放最前面。
            var second = new List<Token>();
            for (int ti = onceIdx; ti < actionStart && ti < src.Count; ti++) second.Add(src[ti]);
            for (int ti = 0; ti <= headEnd; ti++) second.Add(src[ti]);
            for (int ti = actionStart; ti < src.Count; ti++) second.Add(src[ti]);

            // 第一句截到 and 之前
            var first = new List<Token>();
            for (int ti = 0; ti < andIdx; ti++) first.Add(src[ti]);

            sentences[si] = first;
            sentences.Insert(si + 1, second);
            sentOptions.Insert(si + 1, sentOptions[si]);
            sentDepth.Insert(si + 1, sentDepth[si]);
            si++;
        }

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

        // 段级活动区域："Activates in deck." 往往单独成句，
        // 紧跟的 "At the start of your turn, ..." 又是另一句。
        // 如果不在段内传递，后一句的 ActiveZone 会重置回 Board，
        // 牌组/手牌里的能力就永远不会触发。
        var segmentZone = ActiveZoneScope.Board;

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
                // 每个编号选项是**互斥分支**，切入新选项号时必须重置槽位历史，
                // 否则选项 1 里的 "Evolve this follower"（绑了 $self）
                // 或 "Select ..."（绑了 $selected）会泄漏到选项 2，
                // 让选项 2 的 it 指到选项 1 的目标。
                {
                    int newKey = sentDepth[si] * 100 + sentOptions[si];
                    bool isNewOption = modeOptions.Count == 0 || modeOptionIds[^1] != newKey;
                    if (isNewOption && modeAttachTo == null) _target.ResetSlots();
                }

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

                    // 消耗型代价必须在 Then 分支**真的扣掉**，不能只加条件守卫。
                    //   2. Earth Rite (1) - Give this follower +2/+2 and Ward.
                    // 早先只生成了 `if ctr:earth_sigil >= 1` 守卫，却把 ohb.Costs 整个丢弃 ——
                    // 于是选了这个选项，土之印一点不扣。
                    // 也不能挂到整卡顶层 Costs：那会让选项 1「获得土之印」也被扣。
                    var thenNodes = new List<EffectNode>();
                    foreach (var cs in ohb.Costs)
                    {
                        string? ctrName = cs.Kind switch
                        {
                            CostKind.DestroyAlly => "earth_sigil",
                            CostKind.Necromance => "shadow",
                            _ => null,
                        };
                        if (ctrName == null) continue;
                        thenNodes.Add(new ModifyCounterNode
                        {
                            Counter = _lex.Counter(ctrName),
                            Op = CounterOp.Add,
                            Delta = NumExpr.Sub(NumExpr.Const(0), cs.Amount),
                        });
                    }
                    thenNodes.AddRange(ob);

                    ob = new List<EffectNode>
                    {
                        new ConditionalNode { Condition = optCond, Then = thenNodes },
                    };
                }

                // 带层级信息的键：同一层的同一编号才并入同一个选项。
                int optKey = sentDepth[si] * 100 + sentOptions[si];
                if (modeOptions.Count > 0 && modeOptionIds[^1] == optKey)
                {
                    ((List<EffectNode>)modeOptions[^1]).AddRange(ob);
                }
                else
                {
                    modeOptions.Add(ob);
                    modeOptionIds.Add(optKey);
                    modeDepth.Add(sentDepth[si]);
                }
                continue;
            }

            // 遇到非选项句时，先把已收集的模式选项落成一个 BranchModeNode。
            // 落盘必须发生在解析本句**之前** —— 本句可能正是一个新的模式块引导句
            // （"1. ... 2. ... Evolve: Select a Mode to activate." 里那个 Evolve 头），
            // 先落盘才能保证两段选项不会被并进同一个 BranchModeNode。
            FlushModeBlock(result, ref modeOptions, ref modeOptionIds, ref modeTrigger,
                           ref modeCondition, ref modeCosts, ref modeZone, ref modePick, ref modeKind,
                           modeDepth, modeGrantTarget, modeAttachTo);
            modeDepth.Clear();
            modeGrantTarget = null;
            modeAttachTo = null;

            var c = new TokenCursor(sent);

            // 纯时机短语：不产生动作。
            // 但 "Activates in hand / in deck" 不能一律丢弃——它定义了后续能力在哪个区域监听。
            var joinedSent = string.Join(" ", sent.Select(t => t.Text));
            var joinedKey = joinedSent.Trim().TrimEnd('.').ToLowerInvariant();
            if (joinedKey == "activates in hand" || joinedKey == "activates")
            {
                segmentZone = ActiveZoneScope.Hand;
                continue;
            }
            if (joinedKey == "activates in deck")
            {
                segmentZone = ActiveZoneScope.Deck;
                continue;
            }
            if (IsPureStatePhrase(joinedSent)) continue;

            // 变量声明句：登记变量绑定，不产生动作
            if (IsVariableDeclaration(sent))
            {
                TryBindVariableDeclaration(sent);
                continue;
            }

            // 融合分支表 "1: <Card> 2: <Card> 3 or more: <Card>"
            // 三档在卡面上是三句，必须先累积、段末合成一条能力。
            // 表必须紧跟在它所属的句子后面：「When you Fuse to this card,
            // transform it ...」才是它的触发前提，独立落成 OnPlay 就丢了。
            if (IsFuseSwitchTableHead(c))
            {
                ParseFuseSwitchTable(c);
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


            // "Keyword: Ward, Storm, Rush" —— DIY 模板专用的关键词声明行。
            //
            // 与裸关键词的区别：这里带显式前缀与逗号分隔，一个段里可以声明多个。
            // 裸关键词形式（Ward 单独一行）继续走下面的循环；两者结果相同，
            // 都写进 innateSink（CardDef.InnateKeywords），不生成能力。
            if (c.Current.IsAnyWord("keyword", "keywords", "key"))
            {
                int saveKw = c.Position;
                c.Advance();
                if (c.TryMatch(TokKind.Colon))
                {
                    bool consumedAny = false;
                    while (!c.IsAtEnd)
                    {
                        if (c.Current.Kind == TokKind.Keyword)
                        {
                            var kwSym2 = _lex.Keyword(c.Current.Text);
                            if (kwSym2.IsValid && innateSink != null && !innateSink.Contains(kwSym2))
                                innateSink.Add(kwSym2);
                            c.Advance();
                            consumedAny = true;
                        }
                        else
                        {
                            string raw = c.Current.Text;
                            var kwSym3 = _lex.Keyword(raw);
                            if (kwSym3.IsValid && innateSink != null && !innateSink.Contains(kwSym3))
                                innateSink.Add(kwSym3);
                            c.Advance();
                            consumedAny = true;
                        }
                        if (c.TryMatch(TokKind.Comma)) continue;
                        if (c.MatchWord("and")) continue;
                        break;
                    }
                    if (consumedAny) continue;
                }
                c.Seek(saveKw);
            }

            // 句首裸关键词（Ward / Rush / Storm …）：每个词生成一个独立常驻能力
            while (c.Current.Kind == TokKind.Keyword)
            {
                // "1. Rush, Ward, and Bane." 这种并列交给动作解析器
                if (c.Peek(1).Kind == TokKind.Comma || c.Peek(1).IsWord("and")) break;
                var kwText = c.Current.Text;
                c.Advance();
                // 裸关键词是卡牌定义属性，写进 InnateKeywords，不生成能力。
                // 生成 PassiveAura 会让它在 Abilities 里再存一份，
                // 卡面还会多出一条空的 aura: 段（同一个 ward 记两遍）。
                var kwSym = _lex.Keyword(kwText);
                if (kwSym.IsValid && innateSink != null && !innateSink.Contains(kwSym))
                    innateSink.Add(kwSym);
            }
            if (c.IsAtEnd) continue;

            // 循环剥离所有句首前缀（回合 / 频率 / 边界 / 头 / 事件 / 条件）
            var hb = new AbilityHeaderBuilder();
            _prefixes.ParseAll(c, hb);
            if (hb.HasFuse) _sawFuseDecl = true;
            if (hb.FuseMaterialFilter != null) _fuseMaterialFilter = hb.FuseMaterialFilter;

            // 剥离掉能力头之后才露出来的变身表，例如
            //   "Fanfare: 1: A. 2: B. 3 or more: C."
            // 前缀循环之外的检查看不到它（当前 token 还是 Fanfare），
            // 于是编号被当作动作交给动作解析器，整句报「无法推进」。
            // 在这里接住，并把能力头留存给落盘时使用。
            if (IsFuseSwitchTableHead(c))
            {
                ParseFuseSwitchTable(c);
                _fuseHeader = hb;
                _fuseZone = zone;
                if (c.IsAtEnd) continue;
            }

            // 段级 zone 传递：本句没有显式指定区域时，沿用段内已有的 zone。
            if (hb.ActiveZone != ActiveZoneScope.Board) segmentZone = hb.ActiveZone;
            else hb.ActiveZone = segmentZone;

            // ---- 代词作用域（R-4 / Stage 4）----
            //
            // 何时清空 _target 的槽位登记，是这套编译器最容易出错的地方。
            // 这里有三条互相冲突的需求必须同时满足：
            //
            //   ① "Fanfare: Summon a Megalorca." <hr> "Whenever X enters, give it Ward."
            //      第二句的 it 必须指向新的触发主体 $trigger，
            //      不能去读第一句留下的 $created（那是个早已死掉的引用）。
            //      → 新的**显式事件头**要清空。
            //
            //   ② "Evolve: Summon 2 copies of Ghost."
            //      "Super-Evolve: Give them Drain."
            //      them 指向上一段召出的 Ghost，属于跨能力头的合法引用。
            //      → Evolve → Super-Evolve 的耦合链**不能**清空。
            //
            //   ③ "When you gain this crest, replace your deck with a deck of 40 cards."
            //      "Select another allied card. If you selected one, destroy it."
            //      第二句没有事件头，是同一能力内的条件后续句。
            //      → 无头的后续句**不能**清空。
            //
            // 早先这里无条件调用 ResetSlots()，是按句遍历时就清，
            // 于是只要跨了一个句号，上一句绑定的 $selected / $created 就被抹掉，
            // 官方卡池里 14 张卡统统报「代词 it 缺少前序绑定」。
            bool hasInsteadWord = sent.Any(t => t.IsWord("instead"));

            // Evolve → Super-Evolve 是**跨段落**的耦合对：
            //   "<ev>Evolve: Summon 2 copies of Ghost.</ev>"
            //   "<sev>Super-Evolve: Give them Drain.</sev>"
            // 两段各走一次 CompileSegment，currentAbility 在第二段开头还是 null，
            // 所以必须看 _lastBuiltAbility（跨段保留）才能接上。
            bool isCoupledSuperEvolve = hb.EventExplicit
                && hb.Event == CoreEvent.OnSuperEvolve
                && ((currentAbility?.Trigger?.Event == CoreEvent.OnEvolve)
                    || (_lastBuiltAbility?.Trigger?.Event == CoreEvent.OnEvolve));

            // 注意：这里**不能**再加「Super-Evolve 段只要 BoundSlots 非空就保留」的兜底。
            // 那样 Fanfare 的旧槽位会泄漏进 Super-Evolve：
            //   "Fanfare: Summon X."  ...  "Super-Evolve: Give them Drain."
            // them 会错误指向 Fanfare 召出的东西。
            // 判据只认「上一段确实是 OnEvolve」这一条。
            if (hb.EventExplicit && !isCoupledSuperEvolve && !hasInsteadWord)
                _target.ResetSlots();

            // 触发主体优先；没有任何主体时，若本句带交战目标或条件里提到 $opposing，
            // 就把代词默认槽切到 $opposing（"Follower Strike: ... destroy it"）。
            if (hb.SubjectFilter != null)
            {
                // 主语是 "this card / this follower"（$self）时，代词 it 应指向本卡，
                // 不是 $trigger 槽位 —— 后者在「进入战场时」这类没有任何事件主体的
                // 触发里是空的（10313110 / 90034120）。
                string subjSlot = (hb.SubjectFilter.SourceKind == TargetSourceKind.Slot
                                   && hb.SubjectFilter.Slot == TargetSpec.SlotSelf)
                    ? TargetSpec.SlotSelf
                    : TargetSpec.SlotTrigger;
                _target.DefaultPronounSlot = subjSlot;
                _target.MarkBound(subjSlot);
            }
            else if (hb.HasOpposingTarget || MentionsOpposingSlot(hb.Condition))
            {
                _target.DefaultPronounSlot = TargetSpec.SlotOpposing;
                _target.MarkBound(TargetSpec.SlotOpposing);
            }
            else if (hb.EventExplicit && !isCoupledSuperEvolve && !hasInsteadWord)
            {
                _target.DefaultPronounSlot = null;
            }

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
                        Condition = AndZone(zone, hb.Condition, CoreEvent.PassiveAura),
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

                var builtTrig = hb.BuildTrigger();
                result.Add(new AbilityDef
                {
                    Trigger = builtTrig,
                    Condition = AndZone(zone, AndCosts(hb.Condition, hb.Costs), builtTrig.Event),
                    Costs = hb.Costs,
                    Body = Array.Empty<EffectNode>(),
                });
                continue;
            }

            // 模式引导句（"Select a Mode to activate." / "Select 2 Modes..."）
            // 本身不产出动作（ParseSelect 把它消费成空 body），但它确定了
            // 后续 "1. 2. 3." 选项块的选取数量，必须先于空 body 判定接住。
            var pick = ExtractModePick(sent, out var pickKind);
            if (pick != null)
            {
                // "Give <目标> N random abilities from the following" 的受予者。
                // 不解析它，模式块就是空壳：选项正确却不知道该挂给谁。
                modeGrantTarget = TryExtractModeGrantTarget(sent);

                // 模式引导句若**没有自己的事件头**，它沿用的是本段前面那条能力。
                //   "Engage: Destroy this card. Select a Mode to activate. 1. ... 2. ..."
                // 第 1 句建了 currentAbility（OnEngage），第 2 句没重抄 "Engage:"，
                // 旧实现直接 hb.BuildTrigger() 生成 OnPlay —— 模式块因此
                // 被分裂成一条独立的入场曲能力，与启动动作脱节（10322210 / 10463210）。
                //
                // 注意必须读 _lastBuiltAbility 而不是 currentAbility：
                // currentAbility 要到本方法末尾（能力落盘处）才赋值，
                // 模式引导句在这一轮循环里看到它永远是 null。
                var prevAb = currentAbility ?? _lastBuiltAbility;

                // 两种情况都并入前一条能力：
                //  · 本句没有自己的事件头（沿用了前一句的时机）；
                //  · 本句虽有头，但事件与前一条**相同**——
                //    "Engage: Destroy this card. Select a Mode to activate."
                //    两句同属一个启动动作，hb 一路带着 Engage 头走到这一句。
                bool noOwnHead = !hb.EventExplicit && hb.Costs.Count == 0
                                 && (hb.Condition == null || hb.Condition is ConstBoolExpr);
                bool sameEvent = hb.EventExplicit && prevAb != null
                                 && prevAb.Trigger?.Event == hb.BuildTrigger().Event;
                modeAttachTo = ((noOwnHead || sameEvent) && prevAb != null
                                && prevAb.Trigger?.Event != CoreEvent.PassiveAura)
                    ? prevAb : null;

                modeTrigger = modeAttachTo?.Trigger ?? hb.BuildTrigger();
                modeCondition = AndCosts(hb.Condition, hb.Costs);
                modeCosts = hb.Costs;
                modeZone = zone;
                modePick = pick;
                modeKind = pickKind;
                continue;
            }

            // "Otherwise, <else>" 是独立成句的，它修饰的是上一句的条件分支。
            // 先把它认出来：解析内容后挂到上一条能力的条件节点 Else 上，
            // 而不是当成一条新动作（那样两条分支都无条件执行）。
            if (c.Current.IsWord("otherwise"))
            {
                c.Advance();
                if (c.Current.Kind == TokKind.Comma || c.Current.Kind == TokKind.Colon) c.Advance();
                var elseBody = c.IsAtEnd ? new List<EffectNode>() : _actions.ParseSequence(c);
                bool attached = false;

                // 情形 1：条件挂在**节点级**（句中 if）。
                if (currentAbility?.Body is List<EffectNode> prevBody)
                {
                    for (int k = prevBody.Count - 1; k >= 0; k--)
                    {
                        if (prevBody[k] is ConditionalNode cn)
                        {
                            prevBody[k] = new ConditionalNode
                            {
                                Condition = cn.Condition,
                                Then = cn.Then,
                                Else = elseBody,
                                If = cn.If,
                            };
                            attached = true;
                            break;
                        }
                    }
                }

                // 情形 2：条件挂在**能力级**（句首 if 被 PrefixParser 折成 Condition）。
                // 这才是最常见的写法：「Fanfare: If you're in Overflow, draw 2 cards.
                // Otherwise, draw a card.」—— 从句在句首，条件就成了能力属性。
                // 必须把整条能力改写成 Conditional(cond, Then=原Body, Else=elseBody)，
                // 并把能力级条件清空，否则 Then 与 Else 会同时在 cond 成立时执行。
                if (!attached && currentAbility != null
                    && currentAbility.Condition != null
                    && !(currentAbility.Condition is ConstBoolExpr)
                    && currentAbility.Body is List<EffectNode> srcBody)
                {
                    var wrapped = new List<EffectNode>
                    {
                        new ConditionalNode
                        {
                            Condition = currentAbility.Condition,
                            Then = new List<EffectNode>(srcBody),
                            Else = elseBody,
                        },
                    };
                    var rebuilt = new AbilityDef
                    {
                        Trigger = currentAbility.Trigger,
                        Condition = BoolExpr.True,
                        Costs = currentAbility.Costs,
                        Body = wrapped,
                    };
                    int at = result.IndexOf(currentAbility);
                    if (at >= 0) result[at] = rebuilt; else result.Add(rebuilt);
                    currentAbility = rebuilt;
                    _lastBuiltAbility = rebuilt;
                    attached = true;
                }

                // 实在没有可挂的条件：把 else 并进同一能力的 Body，至少不让它丢。
                if (!attached && currentAbility?.Body is List<EffectNode> pb2)
                    pb2.AddRange(elseBody);
                if (c.IsAtEnd) continue;
            }

            var body = _actions.ParseSequence(c);
            c.AssertEnd(Trunc(segment));

            // 取走「刚解析过 transform based on total cost」的标志。
            // 那句话本身不产出动作，但它是分档表的宿主：没有它，
            // 随后三档会各自独立落成 OnPlay 能力（". 1: A. 2: B. 3: C."），
            // 打出时三档同时判定，可能同时召唤多个随从。
            if (_actions.FuseTierByCost)
            {
                _actions.FuseTierByCost = false;
                _fuseTierByCost = true;
                if (body.Count == 0)
                {
                    // 建一条空的宿主能力，分档表会挂进它的 Body。
                    var hostTrig = hb.BuildTrigger();
                    var host = new AbilityDef
                    {
                        Trigger = hostTrig,
                        Condition = AndZone(zone, AndCosts(hb.Condition, hb.Costs), hostTrig.Event),
                        Costs = hb.Costs,
                        Body = new List<EffectNode>(),
                    };
                    result.Add(host);
                    currentAbility = host;
                    _lastBuiltAbility = host;
                    // 单独留引用：后面的档位句会把 currentAbility 覆盖掉，
                    // 而分档表必须挂回这条 host，不能挂到最后一个档位句上。
                    _fuseHostAbility = host;
                    continue;
                }
            }

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

            // ---- 带条件的后续句也要并进来（不是新的 AbilityDef）----
            //
            //   "Select another allied card on the field.
            //    If you selected one, destroy it and deal 2 damage ..."
            //   "Add a Fairy to your hand. Combo (3) - Give it Bane."
            //   "Select an enemy follower and deal it 3 damage.
            //    Rally (10) - Give it ..."
            //   "At the end of your turn, if this follower is unevolved, ...
            //    If this follower is evolved, ..."
            //
            // 这些第二句带的是**条件门控**（If / Combo / Rally），不是新的触发时机。
            // 若让它们各自成为一条 AbilityDef：
            //   · 同一张卡的不同 AbilityDef 各有独立 VmScope，
            //     第一句选中的 $selected 在第二条里是空的 —— destroy it 什么都破坏不了；
            //   · 第二句没有事件头，Trigger 会退化成默认的 OnPlay，
            //     超进化能力 / 回合结束能力直接变成入场曲。
            //
            // 所以必须并进当前能力的 Body，用 ConditionalNode 包住条件。
            bool isEnhanceHead = hb.Costs.Any(c => c.Kind == CostKind.UsePp);
            bool isInstead = hasInsteadWord;
            bool onlyGateCosts = hb.Costs.All(c =>
                c.Kind == CostKind.TurnPlayCount || c.Kind == CostKind.MatchPlayCount);

            if (!hb.EventExplicit
                && !isEnhanceHead
                && !isInstead
                && onlyGateCosts
                && currentAbility != null
                && currentAbility.Trigger != null
                && currentAbility.Trigger.Event != CoreEvent.PassiveAura)
            {
                // 若当前能力身上还挂着第一句的句首 If 条件，
                // 先把已有 Body 包进 ConditionalNode，再把 Condition 复位 ——
                // 否则第二句的条件会盖在整体上，两个 if 变成嵌套而非并列。
                //
                // AbilityDef.Condition 是 init-only，不能就地赋值，
                // 只能重建一条并把 result 里的旧引用换掉。
                // 判据必须与「当前段的基准进化域条件」对比，而不是简单看
                // 「不是 ConstBoolExpr」——
                //
                // 只要在 <ev> / <sev> 段内，第一句的 Condition 就已经是
                //     AndZone(zone, AndCosts(null, []))
                // 即 HasStateExpr(SuperEvolved, Self) 之类的进化域门槛。
                // 若把它当成「第一句自带的句首 If」下沉包裹，
                // 再把外层能力重建成 BoolExpr.True，整个进化域门槛就被洗掉了 ——
                // 「Super-Evolve: ...」会变成无条件触发。
                //
                // 只有当第一句**除了进化域本身之外**还额外挂着条件时，才需要下沉。
                var zoneBaseCond = AndZone(zone, BoolExpr.True);
                var prevCond = currentAbility.Condition;
                bool prevHasExtraCond = prevCond != null
                    && !(prevCond is ConstBoolExpr)
                    && AstCanonicalPrinter.PrintBoolExpr(prevCond) != AstCanonicalPrinter.PrintBoolExpr(zoneBaseCond);

                var bodyList = (List<EffectNode>)currentAbility.Body;
                if (prevHasExtraCond && bodyList.Count > 0)
                {
                    var wrapped = new List<EffectNode>
                    {
                        new ConditionalNode
                        {
                            Condition = prevCond,
                            Then = bodyList.ToList(),
                        },
                    };
                    bodyList.Clear();
                    bodyList.AddRange(wrapped);

                    var rebuilt = new AbilityDef
                    {
                        Trigger = currentAbility.Trigger,
                        // 保留进化域基准条件，绝不能写成 BoolExpr.True
                        Condition = zoneBaseCond,
                        Costs = currentAbility.Costs,
                        Body = currentAbility.Body,
                        HasFuse = currentAbility.HasFuse,
                    };
                    int at = result.IndexOf(currentAbility);
                    if (at >= 0) result[at] = rebuilt;
                    currentAbility = rebuilt;
                    _lastBuiltAbility = rebuilt;
                }

                var followCond = AndCosts(hb.Condition, hb.Costs);
                if (followCond == null || followCond is ConstBoolExpr)
                {
                    bodyList.AddRange(body);
                }
                else
                {
                    bodyList.Add(new ConditionalNode
                    {
                        Condition = followCond,
                        Then = body.ToList(),
                    });
                }
                continue;
            }

            // 无头独立段落要分两种时机：
            //   "Deal 4 damage to all enemy followers."   → OnPlay（动作句，打出时结算一次）
            //   "Can't be destroyed by abilities."        → PassiveAura（常驻修正）
            //   "Can attack 2 times per turn."            → PassiveAura（常驻修正）
            //
            // 判据是「这句是不是纯自指常驻修正」：无显式头、无条件、无代价、
            // body 全部是指向本卡槽位的 ModifyKeywordNode。动作句永不满足。
            // 不分的话，静态限制词会编成 OnPlay —— 只有从手牌打出才授予，
            // 召唤 / 复活 / 复制进场的同一张卡完全没有这个关键词。
            //
            // 封印光环（suppress_fanfare / suppress_enhance）必须是常驻：
            // 它的目标写的是 `self:board All where is Follower`，不是 $self。
            // 只认 $self 会让「我方随从的入场曲无法发动」被编成 OnPlay，
            // 纹章在纹章区永远不生效。
            static bool IsAuraGrant(EffectNode n)
            {
                if (n is not ModifyKeywordNode mk0 || mk0.Op != KeywordOp.Grant || mk0.Target == null)
                    return false;

                bool isSelfSlot = mk0.Target.SourceKind == TargetSourceKind.Slot
                                  && mk0.Target.Slot == TargetSpec.SlotSelf;
                if (isSelfSlot) return true;

                string kw = mk0.Keyword.Name ?? "";
                return kw.Equals("suppress_fanfare", StringComparison.OrdinalIgnoreCase)
                       || kw.Equals("suppress_enhance", StringComparison.OrdinalIgnoreCase)
                       // "Your opponent can't select anything other than this card
                       //  for abilities" —— 这是常驻的限制光环，不是入场曲。
                       // 编成 OnPlay 的话只有从手牌打出才生效，被召唤/复活进场时形同虚设。
                       || kw.Equals("targeting_restriction", StringComparison.OrdinalIgnoreCase);
            }

            bool selfOnlyRestriction =
                !hb.EventExplicit
                && hb.Costs.Count == 0
                && (hb.Condition == null || hb.Condition is ConstBoolExpr)
                && body.All(IsAuraGrant);

            // 无显式事件头的替代后续句（"At the end of your turn, restore 2."
            // 之后紧跟 "If super-evolved, restore 4 instead"）必须沿用前一句的 Trigger。
            // 否则它被默认赋成 OnPlay，InsteadWeaver.FindPartner 拿 OnPlay 去配 TurnEnd
            // 必然配不上 —— 既不合成 if/else，也不会继承 self:leader 这类目标。
            var mainTrig = selfOnlyRestriction
                ? new TriggerSpec { Event = CoreEvent.PassiveAura }
                : (!hb.EventExplicit && hasInsteadWord && currentAbility?.Trigger != null)
                    ? currentAbility.Trigger
                    : hb.BuildTrigger();
            var built = new AbilityDef
            {
                Trigger = mainTrig,
                Condition = AndZone(zone, AndCosts(hb.Condition, hb.Costs), mainTrig.Event),
                Costs = hb.Costs,
                Body = body,
            };
            result.Add(built);
            currentAbility = built;
            _lastBuiltAbility = built;
        }

        // 收尾：把剩余的模式选项落成 BranchModeNode
        FlushModeBlock(result, ref modeOptions, ref modeOptionIds, ref modeTrigger,
                       ref modeCondition, ref modeCosts, ref modeZone, ref modePick, ref modeKind,
                       modeDepth, modeGrantTarget, modeAttachTo);
        modeDepth.Clear();
        modeGrantTarget = null;
        modeAttachTo = null;

        // 收尾：融合变身表并进它所修饰的那条能力
        // 档位表常被换行切成独立段落（"…transform it…\n1: A\n2: B\n3 or more: C"），
        // 本段没有能力时回落到上一段落成的能力。
        // 分档判据由那句正文的措辞决定（total cost → fuse_cost，否则 fuse_count）。
        // 只在没被 ParseTransform 显式置位时，才用文本兜底判定。
        // 否则 host 能力（Body 为空、早已不含那句 transform）会把标志冲掉，
        // 分档表又退回 fuse_count + Spawn。
        if (!_fuseTierByCost) _fuseTierByCost = MentionsTotalCost(_lastBuiltAbility ?? currentAbility);
        FlushFuseTable(result, _fuseHostAbility ?? currentAbility ?? _lastBuiltAbility);
        _fuseHostAbility = null;
        _fuseTierByCost = false;
        _fuseHeader = null;

        // 三大编织器（MetaMode / Instead / Enhance）**不在这里跑**。
        //
        // 它们在 CompileSegment 里只能看到当前这一段：
        //   "Fanfare: ..." / "Enhance (7): ..." / "Enhance (8): ..."
        // 被 Preprocess 按换行切成 3 个独立 Segment，分别进 3 次本方法。
        // 在这样的粒度上跑编织：
        //   · 多档爆能看到不彼此的兄弟段，编出 3 条平行能力；
        //   · EnhanceWeaver 先把 instead 句包进两层 ConditionalNode，
        //     等整卡级 InsteadWeaver 再跑时 marker 已经看不见了；
        //   · 单段只剩 1 条能力时，InsteadWeaver 还会把 marker 当场清掉。
        // 它们统一在 CompileCore 里对整卡 buffer 跑一次（顺序 MetaMode → Instead → Enhance）。

        // 段内累积状态不得跨段：漏清会让下一段的档位并进上一张卡的变身表。
        _fuseTiers.Clear();

        // _lastBuiltAbility **不能在此清空**。
        // 它的用途就是跨段落记忆上一条落盘的能力：
        //   <ev> Evolve: Summon 2 copies of Ghost. </ev>
        //   <sev> Super-Evolve: Give them Drain. </sev>
        // 两段各走一次 CompileSegment，第二段开头靠它才能识别
        // 「上一段是 OnEvolve」从而不清槽位。
        // 整张卡的复位在 CompileCore 入口处做（那才是正确的生命周期起点）。

        // 无参关键词提升：Target=self / Grant / 无 Value 的 ModifyKeywordNode
        // 本质就是**卡牌定义属性**，与裸词 Ward 同构。
        //   Can't be played.  -> InnateKeywords=[cannot_be_played]
        //   Can attack 2 times. -> 带 Value，留在能力里（InnateKeywords 存不下数值）
        // 不提升会让「Ward」与「Can't be played」明明同类却编码不同。
        PromoteParameterlessKeywords(result, innateSink);

        return result;
    }

    /// <summary>
    /// 把无参的「给自身授予关键词」节点提升成卡牌定义属性。
    ///
    /// 只认 Target=self、Op=Grant、Value 为空这三个条件同时成立的节点 ——
    /// 带 Value 的（max_attacks_per_turn(2) / damage_cap(5)）必须留在能力里，
    /// 因为 InnateKeywords 是 List&lt;SymbolId&gt;，存不下数值。
    /// 带时限或条件的也不提升：它们是可求值效果，不是定义属性。
    /// </summary>
    private static void PromoteParameterlessKeywords(List<AbilityDef> abilities,
                                                     List<Core.SymbolId<Core.KeywordTag>> sink)
    {
        if (abilities == null || sink == null) return;
        for (int i = abilities.Count - 1; i >= 0; i--)
        {
            var ab = abilities[i];
            if (ab?.Trigger?.Event != CoreEvent.PassiveAura) continue;
            if (ab.Costs != null && ab.Costs.Count > 0) continue;
            if (ab.Condition is not null and not ConstBoolExpr) continue;
            if (ab.Body == null || ab.Body.Count != 1) continue;
            if (ab.Body[0] is not ModifyKeywordNode { Op: KeywordOp.Grant } mk) continue;
            if (mk.Value != null) continue;
            if (mk.Target == null || mk.Target.SourceKind != TargetSourceKind.Slot
                || mk.Target.Slot != TargetSpec.SlotSelf) continue;
            if (!mk.Keyword.IsValid) continue;
            if (!sink.Contains(mk.Keyword)) sink.Add(mk.Keyword);
            abilities.RemoveAt(i);   // 整条能力移除：关键词已提升为定义属性
        }
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
    /// <summary>
    /// "Give <目标> N random abilities from the following" 里的受予者。
    /// 只对以 give 引导的模式句有意义；其余返回 null（模式块没有受予者）。
    /// </summary>
    private TargetSpec TryExtractModeGrantTarget(IReadOnlyList<Token> sent)
    {
        int gi = -1;
        for (int k = 0; k < sent.Count; k++)
            if (sent[k].IsWord("give")) { gi = k; break; }
        if (gi < 0) return null;

        var rest = new List<Token>();
        for (int k = gi + 1; k < sent.Count; k++)
        {
            if (sent[k].Kind == TokKind.Number) break;
            if (sent[k].IsAnyWord("random", "abilities", "ability", "from", "the", "following")) break;
            rest.Add(sent[k]);
        }
        if (rest.Count == 0) return null;
        return _target.TryParse(new TokenCursor(rest));
    }

    /// <summary>模式块「Give <目标> N random abilities」的受予者。</summary>
    private TargetSpec modeGrantTarget;

    private NumExpr ExtractModePick(IReadOnlyList<Token> sent, out ModeDispatchKind kind)
    {
        kind = ModeDispatchKind.Choose;
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
        NumExpr pickExpr = null;

        if (i < sent.Count && sent[i].Kind == TokKind.Number)
        {
            n = (int)sent[i].Value;
            sawDigit = true;
            i++;
        }
        else if (i < sent.Count && sent[i].IsAnyWord("x", "y", "z"))
        {
            // "Activate Y random abilities from the following" —— Y 是卡面
            // 前面声明过的变量（如「Y 为此卡费用」），必须按变量求值，
            // 否则会退化成恒发 1 个。
            var vn = sent[i].Text.ToUpperInvariant();
            if (_variableBindings.TryGetValue(vn, out var bound)) pickExpr = bound;
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

        // 引导句决定**选取方式**，四种互不相同，必须逐一分清：
        //   "hasn't been activated yet" 是**正交**维度（跨回合的「未激活」状态），
        //   所以它与 random / sequence 再组合出两种：
        //     random + 未激活  -> RandomUnused    （Slaus：每回合随机一个没激活过的）
        //     sequence + 未激活 -> SequenceUnused  （Noeinhowell：按序取没激活过的下一个）
        // 把 "in sequence" 当成 Choose 是本次要修的主缺陷：
        // 卡面明确「按顺序自动全部发动」，编译成 Choose 会变成弹窗让玩家手选。
        bool sawRandom = false, sawSequence = false, sawUnused = false;
        for (int k = 0; k < sent.Count; k++)
        {
            if (sent[k].IsWord("random")) sawRandom = true;
            if (sent[k].IsWord("sequence")) sawSequence = true;
            // "hasn't been activated yet" —— 两个词分开出现，任一缺失都不算
            if (sent[k].IsWord("hasn't") || sent[k].IsWord("hasnt")) sawUnused = true;
        }
        if (sawUnused && !sent.Any(t => t.IsWord("activated"))) sawUnused = false;

        kind = sawUnused
            ? (sawSequence ? ModeDispatchKind.SequenceUnused : ModeDispatchKind.RandomUnused)
            : sawSequence ? ModeDispatchKind.Sequence
            : sawRandom ? ModeDispatchKind.Random
            : ModeDispatchKind.Choose;

        // 有变量绑定（如 Y = 此卡费用）就用它，否则退到常数。
        if (pickExpr != null) return pickExpr;
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
        ref ModeDispatchKind kind,
        List<int> depths = null,
        TargetSpec grantTarget = null,
        AbilityDef attachTo = null)
    {
        if (options.Count == 0) return;

        // P3：按深度把选项组装成嵌套模式树。
        // 从最深层开始，把同层选项收成一个子 BranchModeNode，
        // 挂到上一层最后一个选项的末尾，逐层上收。
        if (depths != null && depths.Count == options.Count)
        {
            var opts = new List<IReadOnlyList<EffectNode>>(options);
            var ds = new List<int>(depths);
            int maxD = 0;
            foreach (var x in ds) if (x > maxD) maxD = x;

            for (int d = maxD; d >= 1; d--)
            {
                var childIdx = new List<int>();
                for (int q = 0; q < ds.Count; q++) if (ds[q] == d) childIdx.Add(q);
                if (childIdx.Count == 0) continue;

                int parentIdx = -1;
                for (int q = childIdx[0] - 1; q >= 0; q--)
                    if (ds[q] < d) { parentIdx = q; break; }
                if (parentIdx < 0) continue;

                var childOpts = childIdx.Select(q => opts[q]).ToList();
                var merged = new List<EffectNode>(opts[parentIdx]);
                merged.Add(new BranchModeNode
                {
                    Kind = ModeDispatchKind.Choose,
                    PickCount = NumExpr.Const(1),
                    Options = childOpts,
                });
                opts[parentIdx] = merged;

                for (int q = childIdx.Count - 1; q >= 0; q--)
                {
                    opts.RemoveAt(childIdx[q]);
                    ds.RemoveAt(childIdx[q]);
                }
            }
            options = opts;
        }

        var branch = new BranchModeNode
        {
            Kind = kind,
            PickCount = pick,
            Options = options,
            // "Give <目标> N random abilities from the following" 的受予者。
            // 为 null 表示「模式是玩家自己选的」，与受予者无关（Select a Mode）。
            GrantTarget = grantTarget,
        };

        // 附属于本段前面那条能力时，直接追加进它的 Body ——
        // "Engage: Destroy this card. Select a Mode to activate. 1. ... 2. ..."
        // 是一个启动动作内的模式选择，不是两条独立能力。
        if (attachTo?.Body is List<EffectNode> hostBody)
        {
            hostBody.Add(branch);
        }
        else
        {
            var modeTrig = trigger ?? new TriggerSpec { Event = CoreEvent.OnPlay };
            result.Add(new AbilityDef
            {
                Trigger = modeTrig,
                Condition = AndZone(zone, condition, modeTrig.Event),
                Costs = costs,
                Body = new List<EffectNode> { branch },
            });
        }

        kind = ModeDispatchKind.Choose;
        options = new List<IReadOnlyList<EffectNode>>();
        optionIds = new List<int>();
        // 深度表是值传参，清空不到调用方的 modeDepth，
        // 所以调用方要在调用后自行 Clear()。
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
                Right = NumExpr.Const(_lex?.Schema?.Rules?.OverflowThreshold ?? 7),
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

        // "This faith's value starts at 0." —— 信仰的初始值声明。
        // 它不是动作，而是资源计数器的初值，必须在编译期登记到 _initialCounters。
        // 不识别的话整句会被当成动作解析，停在 [This] 报「无法推进」。
        if (sent.Count >= 5
            && sent[0].IsWord("this")
            && sent[1].IsAnyWord("faith's", "crest's")
            && sent[2].IsWord("value")
            && sent[3].IsWord("starts"))
            return true;

        return false;
    }

    /// <summary>是否是融合分支表的开头（1: / 1 or more:）。</summary>
    private static bool IsFuseSwitchTableHead(TokenCursor c)
    {
        if (c.Current.Kind != TokKind.Number) return false;
        var nx = c.Peek(1);
        return nx.Kind == TokKind.Colon || nx.IsAnyWord("or");
    }

    /// <summary>当前段里累积的融合变身表档位（跨句累积，段末一次性落盘）。</summary>
    private readonly List<(int lo, int hi, string cardName, int cardId)> _fuseTiers = new();

    /// <summary>
    /// 分档表前面那句是否声明「按融合材料的**总费用**分档」。
    /// 官方只有 Striker / Fortifier Artifact 这样写；其余分档表（如
    /// 「Summon a Fused follower. 1: …」）分的是融合**次数**。
    /// 判据来自 SourceText，因为这条信息只存在于原始句子里，AST 上没有痕迹。
    /// </summary>
    private static bool MentionsTotalCost(AbilityDef ab)
    {
        if (ab == null) return false;
        foreach (var n in ab.Body ?? Array.Empty<EffectNode>())
        {
            var text = AstCanonicalPrinter.PrintEffectNode(n) ?? "";
            if (text.Contains("total cost", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>上一次落盘的能力。跨段落延续，用于把被换行切开的后续表接回原句。</summary>
    private AbilityDef _lastBuiltAbility;

    /// <summary>变身表紧跟在能力头之后时，落盘用的头（"Fanfare: 1: A …"）。</summary>
    private AbilityHeaderBuilder _fuseHeader;
    private SectionZone _fuseZone = SectionZone.Normal;

    /// <summary>
    /// 本段的分档判据是否按「材料总费用」——由分档表前面那句的措辞决定：
    ///   「...based on the total cost of the cards fused」→ true（fuse_cost）
    ///   其余（「Summon a Fused follower.」等）            → false（fuse_count）
    /// </summary>
    private bool _fuseTierByCost;

    /// <summary>分档表要挂回去的那条 host 能力（由 transform 句建立）。</summary>
    private AbilityDef _fuseHostAbility;

    /// <summary>
    /// 解析 "1: A 2: B 3 or more: C" 变身表。
    ///
    /// 这是**按融合次数分档的单次选择**，不是模式抉择：
    /// 旧实现落成 BranchModeNode(Sequence, pick=档数)，VM 会依次把每一档的
    /// 随从都召唤出来 —— 一次融合召出三个随从。
    ///
    /// 分档判据是 fuse_count（TurnRunner.FuseCards 每次融合 +1）：
    ///   "1:" 覆盖 lo..hi，"N or more:" 是开区间 lo..∞。
    /// 表达成按档位降序的 ConditionalNode 链，第一个命中的档位执行，其余落 Else。
    ///
    /// 各档在卡面上是**三个独立句子**（"1: A. 2: B. 3 or more: C."），
    /// 逐句落盘会得到三条各自独立触发的能力，所以这里只登记档位，
    /// 由段末的 FlushFuseTable 合成一条。
    /// </summary>
    private void ParseFuseSwitchTable(TokenCursor c)
    {
        while (!c.IsAtEnd && c.Current.Kind == TokKind.Number)
        {
            int lo = c.Current.Value;
            c.Advance();                       // 序号
            bool orMore = c.MatchPhrase("or", "more");
            c.TryMatch(TokKind.Colon);

            string cardName = "";
            int cardId = 0;
            if (c.Current.Kind == TokKind.CardName)
            {
                cardName = c.Current.Text;
                cardId = c.Current.CardId;
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

            if (string.IsNullOrWhiteSpace(cardName)) continue;
            // 卡号解析不了也要留住名字：SpawnCardNode 只有 CardId 能真正生成实体，
            // 但名字是审查与报错唯一的线索。
            if (cardId == 0) _lex.TryGetCardId(cardName, out cardId);
            _fuseTiers.Add((lo, orMore ? int.MaxValue : lo, cardName, cardId));
        }
    }

    /// <summary>
    /// 把本段累积的融合档位合成**一条**按 fuse_count 分档的能力。
    /// 从最高档往前折成 ConditionalNode 链，Else 指向下一档 ——
    /// 命中的那一档执行完就落到链尾的空 Else，其余档位不会执行。
    /// </summary>
    private void FlushFuseTable(List<AbilityDef> result, AbilityDef owner)
    {
        if (_fuseTiers.Count == 0) return;

        // 分档判据取决于卡面用的是哪种句式，两种都在官方卡池里出现：
        //
        //   「transform it based on the total cost of the cards fused」
        //     → 判据 **fuse_cost**（材料总费用），动作是 Transform
        //     → Striker Artifact：一次喂 3 张 1 费材料直接进「3 or more」档
        //
        //   「Summon a Fused follower. 1: A. 2: B. 3 or more: C.」
        //     → 判据 **fuse_count**（融合次数），动作是 Spawn
        //
        // 判据只能来自前一句的措辞，不能一律取一个：
        // 用 fuse_count 分 cost 表会让「一次喂 3 张」永远到不了第三档；
        // 用 fuse_cost 分 count 表会让「融合 2 次」在不同费用下跳错档。
        bool byCost = _fuseTierByCost;
        var fuseCount = NumExpr.Ctr(_lex.Counter(byCost ? "fuse_cost" : "fuse_count"));

        IReadOnlyList<EffectNode> chain = Array.Empty<EffectNode>();
        for (int i = _fuseTiers.Count - 1; i >= 0; i--)
        {
            var (lo, hi, card, cardId) = _fuseTiers[i];
            var cond = hi == int.MaxValue
                ? (BoolExpr)new CompareExpr { Left = fuseCount, Op = CompareOp.Ge, Right = NumExpr.Const(lo) }
                : BoolExpr.And(
                    new CompareExpr { Left = fuseCount, Op = CompareOp.Ge, Right = NumExpr.Const(lo) },
                    new CompareExpr { Left = fuseCount, Op = CompareOp.Le, Right = NumExpr.Const(hi) });
            chain = new List<EffectNode>
            {
                new ConditionalNode
                {
                    Condition = cond,
                    // 动作由同一个 byCost 决定：
                    //   total cost 句式 → **Transform**（这张卡自己变成那一档，不召唤）
                    //   其余句式        → **Spawn**（召唤那一档的随从上场）
                    //
                    // 早期一律 Spawn，于是 Striker Artifact 融合后场上多出随从、
                    // 而它自己没变 —— 卡面写的是 transform it。
                    Then = new List<EffectNode>
                    {
                        byCost
                            ? (EffectNode)new TransformNode
                            {
                                Target = TargetSpec.Self,
                                IntoCardId = cardId,
                                IntoCardName = card,
                            }
                            : new SpawnCardNode
                            {
                                SourceKind = CardSourceKind.CardId,
                                // CardId 必须一起带上：DoSpawn 只按 CardId 生成实体，
                                // 只写 CardName 的节点永远产出空实体。
                                CardId = cardId,
                                CardName = card,
                                ToZone = ZoneRef.Board,
                                ToOwner = ZoneScope.Self,
                                Count = NumExpr.Const(1),
                            },
                    },
                    Else = chain,
                },
            };
        }

        _fuseTiers.Clear();

        // 档位表是**前一句**的后续（"When you Fuse to this card, transform it …
        // 1: A 2: B 3 or more: C"），触发时机必须沿用那一句：
        // 独立落成 OnPlay 会让变身表在打出时凭空触发，而不是融合时。
        if (owner != null && owner.Body is List<EffectNode> body)
        {
            body.AddRange(chain);
            return;
        }

        // 没有前句可依附时，"Fanfare: 1: A …" 这类由能力头直接引导的表
        // 要沿用那个头；完全裸的表才退回 OnPlay。
        var trigger = _fuseHeader?.BuildTrigger() ?? new TriggerSpec { Event = CoreEvent.OnPlay };
        result.Add(new AbilityDef
        {
            Trigger = trigger,
            Condition = _fuseHeader != null ? AndZone(_fuseZone, _fuseHeader.Condition) : BoolExpr.True,
            Costs = _fuseHeader != null ? _fuseHeader.Costs : Array.Empty<CostSpec>(),
            Body = chain,
        });
        _lastBuiltAbility = result[^1];
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

        // "X is a random number between 2 and 10" / "X is a number from 2 to 10"
        //
        // 必须绑成**同作用域锁定的随机数**：卡面会把 X 用在费用、攻击、生命
        // 三处，每次求值都重掷骰的话三者会各不相同。
        {
            bool hasRandom = joined.Contains("random") || joined.Contains("determined randomly");
            bool hasRange = joined.Contains(" between ") || joined.Contains(" from ");
            if (hasRandom && hasRange && sent.Count > 0 && sent[0].IsAnyWord("x", "y", "z"))
            {
                var nums = sent.Where(t => t.Kind == TokKind.Number).ToList();
                if (nums.Count >= 2)
                {
                    var vn = sent[0].Text.ToUpperInvariant();
                    int rollId = vn == "X" ? 0 : vn == "Y" ? 1 : 2;
                    _variableBindings[vn] = NumExpr.RandRange(
                        nums[0].Value, nums[1].Value, rollId);
                    return;
                }
            }
        }

        // "X is the enemy leader's max defense minus its defense."
        //
        // 必须绑成「生命上限 − 当前生命」的差值表达式。
        // 不绑的话变量量词会退化成 ctr:X（读一个不存在的计数器，恒为 0），
        // 这张卡的 X 伤害永远是 0。
        if (sent.Count > 2 && sent[0].IsAnyWord("x", "y", "z")
            && joined.Contains("max defense"))
        {
            bool isEnemy = joined.Contains("enemy leader");
            var leader = isEnemy ? TargetSpec.OpponentLeader : TargetSpec.SelfLeader;
            var vn2 = sent[0].Text.ToUpperInvariant();

            if (joined.Contains("minus"))
            {
                _variableBindings[vn2] = NumExpr.Sub(
                    NumExpr.Prop(StatKey.MaxLife, leader),
                    NumExpr.Prop(StatKey.Life, leader));
                return;
            }
            // 只写了 max defense 时就是生命上限本身
            _variableBindings[vn2] = NumExpr.Prop(StatKey.MaxLife, leader);
            return;
        }

        // "X, Y, and Z are determined randomly and add up to your faith's value"
        if (joined.Contains("add up to") && joined.Contains("faith"))
        {
            var ctr = _lex.Counter("faith");
            _variableBindings["X"] = NumExpr.SplitOf(ctr, 0);
            _variableBindings["Y"] = NumExpr.SplitOf(ctr, 1);
            _variableBindings["Z"] = NumExpr.SplitOf(ctr, 2);
            return;
        }

        // "This faith's value starts at N" —— 信仰初始值，直接写进 faith 计数器。
        if (sent.Count >= 5 && sent[0].IsWord("this") && sent[2].IsWord("value"))
        {
            string ctrName = sent[1].IsAnyWord("crest's") ? "crest" : "faith";
            for (int k = 3; k < sent.Count; k++)
            {
                if (sent[k].Kind != TokKind.Number) continue;
                _initialCounters[ctrName] = sent[k].Value;
                return;
            }
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
    /// <summary>
    /// 把长句压成可读的计数器名。
    /// 它不负责语义（语义已由上面的分支处理），
    /// 只保证同一句话每次得到同一个名字，且不会把整句拼进去。
    /// </summary>
    private static string CondenseCounterName(string joined)
    {
        var words = joined.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w is not ("the" or "a" or "an" or "of" or "you" or "your" or "have" or "has"
                                  or "number" or "is" or "are" or "and" or "or" or "in" or "on"
                                  or "this" or "that" or "each" or "total" or "all"))
            .Take(4);
        var name = string.Join("_", words);
        return string.IsNullOrEmpty(name) ? "unnamed" : name;
    }

    /// <summary>在 BuildVariableExpr 的游标上解析序数词（first/1st/…）。</summary>
    private static int ParseOrdinalWordInExpr(TokenCursor c)
    {
        if (c.IsAtEnd) return 0;
        string t = (c.Current.Text ?? "").ToLowerInvariant();
        int n = t switch
        {
            "first" or "1st" => 1, "second" or "2nd" => 2, "third" or "3rd" => 3,
            "fourth" or "4th" => 4, "fifth" or "5th" => 5, "sixth" or "6th" => 6,
            "seventh" or "7th" => 7, "eighth" or "8th" => 8, "ninth" or "9th" => 9,
            _ => 0,
        };
        if (n > 0) c.Advance();
        return n;
    }

    private NumExpr BuildVariableExpr(IReadOnlyList<Token> rest)
    {
        if (rest.Count == 0) return NumExpr.Const(0);
        var c = new TokenCursor(rest);
        var joined = string.Join(" ", rest.Select(t => t.Text.ToLowerInvariant()));

        // "the first/second/Nth selected follower's attack/defense/cost"
        // 多目标独立交互卡要按序数取属性（$choose1 / $choose2 …）。
        // 注意分词："follower's" 是一个整体 token，不能按 follower + 's 两段匹配。
        {
            int ordSave = c.Position;
            if (c.MatchWord("the"))
            {
                int ord = ParseOrdinalWordInExpr(c);
                if (ord > 0 && c.MatchWord("selected"))
                {
                    if (!c.IsAtEnd && c.Current.Kind == TokKind.Word)
                    {
                        var w0 = c.Current.Text.ToLowerInvariant();
                        var bare = w0.EndsWith("'s") ? w0.Substring(0, w0.Length - 2) : w0;
                        if (bare is "follower" or "card" or "amulet" or "target") c.Advance();
                    }
                    c.TryMatchAnyWord("s");
                    if (!c.IsAtEnd)
                    {
                        var w1 = c.Current.Text.ToLowerInvariant();
                        var statKey = w1.StartsWith("attack") ? StatKey.Attack
                            : (w1.StartsWith("defense") || w1.StartsWith("life")) ? StatKey.Life
                            : w1.StartsWith("cost") ? StatKey.Cost
                            : (StatKey?)null;
                        if (statKey.HasValue)
                        {
                            c.AdvanceToClauseBoundary();
                            return NumExpr.Prop(statKey.Value, TargetSpec.ChooseSlot(ord));
                        }
                    }
                }
            }
            c.Seek(ordSave);
        }

        // "the number of <TargetNP>"
        if (c.MatchPhrase("the", "number", "of"))
        {
            // "X is the number of amulets destroyed by this ability"（白骨圣堂之主）
            // "X is the number of cards you banished"（破灭屠戮者）
            // "X is the number of cards you discarded"（莱特欧芙）
            //
            // 全部读 **本次动作** 实际移动的张数，由 DoMoveZone 写入。
            // 不接这几条的话，X 会退回默认的「我方场上不同名卡数」，伤害完全错。
            {
                int ap = c.Position;
                bool sawThisAbility = false, sawBanish = false, sawDiscard = false, sawDestroy = false;
                while (!c.IsAtEnd)
                {
                    if (c.Current.Kind == TokKind.Period) break;
                    if (c.Current.IsAnyWord("banished", "banish")) sawBanish = true;
                    if (c.Current.IsAnyWord("discarded", "discard")) sawDiscard = true;
                    if (c.Current.IsAnyWord("destroyed", "destroy")) sawDestroy = true;
                    if (c.Current.IsAnyWord("this")) sawThisAbility = true;
                    c.Advance();
                }
                if (sawBanish || sawDiscard || (sawDestroy && sawThisAbility))
                {
                    c.AdvanceToClauseBoundary();
                    string key = sawBanish ? "cards_banished_by_ability"
                               : sawDiscard ? "cards_discarded_by_ability"
                               : "cards_destroyed_by_ability";
                    return NumExpr.Ctr(_lex.Counter(key));
                }
                c.Seek(ap);
            }

            // "X is the number of differently named cards Fused to this card"
            //   → 实体级 fuse_kinds（不同名材料的种类数）
            // "X is the number of cards Fused to this card"
            //   → fuse_count（融合次数）
            //
            // 不接这条的话，TargetParser 吃完 "differently named cards" 就停下，
            // 把 "Fused to this card" 丢在流里，X 退回默认的「我方场上不同名卡数」——
            // 数据源整个错了（卡面说的是**这张卡**吃进去的材料）。
            {
                int fp = c.Position;
                bool sawFused = false;
                bool sawDiffNamed = false;
                while (!c.IsAtEnd)
                {
                    if (c.Current.IsAnyWord("fused", "fuse")) { sawFused = true; break; }
                    if (c.Current.IsAnyWord("differently")) sawDiffNamed = true;
                    if (c.Current.Kind == TokKind.Period) break;
                    c.Advance();
                }
                if (sawFused)
                {
                    c.AdvanceToClauseBoundary();
                    return NumExpr.Ctr(_lex.Counter(sawDiffNamed ? "fuse_kinds" : "fuse_count"));
                }
                c.Seek(fp);
            }

            // 历史池计数（"... destroyed this match" / "... entered the field this match"）
            // **不在这里特判**：那些限定词由 TargetParser 正规解析，
            // 兵种 / 卡种 / 敌我归属 / differently named 一个都不能丢。
            // 曾用 StringBuilder 把剩余整句拼成字符串再 Contains 硬吞，
            // 结果是 "allied Shikigami followers destroyed this match" 里的
            // Shikigami 与 "enemy ... this turn" 里的 enemy 全被吃掉。

            // "X is the number of cards you returned [to your hand this turn]"
            // TargetParser 吃完 "cards" 就停下，剩下的 "you returned ..." 无人消费，
            // 结果 X 被当成「我方场上卡牌数量」—— 语义完全错位。
            // 这类「本回合回到某区域的卡数」引擎没有独立计数器，但两条区域转移
            // （回手 / 弃牌 / 放逐）都已经打通，用 CountOverHistory：
            //   · 弃牌 = 进墓地，DestroyedThisTurn 里查不到弃牌，所以仍走 OnDiscard 计数器
            //   · 回手 / 放逐同理由 MoveZone 完成，落在历史池之外
            // 本任务只处理点名的那一句：回手。
            {
                int rv = c.Position;
                bool sawCards = c.MatchAnyWord("cards", "card");
                bool sawReturned = sawCards && (c.MatchPhrase("you", "returned")
                                                || c.MatchPhrase("returned"));
                if (sawReturned)
                {
                    c.AdvanceToClauseBoundary();
                    return NumExpr.Ctr(_lex.Counter("cards_returned_to_hand"));
                }
                c.Seek(rv);
            }
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
                    c.AdvanceToClauseBoundary();
                    return NumExpr.Ctr(_lex.Counter(resAlias));
                }
            }
            c.Seek(saved);
            var tgt = _target.TryParse(c);
            // 必须归一成集合过滤器：TargetParser 见到冠词 a/an/other 会设成
            // Choose(1)，那是「弹窗手选一个」的语义，用来计数会恒等于 1。
            //   "X is the number of other allied followers" -> 整个集合的大小
            if (tgt != null)
            {
                var leftCount = NumExpr.CountOf(tgt.AsPoolFilter());
                // "X is the number of enemy followers on the field **minus** the number
                //  of allied followers on the field"（10811110 马龙）。
                // 不接这条时后半段被整段丢弃，X 只剩左半边的计数。
                if (c.MatchWord("minus"))
                {
                    var right = BuildVariableExpr(c.Rest());
                    if (right != null) return NumExpr.Sub(leftCount, right);
                }
                if (c.MatchWord("plus"))
                {
                    var right = BuildVariableExpr(c.Rest());
                    if (right != null) return NumExpr.Add(leftCount, right);
                }
                return leftCount;
            }
            // 还是不行就把剩余词当资源名（如 "cards in your hand"）
            c.Seek(saved);
            c.AdvanceToClauseBoundary();
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
            // "X is the cost of the card you drew"（10541310 波摇花的裁决）：
            // 这里的 "the card" 不是场上某张卡，而是**刚抽到的那张**（$created）。
            // 交给 TargetParser 会解析成 any:board All，X 变成场上随机一格费用。
            if (c.MatchPhrase("the", "card", "you", "drew")
                || c.MatchPhrase("the", "follower", "you", "drew")
                || c.MatchPhrase("the", "card", "drawn"))
                return NumExpr.Prop(StatKey.Cost, TargetSpec.Created);

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
            // 原文写的是 "total **base** attack/defense"，取印刷值不是当前值。
            var statKey = idf > ia ? StatKey.BaseLife : StatKey.BaseAttack;

            // 目标池优先交给 TargetParser 从 "of" 之后的那一段解析出来：
            //   "... of allied Shikigami followers destroyed this turn"
            // 它自带 zone=cemetery / history=DestroyedThisTurn / is Follower，
            // 比在这里手工拼 TargetSpec 可靠得多。
            TargetSpec filt = null;
            bool ofParsed = false;
            {
                int ofIdx = -1;
                for (int k = 0; k < rest.Count; k++)
                    if (rest[k].IsWord("of")) { ofIdx = k; break; }
                if (ofIdx >= 0 && ofIdx + 1 < rest.Count)
                {
                    var subCur = new TokenCursor(rest.Skip(ofIdx + 1).ToList());
                    try { filt = _target.TryParse(subCur)?.AsPoolFilter(); }
                    catch { filt = null; }
                    ofParsed = filt != null;
                }
            }
            if (filt == null) filt = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Self);

            // "... destroyed this turn" 的数据源是**历史池**，不是当前战场。
            // 不认这一条的话，「本回合被破坏的式神之基础攻防合计」
            // 会变成「现在场上的式神之当前攻防合计」（90034120）。
            System.Func<TargetSpec, ZoneRef, TargetSpec> withZone = (t0, z) => new TargetSpec
            {
                SourceKind = t0.SourceKind, Owner = t0.Owner, Zone = z, Slot = t0.Slot,
                CardId = t0.CardId, CardName = t0.CardName, Where = t0.Where,
                Sort = t0.Sort, SortDescending = t0.SortDescending, TopN = t0.TopN,
                DistinctByName = t0.DistinctByName, DuplicatesOnly = t0.DuplicatesOnly,
                Mode = t0.Mode, Count = t0.Count, History = t0.History,
                IncludeLeader = t0.IncludeLeader, OmittedFields = t0.OmittedFields,
            };
            System.Func<TargetSpec, HistoryScope, TargetSpec> withHist = (t0, h) => new TargetSpec
            {
                SourceKind = t0.SourceKind, Owner = t0.Owner, Zone = t0.Zone, Slot = t0.Slot,
                CardId = t0.CardId, CardName = t0.CardName, Where = t0.Where,
                Sort = t0.Sort, SortDescending = t0.SortDescending, TopN = t0.TopN,
                DistinctByName = t0.DistinctByName, DuplicatesOnly = t0.DuplicatesOnly,
                Mode = t0.Mode, Count = t0.Count, History = h,
                IncludeLeader = t0.IncludeLeader, OmittedFields = t0.OmittedFields,
            };
            if (joined.Contains("destroyed this turn"))
                filt = withHist(filt, HistoryScope.DestroyedThisTurn);
            else if (joined.Contains("destroyed this match"))
                filt = withHist(filt, HistoryScope.DestroyedThisMatch);
            // 以下两处手工补正**只在 TargetParser 没解析出目标池时**才做，
            // 否则会和它已解析出的结果叠成「Shikigami and Shikigami」或重复 zone。
            if (ofParsed == false)
            {
                // 「... destroyed this turn」统计的是**已离场**的卡，区必须是墓地。
                // 只把 History 改成 DestroyedThisTurn 而 zone 仍是 Board，
                // 在引用池里筛不到任何东西（已破坏的实体 Zone 早就是 Cemetery）。
                if (joined.Contains("destroyed")) filt = withZone(filt, ZoneRef.Cemetery);

                // "... Shikigami followers" 里的 followers 是名词，不是可选过滤 ——
                // 不补 is Follower 会把同兵种的非随从（护符/法术）也算进合计。
                if (joined.Contains("follower"))
                    filt = filt.WithWhere(new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item });

                foreach (var tr in new[] { "Shikigami", "Artifact", "Golem", "Marine", "Puppetry", "Departed", "Officer", "Pixie", "Clue", "Loot" })
                {
                    if (!joined.Contains(tr.ToLowerInvariant())) continue;
                    filt = filt.WithWhere(new HasTraitExpr { Trait = _lex.Trait(tr), Of = TargetSpec.Item });
                    break;
                }
            }
            return NumExpr.SumOf(filt, statKey);
        }

        // 资源计数器：「X is the number of earth sigils you have」这类不能把整句当计数器名。
        // 之前会生成 the_number_of_earth_sigils_you_have 这种伪造名，
        // 它会变成独立的原子，把原子池撑大、把覆盖集带偏。
        {
            string resKey = null;
            if (joined.Contains("earth sigil")) resKey = "earth_sigil";
            else if (joined.Contains("crest")) resKey = "crest";
            else if (joined.Contains("faith")) resKey = "faith";
            else if (joined.Contains("clue")) resKey = "clues";
            else if (joined.Contains("shadow")) resKey = "shadow";
            else if (joined.Contains("max play point")) resKey = "max_pp";
            else if (joined.Contains("play point")) resKey = "pp";
            else if (joined.Contains("evolution point")) resKey = "ep";
            else if (joined.Contains("super evolution point")) resKey = "sep";
            else if (joined.Contains("spellboost")) resKey = "spellboost";
            if (resKey != null) return NumExpr.Ctr(_lex.Counter(resKey));
        }

        // 最后才是真的无名计数器：用规范化名，而不是整句。
        return NumExpr.Ctr(_lex.Counter(CondenseCounterName(joined)));
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
                // Enhance (N)：判档读「打出前」的 PP（TurnRunner.PlayCard 写入）。
                // 读当前 pp 是错的 —— 费用已经扣掉，5 费打 Enhance (4) 时只剩 1，判定必然失败。
                // Engage (N)：不生成条件。档位由 TurnRunner.EngageAmulet 这个入口检查并扣费，
                // 这里再判一次就会和入口互相打架。
                CostKind.UsePp => IsEnhanceCost(c)
                    ? Compare("pp_at_play", c.Amount)
                    : null,
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

        static bool IsEnhanceCost(CostSpec c)
            => c.Label != null && c.Label.Equals("enhance", StringComparison.OrdinalIgnoreCase);

        CompareExpr Compare(string counter, NumExpr amount) => new()
        {
            Left = NumExpr.Ctr(_lex.Counter(counter)),
            Op = CompareOp.Ge,
            Right = amount ?? NumExpr.Const(0),
        };
    }

    /// <summary>
    /// 给 &lt;ev&gt; / &lt;sev&gt; 段内的能力补上「已进化」条件。
    ///
    /// 但如果触发器本身就是 OnEvolve / OnSuperEvolve，这个条件已经完全重复 ——
    /// 印出来会变成「【进化时】若已进化，…」这种蠢话，逼着每个打印器
    /// 各写一套 StripDomain 去剥它。在源头不生成才是对的。
    /// </summary>
    private static BoolExpr AndZone(SectionZone zone, BoolExpr cond,
                                    CoreEvent trigEvent = CoreEvent.PassiveAura)
    {
        if (zone == SectionZone.Normal) return cond;
        if (zone == SectionZone.Evolved && trigEvent == CoreEvent.OnEvolve) return cond;
        if (zone == SectionZone.SuperEvolved && trigEvent == CoreEvent.OnSuperEvolve) return cond;
        var state = zone == SectionZone.Evolved ? UnitState.Evolved : UnitState.SuperEvolved;
        return BoolExpr.And(cond, new HasStateExpr { State = state, Of = TargetSpec.Self });
    }

    private static string Trunc(string s)
        => s != null && s.Length > 50 ? s.Substring(0, 50) + "…" : (s ?? "");
}

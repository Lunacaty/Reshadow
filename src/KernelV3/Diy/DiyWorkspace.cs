using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// DIY 工作区：把「编译期」与「运行时」的符号表、词法表、术语表绑成一套。
///
/// 玩家造卡的第一步不是写效果，而是**定义自己的词**：
/// 新兵种「机甲」、新计数器「剑意」、新职业。这些词必须同时进入四处，
/// 少一处就会在某个环节静默失效：
///
///   GameSchema   分配稳定 Id（运行时的 HasTraitExpr 靠它比对新兵种）
///   Lexicon      让英文/脚本编译器认得这个词（否则 "allied Mecha follower" 解析不出）
///   Glossary     让中文渲染器把它译成「机甲」（否则卡面上出现英文）
///   BattleState  让自定义计数器真的能读写
///
/// 这四处各写一份必然漂移 —— 所以统一由 DefineTrait / DefineCounter / DefineClan 收口。
/// </summary>
public sealed class DiyWorkspace
{
    public CompileContext Context { get; }
    public BattleState State => Context.State;
    public GameSchema Schema => Context.Schema;
    public Lexicon Lexicon => Context.Lexicon;
    public Glossary Glossary { get; }
    public ChineseCardPrinter Printer { get; }

    /// <summary>本工作区里由玩家 DIY 出来的卡（按卡号索引）。</summary>
    private readonly Dictionary<int, CardDef> _diyCards = new();

    public IReadOnlyDictionary<int, CardDef> DiyCards => _diyCards;

    private DiyWorkspace(CompileContext ctx, Glossary glossary)
    {
        Context = ctx;
        Glossary = glossary ?? Glossary.CreateDefault();
        Printer = new ChineseCardPrinter(Glossary);
    }

    public static DiyWorkspace Create(int seed = 1, Glossary glossary = null)
    {
        var ctx = CompileContext.Create(seed);
        return new DiyWorkspace(ctx, glossary);
    }

    /// <summary>
    /// 换一个全新的 BattleState，并把已登记的卡定义重新灌进去。
    ///
    /// 测试里每个用例都该从干净状态开始：复用同一个 State 会让上一个用例
    /// 留下的实体、计数器、纹章全部渗进来，把失败伪装成通过（或反过来），
    /// 而报错文字看起来完全正常。
    /// </summary>
    public BattleState ResetState()
    {
        // 必须复用工作区的 Schema，**不能** new BattleState(seed:31) 新造一套。
        //
        // GameSchema 的兵种 / 职业 / 计数器 id 是按**注册顺序**分配的。
        // 新造一套 Schema 时顺序不同，同一个「机甲」在编译期是 id=16、
        // 在运行时是 id=14 —— 编译期写进 HasTraitExpr 的 id 与运行期对不上，
        // 按兵种筛选的效果会**静默失效**（卡能打出，效果什么都不发生）。
        //
        // 规则也一并沿用，否则 ResetState 出来的局面与工作区其余部分不一致。
        var fresh = new BattleState(Context.Schema, 31, Context.State?.Rules);
        // 内置衍生卡（Fairy / Ominous Artifact 等）必须一起灌：
        // 少了它们，召唤出来的是 CardId 未注册的空壳 —— UnresolvedSpawnCount++，
        // 而现象只是「召唤了 0 个妖精」，看起来像效果没实现。
        fresh.RegisterBuiltinTokens(Lexicon);
        // 卡定义属于「工作区」，不属于某一局：重建 State 后要灌回去。
        foreach (var kv in _diyCards) fresh.RegisterCard(kv.Value);
        foreach (var def in _registered) fresh.RegisterCard(def);
        return fresh;
    }

    private readonly List<CardDef> _registered = new();

    /// <summary>接管一个已存在的上下文（共享它已有的词条与卡池）。</summary>
    public static DiyWorkspace For(CompileContext ctx, Glossary glossary = null)
        => new(ctx, glossary);

    // ---------------------------------------------------------------- 词条定义

    /// <summary>
    /// 注册自定义兵种。
    /// 例：<c>DefineTrait("Mecha", "机甲")</c> 之后，
    /// 英文里写 "allied Mecha follower" 就能编译，中文卡面也会显示「机甲兵种」。
    /// </summary>
    public SymbolId<TraitTag> DefineTrait(string name, string nameChs, string modSource = "diy")
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<TraitTag>.Invalid;
        var id = Schema.Trait(name.Trim(), modSource);
        Lexicon.AddTrait(name.Trim());
        if (!string.IsNullOrEmpty(nameChs)) Glossary.Add(name.Trim(), nameChs.Trim());
        Rebuild();
        return id;
    }

    /// <summary>
    /// 注册自定义计数器。
    /// 上下限同时写进 GameSchema（运行期 ClampCounter 会用它），
    /// 否则 WriteCounter 会把「剑意 +2」钳成别的值。
    /// </summary>
    public SymbolId<CounterTag> DefineCounter(string name, string nameChs, int min = 0, int max = 99,
                                              string modSource = "diy")
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<CounterTag>.Invalid;
        var id = Schema.Counter(name.Trim(), modSource);
        Schema.CounterLimits[name.Trim()] = (min, max);
        if (!string.IsNullOrEmpty(nameChs)) Glossary.Add(name.Trim(), nameChs.Trim());
        // 计数器初值也要落到运行时，否则卡面写「初始 0」而实际读不到。
        State.WriteCounter(0, id, min);
        State.WriteCounter(1, id, min);
        Rebuild();
        return id;
    }

    /// <summary>注册自定义职业。</summary>
    public SymbolId<ClanTag> DefineClan(string name, string nameChs, string modSource = "diy")
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<ClanTag>.Invalid;
        var id = Schema.Clan(name.Trim(), modSource);
        Lexicon.AddClan(name.Trim());
        if (!string.IsNullOrEmpty(nameChs)) Glossary.Add(name.Trim(), nameChs.Trim());
        Rebuild();
        return id;
    }

    /// <summary>
    /// 注册自定义关键词。
    /// 关键词不是只加词条 —— 运行时要认得它（例如 cannot_attack 之类有读取端）。
    /// 这里只负责「词法 + 术语」，具体行为仍由引擎的读取端决定。
    /// </summary>
    public SymbolId<KeywordTag> DefineKeyword(string name, string nameChs, string modSource = "diy")
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<KeywordTag>.Invalid;
        var id = Schema.Keyword(name.Trim(), modSource);
        Lexicon.AddKeyword(name.Trim());
        if (!string.IsNullOrEmpty(nameChs)) Glossary.Add(name.Trim(), nameChs.Trim());
        Rebuild();
        return id;
    }

    /// <summary>注册卡名（让「Add a X to your hand」之类句子认得出 X）。</summary>
    public void DefineCardName(string name, int cardId)
    {
        if (string.IsNullOrWhiteSpace(name) || cardId <= 0) return;
        Lexicon.AddCard(name.Trim(), cardId);
        Rebuild();
    }

    private void Rebuild() => Lexicon.Build();

    // ---------------------------------------------------------------- 方式 ④：受限英文 CNL

    /// <summary>受限英文造卡的入参。</summary>
    public sealed class CnlCardSpec
    {
        public int CardId { get; init; }
        public string Name { get; init; } = "";
        public string NameChs { get; init; } = "";
        public CardKind Kind { get; init; } = CardKind.Follower;
        public int Cost { get; init; }
        public int Attack { get; init; }
        public int Life { get; init; }
        public string Clan { get; init; }
        public IReadOnlyList<string> Traits { get; init; } = Array.Empty<string>();

        /// <summary>整段官方英文卡面（含 Fanfare: / Evolve: / 关键词行）。</summary>
        public string EngText { get; init; } = "";

        /// <summary>可选的 alt_modes（激奏/结晶/纹章/信仰）。</summary>
        public IReadOnlyList<(string typeEng, int cost, string textEng)> AltModes { get; init; }
            = Array.Empty<(string, int, string)>();
    }

    /// <summary>
    /// 受限英文自然语言造卡。
    ///
    /// 复用官方卡池跑通的那套 EnglishCompiler —— DIY 卡与官方卡走的是**同一个**编译器，
    /// 所以「DIY 造出来的卡能跑」与「官方卡能跑」是同一件事，不存在两套语义。
    /// </summary>
    public CardDef CompileFromEnglishCnl(CnlCardSpec spec, out IReadOnlyList<string> errors)
    {
        if (spec == null) { errors = new[] { "spec 为空" }; return null; }

        if (!string.IsNullOrEmpty(spec.Name)) DefineCardName(spec.Name, spec.CardId);

        var traits = new List<SymbolId<TraitTag>>();
        foreach (var t in spec.Traits ?? Array.Empty<string>())
            traits.Add(Schema.Trait(t));

        var clan = string.IsNullOrEmpty(spec.Clan)
            ? SymbolId<ClanTag>.Invalid
            : Schema.Clan(spec.Clan);

        var res = Context.Compile(spec.EngText, spec.AltModes?.ToArray(), spec.Name, spec.CardId);
        errors = res.Errors;

        var def = new CardDef
        {
            CardId = spec.CardId,
            Name = string.IsNullOrEmpty(spec.NameChs) ? spec.Name : spec.NameChs,
            Kind = spec.Kind,
            BaseCost = spec.Cost,
            BaseAttack = spec.Attack,
            BaseLife = spec.Life,
            Abilities = res.Abilities,
            // 卡面裸关键词（Ward / Rush …）是卡牌定义属性，编译期收集在 Result 上。
            // 不传会让 DIY 造的卡「卡面写着守护、实际不守护」。
            InnateKeywords = res.InnateKeywords,
            Traits = traits,
            Clan = clan,
            HasFuse = res.HasFuse,
            FuseMaterialFilter = res.FuseMaterialFilter,
            CrestDef = res.CrestDef,
            FaithDef = res.FaithDef,
            AccelerateDef = res.AccelerateDef,
            CrystallizeDef = res.CrystallizeDef,
            SourceText = spec.EngText,
        };
        return def;
    }


    // ---------------------------------------------------------------- 方式 ⑤：PyAST DSL

    /// <summary>
    /// 从 PyAST DSL 文本编译一张卡（204 号文档第五节第 2 步）。
    ///
    /// 与 CNL 那条路的区别：CNL 吃自然语言，这条路吃 AstCanonicalPrinter 打出来的规范文本。
    /// 两者共用同一个 Schema 与 Lexicon —— SymbolId 只按 Id 判等，
    /// 另造一套会让 storm 解析成别的 Id。
    /// </summary>
    public CardDef CompileFromCanonicalAst(string dslText, out IReadOnlyList<string> errors)
    {
        if (string.IsNullOrWhiteSpace(dslText))
        {
            errors = new[] { "DSL 文本为空" };
            return null;
        }

        var parser = new AstCanonicalParser(Schema, Lexicon);
        var def = parser.ParseCard(dslText);
        errors = parser.Errors.ToList();

        if (def == null) return null;
        // 解析器只填 AST 字段，卡名要先登记进词法层，
        // 否则同一张卡里的 summon/transform 按名反查会查不到自己。
        if (!string.IsNullOrEmpty(def.Name)) DefineCardName(def.Name, def.CardId);
        def = new CardDef
        {
            CardId = def.CardId,
            Name = def.Name,
            NameChs = def.NameChs,
            Kind = def.Kind,
            BaseCost = def.BaseCost,
            BaseAttack = def.BaseAttack,
            BaseLife = def.BaseLife,
            Abilities = def.Abilities,
            Traits = def.Traits,
            InnateKeywords = def.InnateKeywords,
            Clan = def.Clan,
            HasFuse = def.HasFuse,
            FuseMaterialFilter = def.FuseMaterialFilter,
            CrestDef = def.CrestDef,
            FaithDef = def.FaithDef,
            AccelerateDef = def.AccelerateDef,
            CrystallizeDef = def.CrystallizeDef,
            IsToken = def.IsToken,
            // rarity 与 innate counters 也必须带走：
            // 漏了它们，导入导出一轮后稀有度归 0、纹章层数归 0。
            Rarity = def.Rarity,
            InnateCounters = def.InnateCounters,
            SourceText = dslText,
        };
        return def;
    }


    /// <summary>
    /// 从 PyAST DSL 的**效果块**（纯能力列表，无卡头）编译。
    ///
    /// 与 CompileFromCanonicalAst 的区别：那个吃整卡（含 card 头），
    /// 这个只吃能力列表 —— DIY 编辑器的 ③ 端与卡包里的 py_ast 都可能是这种形态。
    /// 卡头字段（名/费/攻/命）由调用方沿用原卡。
    /// </summary>
    public List<AbilityDef> CompileAbilitiesFromCanonicalAst(string effectBlock, out IReadOnlyList<string> errors)
    {
        var parser = new AstCanonicalParser(Schema, Lexicon);
        List<AbilityDef> abilities = null;
        try { abilities = parser.ParseAbilities(effectBlock ?? ""); }
        catch (Exception ex)
        {
            errors = new[] { "AST 解析失败：" + ex.Message };
            return null;
        }
        errors = parser.Errors.ToList();
        return abilities;
    }

    /// <summary>把一张卡导出成 PyAST DSL 文本（与 CompileFromCanonicalAst 互逆）。</summary>
    public string ExportCanonicalAst(CardDef def) => AstCanonicalPrinter.PrintCard(def);

    /// <summary>把造好的卡登记进本工作区与运行时卡池。</summary>
    public void Register(CardDef def)
    {
        if (def == null || def.CardId <= 0) return;
        _diyCards[def.CardId] = def;
        _registered.Add(def);
        State.RegisterCard(def);
        DefineCardName(def.Name, def.CardId);
    }

    /// <summary>渲染中文卡面（造卡结果立刻能看到人话）。</summary>
    public string PrintChinese(CardDef def) => Printer.PrintCard(def);

    // ---------------------------------------------------------------- 门禁辅助

    /// <summary>
    /// 两张卡的**结构等价**判定，用于四端同构门禁。
    ///
    /// 比较的是 AST 规范串而不是源码文本：四个端各自生成的 CardDef
    /// 只要语义一致就必须判等，而注释、空白、字段书写顺序都不该影响结论。
    /// </summary>
    /// <summary>把 PassiveAura 上的裸关键词���离开，其余能力原样返回。</summary>
    /// <summary>
    /// 归一化后再打印比对。
    ///
    /// 有些字段语义等价、AST 上却可能取两个值：
    ///   · All 模式的 Count：null 与 Const(1) 都是「所有」，没有个数概念；
    ///   · 条件里的恒真项（true and X）。
    /// 不归一化就会把同一张卡判成两张，让门禁产生假红。
    /// </summary>
    private static string PrintNormalized(IReadOnlyList<AbilityDef> abilities)
    {
        var norm = new List<AbilityDef>();
        foreach (var ab in abilities ?? Array.Empty<AbilityDef>())
        {
            norm.Add(new AbilityDef
            {
                Trigger = ab.Trigger,
                Condition = NormalizeBoolDeep(ab.Condition),
                Costs = ab.Costs,
                Body = (ab.Body ?? Array.Empty<EffectNode>()).Select(NormalizeNode).ToList(),
            });
        }
        return AstCanonicalPrinter.PrintAbilities(norm);
    }

    /// <summary>归一化一条 AbilityDef（用于被授予的能力）。</summary>
    private static AbilityDef NormalizeAbility(AbilityDef ab)
    {
        if (ab == null) return null;
        return new AbilityDef
        {
            Trigger = ab.Trigger,
            Condition = NormalizeBoolDeep(ab.Condition),
            Costs = ab.Costs,
            Body = (ab.Body ?? Array.Empty<EffectNode>()).Select(NormalizeNode).ToList(),
        };
    }

    private static BoolExpr NormalizeBool(BoolExpr e)
    {
        if (e is AndExpr a)
        {
            var kept = a.Items.Where(x => !(x is ConstBoolExpr c && c.Value)).ToList();
            if (kept.Count == 0) return BoolExpr.True;
            if (kept.Count == 1) return kept[0];
            return new AndExpr(kept);
        }
        return e;
    }

    /// <summary>All 模式的 Count 归一成 null（语义等价，避免假红）。</summary>
    private static EffectNode NormalizeNode(EffectNode n)
    {
        switch (n)
        {
            case DealDamageNode d:
                return new DealDamageNode { Target = NormalizeTarget(d.Target), Amount = NormalizeNum(d.Amount), IsSplit = d.IsSplit, IsAbility = d.IsAbility };
            case RestoreLifeNode r:
                return new RestoreLifeNode { Target = NormalizeTarget(r.Target), Amount = NormalizeNum(r.Amount), FullRestore = r.FullRestore };
            case MoveZoneNode m:
                return new MoveZoneNode { Target = NormalizeTarget(m.Target), ToZone = m.ToZone, IsDestroy = m.IsDestroy, OutSlot = m.OutSlot };
            case SelectTargetNode s:
                return new SelectTargetNode { Target = NormalizeTarget(s.Target), OutSlot = s.OutSlot };
            case ModifyStatsNode ms:
                return new ModifyStatsNode { Target = NormalizeTarget(ms.Target), Op = ms.Op, Attack = NormalizeNum(ms.Attack), Life = NormalizeNum(ms.Life), Duration = ms.Duration };
            case ModifyKeywordNode mk:
                return new ModifyKeywordNode { Target = NormalizeTarget(mk.Target), Op = mk.Op, Keyword = mk.Keyword, Value = mk.Value, Duration = mk.Duration };

            // 被授予的能力内部同样有条件与目标，也必须归一化：
            // 否则「(true and is Evolved)」与「is Evolved」会被判成两张不同的卡。
            // SpawnCardNode 的 CopyFrom.Where 也要归一化：
            // 不归一的话，脚本端产出的 [HasKind, Compare] 与表单端产出的
            // [Compare, HasKind] 会被判成两个不同的卡。
            case SpawnCardNode sp:
                return new SpawnCardNode
                {
                    SourceKind = sp.SourceKind, CardId = sp.CardId, CardName = sp.CardName,
                    IsCrest = sp.IsCrest, CrestDef = sp.CrestDef, IsExactCopy = sp.IsExactCopy,
                    AddDepartedTrait = sp.AddDepartedTrait,
                    CopyFrom = NormalizeTarget(sp.CopyFrom),
                    ToOwner = sp.ToOwner, ToZone = sp.ToZone,
                    Count = NormalizeNum(sp.Count), OutSlot = sp.OutSlot,
                    OfferCount = sp.OfferCount,
                };

            case GrantAbilityNode ga:
                return new GrantAbilityNode
                {
                    Target = NormalizeTarget(ga.Target),
                    Granted = NormalizeAbility(ga.Granted),
                    Duration = ga.Duration,
                };

            case GrantCounterAbilityNode gc:
                return new GrantCounterAbilityNode
                {
                    Counter = gc.Counter,
                    Owner = gc.Owner,
                    Granted = NormalizeAbility(gc.Granted),
                };
            case ConditionalNode cd:
                return new ConditionalNode
                {
                    Condition = NormalizeBoolDeep(cd.Condition),
                    Then = (cd.Then ?? Array.Empty<EffectNode>()).Select(NormalizeNode).ToList(),
                    Else = (cd.Else ?? Array.Empty<EffectNode>()).Select(NormalizeNode).ToList(),
                };
            case LoopNode lp:
                return new LoopNode { Kind = lp.Kind, Count = NormalizeNum(lp.Count), Collection = NormalizeTarget(lp.Collection), Until = NormalizeBoolDeep(lp.Until), Body = (lp.Body ?? Array.Empty<EffectNode>()).Select(NormalizeNode).ToList(), MaxIterations = lp.MaxIterations };
            case BranchModeNode bm:
                return new BranchModeNode
                {
                    Kind = bm.Kind,
                    PickCount = bm.PickCount,
                    Options = (bm.Options ?? Array.Empty<IReadOnlyList<EffectNode>>())
                        .Select(o => (IReadOnlyList<EffectNode>)o.Select(NormalizeNode).ToList()).ToList(),
                };
            default:
                return n;
        }
    }

    private static NumExpr NormalizeNum(NumExpr e)
    {
        if (e == null) return null;
        return new NumExpr
        {
            Op = e.Op, Value = e.Value, Stat = e.Stat,
            Of = NormalizeTarget(e.Of), Counter = e.Counter, Arith = e.Arith,
            Args = (e.Args ?? Array.Empty<NumExpr>()).Select(NormalizeNum).ToList(),
            Cond = NormalizeBool(e.Cond),
            Then = NormalizeNum(e.Then),
            Else = NormalizeNum(e.Else),
            TakeMax = e.TakeMax,
        };
    }

    private static BoolExpr NormalizeBoolDeep(BoolExpr e)
    {
        if (e == null) return null;
        if (e is AndExpr a)
        {
            // And 是集合语义：项目顺序不影响含义，归一化时排序，
            // 否则「is Follower and attack<=3」与「attack<=3 and is Follower」
            // 会被判成两张不同的卡，门禁产生假红。
            //
            // 还必须**扁平化嵌套 And**：积木端会把 where 拆成
            // (trait and cost) and kind，脚本端产出扁平的 (trait and cost and kind)，
            // 只排顶层不拍平的话两者结构仍然不同 —— 表现为积木往返判等失败。
            var kept = new List<BoolExpr>();
            void Flatten(BoolExpr x)
            {
                if (x is ConstBoolExpr cb) { if (cb.Value) kept.Add(BoolExpr.True); return; }
                var nx = NormalizeBoolDeep(x);
                if (nx is AndExpr na) { foreach (var y in na.Items) kept.Add(y); return; }
                kept.Add(nx);
            }
            foreach (var x in a.Items) Flatten(x);
            kept = kept.Where(x => !(x is ConstBoolExpr c2 && c2.Value)).ToList();
            if (kept.Count == 0) return BoolExpr.True;
            if (kept.Count == 1) return kept[0];
            // 排序键必须含内容：只按类型名排的话，两个 CompareExpr
            // （如 Cost>=2 与 Cost<=4）谁先谁后取决于原始顺序 ——
            // 不同端产出的顺序不同，判等就随机失败。
            kept = kept.OrderBy(x => x.GetType().Name, StringComparer.Ordinal)
                       .ThenBy(x => AstCanonicalPrinter.PrintBoolExpr(x), StringComparer.Ordinal)
                       .ToList();
            return new AndExpr(kept);
        }
        if (e is CompareExpr cmp)
            return new CompareExpr { Left = NormalizeNum(cmp.Left), Op = cmp.Op, Right = NormalizeNum(cmp.Right) };
        if (e is HasKindExpr hk)
            return new HasKindExpr { Kind = hk.Kind, Of = NormalizeTarget(hk.Of) };
        if (e is HasTraitExpr ht)
            return new HasTraitExpr { Trait = ht.Trait, Of = NormalizeTarget(ht.Of) };
        if (e is HasClanExpr hcl)
            return new HasClanExpr { Clan = hcl.Clan, Clans = hcl.Clans,
                                     IsSelfClan = hcl.IsSelfClan, Of = NormalizeTarget(hcl.Of) };
        if (e is HasPackExpr hpk)
            return new HasPackExpr { PackId = hpk.PackId, Of = NormalizeTarget(hpk.Of) };
        if (e is NotSameEntityExpr ne)
            return new NotSameEntityExpr { ExcludeSlot = ne.ExcludeSlot, Of = NormalizeTarget(ne.Of) };
        return e;
    }

    private static TargetSpec NormalizeTarget(TargetSpec t)
    {
        if (t == null) return null;
        // 无论 Count 是否已为 null，Where 都要深归一 —— 嵌套的 count(...)/where(...)
        // 里还有目标，只在外层判断会漏掉它们。
        if (t.Mode != SelectMode.All)
        {
            return new TargetSpec
            {
                SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
                Mode = t.Mode, Count = t.Count, Where = NormalizeBoolDeep(t.Where), Sort = t.Sort,
                SortDescending = t.SortDescending, DistinctByName = t.DistinctByName,
                DuplicatesOnly = t.DuplicatesOnly, History = t.History, IncludeLeader = t.IncludeLeader,
                TopN = t.TopN, CardId = t.CardId, CardName = t.CardName,
            };
        }
        return new TargetSpec
        {
            SourceKind = t.SourceKind, Owner = t.Owner, Zone = t.Zone, Slot = t.Slot,
            Mode = t.Mode, Count = null, Where = NormalizeBoolDeep(t.Where), Sort = t.Sort,
            SortDescending = t.SortDescending, DistinctByName = t.DistinctByName,
            DuplicatesOnly = t.DuplicatesOnly, History = t.History, IncludeLeader = t.IncludeLeader,
            TopN = t.TopN, CardId = t.CardId, CardName = t.CardName,
        };
    }

    /// <summary>两张子卡（激奏/结晶/纹章/信仰）是否等价。</summary>
        /// <summary>把构筑期修正打成一行的稳定文本，用于判等。</summary>
    private static string PrintDeckMods(IReadOnlyList<DeckBuildModifier> mods)
    {
        if (mods == null || mods.Count == 0) return "";
        return string.Join(";", mods
            .Where(m => m != null)
            .Select(m => m.Kind + "/" + m.Value + "/" + m.Value2 + "/" + (m.TargetClan ?? "")
                         + "/" + m.StackPerCopy + "/" + (m.ExclusiveGroup ?? "")));
    }

private static bool SubEqual(CardDef x, CardDef y, string label, out string diff)
    {
        diff = "";
        if (x == null && y == null) return true;
        if (x == null || y == null) { diff = label + " 子卡一方为 null"; return false; }
        if (x.BaseCost != y.BaseCost) { diff = label + " 费用不同：" + x.BaseCost + " vs " + y.BaseCost; return false; }

        var (kx, rx) = SplitKeywords(x.Abilities);
        var (ky, ry) = SplitKeywords(y.Abilities);
        string skx = string.Join(",", kx.OrderBy(z => z, StringComparer.Ordinal));
        string sky = string.Join(",", ky.OrderBy(z => z, StringComparer.Ordinal));
        if (skx != sky) { diff = label + " 关键词不同：A=[" + skx + "] B=[" + sky + "]"; return false; }

        string sx = AstCanonicalPrinter.PrintAbilities(rx);
        string sy = AstCanonicalPrinter.PrintAbilities(ry);
        if (sx != sy) { diff = label + " 能力不同：\n  A=" + sx + "\n  B=" + sy; return false; }
        return true;
    }

    private static (List<string> keywords, List<AbilityDef> rest) SplitKeywords(
        IReadOnlyList<AbilityDef> abilities)
    {
        var kws = new List<string>();
        var rest = new List<AbilityDef>();
        foreach (var ab in abilities ?? Array.Empty<AbilityDef>())
        {
            bool isBareKeyword = ab?.Trigger?.Event == CoreEvent.PassiveAura
                                 && ab.Body != null && ab.Body.Count == 1
                                 && ab.Body[0] is ModifyKeywordNode mk
                                 && mk.Op == KeywordOp.Grant;
            if (isBareKeyword)
            {
                var mk2 = (ModifyKeywordNode)ab.Body[0];
                kws.Add(mk2.Value != null && mk2.Value.Op == NumOp.Const
                    ? mk2.Keyword.Name + "(" + mk2.Value.Value + ")"
                    : mk2.Keyword.Name);
                continue;
            }
            rest.Add(ab);
        }
        return (kws, rest);
    }

    public static bool StructurallyEqual(CardDef a, CardDef b, out string diff)
    {
        diff = "";
        if (a == null || b == null) { diff = "一方为 null"; return false; }
        if (a.Kind != b.Kind) { diff = "卡种不同"; return false; }
        if (a.BaseCost != b.BaseCost) { diff = "费用不同"; return false; }
        if (a.BaseAttack != b.BaseAttack) { diff = "攻击不同"; return false; }
        if (a.BaseLife != b.BaseLife) { diff = "生命不同"; return false; }

        // 裸关键词（PassiveAura 上的 Grant）是**集合**，不是序列：
        // [Ward, Rush] 与 [Rush, Ward] 是同一张卡，写序不同不该判不等。
        // 把它们抽出来单独按集合比，其余能力才按顺序比。
        // 关键词有两个来源：卡牌定义属性（InnateKeywords，裸词）与能力节点
        // （PassiveAura + Grant，动词短语如 Can't be played）。两边都要比，
        // 只比后者会把「InnateKeywords 丢了」判成等价。
        var (ka, ra) = SplitKeywords(a.Abilities);
        var (kb, rb) = SplitKeywords(b.Abilities);
        ka.AddRange((a.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>()).Select(x => x.Name));
        kb.AddRange((b.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>()).Select(x => x.Name));
        ka = ka.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        kb = kb.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

        string sak = string.Join(",", ka.OrderBy(x => x, StringComparer.Ordinal));
        string sbk = string.Join(",", kb.OrderBy(x => x, StringComparer.Ordinal));
        if (sak != sbk) { diff = "裸关键词不同：A=[" + sak + "] B=[" + sbk + "]"; return false; }

        string sa = PrintNormalized(ra);
        string sb = PrintNormalized(rb);
        if (sa != sb)
        {
            diff = "能力不同：\n  A=" + sa + "\n  B=" + sb;
            return false;
        }

        // 卡牌顶层的融合声明与四个子卡必须纳入判等。
        // 不判它们，「Fuse: Loot cards」往返后变成「不能融合」也会被判成等价 ——
        // 而这正是「打印出来一样、实战完全不同」的盲区。
        if (a.HasFuse != b.HasFuse) { diff = "HasFuse 不同"; return false; }

        // All 模式的 Count 是等价字段（null 与 Const(1) 都表示「所有」），
        // 比对前先归一，否则会把同一份筛选判成两份。
        string fa = AstCanonicalPrinter.PrintTargetExpr(
            a.FuseMaterialFilter == null ? null : NormalizeTarget(a.FuseMaterialFilter));
        string fb = AstCanonicalPrinter.PrintTargetExpr(
            b.FuseMaterialFilter == null ? null : NormalizeTarget(b.FuseMaterialFilter));
        if (fa != fb) { diff = "融合材料过滤不同：A=" + fa + " B=" + fb; return false; }

        if (!SubEqual(a.AccelerateDef, b.AccelerateDef, "激奏", out diff)) return false;
        if (!SubEqual(a.CrystallizeDef, b.CrystallizeDef, "结晶", out diff)) return false;
        if (!SubEqual(a.CrestDef, b.CrestDef, "纹章", out diff)) return false;
        if (!SubEqual(a.FaithDef, b.FaithDef, "信仰", out diff)) return false;

        string ta = string.Join(",", (a.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(x => x.Name).OrderBy(x => x));
        string tb = string.Join(",", (b.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(x => x.Name).OrderBy(x => x));
        if (ta != tb) { diff = "兵种不同：A=[" + ta + "] B=[" + tb + "]"; return false; }

        // 构筑期修正也必须纳入判等。
        // 不判它，「deck_mod size(+20)」往返后丢失会被判成等价 ——
        // 而卡面中文读起来完全一样，只有组牌时才会发现牌组上限没变。
        string da = PrintDeckMods(a.DeckModifiers);
        string db = PrintDeckMods(b.DeckModifiers);
        if (da != db) { diff = "构筑期修正不同：A=[" + da + "] B=[" + db + "]"; return false; }

        // 卡包归属与 Token 标记同样影响「能否进构筑」，一并纳入。
        if (!string.Equals(a.PackId ?? "", b.PackId ?? "", StringComparison.OrdinalIgnoreCase))
        { diff = "PackId 不同：A=" + a.PackId + " B=" + b.PackId; return false; }
        if (a.IsToken != b.IsToken) { diff = "IsToken 不同"; return false; }

        // 卡面自带关键词（InnateKeywords）也参与判等：
        // 它决定「守护魔像有没有守护」，而卡面中文只在 Abilities 里体现。
        string ika = string.Join(",", (a.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>()).Select(x => x.Name).OrderBy(x => x));
        string ikb = string.Join(",", (b.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>()).Select(x => x.Name).OrderBy(x => x));
        if (ika != ikb) { diff = "自带关键词不同：A=[" + ika + "] B=[" + ikb + "]"; return false; }

        return true;
    }
}

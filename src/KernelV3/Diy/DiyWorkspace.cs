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

    /// <summary>把造好的卡登记进本工作区与运行时卡池。</summary>
    public void Register(CardDef def)
    {
        if (def == null || def.CardId <= 0) return;
        _diyCards[def.CardId] = def;
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
        return AstEnglishPrinter.PrintCard(norm);
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
            var kept = a.Items.Where(x => !(x is ConstBoolExpr c && c.Value))
                             .Select(NormalizeBoolDeep).ToList();
            if (kept.Count == 0) return BoolExpr.True;
            if (kept.Count == 1) return kept[0];
            return new AndExpr(kept);
        }
        if (e is CompareExpr cmp)
            return new CompareExpr { Left = NormalizeNum(cmp.Left), Op = cmp.Op, Right = NormalizeNum(cmp.Right) };
        if (e is HasKindExpr hk)
            return new HasKindExpr { Kind = hk.Kind, Of = NormalizeTarget(hk.Of) };
        if (e is HasTraitExpr ht)
            return new HasTraitExpr { Trait = ht.Trait, Of = NormalizeTarget(ht.Of) };
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
        var (ka, ra) = SplitKeywords(a.Abilities);
        var (kb, rb) = SplitKeywords(b.Abilities);

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

        string ta = string.Join(",", (a.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(x => x.Name).OrderBy(x => x));
        string tb = string.Join(",", (b.Traits ?? Array.Empty<SymbolId<TraitTag>>()).Select(x => x.Name).OrderBy(x => x));
        if (ta != tb) { diff = "兵种不同：A=[" + ta + "] B=[" + tb + "]"; return false; }

        return true;
    }
}

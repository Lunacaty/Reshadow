using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 全量宇宙总表（GlobalCatalog）
//
// 卡池（CardPool）是「这一个赛制允许用什么」，总表是「玩家电脑上装了什么」。
// 分开的理由：卡池 A 里的卡若点名召唤卡包 B 的 Token，
//   情况 1 —— B 已安装但没勾进卡池 A：照样能召出来（其余 B 的卡不污染 A 的构筑表）
//   情况 2 —— B 根本没装：报错，说清楚缺哪个包
// 合成一层的话只能二选一：要么禁止一切跨包引用，要么让被引用的包整个污染进来。
// ============================================================================

/// <summary>全量宇宙总表：启动时一次性加载 packs/ 下的全部卡包。</summary>
public sealed class GlobalCatalog
{
    public GameSchema Schema { get; } = GameSchema.CreateDefault();
    public Lexicon Lexicon { get; }
    public Glossary Glossary { get; } = Glossary.CreateDefault();
    public DeckDefTable DeckDefs { get; } = new();

    private readonly Dictionary<string, CardPackDef> _packs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, CardDef> _allCards = new();
    private readonly Dictionary<string, CardDef> _cardsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CardPackDef> _pending = new();

    public IReadOnlyDictionary<string, CardPackDef> Packs => _packs;
    public IReadOnlyDictionary<int, CardDef> AllCards => _allCards;
    public IReadOnlyDictionary<string, CardDef> CardsByName => _cardsByName;

    /// <summary>构建期收集的非致命提示（如跨包引用被总表自动挂载）。</summary>
    public List<string> Notices { get; } = new();

    public GlobalCatalog() { Lexicon = new Lexicon(Schema); }

    /// <summary>把已编译好的卡放进总表（工具/测试与卡池运行时都要用）。</summary>
    public void RegisterCompiledCard(CardDef def)
    {
        if (def == null) return;
        _allCards[def.CardId] = def;
        if (!string.IsNullOrWhiteSpace(def.Name)) _cardsByName[def.Name.Trim()] = def;
    }

    public CardDef TryGetCard(int cardId)
        => _allCards.TryGetValue(cardId, out var d) ? d : null;

    public CardDef TryGetCard(string name)
        => name != null && _cardsByName.TryGetValue(name.Trim(), out var d) ? d : null;

    public string PackOf(int cardId) => TryGetCard(cardId)?.PackId ?? "";

    /// <summary>枚举全宇宙可用于「混沌发现」的卡片。</summary>
    public IEnumerable<CardDef> EnumerateChaosDiscoverPool(bool includeTokens = false)
        => _allCards.Values.Where(c =>
               c.Kind is CardKind.Follower or CardKind.Spell or CardKind.Amulet
               && (includeTokens || !c.IsToken))
           .OrderBy(c => c.CardId);

    /// <summary>加载全部卡包并两遍编译。</summary>
    public static GlobalCatalog LoadAll(string packsRoot)
    {
        var cat = new GlobalCatalog();
        foreach (var pack in CardPackLoader.LoadAll(packsRoot)) cat.Register(pack);
        cat.Build();
        return cat;
    }

    public void Register(CardPackDef pack)
    {
        if (pack == null || string.IsNullOrEmpty(pack.PackId)) return;
        _packs[pack.PackId] = pack;
        _pending.Add(pack);
    }

    /// <summary>
    /// 两遍编译：先登记全部名字，再编译。
    ///
    /// 边扫边编译会让「卡包 A 引用卡包 B 的卡名」失败 ——
    /// 编译 A 时 B 的名字还没进词法表。
    /// </summary>
    public void Build()
    {
        foreach (var pack in _pending)
        {
            foreach (var t in pack.Traits)
            {
                Schema.Trait(t.Name);
                Lexicon.AddTrait(t.Name);
                if (!string.IsNullOrEmpty(t.NameChs)) Glossary.Add(t.Name, t.NameChs);
            }
            foreach (var c in pack.Clans)
            {
                Schema.Clan(c.Name);
                Lexicon.AddClan(c.Name);
                if (!string.IsNullOrEmpty(c.NameChs)) Glossary.Add(c.Name, c.NameChs);
            }
            foreach (var k in pack.Keywords)
            {
                Schema.Keyword(k.Name);
                Lexicon.AddKeyword(k.Name);
                if (!string.IsNullOrEmpty(k.NameChs)) Glossary.Add(k.Name, k.NameChs);
            }
            foreach (var ctr in pack.Counters)
            {
                Schema.Counter(ctr.Name);
                // 总表不判冲突：后注册的覆盖先注册的，等进卡池时再报错。
                Schema.CounterLimits[ctr.Name] = (ctr.Min, ctr.Max);
                if (!string.IsNullOrEmpty(ctr.NameChs)) Glossary.Add(ctr.Name, ctr.NameChs);
            }
            foreach (var sa in pack.StackableAmulets)
                Schema.StackableAmulets[sa.CounterName] = sa;

            foreach (var c in pack.Cards) Lexicon.AddCard(c.NameEng, c.CardId);
        }
        Lexicon.Build();

        var comp = new EnglishCompiler(Lexicon);
        foreach (var pack in _pending)
        {
            foreach (var c in pack.Cards)
            {
                var def = CompileCard(pack, c, comp);
                if (def == null) continue;
                _allCards[def.CardId] = def;
                if (!string.IsNullOrWhiteSpace(def.Name)) _cardsByName[def.Name.Trim()] = def;
            }

            foreach (var nd in pack.NamedDecks)
            {
                var entries = new List<DeckEntry>();
                foreach (var e in nd.Entries)
                {
                    int id = e.CardId;
                    if (id <= 0 && !string.IsNullOrEmpty(e.CardName))
                        Lexicon.TryGetCardId(e.CardName, out id);
                    if (id > 0) entries.Add(new DeckEntry(id, e.Copies));
                    else Notices.Add("具名牌组《" + nd.DeckName + "》引用的卡片 '"
                                     + (e.CardName ?? e.CardId.ToString()) + "' 不在已安装卡包中。");
                }
                DeckDefs.Register(nd.DeckName, entries);
            }
        }
    }

    private CardDef CompileCard(CardPackDef pack, CardRawEntry c, EnglishCompiler comp)
    {
        var traits = new List<SymbolId<TraitTag>>();
        foreach (var t in c.Traits) traits.Add(Schema.Trait(t));
        var clan = string.IsNullOrEmpty(c.Clan) ? SymbolId<ClanTag>.Invalid : Schema.Clan(c.Clan);

        if (string.IsNullOrWhiteSpace(c.Text))
        {
            return new CardDef
            {
                CardId = c.CardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
                BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
                Traits = traits, Clan = clan,
                InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => Schema.Keyword(k)).ToList(),
                PackId = pack.PackId, IsToken = c.IsToken, SourceText = "",
            };
        }

        var alts = c.AltModes.Select(a => (a.TypeEng, a.Cost, a.TextEng)).ToArray();
        var r = comp.Compile(c.Text, alts, c.NameEng, c.CardId);
        foreach (var e in r.Errors) Notices.Add("卡 " + c.CardId + "《" + c.NameEng + "》编译失败：" + e);

        return new CardDef
        {
            CardId = c.CardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
            BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
            Abilities = r.Abilities, Traits = traits, Clan = clan,
            InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => Schema.Keyword(k)).ToList(),
            HasFuse = r.HasFuse, FuseMaterialFilter = r.FuseMaterialFilter,
            CrestDef = r.CrestDef, FaithDef = r.FaithDef,
            AccelerateDef = r.AccelerateDef, CrystallizeDef = r.CrystallizeDef,
            PackId = pack.PackId, IsToken = c.IsToken, SourceText = c.Text,
        };
    }
}

/// <summary>卡池视图：GlobalCatalog 之上的轻量环境（启用包集合 + 规则）。</summary>
public sealed class CardPool
{
    public string PoolId { get; init; } = "standard";
    public string Name { get; init; } = "标准卡池";
    public GlobalCatalog Catalog { get; init; }
    public MatchRules BaseRules { get; init; } = MatchRules.CreateStandard();
    public HashSet<string> EnabledPackIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool ContainsCard(int cardId)
        => Catalog?.TryGetCard(cardId) is { } def && EnabledPackIds.Contains(def.PackId);

    public IEnumerable<CardDef> EnumerateCollectibleCards()
        => Catalog.AllCards.Values.Where(c =>
               EnabledPackIds.Contains(c.PackId) && !c.IsToken
               && c.Kind is CardKind.Follower or CardKind.Spell or CardKind.Amulet)
           .OrderBy(c => c.CardId);

    public IEnumerable<CardDef> EnumerateCurrentPoolDiscover(bool includeTokens = false)
        => Catalog.AllCards.Values.Where(c =>
               EnabledPackIds.Contains(c.PackId)
               && (includeTokens || !c.IsToken)
               && c.Kind is CardKind.Follower or CardKind.Spell or CardKind.Amulet)
           .OrderBy(c => c.CardId);
}

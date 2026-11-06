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
/// <summary>一次编译失败的记录（卡包 / 卡号 / 卡名 / 阶段 / 详情）。</summary>
public readonly record struct CardAuditIssue(
    string PackId, int CardId, string NameEng, string NameChs,
    string Stage, string Detail);

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
    private readonly Dictionary<CardRawEntry, int> _idOf = new();

    /// <summary>PackId:LocalId ↔ int CardId。只写 local_id 的卡由它分配卡号。</summary>
    public Core.PoolCardIndex CardIndex { get; } = new();

    public IReadOnlyDictionary<string, CardPackDef> Packs => _packs;
    public IReadOnlyDictionary<int, CardDef> AllCards => _allCards;
    public IReadOnlyDictionary<string, CardDef> CardsByName => _cardsByName;

    /// <summary>构建期收集的非致命提示（如跨包引用被总表自动挂载）。</summary>
    public List<string> Notices { get; } = new();

    /// <summary>编译失败的卡片清单（结构化，供审计器与门禁读取）。</summary>
    public List<CardAuditIssue> CompileFailures { get; } = new();

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

            // 只写 local_id 的卡在这里分配卡号；否则它们全是 CardId=0 并在 _allCards[0] 上互相覆盖。
            foreach (var c in pack.Cards)
            {
                int eid = c.CardId;
                if (eid <= 0 && !string.IsNullOrEmpty(c.LocalId))
                    eid = CardIndex.Assign(pack.PackId, c.LocalId);
                else if (eid > 0)
                    CardIndex.RegisterExplicit(pack.PackId, c.LocalId, eid);
                _idOf[c] = eid;
                if (eid > 0)
                {
                    // 英文名要进词法表：英文编译器靠它把 "Add a Fairy" 里的
                    // Fairy 认成一张卡，缺了这一句整条效果会静默消失。
                    //
                    // 两个来源都登记，因为**产物卡包的 NameEng 里其实是中文名**
                    // （卡包只剩一个 name 字段，加载器把它读进 NameEng）：
                    //   · 权威表英文名 -> 编译英文原文时用
                    //   · 卡包里的名字 -> 具名牌组按名引用、DIY 按名查询时用
                    var authEn = AuthoritativeNames.EnglishOf(eid);
                    if (!string.IsNullOrWhiteSpace(authEn)) Lexicon.AddCard(authEn, eid);
                    if (!string.IsNullOrWhiteSpace(c.NameEng)) Lexicon.AddCard(c.NameEng, eid);
                }

                // 卡名中文注入 Glossary。
                // 缺这一句，纹章名 / 衍生 Token 名在卡面上全是英文 ——
                // 纹章名就是本卡名，卡名有译文却查不到，白瞎了数据。
                if (!string.IsNullOrEmpty(c.NameChs) && !string.IsNullOrEmpty(c.NameEng))
                    Glossary.Add(c.NameEng, c.NameChs);
            }
        }
        Lexicon.Build();

        var comp = new EnglishCompiler(Lexicon);
        foreach (var pack in _pending)
        {
            foreach (var c in pack.Cards)
            {
                var def = CompileCard(pack, c, comp,
                                      _idOf.TryGetValue(c, out var eid2) ? eid2 : c.CardId);
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

    private CardDef CompileCard(CardPackDef pack, CardRawEntry c, EnglishCompiler comp, int cardId)
    {
        var traits = new List<SymbolId<TraitTag>>();
        foreach (var t in c.Traits) traits.Add(Schema.Trait(t));
        var clan = string.IsNullOrEmpty(c.Clan) ? SymbolId<ClanTag>.Invalid : Schema.Clan(c.Clan);

        if (string.IsNullOrWhiteSpace(c.Text))
        {
            return new CardDef
            {
                CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
                BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
                Traits = traits, Clan = clan,
                InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => Schema.Keyword(k)).ToList(),
                PackId = pack.PackId, IsToken = c.IsToken, SourceText = "",
                // 立绘来源：卡包里写明的 art_card_id 优先，否则用卡号本身。
                ArtCardId = c.ArtCardId > 0 ? c.ArtCardId : cardId,
            };
        }

        // 脚本格式的卡不能丢给 EnglishCompiler —— 当场会编译报错。
        // 必须复用本总表的 Schema —— SymbolId 只按 Id 判等，
        // 新造一套会让 storm 解析成 Id=1，而本仓的 ward 才是 0/storm 是 1 的对应关系只在本 Schema 成立。
        
        // ---- source_format: "py_ast"（204 号文档第五节第 2 步）
        // 复用本总表的 Schema / Lexicon：SymbolId 只按 Id 判等，
        // 新造一套会让同一张卡里的 trait / keyword 与本仓串号。
        if (string.Equals(c.SourceFormat, "py_ast", StringComparison.OrdinalIgnoreCase))
        {
            var pyParser = new Lang.AstCanonicalParser(Schema, Lexicon);
            CardDef pyDef = null;
            try { pyDef = pyParser.ParseCard(c.Text); }
            catch (Exception ex)
            {
                Notices.Add("卡 " + c.CardId + "《" + c.NameEng + "》PyAST 解析失败：" + ex.Message);
                return null;
            }
            foreach (var e in pyParser.Errors)
                Notices.Add("卡 " + c.CardId + "《" + c.NameEng + "》PyAST：" + e);
            if (pyDef == null)
            {
                Notices.Add("卡 " + c.CardId + "《" + c.NameEng + "》PyAST 解析为空");
                return null;
            }

            // 卡包声明优先：卡号 / 名 / 费 / 攻 / 命 以外层为准。
            return new CardDef
            {
                CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = pyDef.Kind,
                BaseCost = c.Cost > 0 ? c.Cost : pyDef.BaseCost,
                BaseAttack = c.Attack > 0 ? c.Attack : pyDef.BaseAttack,
                BaseLife = c.Life > 0 ? c.Life : pyDef.BaseLife,
                Abilities = pyDef.Abilities,
                Traits = traits.Count > 0 ? traits : pyDef.Traits,
                Clan = clan.IsValid ? clan : pyDef.Clan,
                InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => Schema.Keyword(k))
                    .Concat(pyDef.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
                    .Distinct().ToList(),
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
                HasFuse = pyDef.HasFuse, FuseMaterialFilter = pyDef.FuseMaterialFilter,
                CrestDef = pyDef.CrestDef, FaithDef = pyDef.FaithDef,
                AccelerateDef = pyDef.AccelerateDef, CrystallizeDef = pyDef.CrystallizeDef,
                PackId = pack.PackId, IsToken = c.IsToken, SourceText = c.Text, Rarity = c.Rarity,
                // 立绘来源：卡包写的 art_card_id 优先，否则用卡号本身。
                ArtCardId = c.ArtCardId > 0 ? c.ArtCardId : cardId,
            };
        }

        

        var alts = c.AltModes.Select(a => (a.TypeEng, a.Cost, a.TextEng)).ToArray();
        var r = comp.Compile(c.Text, alts, c.NameEng, c.CardId);

        // 黑洞封堵：以前这里只把错误丢进没人看的 Notices，然后照常返回
        // 带「半截效果」的 CardDef —— 卡进了游戏、效果错了一半，且无任何汇总。
        // 现在：错误带卡号前缀统一登记，并在卡片上留下可查标记。
        foreach (var e in r.Errors)
            Notices.Add("[编译失败] " + c.CardId + "《" + c.NameEng + "》" + e);
        if (r.Errors.Count > 0)
            CompileFailures.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                "总表编译", string.Join(" | ", r.Errors)));

        return new CardDef
        {
            CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
            BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
            Abilities = r.Abilities, Traits = traits, Clan = clan,
            // 卡包 JSON 的 keywords 与卡面原文解析出的裸关键词必须合并，
            // 与 CardPool.CompileCard 同口径：只取前者会让原文有关键词、
            // JSON 没写 keywords 的卡失去能力。
            InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => Schema.Keyword(k))
                .Concat(r.InnateKeywords)
                .Distinct().ToList(),
            HasFuse = r.HasFuse, FuseMaterialFilter = r.FuseMaterialFilter,
            CrestDef = r.CrestDef, FaithDef = r.FaithDef,
            AccelerateDef = r.AccelerateDef, CrystallizeDef = r.CrystallizeDef,
            PackId = pack.PackId, IsToken = c.IsToken, SourceText = c.Text, Rarity = c.Rarity,
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

    /// <summary>
    /// 用本卡池视图建一个对局。
    ///
    /// 与 CardPoolRuntime.CreateBattleState 的区别只是入口不同：
    /// 两条路都必须把 Pool 挂上去，否则发现类效果恒为空。
    /// </summary>
    public Battle.BattleState CreateBattleState(int seed = 0)
    {
        var state = new Battle.BattleState(Catalog?.Schema, seed, BaseRules);
        if (Catalog != null)
            foreach (var c in Catalog.AllCards.Values) state.RegisterCard(c);
        state.Pool = this;
        return state;
    }

    public IEnumerable<CardDef> EnumerateCurrentPoolDiscover(bool includeTokens = false)
        => Catalog.AllCards.Values.Where(c =>
               EnabledPackIds.Contains(c.PackId)
               && (includeTokens || !c.IsToken)
               && c.Kind is CardKind.Follower or CardKind.Spell or CardKind.Amulet)
           .OrderBy(c => c.CardId);
}

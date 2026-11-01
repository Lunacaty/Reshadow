using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 卡池（CardPool）—— 一套自洽的「游戏宇宙 / 赛制环境」
//
// 卡包管「我有什么」，卡池管「把这些包合流成一个不自相矛盾的宇宙」。
// 构建卡池是**编译**，不是拼装：要跑两遍扫描、跨包解析、冲突拦截。
//
// 为什么必须两遍：
//   卡包 A 的卡面写着「Summon a Fairy」，而 Fairy 定义在卡包 B。
//   如果边扫边编译，A 在编译时 B 的卡名还没进词法表 —— 认不出，静默失败。
//   所以 Pass 1 只登记名字与卡号，Pass 2 才真正编译。
// ============================================================================

/// <summary>卡池配置单。</summary>
public sealed class CardPoolConfig
{
    public string PoolId { get; init; } = "standard";
    public string Name { get; init; } = "标准卡池";
    public MatchRules Rules { get; init; } = MatchRules.CreateStandard();
    public List<string> EnabledPackIds { get; init; } = new();
}

/// <summary>编译并校验通过后的卡池运行时环境。</summary>
public sealed class CardPoolRuntime
{
    public string PoolId { get; init; } = "";
    public MatchRules Rules { get; init; } = MatchRules.CreateStandard();
    public GameSchema Schema { get; init; }
    public Lexicon Lexicon { get; init; }
    public Glossary Glossary { get; init; }
    public DeckDefTable DeckDefs { get; init; }

    /// <summary>PackId:LocalId ↔ 局内 int CardId 的双向映射（跨作者撞号消歧的依据）。</summary>
    public Core.PoolCardIndex CardIndex { get; init; } = new();

    /// <summary>本卡池启用的卡包 ID（发现与组牌要按它过滤）。</summary>
    public HashSet<string> EnabledPackIds { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<int, CardDef> _cardsById = new();
    private readonly Dictionary<string, CardDef> _cardsByName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<int, CardDef> AllCards => _cardsById;
    public IReadOnlyDictionary<string, CardDef> CardsByName => _cardsByName;

    /// <summary>可收藏卡（供组牌界面）。Token 不在其中。</summary>
    public IReadOnlyList<CardDef> CollectibleCards
        => _cardsById.Values.Where(c => !c.IsToken).OrderBy(c => c.CardId).ToList();

    internal void RegisterCompiledCard(CardDef def)
    {
        if (def == null) return;
        _cardsById[def.CardId] = def;
        if (!string.IsNullOrWhiteSpace(def.Name)) _cardsByName[def.Name.Trim()] = def;
    }

    /// <summary>用本卡池的完整环境建一个全新对局状态。</summary>
    public BattleState CreateBattleState(int seed = 0)
    {
        var state = new BattleState(Schema, seed, Rules);

        // 必须把卡池视图挂上去。
        // 不挂的话 state.Pool == null，EnumerateDiscoverPool 恒返回空集合 ——
        // 表现为「发现类效果什么都没发生」，而卡面中文完全正常。
        var catalog = new GlobalCatalog();
        foreach (var card in _cardsById.Values) catalog.RegisterCompiledCard(card);
        state.Pool = new CardPool
        {
            PoolId = PoolId,
            Name = PoolId,
            Catalog = catalog,
            BaseRules = Rules,
            EnabledPackIds = new HashSet<string>(EnabledPackIds, StringComparer.OrdinalIgnoreCase),
        };

        foreach (var card in _cardsById.Values) state.RegisterCard(card);
        return state;
    }
}

public sealed class PoolBuildResult
{
    public CardPoolRuntime Pool { get; init; }
    public List<string> Errors { get; } = new();
    public bool Ok => Errors.Count == 0 && Pool != null;

    internal PoolBuildResult With(CardPoolRuntime pool)
    {
        var r = new PoolBuildResult { Pool = pool };
        r.Errors.AddRange(Errors);
        return r;
    }
}

public static class CardPoolBuilder
{
    /// <summary>
    /// 五步流水线：
    ///   1. 原生卡池防脱离 + 显式依赖检查
    ///   2. Pass 1：全局符号 / 卡号 / 卡名预注册 + 冲突拦截
    ///   3. 具名牌组跨包解析
    ///   4. Pass 2：编译全部卡片 AST
    ///   5. 组装运行时环境
    /// </summary>
    public static PoolBuildResult Build(
        CardPoolConfig config,
        IReadOnlyDictionary<string, CardPackDef> availablePacks)
    {
        var res = new PoolBuildResult();
        if (config == null) { res.Errors.Add("卡池配置为空"); return res; }
        if (availablePacks == null) { res.Errors.Add("无可用的卡包"); return res; }

        config.Rules.Validate(res.Errors);

        var enabledSet = new HashSet<string>(config.EnabledPackIds, StringComparer.OrdinalIgnoreCase);

        // ================= 第 1 步 =================
        foreach (var pack in availablePacks.Values)
        {
            if (string.Equals(pack.NativePoolId, config.PoolId, StringComparison.OrdinalIgnoreCase)
                && !enabledSet.Contains(pack.PackId))
            {
                res.Errors.Add("卡包 [" + pack.PackId + "] 的原生卡池是 [" + config.PoolId
                               + "]，不能脱离原生卡池被移除！");
            }
        }

        var packs = new List<CardPackDef>();
        foreach (var pid in config.EnabledPackIds)
        {
            if (!availablePacks.TryGetValue(pid, out var pack))
            {
                res.Errors.Add("卡池引用了不存在的卡包：[" + pid + "]");
                continue;
            }
            foreach (var dep in pack.Dependencies)
                if (!enabledSet.Contains(dep))
                    res.Errors.Add("卡包 [" + pack.PackId + "] 依赖卡包 [" + dep + "]，必须一并加入卡池！");
            packs.Add(pack);
        }
        if (res.Errors.Count > 0) return res;

        // ================= 第 2 步：Pass 1 =================
        var schema = GameSchema.CreateDefault();
        var lexicon = new Lexicon(schema);
        var glossary = Glossary.CreateDefault();
        var deckDefs = new DeckDefTable();

        var cardOwnerPack = new Dictionary<int, string>();
        var nameOwnerPack = new Dictionary<string, (string packId, int cardId)>(StringComparer.OrdinalIgnoreCase);

        foreach (var pack in packs)
        {
            foreach (var t in pack.Traits)
            {
                schema.Trait(t.Name);
                lexicon.AddTrait(t.Name);
                if (!string.IsNullOrEmpty(t.NameChs)) glossary.Add(t.Name, t.NameChs);
            }
            foreach (var c in pack.Clans)
            {
                schema.Clan(c.Name);
                lexicon.AddClan(c.Name);
                if (!string.IsNullOrEmpty(c.NameChs)) glossary.Add(c.Name, c.NameChs);
            }
            foreach (var k in pack.Keywords)
            {
                schema.Keyword(k.Name);
                lexicon.AddKeyword(k.Name);
                if (!string.IsNullOrEmpty(k.NameChs)) glossary.Add(k.Name, k.NameChs);
            }
            foreach (var ctr in pack.Counters)
            {
                if (schema.CounterLimits.TryGetValue(ctr.Name, out var ex)
                    && (ex.min != ctr.Min || ex.max != ctr.Max))
                {
                    res.Errors.Add("计数器冲突：卡包 [" + pack.PackId + "] 把 '" + ctr.Name
                                   + "' 定义为 (" + ctr.Min + ".." + ctr.Max + ")，与已有定义 ("
                                   + ex.min + ".." + ex.max + ") 矛盾！");
                    continue;
                }
                schema.Counter(ctr.Name);
                schema.CounterLimits[ctr.Name] = (ctr.Min, ctr.Max);
                if (!string.IsNullOrEmpty(ctr.NameChs)) glossary.Add(ctr.Name, ctr.NameChs);
            }
            foreach (var sa in pack.StackableAmulets)
                schema.StackableAmulets[sa.CounterName] = sa;

            foreach (var c in pack.Cards)
            {
                if (cardOwnerPack.TryGetValue(c.CardId, out var prevPack))
                {
                    res.Errors.Add("卡号冲突：卡包 [" + pack.PackId + "] 与 [" + prevPack
                                   + "] 都定义了 CardId=" + c.CardId + "（《" + c.NameEng + "》）！");
                    continue;
                }
                cardOwnerPack[c.CardId] = pack.PackId;

                // 同名消歧（文档「破局 1」的三级优先）：
                //   本包优先 → 显式依赖包优先 → 卡池全局唯一匹配
                //
                // 前两级在**单包内**已经由「名字只在本包的卡里出现」自然满足；
                // 真正要拦的是第三级失败的情况：两个互不相关的包都定义了同名卡，
                // 谁赢都是错的 —— 因为引用方没有表达偏好，引擎也不该替它猜。
                if (nameOwnerPack.TryGetValue(c.NameEng, out var prevName) && prevName.cardId != c.CardId)
                {
                    bool sameOwnerChain = IsDependencyRelated(pack.PackId, prevName.packId, packs)
                                          || IsDependencyRelated(prevName.packId, pack.PackId, packs);
                    if (!sameOwnerChain)
                    {
                        res.Errors.Add("同名歧义冲突：卡包 [" + pack.PackId + "] 与 [" + prevName.packId
                                       + "] 都定义了同名卡 '" + c.NameEng + "'（id=" + c.CardId
                                       + " 与 id=" + prevName.cardId + "），且两包之间没有依赖关系 —— "
                                       + "模糊引用无法消歧，拒绝启动该卡池！");
                        continue;
                    }
                    // 有依赖关系时，被依赖的一方胜出（依赖方知道自己引用的是谁）。
                    bool prevWins = IsDependencyRelated(pack.PackId, prevName.packId, packs);
                    if (prevWins) continue;
                }
                nameOwnerPack[c.NameEng] = (pack.PackId, c.CardId);
                lexicon.AddCard(c.NameEng, c.CardId);
            }
        }
        if (res.Errors.Count > 0) return res;
        lexicon.Build();

        // ================= 第 3 步：具名牌组跨包解析 =================
        foreach (var pack in packs)
        {
            foreach (var nd in pack.NamedDecks)
            {
                var entries = new List<DeckEntry>();
                foreach (var e in nd.Entries)
                {
                    int resolvedId = e.CardId;
                    if (resolvedId <= 0 && !string.IsNullOrEmpty(e.CardName))
                        lexicon.TryGetCardId(e.CardName, out resolvedId);

                    if (resolvedId <= 0 || !cardOwnerPack.ContainsKey(resolvedId))
                    {
                        res.Errors.Add("跨包依赖缺失：卡包 [" + pack.PackId + "] 的具名牌组《" + nd.DeckName
                                       + "》引用了卡片 '"
                                       + (string.IsNullOrEmpty(e.CardName) ? e.CardId.ToString() : e.CardName)
                                       + "'，但该卡片不在当前卡池的任何已启用卡包中！");
                        continue;
                    }
                    entries.Add(new DeckEntry(resolvedId, e.Copies));
                }
                deckDefs.Register(nd.DeckName, entries);
            }
        }
        if (res.Errors.Count > 0) return res;

        // ================= 第 4 步：Pass 2 编译 =================
        var pool = new CardPoolRuntime
        {
            PoolId = config.PoolId,
            Rules = config.Rules,
            Schema = schema,
            Lexicon = lexicon,
            Glossary = glossary,
            DeckDefs = deckDefs,
            EnabledPackIds = new HashSet<string>(config.EnabledPackIds, StringComparer.OrdinalIgnoreCase),
        };

        // 全卡包的卡定义（含未启用包）—— 依赖校验要靠它说清「缺的那张在哪个包」。
        var allCardsLookupTable = new Dictionary<int, CardDef>();
        foreach (var pk in availablePacks.Values)
            foreach (var c in pk.Cards)
                allCardsLookupTable[c.CardId] = null;      // 占位，下面填真定义

        var comp = new EnglishCompiler(lexicon);
        foreach (var pack in packs)
            foreach (var c in pack.Cards)
            {
                var card = CompileCard(pack, c, schema, comp, res.Errors);
                if (card != null)
                {
                    pool.RegisterCompiledCard(card);
                    allCardsLookupTable[card.CardId] = card;
                }
            }

        if (res.Errors.Count > 0) return res;

        // ================= 第 4.5 步：AST 静态依赖完整性 =================
        // 两遍编译只保证「认得出名字」，认得出不代表那张卡**在卡池里**。
        // 少了这一步，跨包引用缺失要等对局打到一半 summon 出空壳才暴露。
        var poolIds = new HashSet<int>(pool.AllCards.Keys);
        var depReport = PoolDependencyVerifier.Verify(
            pool.AllCards, deckDefs, poolIds,
            id => allCardsLookupTable.TryGetValue(id, out var d) ? d : null);
        foreach (var e in depReport.Errors) res.Errors.Add(e);
        if (res.Errors.Count > 0) return res;

        // ================= 第 5 步 =================
        return res.With(pool);
    }

    /// <summary>A 是否直接或间接依赖 B（含 A == B）。</summary>
    private static bool IsDependencyRelated(
        string a, string b, List<CardPackDef> packs, int depth = 0)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (depth > 8) return false;      // 防御环状依赖
        var pa = packs.FirstOrDefault(x => string.Equals(x.PackId, a, StringComparison.OrdinalIgnoreCase));
        if (pa?.Dependencies == null) return false;
        foreach (var dep in pa.Dependencies)
            if (IsDependencyRelated(dep, b, packs, depth + 1)) return true;
        return false;
    }

    private static CardDef CompileCard(
        CardPackDef pack, CardRawEntry c, GameSchema schema,
        EnglishCompiler comp, List<string> errors)
    {
        var traits = new List<SymbolId<TraitTag>>();
        foreach (var t in c.Traits) traits.Add(schema.Trait(t));

        var clan = string.IsNullOrEmpty(c.Clan) ? SymbolId<ClanTag>.Invalid : schema.Clan(c.Clan);

        // 无效果文本的卡不必过编译器：省一次解析，也避免空串误报。
        if (string.IsNullOrWhiteSpace(c.Text))
        {
            return new CardDef
            {
                CardId = c.CardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
                BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
                Traits = traits, Clan = clan,
                PackId = pack.PackId, IsToken = c.IsToken,
                SourceText = "",
            };
        }

        // 卡包可以声明 source_format: "reshadow_script"，
        // 让 DIY 作者用脚本语言（含 deck_mod / discover）写卡。
        // 不分支的话这类卡会无脑丢给 EnglishCompiler，当场编译报错。
        if (string.Equals(c.SourceFormat, "reshadow_script", StringComparison.OrdinalIgnoreCase))
        {
            var ws = Diy.DiyWorkspace.Create(c.CardId);
            var sc = new Diy.ReshadowScriptCompiler(ws);
            var made = sc.Compile(c.Text);
            if (made.Count == 0)
            {
                foreach (var e in sc.Errors)
                    errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》脚本编译失败：" + e);
                return null;
            }
            var d = made[0];
            return new CardDef
            {
                CardId = c.CardId, Name = c.NameEng, NameChs = c.NameChs, Kind = d.Kind,
                BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
                Abilities = d.Abilities, Traits = traits, Clan = clan,
                InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k)).ToList(),
                DeckModifiers = d.DeckModifiers ?? c.DeckModifiers ?? new List<DeckBuildModifier>(),
                PackId = pack.PackId, IsToken = c.IsToken, SourceText = c.Text,
            };
        }

        var alts = c.AltModes.Select(a => (a.TypeEng, a.Cost, a.TextEng)).ToArray();
        var result = comp.Compile(c.Text, alts, c.NameEng, c.CardId);
        foreach (var e in result.Errors)
            errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》编译失败：" + e);

        return new CardDef
        {
            CardId = c.CardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
            BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
            Abilities = result.Abilities,
            Traits = traits, Clan = clan,
            InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k)).ToList(),
            HasFuse = result.HasFuse, FuseMaterialFilter = result.FuseMaterialFilter,
            CrestDef = result.CrestDef, FaithDef = result.FaithDef,
            AccelerateDef = result.AccelerateDef, CrystallizeDef = result.CrystallizeDef,
            PackId = pack.PackId, IsToken = c.IsToken,
            SourceText = c.Text,
        };
    }
}

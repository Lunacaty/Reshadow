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

    /// <summary>
    /// 转成轻量的 CardPool 视图（GlobalCatalog + 启用包集合）。
    ///
    /// DeckRuleResolver 与发现机制都吃 CardPool，而组牌/开局侧拿到的是
    /// CardPoolRuntime —— 没有这条转换，两边就会各用一套校验。
    /// </summary>
    public CardPool ToCardPoolView()
    {
        var catalog = new GlobalCatalog();
        foreach (var card in _cardsById.Values) catalog.RegisterCompiledCard(card);
        return new CardPool
        {
            PoolId = PoolId,
            Name = PoolId,
            Catalog = catalog,
            BaseRules = Rules,
            EnabledPackIds = new HashSet<string>(EnabledPackIds, StringComparer.OrdinalIgnoreCase),
        };
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
        // 必须把规则同步给 Schema：ConditionCompiler / PrefixParser 读阈值时
        // 只拿得到 Schema，读不到 BattleState。漏这一行，编译期用的永远是默认规则。
        schema.Rules = config.Rules;
        var lexicon = new Lexicon(schema);
        var glossary = Glossary.CreateDefault();
        var deckDefs = new DeckDefTable();

        var cardIndex = new Core.PoolCardIndex();
        var idOf = new Dictionary<CardRawEntry, int>();
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
                // local_id 分配：作者可以不写 card_id，只写 local_id。
                // 之前 PoolCardIndex 是彻头彻尾的死代码 —— 建了类、加了属性，
                // 但从没调用过 Assign，于是第二张 local_id 的卡就撞 CardId=0。
                int effectiveId = c.CardId;
                if (effectiveId <= 0 && !string.IsNullOrEmpty(c.LocalId))
                    effectiveId = cardIndex.Assign(pack.PackId, c.LocalId);
                else if (effectiveId > 0)
                    cardIndex.RegisterExplicit(pack.PackId, c.LocalId, effectiveId);
                idOf[c] = effectiveId;
                if (effectiveId <= 0)
                {
                    res.Errors.Add("卡包 [" + pack.PackId + "] 的卡《" + c.NameEng
                                   + "》既没有 card_id 也没有 local_id，无法分配卡号！");
                    continue;
                }

                if (cardOwnerPack.TryGetValue(effectiveId, out var prevPack))
                {
                    res.Errors.Add("卡号冲突：卡包 [" + pack.PackId + "] 与 [" + prevPack
                                   + "] 都定义了 CardId=" + effectiveId + "（《" + c.NameEng + "》）！");
                    continue;
                }
                cardOwnerPack[effectiveId] = pack.PackId;

                // 同名消歧（文档「破局 1」的三级优先）：
                //   本包优先 → 显式依赖包优先 → 卡池全局唯一匹配
                //
                // 前两级在**单包内**已经由「名字只在本包的卡里出现」自然满足；
                // 真正要拦的是第三级失败的情况：两个互不相关的包都定义了同名卡，
                // 谁赢都是错的 —— 因为引用方没有表达偏好，引擎也不该替它猜。
                if (nameOwnerPack.TryGetValue(c.NameEng, out var prevName) && prevName.cardId != effectiveId)
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
                nameOwnerPack[c.NameEng] = (pack.PackId, effectiveId);
                lexicon.AddCard(c.NameEng, effectiveId);
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
            CardIndex = cardIndex,
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
                var card = CompileCard(pack, c, schema, comp, res.Errors,
                                       idOf.TryGetValue(c, out var eid) ? eid : c.CardId,
                                       lexicon, glossary, nameOwnerPack, config.Rules);
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
        EnglishCompiler comp, List<string> errors, int cardId,
        Lexicon sharedLex = null, Glossary sharedGlossary = null,
        Dictionary<string, (string packId, int cardId)> sharedNames = null,
        MatchRules rules = null)
    {
        var traits = new List<SymbolId<TraitTag>>();
        foreach (var t in c.Traits) traits.Add(schema.Trait(t));

        var clan = string.IsNullOrEmpty(c.Clan) ? SymbolId<ClanTag>.Invalid : schema.Clan(c.Clan);

        // 无效果文本的卡不必过编译器：省一次解析，也避免空串误报。
        if (string.IsNullOrWhiteSpace(c.Text))
        {
            return new CardDef
            {
                CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
                BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
                Traits = traits, Clan = clan,
                // 无文本分支也必须带关键词：《守护魔像》《寂静骑士》的 text 是空串，
                // 关键词只写在 "keywords" 里。漏了这一行，它们上场就是白板。
                InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k)).ToList(),
                PackId = pack.PackId, IsToken = c.IsToken, Rarity = c.Rarity,
                SourceText = "",
            };
        }

        // 卡包可以声明 source_format: "reshadow_script"，
        // 让 DIY 作者用脚本语言（含 deck_mod / discover）写卡。
        // 不分支的话这类卡会无脑丢给 EnglishCompiler，当场编译报错。
        // 卡包也可以声明 source_format: "ast_json"：
        // DIY 作者的 AST 语法树文本（与 AstJsonCodec.Encode 同一格式）。
        // 与脚本端共用同一套 Schema —— 否则 storm 会被解析成 ward（SymbolId 只按 Id 判等）。
        if (string.Equals(c.SourceFormat, "ast_json", StringComparison.OrdinalIgnoreCase))
        {
            Ast.CardDef astDef;
            try
            {
                astDef = Diy.AstJsonCodec.Decode(c.Text, schema);
            }
            catch (Exception ex)
            {
                errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》AST JSON 解析失败：" + ex.Message);
                return null;
            }

            if (astDef == null)
            {
                errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》AST JSON 为空");
                return null;
            }

            var mergedMods2 = (astDef.DeckModifiers != null && astDef.DeckModifiers.Count > 0)
                ? astDef.DeckModifiers
                : (c.DeckModifiers ?? new List<DeckBuildModifier>());
            var mergedKws2 = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k))
                .Concat(astDef.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
                .Distinct().ToList();

            return new CardDef
            {
                CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = astDef.Kind,
                BaseCost = c.Cost > 0 ? c.Cost : astDef.BaseCost,
                BaseAttack = astDef.BaseAttack,
                BaseLife = astDef.BaseLife,
                Clan = string.IsNullOrEmpty(c.Clan) ? astDef.Clan : schema.Clan(c.Clan),
                Traits = (astDef.Traits ?? Array.Empty<SymbolId<TraitTag>>()).ToList(),
                Abilities = astDef.Abilities ?? Array.Empty<AbilityDef>(),
                InnateKeywords = mergedKws2,
                DeckModifiers = mergedMods2,
                HasFuse = astDef.HasFuse,
                FuseMaterialFilter = astDef.FuseMaterialFilter,
                AccelerateDef = astDef.AccelerateDef,
                CrystallizeDef = astDef.CrystallizeDef,
                CrestDef = astDef.CrestDef,
                FaithDef = astDef.FaithDef,
            };
        }
        if (string.Equals(c.SourceFormat, "reshadow_script", StringComparison.OrdinalIgnoreCase))
        {
            // 必须**复用当前卡池的 schema 与 lexicon**。
            // DiyWorkspace.Create 会新造一套 GameSchema —— 脚本里拿到的兵种/计数器
            // SymbolId.Id 与本卡池对不上，summon("Fairy") 也会因为空 lexicon
            // 解析成 CardId=0，召唤出空壳。
            // CompileContext.For 接管一个已存在的 BattleState，它自己的 Schema
            // 就是唯一真相源 —— 这样脚本里定义的兵种/计数器与卡池共用同一套 SymbolId。
            var probeState = new Battle.BattleState(schema, 0, rules ?? MatchRules.CreateStandard());
            probeState.Pool = new CardPool
            {
                PoolId = "script", Catalog = new GlobalCatalog(), BaseRules = MatchRules.CreateStandard(),
            };
            var ctx = Lang.CompileContext.For(probeState);
            // 把 Pass 1 已预注册的卡名同步进脚本词法表，
            // 否则脚本里 summon("Fairy") 会解析成 CardId=0。
            if (sharedNames != null)
                foreach (var kv in sharedNames) ctx.Lexicon.AddCard(kv.Key, kv.Value.cardId);
            ctx.Lexicon.Build();
            var ws = Diy.DiyWorkspace.For(ctx, sharedGlossary);
            var sc = new Diy.ReshadowScriptCompiler(ws);
            var made = sc.Compile(c.Text);
            if (made.Count == 0)
            {
                foreach (var e in sc.Errors)
                    errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》脚本编译失败：" + e);
                return null;
            }
            var d = made[0];

            // 合并而不是 ?? —— d.DeckModifiers 可能是空列表（非 null），
            // 用 ?? 会把 JSON 上的 c.DeckModifiers 整个丢掉。
            var mergedMods = (d.DeckModifiers != null && d.DeckModifiers.Count > 0)
                ? d.DeckModifiers
                : (c.DeckModifiers ?? new List<DeckBuildModifier>());
            var mergedKws = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k))
                .Concat(d.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
                .Distinct().ToList();

            return new CardDef
            {
                CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = d.Kind,
                BaseCost = c.Cost > 0 ? c.Cost : d.BaseCost,
                BaseAttack = c.Attack > 0 ? c.Attack : d.BaseAttack,
                BaseLife = c.Life > 0 ? c.Life : d.BaseLife,
                Abilities = d.Abilities,
                Traits = traits.Count > 0 ? traits : d.Traits,
                Clan = clan.IsValid ? clan : d.Clan,
                InnateKeywords = mergedKws,
                DeckModifiers = mergedMods,
                // 这几个字段也必须拷：漏了会让「脚本写的纹章/融合/激奏卡」变成白板。
                HasFuse = d.HasFuse, FuseMaterialFilter = d.FuseMaterialFilter,
                CrestDef = d.CrestDef, FaithDef = d.FaithDef,
                AccelerateDef = d.AccelerateDef, CrystallizeDef = d.CrystallizeDef,
                PackId = pack.PackId, IsToken = c.IsToken, SourceText = c.Text, Rarity = c.Rarity,
            };
        }

        var alts = c.AltModes.Select(a => (a.TypeEng, a.Cost, a.TextEng)).ToArray();
        var result = comp.Compile(c.Text, alts, c.NameEng, c.CardId);
        foreach (var e in result.Errors)
            errors.Add("卡包 [" + pack.PackId + "] 的卡 " + c.CardId + "《" + c.NameEng + "》编译失败：" + e);

        return new CardDef
        {
            CardId = cardId, Name = c.NameEng, NameChs = c.NameChs, Kind = c.Kind,
                DeckModifiers = c.DeckModifiers ?? new List<DeckBuildModifier>(),
            BaseCost = c.Cost, BaseAttack = c.Attack, BaseLife = c.Life,
            Abilities = result.Abilities,
            Traits = traits, Clan = clan,
            InnateKeywords = (c.Keywords ?? new List<string>()).Select(k => schema.Keyword(k)).ToList(),
            HasFuse = result.HasFuse, FuseMaterialFilter = result.FuseMaterialFilter,
            CrestDef = result.CrestDef, FaithDef = result.FaithDef,
            AccelerateDef = result.AccelerateDef, CrystallizeDef = result.CrystallizeDef,
            PackId = pack.PackId, IsToken = c.IsToken, Rarity = c.Rarity,
            SourceText = c.Text,
        };
    }
}

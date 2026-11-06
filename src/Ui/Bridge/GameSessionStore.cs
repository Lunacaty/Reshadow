using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.Ui.Bridge;

/// <summary>
/// 全局前端数据仓：加载卡包总表、卡池环境、构筑存档，并负责磁盘持久化。
///
/// **同样不引用 Godot** —— 它在无头测试里要被直接 new 出来跑。
/// </summary>
public sealed class GameSessionStore
{
    public GlobalCatalog Catalog { get; private set; }
    public Dictionary<string, CardPoolConfig> PoolConfigs { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, CardPoolRuntime> PoolRuntimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>PoolId → 该卡池来自的 .pool.json 绝对路径（保存时写回原文件）。</summary>
    public Dictionary<string, string> PoolFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ConstructedDeck> SavedDecks { get; } = new();
    public ChineseCardPrinter CardPrinter { get; private set; }

    /// <summary>只读内容根（data/packs、data/pools 所在）。</summary>
    public string DataRoot { get; private set; } = "";

    /// <summary>
    /// 玩家存档根：exe 根目录下的 decks/。
    ///
    /// 与 DataRoot 分开是有意的 —— 安装目录可能只读（Program Files），
    /// 而牌组必须可写。玩家在资源管理器里打开 exe 所在文件夹就能看到自己的牌组。
    /// </summary>
    public string DeckRoot { get; private set; } = "";

    /// <summary>
    /// 推导可写存档目录：优先 exe（可执行文件）所在目录下的 decks/。
    ///
    /// 开发期从 .godot/mono/temp/bin/Debug 启动时，会向上找到仓库根，
    /// 落到 仓库根/decks/，不至于把存档丢进构建输出目录里。
    /// </summary>
    private string ResolveWritableDeckRoot()
    {
        // 判据只有一个：**这个目录里有 data/ 吗**。
        //
        // 不用 Environment.ProcessPath 判断「是不是导出环境」——
        // 开发期用 dotnet run 时它指向 dotnet.exe，会被误判成非导出，
        // 于是把存档写进 .et/bin/Debug/net9.0/decks（构建输出目录里）。
        // 而「有 data/ 的目录」在两种环境下都恰好是正确答案：
        //   导出后 = exe 所在目录；开发期 = 仓库根。
        string exeDir = null;

        // 1) 优先：从程序集位置向上找带 data/ 的目录
        var dir0 = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir0); i++)
        {
            if (Directory.Exists(Path.Combine(dir0, "data"))) { exeDir = dir0; break; }
            var parent = Path.GetDirectoryName(dir0);
            if (parent == dir0) break;
            dir0 = parent;
        }

        // 2) 兜底：真实 exe 所在目录
        if (string.IsNullOrEmpty(exeDir))
        {
            try
            {
                var procPath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(procPath) && File.Exists(procPath))
                    exeDir = Path.GetDirectoryName(procPath);
            }
            catch { }
        }

        if (string.IsNullOrEmpty(exeDir)) exeDir = Directory.GetCurrentDirectory();

        var decks = Path.Combine(exeDir, "decks");
        try { Directory.CreateDirectory(decks); }
        catch
        {
            // 实在写不了（只读安装目录）才退到用户目录
            decks = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Reshadow", "decks");
            Directory.CreateDirectory(decks);
        }
        return decks;
    }

    /// <summary>加载全部内容。已加载过则直接返回（幂等）。</summary>
    public void Initialize()
    {
        if (Catalog != null) return;

        // 先把内容定位结果打出来 —— 打包后加载失败时这是唯一线索。
        // 用 Console 而不是 GD.Print：Bridge 层必须保持零 Godot 依赖，
        // 否则无头门禁工程将无法引用它。
        Console.WriteLine(ShadowverseFan.Ui.ContentPaths.Diagnose());

        var packsRoot = CardPackLoader.ResolvePacksRoot()
            ?? throw new DirectoryNotFoundException("找不到 data/packs 目录");

        // DataRoot = 只读内容根（data/packs / data/pools 所在）。
        // 打包后它是 exe 旁边的 data/ —— 玩家可能把它装在 Program Files，
        // 那里没有写权限，所以**玩家存档不能写这里**。
        DataRoot = Directory.GetParent(packsRoot)?.FullName ?? packsRoot;

        // DeckRoot = 玩家可写根。固定落在 exe 根目录下的 decks/。
        // 开发期（从 .godot 目录跑）会推到仓库根的 decks/，同样可用。
        DeckRoot = ResolveWritableDeckRoot();

        // 1. 全量宇宙总表
        Catalog = GlobalCatalog.LoadAll(packsRoot);
        CardPrinter = new ChineseCardPrinter(Catalog.Glossary);

        // 2. 全部赛制卡池
        var poolsDir = Path.Combine(DataRoot, "pools");
        PoolFilePaths.Clear();
        foreach (var (cfg, path) in PoolConfigLoader.LoadAllPoolsWithPaths(poolsDir))
        {
            PoolConfigs[cfg.PoolId] = cfg;
            // 记住原文件：official-standard.pool.json 里的 id 是 "standard"，
            // 按 id 拼文件名会在磁盘上造出第二个同 id 文件互相打架。
            PoolFilePaths[cfg.PoolId] = path;

            // 卡池自定义的职业与计数器存在 .pool.json 里，不在 pack.json 里。
            // 每次 Initialize 都要把它们同步进全局 Schema/Glossary ——
            // 只靠「新建时注册一次」的话，下一次重载 Catalog 就重建了，
            // 自定义职业从勾选栏里消失，引用它的效果也拿不到符号。
            foreach (var cn in cfg.PoolClans ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(cn)) continue;
                Catalog.Schema.Clan(cn, cfg.PoolId);
                Catalog.Lexicon.AddClan(cn);
            }
            foreach (var ctr in cfg.LeaderCounters ?? new List<KernelV3.Packs.PoolCounterDef>())
            {
                if (ctr == null || string.IsNullOrWhiteSpace(ctr.Name)) continue;
                Catalog.Schema.Counter(ctr.Name, cfg.PoolId);
                Catalog.Schema.CounterLimits[ctr.Name] = (ctr.Min, ctr.Max);
                if (!string.IsNullOrWhiteSpace(ctr.NameChs)) Catalog.Glossary.Add(ctr.Name, ctr.NameChs);
            }
            var built = CardPoolBuilder.Build(cfg, Catalog.Packs);
            if (built.Ok)
            {
                PoolRuntimes[cfg.PoolId] = built.Pool;

                // 卡号被自动隔离的卡：把「原号 -> 所属卡包」喂给找图器，
                // 让两张撞号的卡各自去自己的 cards/ 目录取立绘，绝不串图。
                foreach (var kv in built.ArtPackMap)
                    CardArtLoader.RegisterCardPack(kv.Key, kv.Value);
            }
        }

        // 3. 玩家构筑存档（写在 exe 旁的 decks/，不是只读的 data/）
        var decksDir = DeckRoot;
        Directory.CreateDirectory(decksDir);
        SavedDecks.Clear();
        SavedDecks.AddRange(PoolConfigLoader.LoadAllDecks(decksDir));
    }

    /// <summary>取某个卡池的轻量视图；找不到时回落到第一个可用卡池。</summary>
    public CardPool GetPoolView(string poolId)
    {
        Initialize();
        if (!string.IsNullOrEmpty(poolId)
            && PoolRuntimes.TryGetValue(poolId, out var rt))
            return rt.ToCardPoolView();

        var fallback = PoolRuntimes.Values.FirstOrDefault();
        return fallback?.ToCardPoolView()
               ?? new CardPool { PoolId = poolId ?? "standard", Catalog = Catalog };
    }

    public CardPoolRuntime GetPoolRuntime(string poolId)
    {
        Initialize();
        if (!string.IsNullOrEmpty(poolId) && PoolRuntimes.TryGetValue(poolId, out var rt)) return rt;
        return PoolRuntimes.Values.FirstOrDefault();
    }

    /// <summary>新建一副牌组并登记（不落盘，由 SaveDeck 负责）。</summary>
    public ConstructedDeck CreateDeck(string poolId, string leaderClan, string name)
    {
        var deck = new ConstructedDeck
        {
            DeckId = Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = string.IsNullOrWhiteSpace(name)
                ? "新建" + SvWbClanName.Of(leaderClan) + "牌组"
                : name,
            PoolId = string.IsNullOrEmpty(poolId) ? "standard" : poolId,
            LeaderClan = string.IsNullOrEmpty(leaderClan) ? "中立" : leaderClan,
        };
        SavedDecks.Add(deck);
        return deck;
    }

    /// <summary>把构筑存成 data/decks/{DeckId}.deck.json。</summary>
    public void SaveDeck(ConstructedDeck deck)
    {
        if (deck == null) return;
        Initialize();

        int idx = SavedDecks.FindIndex(d => d.DeckId == deck.DeckId);
        if (idx >= 0) SavedDecks[idx] = deck;
        else SavedDecks.Add(deck);

        var decksDir = DeckRoot;
        Directory.CreateDirectory(decksDir);
        var path = Path.Combine(decksDir, deck.DeckId + ".deck.json");

        var payload = new
        {
            deck_id = deck.DeckId,
            name = deck.Name,
            pool_id = deck.PoolId,
            leader_clan = deck.LeaderClan,
            chosen_extra_clans = deck.ChosenExtraClans,
            cards = deck.Cards.Select(c => new { card_id = c.cardId, count = c.count }).ToList(),
        };
        var json = JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, new UTF8Encoding(false));
    }

    /// <summary>
    /// 重命名一副牌组。
    ///
    /// ConstructedDeck.Name 是 init-only，改不了 —— 必须重建一份并把原对象换掉。
    /// 直接赋值会编译报错，绕过去（比如用反射）会让「只读字段被悄悄改掉」变成隐性约定。
    /// </summary>
    public ConstructedDeck RenameDeck(ConstructedDeck deck, string newName)
    {
        if (deck == null) return null;
        var renamed = new ConstructedDeck
        {
            DeckId = deck.DeckId,
            Name = string.IsNullOrWhiteSpace(newName) ? deck.Name : newName,
            PoolId = deck.PoolId,
            LeaderClan = deck.LeaderClan,
            ChosenExtraClans = new List<string>(deck.ChosenExtraClans ?? new List<string>()),
            Cards = new List<(int cardId, int count)>(deck.Cards ?? new List<(int, int)>()),
        };
        int idx = SavedDecks.FindIndex(d => d.DeckId == deck.DeckId);
        if (idx >= 0) SavedDecks[idx] = renamed;
        return renamed;
    }

    public void DeleteDeck(string deckId)
    {
        if (string.IsNullOrEmpty(deckId)) return;
        Initialize();
        SavedDecks.RemoveAll(d => d.DeckId == deckId);
        var path = Path.Combine(DeckRoot, deckId + ".deck.json");
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>复制一副牌组（新 DeckId，其余字段照抄）。</summary>
    public ConstructedDeck DuplicateDeck(ConstructedDeck src)
    {
        if (src == null) return null;
        var copy = new ConstructedDeck
        {
            DeckId = Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = src.Name + " 副本",
            PoolId = src.PoolId,
            LeaderClan = src.LeaderClan,
            ChosenExtraClans = new List<string>(src.ChosenExtraClans ?? new List<string>()),
            Cards = new List<(int cardId, int count)>(src.Cards ?? new List<(int, int)>()),
        };
        SavedDecks.Add(copy);
        return copy;
    }

    // ---------------------------------------------------------------- DIY 落盘与热重载

    /// <summary>重新扫描加载所有卡包与卡池（DIY 保存卡牌或改卡池规则后调用）。</summary>
    public void ReloadCatalogAndPools()
    {
        Catalog = null;
        PoolConfigs.Clear();
        PoolRuntimes.Clear();
        Initialize();
    }

    /// <summary>
    /// 把一张 DIY 编译好的 CardDef（以 reshadow_script 格式）写入指定卡包的 pack.json，
    /// 然后热重载 —— 玩家转头去「卡组构筑」就能把刚造的卡塞进牌组出战。
    /// </summary>
    public void SaveCardIntoPack(string packId, CardDef card, string scriptText)
    {
        if (card == null) return;
        Initialize();

        var packDir = Path.Combine(DataRoot, "packs", packId);
        Directory.CreateDirectory(packDir);
        var packFile = Path.Combine(packDir, "pack.json");

        CardPackDef pack = File.Exists(packFile)
            ? CardPackLoader.LoadFromFile(packFile)
            : new CardPackDef { PackId = packId, Name = packId, NativePoolId = "standard" };

        var entry = new CardRawEntry
        {
            CardId = card.CardId,
            NameEng = card.Name ?? ("DIY_" + card.CardId),
            NameChs = !string.IsNullOrWhiteSpace(card.NameChs) ? card.NameChs : card.Name,
            Kind = card.Kind,
            IsToken = card.IsToken,
            Clan = card.Clan.IsValid ? card.Clan.Name : "中立",
            Traits = (card.Traits ?? Array.Empty<SymbolId<TraitTag>>())
                     .Select(t => t.Name).ToList(),
            Cost = card.BaseCost,
            Attack = card.BaseAttack,
            Life = card.BaseLife,
            // 204/206 号文档后，新存档统一写规范 AST 语法。
            // 旧 reshadow_script 只在**读取**旧卡包时保留兼容（见 GlobalCatalog / CardPool），
            // 不再作为写入格式。
            SourceFormat = "py_ast",
            Text = scriptText ?? "",
            DeckModifiers = card.DeckModifiers ?? new(),
            // 稀有度与常驻关键词必须带上：漏了它们，保存一张 DIY 卡
            // 会让这张卡（以及重写后的整个 cards 段）的稀有度归零。
            Rarity = card.Rarity,
            Keywords = (card.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
                       .Select(k => k.Name).ToList(),
        };

        int existing = pack.Cards.FindIndex(c => c.CardId == card.CardId);
        if (existing >= 0) pack.Cards[existing] = entry;
        else pack.Cards.Add(entry);

        // 整个 pack.json 重写：只需保留原有字段并替换 cards 段。
        // 手工拼 JSON 而不是序列化整个 CardPackDef —— 后者会把一堆
        // 只读派生字段也写进去，再读回来时字段多出来就报错。
        var json = SerializePacket(pack);
        File.WriteAllText(packFile, json, new UTF8Encoding(false));

        ReloadCatalogAndPools();
    }

    /// <summary>
    /// 把 CardPackDef 序列化回 pack.json。
    ///
    /// 两条铁律：
    ///   1) **kind 必须按 CardPackLoader 的编码写**（2=护符 3=法术 4=纹章 5=信仰 其余=随从），
    ///      不能直接 (int)CardKind —— 枚举里 Spell=1，而加载器把 1 当随从，
    ///      结果「造一张法术，存盘再读回来变成 0/0 随从」。
    ///   2) **全部字段都要写**。少写 counters / stackable_amulets / named_decks /
    ///      keywords / deck_modifiers / alt_modes，等于往包里存一张卡的同时
    ///      把整个包的其他内容抹掉。
    /// </summary>
    private static string SerializePacket(CardPackDef pack)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"pack_id\": ").Append(JsonStr(pack.PackId)).Append(",\n");
        sb.Append("  \"name\": ").Append(JsonStr(pack.Name)).Append(",\n");
        sb.Append("  \"author\": ").Append(JsonStr(pack.Author ?? "")).Append(",\n");
        sb.Append("  \"version\": ").Append(JsonStr(pack.Version ?? "1.0.0")).Append(",\n");
        sb.Append("  \"native_pool_id\": ").Append(JsonStr(pack.NativePoolId ?? "standard")).Append(",\n");
        sb.Append("  \"dependencies\": ").Append(JsonArr(pack.Dependencies)).Append(",\n");
        sb.Append("  \"clans\": ").Append(JsonSymbolArr(pack.Clans)).Append(",\n");
        sb.Append("  \"traits\": ").Append(JsonSymbolArr(pack.Traits)).Append(",\n");
        sb.Append("  \"keywords\": ").Append(JsonSymbolArr(pack.Keywords)).Append(",\n");
        sb.Append("  \"counters\": ").Append(JsonCounterArr(pack.Counters)).Append(",\n");
        sb.Append("  \"stackable_amulets\": ").Append(JsonStackableArr(pack.StackableAmulets)).Append(",\n");
        sb.Append("  \"named_decks\": ").Append(JsonNamedDeckArr(pack.NamedDecks)).Append(",\n");
        sb.Append("  \"cards\": [\n");

        for (int i = 0; i < pack.Cards.Count; i++)
        {
            var c = pack.Cards[i];
            sb.Append("    {\n");
            sb.Append("      \"card_id\": ").Append(c.CardId).Append(",\n");
            if (!string.IsNullOrEmpty(c.LocalId))
                sb.Append("      \"local_id\": ").Append(JsonStr(c.LocalId)).Append(",\n");
            sb.Append("      \"name_eng\": ").Append(JsonStr(c.NameEng)).Append(",\n");
            sb.Append("      \"name_chs\": ").Append(JsonStr(c.NameChs ?? "")).Append(",\n");
            sb.Append("      \"kind\": ").Append(KindCode(c.Kind)).Append(",\n");
            sb.Append("      \"is_token\": ").Append(c.IsToken ? "true" : "false").Append(",\n");
            sb.Append("      \"clan\": ").Append(JsonStr(c.Clan ?? "中立")).Append(",\n");
            sb.Append("      \"traits\": ").Append(JsonArr(c.Traits)).Append(",\n");
            if (c.Keywords != null && c.Keywords.Count > 0)
                sb.Append("      \"keywords\": ").Append(JsonArr(c.Keywords)).Append(",\n");
            sb.Append("      \"cost\": ").Append(c.Cost).Append(",\n");
            sb.Append("      \"attack\": ").Append(c.Attack).Append(",\n");
            sb.Append("      \"life\": ").Append(c.Life).Append(",\n");
            // rarity：漏写这一行会把整个卡包里所有卡的稀有度清空。
            if (c.Rarity > 0)
                sb.Append("      \"rarity\": ").Append(c.Rarity).Append(",\n");
            sb.Append("      \"source_format\": ").Append(JsonStr(c.SourceFormat ?? "eng_cnl")).Append(",\n");

            if (c.DeckModifiers != null && c.DeckModifiers.Count > 0)
                sb.Append("      \"deck_modifiers\": ").Append(JsonDeckModArr(c.DeckModifiers)).Append(",\n");

            if (c.AltModes != null && c.AltModes.Count > 0)
                sb.Append("      \"alt_modes\": ").Append(JsonAltModeArr(c.AltModes)).Append(",\n");

            sb.Append("      \"text\": ").Append(JsonStr(c.Text ?? "")).Append("\n");
            sb.Append("    }").Append(i < pack.Cards.Count - 1 ? "," : "").Append("\n");
        }

        sb.Append("  ]\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>
    /// CardKind → pack.json 的整数编码。
    ///
    /// 必须与 CardPackLoader 的读取侧严格对齐：2=护符 3=法术 4=纹章 5=信仰 其余=随从。
    /// 直接写 (int)Kind 会把 Spell(1) 写成 1，加载器当成随从。
    /// </summary>
    private static int KindCode(CardKind k) => k switch
    {
        CardKind.Amulet => 2,
        CardKind.Spell => 3,
        CardKind.Crest => 4,
        CardKind.Faith => 5,
        _ => 1,
    };

    private static string JsonSymbolArr(IEnumerable<SymbolRawEntry> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null && !string.IsNullOrEmpty(x.Name))
            .Select(x => string.IsNullOrEmpty(x.NameChs)
                ? "{ \"name\": " + JsonStr(x.Name) + " }"
                : "{ \"name\": " + JsonStr(x.Name) + ", \"name_chs\": " + JsonStr(x.NameChs) + " }");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonCounterArr(IEnumerable<CounterRawEntry> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null && !string.IsNullOrEmpty(x.Name))
            .Select(x => "{ \"name\": " + JsonStr(x.Name)
                       + ", \"name_chs\": " + JsonStr(x.NameChs ?? x.Name)
                       + ", \"min\": " + x.Min + ", \"max\": " + x.Max + " }");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonStackableArr(IEnumerable<StackableAmuletSpec> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null).Select(x =>
            "{ \"counter_name\": " + JsonStr(x.CounterName)
            + ", \"default_token_card_id\": " + x.DefaultTokenCardId
            + ", \"match_trait_name\": " + JsonStr(x.MatchTraitName ?? "")
            + ", \"merge_policy\": " + JsonStr(x.MergePolicy.ToString())
            + ", \"protected_from_abilities\": " + (x.ProtectedFromAbilities ? "true" : "false") + " }");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonNamedDeckArr(IEnumerable<NamedDeckRawDef> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null).Select(nd =>
        {
            var es = (nd.Entries ?? new List<NamedDeckEntryRaw>())
                .Select(e => "{ \"card_id\": " + e.CardId
                           + ", \"card_name\": " + JsonStr(e.CardName ?? "")
                           + ", \"copies\": " + e.Copies + " }");
            return "{ \"deck_name\": " + JsonStr(nd.DeckName)
                 + ", \"deck_name_chs\": " + JsonStr(nd.DeckNameChs ?? "")
                 + ", \"entries\": [" + string.Join(", ", es) + "] }";
        });
        return "[" + string.Join(", ", parts) + "]";
    }

    /// <summary>
    /// 构筑修正的序列化。
    ///
    /// kind 必须写 CardPackLoader.ParseDeckMods 认的**短名**（size / leader_life /
    /// allow_clan_choose …），不能写枚举名。加载器遇到不认识的 kind 会静默落到
    /// ModifyDeckSize —— 于是「主战者 +10 血」存盘再读回来变成「牌组 +10 张」。
    /// 另外必须带 clan 与 value2，否则「允许指定职业」「超进化点」这些字段会丢。
    /// </summary>
    private static string JsonDeckModArr(IEnumerable<DeckBuildModifier> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null).Select(m =>
        {
            string kindStr = m.Kind switch
            {
                DeckModKind.ModifyDeckSize => "size",
                DeckModKind.ModifyLeaderStartLife => "leader_life",
                DeckModKind.ModifyStartingHandSize => "hand",
                DeckModKind.ModifyStartEvolvePoints => m.Value2 != 0 && m.Value == 0 ? "sep" : "ep",
                DeckModKind.OverrideSelfCopyLimit => "self_copy",
                DeckModKind.AllowSpecificClan => "allow_clan_fixed",
                DeckModKind.AllowChosenClans => "allow_clan_choose",
                _ => "size",
            };
            int val = kindStr == "sep" ? m.Value2 : m.Value;
            return "{ \"kind\": " + JsonStr(kindStr)
                 + ", \"value\": " + val
                 + ", \"value2\": " + m.Value2
                 + ", \"clan\": " + JsonStr(m.TargetClan ?? "")
                 + ", \"stack_per_copy\": " + (m.StackPerCopy ? "true" : "false")
                 + ", \"exclusive_group\": " + JsonStr(m.ExclusiveGroup ?? "") + " }";
        });
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonAltModeArr(IEnumerable<AltModeRawEntry> items)
    {
        if (items == null) return "[]";
        var parts = items.Where(x => x != null).Select(a =>
            "{ \"type_eng\": " + JsonStr(a.TypeEng ?? "")
            + ", \"cost\": " + a.Cost
            + ", \"text_eng\": " + JsonStr(a.TextEng ?? "") + " }");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string JsonStr(string s)
    {
        if (s == null) return "\"\"";
        var sb = new StringBuilder("\"");
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(ch); break;
            }
        }
        sb.Append("\"");
        return sb.ToString();
    }

    private static string JsonArr(IEnumerable<string> items)
    {
        if (items == null) return "[]";
        return "[" + string.Join(", ", items.Where(x => x != null).Select(JsonStr)) + "]";
    }

    private static string JsonNameArr(IEnumerable<string> items)
    {
        if (items == null) return "[]";
        return "[" + string.Join(", ", items.Where(x => x != null)
                                     .Select(x => "{ \"name\": " + JsonStr(x) + " }")) + "]";
    }

    /// <summary>
    /// 把一份 MatchRules 写回 data/pools/&lt;id&gt;.pool.json 并热重载。
    ///
    /// 用反射序列化全部字段 —— 手写 36 项既会漏，也会在 MatchRules
    /// 加字段时悄悄丢数据（新字段存不进去，下次读回来是默认值）。
    /// </summary>
    public bool SavePoolRules(string poolId, MatchRules rules, out string message)
    {
        message = "";
        Initialize();
        if (!PoolConfigs.TryGetValue(poolId, out var cfg))
        {
            message = "找不到卡池 " + poolId;
            return false;
        }

        var poolsDir = Path.Combine(DataRoot, "pools");
        Directory.CreateDirectory(poolsDir);

        // 优先写回原文件。PoolId 与文件名未必同名
        // （official-standard.pool.json 里装的是 pool_id="standard"），
        // 按 id 拼名会造出第二个同 id 文件，两边的规则互相覆盖。
        var path = PoolFilePaths.TryGetValue(poolId, out var existingPath)
                   && !string.IsNullOrEmpty(existingPath)
            ? existingPath
            : Path.Combine(poolsDir, poolId + ".pool.json");

        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"pool_id\": ").Append(JsonStr(cfg.PoolId)).Append(",\n");
        sb.Append("  \"name\": ").Append(JsonStr(cfg.Name ?? cfg.PoolId)).Append(",\n");
        sb.Append("  \"enabled_packs\": ").Append(JsonArr(cfg.EnabledPackIds)).Append(",\n");

        // 卡池级职业与主战者计数器（165 专题二断链 A）。
        // 不写回的话，界面上勾选的职业与新增的计数器在下一次重载时全部丢失。
        sb.Append("  \"pool_clans\": ").Append(JsonArr(cfg.PoolClans ?? new List<string>())).Append(",\n");
        sb.Append("  \"leader_counters\": [\n");
        var ctrs = (cfg.LeaderCounters ?? new List<ShadowverseFan.KernelV3.Packs.PoolCounterDef>())
            .Where(x => x != null && !x.IsBuiltin).ToList();
        for (int ci = 0; ci < ctrs.Count; ci++)
        {
            var c = ctrs[ci];
            sb.Append("    { \"name\": ").Append(JsonStr(c.Name))
              .Append(", \"name_chs\": ").Append(JsonStr(c.NameChs ?? ""))
              .Append(", \"min\": ").Append(c.Min)
              .Append(", \"max\": ").Append(c.Max)
              .Append(", \"initial\": ").Append(c.Initial)
              .Append(", \"lifecycle\": ").Append(JsonStr(c.Lifecycle.ToString()))
              .Append(" }").Append(ci < ctrs.Count - 1 ? ",\n" : "\n");
        }
        sb.Append("  ],\n");
        sb.Append("  \"rules\": {\n");

        var props = typeof(MatchRules).GetProperties()
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        for (int i = 0; i < props.Count; i++)
        {
            var p = props[i];
            object v;
            try { v = p.GetValue(rules); }
            catch { continue; }
            if (v == null) continue;

            // 字段名转 snake_case，与现有 pool.json 的写法一致
            sb.Append("    \"").Append(ToSnake(p.Name)).Append("\": ");
            if (v is bool b) sb.Append(b ? "true" : "false");
            else if (v is int n) sb.Append(n);
            else if (v is string sv) sb.Append(JsonStr(sv));
            else sb.Append(JsonStr(v.ToString()));
            sb.Append(i < props.Count - 1 ? ",\n" : "\n");
        }

        sb.Append("  },\n");

        // 具名牌组归属卡池：写回时必须原样带上，否则保存一次卡池规则
        // 就会把 .pool.json 里的 named_decks 整段抹掉。
        sb.Append("  \"named_decks\": [");
        var nds = cfg.NamedDecks ?? new List<NamedDeckRawDef>();
        if (nds.Count == 0) sb.Append("],\n");
        else
        {
            sb.Append('\n');
            for (int i = 0; i < nds.Count; i++)
            {
                var nd = nds[i];
                sb.Append("    {\n");
                sb.Append("      \"deck_name\": ").Append(JsonStr(nd.DeckName)).Append(",\n");
                sb.Append("      \"deck_name_chs\": ").Append(JsonStr(nd.DeckNameChs ?? "")).Append(",\n");
                sb.Append("      \"entries\": [");
                var es = nd.Entries ?? new List<NamedDeckEntryRaw>();
                if (es.Count == 0) sb.Append("]\n");
                else
                {
                    sb.Append('\n');
                    for (int k = 0; k < es.Count; k++)
                    {
                        var e = es[k];
                        sb.Append("        { \"card_id\": ").Append(e.CardId)
                          .Append(", \"card_name\": ").Append(JsonStr(e.CardName ?? ""))
                          .Append(", \"copies\": ").Append(e.Copies).Append(" }")
                          .Append(k < es.Count - 1 ? ",\n" : "\n");
                    }
                    sb.Append("      ]\n");
                }
                sb.Append("    }").Append(i < nds.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n");
        }

        // 末尾补一个无害字段收尾，保证上一段逗号合法。
        sb.Append("  \"_rev\": \"reshadow\"\n");
        sb.Append("}\n");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));

        ReloadCatalogAndPools();
        message = "已保存卡池 [" + poolId + "] 的 " + props.Count + " 项规则并热重载";
        return true;
    }

    private static string ToSnake(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ================================================================ 卡包 / 卡池 CRUD

    /// <summary>新建空白卡包，或更新已有卡包的元信息（保留其卡牌与符号表）。</summary>
    public bool CreateOrUpdatePack(string packId, string name, string author, string nativePoolId,
                                   out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(packId)) { message = "卡包 ID 不能为空"; return false; }
        packId = packId.Trim();
        Initialize();

        var packDir = Path.Combine(DataRoot, "packs", packId);
        Directory.CreateDirectory(packDir);
        Directory.CreateDirectory(Path.Combine(packDir, "cards"));
        var packFile = Path.Combine(packDir, "pack.json");

        CardPackDef existing = File.Exists(packFile) ? CardPackLoader.LoadFromFile(packFile) : null;
        var pack = new CardPackDef
        {
            PackId = packId,
            Name = string.IsNullOrWhiteSpace(name) ? packId : name.Trim(),
            Author = author?.Trim() ?? "",
            Version = existing?.Version ?? "1.0.0",
            NativePoolId = string.IsNullOrWhiteSpace(nativePoolId) ? "standard" : nativePoolId.Trim(),
            Dependencies = existing?.Dependencies ?? new(),
            Traits = existing?.Traits ?? new(),
            Clans = existing?.Clans ?? new(),
            Keywords = existing?.Keywords ?? new(),
            Counters = existing?.Counters ?? new(),
            StackableAmulets = existing?.StackableAmulets ?? new(),
            NamedDecks = existing?.NamedDecks ?? new(),
            Cards = existing?.Cards ?? new(),
        };

        File.WriteAllText(packFile, SerializePacket(pack), new UTF8Encoding(false));

        // 断链 1（168 验收）：新包必须自动进原生卡池的启用列表。
        //
        // CardPoolBuilder 有一条铁律：某卡包的 NativePoolId 指向本卡池、
        // 但 enabled_packs 里没有它，就**直接拒绝构建整个卡池**。
        // 所以只写 pack.json 不勾启用，下一次 Reload 会把那座卡池整个炸掉，
        // 表现为「新建空卡包后卡池消失了」。
        if (PoolConfigs.TryGetValue(pack.NativePoolId, out var nativeCfg) &&
            !nativeCfg.EnabledPackIds.Any(x => string.Equals(x, packId, StringComparison.OrdinalIgnoreCase)))
        {
            nativeCfg.EnabledPackIds.Add(packId);
            SavePoolRules(nativeCfg.PoolId, nativeCfg.Rules, out _);
            // 这里曾经写成 PoolConfigs[packId] = nativeCfg —— 把包 ID 当成卡池 ID 塞进字典，
            // 从此 PoolConfigs 里多出一个叫「某卡包」的假卡池，而它指向的配置还是别人的。
            // nativeCfg 已经在 PoolConfigs[nativeCfg.PoolId] 里，不需要再写一次。
        }

        ReloadCatalogAndPools();
        message = existing == null ? $"已新建空白卡包 [{packId}]" : $"已更新卡包 [{packId}] 信息";
        return true;
    }

    /// <summary>删除整个卡包。官方核心包不可删，并会从所有卡池的启用列表里摘掉。</summary>
    public bool DeletePack(string packId, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(packId)) { message = "未选择卡包"; return false; }
        if (string.Equals(packId, "official-sv", StringComparison.OrdinalIgnoreCase))
        { message = "官方卡包 [official-sv] 不可删除"; return false; }

        var packDir = Path.Combine(DataRoot, "packs", packId);
        if (Directory.Exists(packDir)) Directory.Delete(packDir, recursive: true);

        // 同步从所有卡池的启用列表移除，否则卡池会因「引用了不存在的包」而建不起来。
        foreach (var kv in PoolConfigs.ToList())
        {
            if (kv.Value.EnabledPackIds.RemoveAll(x => string.Equals(x, packId, StringComparison.OrdinalIgnoreCase)) > 0)
                SavePoolRules(kv.Key, kv.Value.Rules, out _);
        }

        ReloadCatalogAndPools();
        message = $"已删除卡包 [{packId}]";
        return true;
    }

    /// <summary>从指定卡包里删掉一张卡。</summary>
    public bool DeleteCardFromPack(string packId, int cardId, out string message)
    {
        message = "";
        var packFile = Path.Combine(DataRoot, "packs", packId ?? "", "pack.json");
        if (!File.Exists(packFile)) { message = "找不到卡包文件"; return false; }

        var pack = CardPackLoader.LoadFromFile(packFile);
        int removed = pack.Cards.RemoveAll(c => c.CardId == cardId);
        if (removed == 0) { message = $"卡包 [{packId}] 中未找到卡号 {cardId}"; return false; }

        File.WriteAllText(packFile, SerializePacket(pack), new UTF8Encoding(false));
        ReloadCatalogAndPools();
        message = $"已从 [{packId}] 删除卡牌 {cardId}";
        return true;
    }

    /// <summary>整体保存一个卡池（名称 / 启用卡包 / 规则 / 具名牌组）。</summary>
    public bool SavePoolConfigFull(
        string poolId, string poolName, List<string> enabledPacks,
        MatchRules rules, List<NamedDeckRawDef> namedDecks, out string message,
        List<string> poolClans = null, List<ShadowverseFan.KernelV3.Packs.PoolCounterDef> leaderCounters = null,
        Dictionary<string, string> poolClanNames = null)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(poolId)) { message = "卡池 ID 不能为空"; return false; }
        poolId = poolId.Trim();
        Initialize();

        var updated = new CardPoolConfig
        {
            PoolId = poolId,
            Name = string.IsNullOrWhiteSpace(poolName) ? poolId : poolName.Trim(),
            EnabledPackIds = enabledPacks ?? new List<string> { "official-sv" },
            Rules = rules ?? MatchRules.CreateStandard(),
            NamedDecks = namedDecks ?? new List<NamedDeckRawDef>(),
            // 职业与主战者计数器归属卡池（164 专题二）。
            // 不传就沿用已有值：只改规则的调用方不该把它们抹掉。
            PoolClans = poolClans ?? (PoolConfigs.TryGetValue(poolId, out var prev) ? prev.PoolClans : new List<string>()),
            LeaderCounters = leaderCounters ?? (PoolConfigs.TryGetValue(poolId, out var prev2) ? prev2.LeaderCounters : new List<ShadowverseFan.KernelV3.Packs.PoolCounterDef>()),
        };
        PoolConfigs[poolId] = updated;
        return SavePoolRules(poolId, updated.Rules, out message);
    }


    /// <summary>
    /// 把卡池连同它启用的全部卡包导出成单个 JSON（167 专项二）。
    ///
    /// 分享自制赛制时只发 .pool.json 会因为缺卡包直接建池失败，
    /// 而报错只说「跨包依赖缺失」，对方根本不知道该找哪个包。
    /// 单文件整合包把两者一起带走，导入即可用。
    /// </summary>
    public bool ExportPoolBundleToFile(string poolId, string targetFilePath, out string message)
    {
        message = "";
        Initialize();
        if (!PoolConfigs.TryGetValue(poolId, out var cfg))
        {
            message = "找不到卡池 [" + poolId + "]";
            return false;
        }

        try
        {
            var bundle = new System.Text.Json.Nodes.JsonObject
            {
                ["bundle_format"] = "reshadow_pool_bundle_v1",
            };

            // 1) 卡池自身：优先读原文件，保证字段一个不漏
            string poolPath = PoolFilePaths.TryGetValue(poolId, out var p) && File.Exists(p)
                ? p : Path.Combine(DataRoot, "pools", poolId + ".pool.json");
            if (!File.Exists(poolPath)) SavePoolRules(poolId, cfg.Rules, out _);
            bundle["pool"] = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(poolPath, System.Text.Encoding.UTF8));

            // 2) 依赖闭包：把启用包 + 它们依赖的包全部收进来
            var wanted = new List<string>();
            void Collect(string id)
            {
                if (string.IsNullOrWhiteSpace(id) || wanted.Contains(id)) return;
                if (!Catalog.Packs.TryGetValue(id, out var pk)) return;
                wanted.Add(id);
                foreach (var dep in pk.Dependencies ?? new List<string>()) Collect(dep);
            }
            foreach (var id in cfg.EnabledPackIds ?? new List<string>()) Collect(id);

            var packsArr = new System.Text.Json.Nodes.JsonArray();
            foreach (var id in wanted)
            {
                string packPath = Path.Combine(DataRoot, "packs", id, "pack.json");
                if (!File.Exists(packPath)) continue;
                packsArr.Add(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(packPath, System.Text.Encoding.UTF8)));
            }
            bundle["packs"] = packsArr;

            var opt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(targetFilePath, bundle.ToJsonString(opt) + "\n", new System.Text.UTF8Encoding(false));
            message = "已导出卡池【" + (cfg.Name ?? poolId) + "】含 " + packsArr.Count + " 个卡包";
            return true;
        }
        catch (Exception ex)
        {
            message = "导出失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 导入单个 JSON 整合包（167 专项二）。
    ///
    /// 卡包按 pack_id 写到 data/packs/&lt;id&gt;/pack.json，已存在则覆盖 ——
    /// 整合包里的卡包就是作者当时那一份，用本地的旧版会与他分享的赛制不一致。
    /// </summary>
    public bool ImportPoolBundleFromFile(string sourceFilePath, out string message)
    {
        message = "";
        Initialize();
        try
        {
            if (!File.Exists(sourceFilePath)) { message = "文件不存在"; return false; }
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sourceFilePath, System.Text.Encoding.UTF8));
            if (root == null) { message = "文件内容为空"; return false; }
            string fmt = root["bundle_format"]?.GetValue<string>();
            if (fmt != "reshadow_pool_bundle_v1")
            {
                message = "不是卡池整合包（bundle_format=" + (fmt ?? "缺失") + "）";
                return false;
            }

            string poolsDir = Path.Combine(DataRoot, "pools");
            Directory.CreateDirectory(poolsDir);

            // 1) 先落卡包：建池时要用它们
            int packCount = 0;
            if (root["packs"] is System.Text.Json.Nodes.JsonArray packs)
            {
                var opt = new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                };
                foreach (var pk in packs)
                {
                    string pid = pk?["pack_id"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(pid)) continue;
                    string dir = Path.Combine(DataRoot, "packs", pid);
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "pack.json"),
                                      pk.ToJsonString(opt) + "\n", new System.Text.UTF8Encoding(false));
                    packCount++;
                }
            }

            // 2) 再落卡池配置
            string poolId = root["pool"]?["pool_id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(poolId)) { message = "卡池缺少 pool_id"; return false; }
            var pOpt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(Path.Combine(poolsDir, poolId + ".pool.json"),
                              root["pool"].ToJsonString(pOpt) + "\n", new System.Text.UTF8Encoding(false));

            ReloadCatalogAndPools();
            message = "已导入卡池 [" + poolId + "] 与 " + packCount + " 个卡包";
            return true;
        }
        catch (Exception ex)
        {
            message = "导入失败：" + ex.Message;
            return false;
        }
    }


    /// <summary>
    /// 把一个自定义符号（兵种 / 计数器）写进指定卡包并落盘。
    ///
    /// 只加内存符号不够：下一次 ReloadCatalogAndPools 重建 Catalog 时，
    /// 没有数据支撑的符号会消失，玩家看到刚加的东西凭空不见。
    /// </summary>
    public bool AddPackSymbolEntry(string packId, string kind, string name,
                                   int min, int max, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(packId) || string.IsNullOrWhiteSpace(name))
        { message = "卡包或名称为空"; return false; }

        try
        {
            string file = Path.Combine(DataRoot, "packs", packId, "pack.json");
            if (!File.Exists(file)) { message = "找不到卡包 " + packId; return false; }

            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file, System.Text.Encoding.UTF8))
                       as System.Text.Json.Nodes.JsonObject;
            if (node == null) { message = "卡包文件格式异常"; return false; }

            string key = kind == "counters" ? "counters" : "traits";
            if (node[key] is not System.Text.Json.Nodes.JsonArray arr)
            {
                arr = new System.Text.Json.Nodes.JsonArray();
                node[key] = arr;
            }
            foreach (var e in arr)
                if (string.Equals(e?["name"]?.GetValue<string>(), name, StringComparison.OrdinalIgnoreCase))
                { message = "已存在：" + name; return true; }

            var entry = new System.Text.Json.Nodes.JsonObject { ["name"] = name };
            if (kind == "counters")
            {
                entry["min"] = min;
                entry["max"] = max;
            }
            arr.Add(entry);

            var opt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(file, node.ToJsonString(opt) + "\n", new UTF8Encoding(false));
            ReloadCatalogAndPools();
            message = "已写入卡包 " + packId;
            return true;
        }
        catch (Exception ex)
        {
            message = "写入失败：" + ex.Message;
            return false;
        }
    }


    /// <summary>
    /// 把卡包数据里的旧职业名换成新名。
    ///
    /// 改名字段本身不够：卡包的 clans 声明和卡片头部 clan= 都写着旧名，
    /// 重载后它们会把旧名重新注册进 Schema，界面上就变成
    /// 「旧名（未勾选）+ 新名（已勾选）」两个职业并存。
    /// </summary>
    public bool RenameClanInPacks(string oldName, string newName, out string message)
    {
        message = "";
        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
        { message = "名称不能为空"; return false; }

        int touched = 0;
        try
        {
            string root = Path.Combine(DataRoot, "packs");
            if (!Directory.Exists(root)) { message = "没有卡包目录"; return false; }

            var opt = new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };

            foreach (var dir in Directory.GetDirectories(root))
            {
                string file = Path.Combine(dir, "pack.json");
                if (!File.Exists(file)) continue;
                string raw = File.ReadAllText(file, System.Text.Encoding.UTF8);
                if (!raw.Contains(oldName, StringComparison.Ordinal)) continue;

                var node = System.Text.Json.Nodes.JsonNode.Parse(raw) as System.Text.Json.Nodes.JsonObject;
                if (node == null) continue;

                // clans 声明
                if (node["clans"] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var e in arr)
                        if (e is System.Text.Json.Nodes.JsonObject o &&
                            string.Equals(o["name"]?.GetValue<string>(), oldName, StringComparison.Ordinal))
                            o["name"] = newName;

                // 卡片头部 clan=
                if (node["cards"] is System.Text.Json.Nodes.JsonArray cards)
                    foreach (var e in cards)
                    {
                        if (e is not System.Text.Json.Nodes.JsonObject c) continue;
                        string cl = c["clan"]?.GetValue<string>();
                        if (string.Equals(cl, oldName, StringComparison.Ordinal))
                        {
                            c["clan"] = newName;
                            string txt = c["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(txt))
                                c["text"] = txt.Replace("clan=" + oldName, "clan=" + newName);
                            touched++;
                        }
                    }

                File.WriteAllText(file, node.ToJsonString(opt) + "\n", new UTF8Encoding(false));
            }

            ReloadCatalogAndPools();
            message = "已把卡包里的「" + oldName + "」改为「" + newName + "」（" + touched + " 张卡）";
            return true;
        }
        catch (Exception ex)
        {
            message = "改名失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>删除一个卡池。标准卡池不可删。</summary>
    public bool DeletePool(string poolId, out string message)
    {
        message = "";
        if (string.Equals(poolId, "standard", StringComparison.OrdinalIgnoreCase))
        { message = "标准卡池 [standard] 不可删除"; return false; }

        if (PoolFilePaths.TryGetValue(poolId, out var path) && File.Exists(path))
            File.Delete(path);
        else
        {
            var fallback = Path.Combine(DataRoot, "pools", poolId + ".pool.json");
            if (File.Exists(fallback)) File.Delete(fallback);
        }

        PoolConfigs.Remove(poolId);
        PoolFilePaths.Remove(poolId);
        ReloadCatalogAndPools();
        message = $"已删除卡池 [{poolId}]";
        return true;
    }
}
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    public List<ConstructedDeck> SavedDecks { get; } = new();
    public ChineseCardPrinter CardPrinter { get; private set; }

    public string DataRoot { get; private set; } = "";

    /// <summary>加载全部内容。已加载过则直接返回（幂等）。</summary>
    public void Initialize()
    {
        if (Catalog != null) return;

        var packsRoot = CardPackLoader.ResolvePacksRoot()
            ?? throw new DirectoryNotFoundException("找不到 data/packs 目录");
        DataRoot = Directory.GetParent(packsRoot)?.FullName ?? packsRoot;

        // 1. 全量宇宙总表
        Catalog = GlobalCatalog.LoadAll(packsRoot);
        CardPrinter = new ChineseCardPrinter(Catalog.Glossary);

        // 2. 全部赛制卡池
        var poolsDir = Path.Combine(DataRoot, "pools");
        foreach (var cfg in PoolConfigLoader.LoadAllPools(poolsDir))
        {
            PoolConfigs[cfg.PoolId] = cfg;
            var built = CardPoolBuilder.Build(cfg, Catalog.Packs);
            if (built.Ok) PoolRuntimes[cfg.PoolId] = built.Pool;
        }

        // 3. 玩家构筑存档
        var decksDir = Path.Combine(DataRoot, "decks");
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
            LeaderClan = string.IsNullOrEmpty(leaderClan) ? "Neutral" : leaderClan,
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

        var decksDir = Path.Combine(DataRoot, "decks");
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
        var path = Path.Combine(DataRoot, "decks", deckId + ".deck.json");
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
}

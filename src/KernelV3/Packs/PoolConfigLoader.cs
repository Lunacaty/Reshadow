using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 磁盘目录约定（ReshadowGame/ 根目录）：
//   packs/<pack_id>/pack.json      物理卡包
//   pools/<name>.pool.json         卡池配置（启用哪些包 + 自定义规则）
//   decks/<name>.deck.json         局外构筑存档
//
// 三层分开存：卡包是内容、卡池是环境、构筑是玩家的配方。
// 混在一起的话，「换一个卡池」就等于「换一套卡包文件」。
// ============================================================================
public static class PoolConfigLoader
{
    /// <summary>从 .pool.json 读卡池配置。</summary>
    public static CardPoolConfig LoadPool(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var r = doc.RootElement;

        var rules = MatchRules.CreateStandard();
        if (r.TryGetProperty("rules", out var ru) && ru.ValueKind == JsonValueKind.Object)
        {
            rules = new MatchRules
            {
                LeaderInitialLife = I(ru, "leader_initial_life", rules.LeaderInitialLife),
                LeaderMaxLife = I(ru, "leader_max_life", rules.LeaderMaxLife),
                MaxBoardSize = I(ru, "max_board_size", rules.MaxBoardSize),
                MaxHandSize = I(ru, "max_hand_size", rules.MaxHandSize),
                MaxCrestSize = I(ru, "max_crest_size", rules.MaxCrestSize),
                InitialMaxPp = I(ru, "initial_max_pp", rules.InitialMaxPp),
                PpGainPerTurn = I(ru, "pp_gain_per_turn", rules.PpGainPerTurn),
                MaxPpCap = I(ru, "max_pp_cap", rules.MaxPpCap),
                OverflowThreshold = I(ru, "overflow_threshold", rules.OverflowThreshold),
                EnableExtraPpForSecondPlayer = B2(ru, "enable_extra_pp", "enable_extra_pp_for_second_player", rules.EnableExtraPpForSecondPlayer),
                ExtraPpEarlyPhaseLastTurn = I2(ru, "extra_pp_last_turn", "extra_pp_early_phase_last_turn", rules.ExtraPpEarlyPhaseLastTurn),
                StartingHandSize = I(ru, "starting_hand_size", rules.StartingHandSize),
                CardsDrawnPerTurn = I(ru, "cards_drawn_per_turn", rules.CardsDrawnPerTurn),
                FirstPlayerDrawsOnTurn1 = B2(ru, "first_player_draws_turn1", "first_player_draws_on_turn1", rules.FirstPlayerDrawsOnTurn1),
                EvolveUnlockTurnFirstPlayer = I2(ru, "evolve_unlock_p0", "evolve_unlock_turn_first_player", rules.EvolveUnlockTurnFirstPlayer),
                EvolveUnlockTurnSecondPlayer = I2(ru, "evolve_unlock_p1", "evolve_unlock_turn_second_player", rules.EvolveUnlockTurnSecondPlayer),
                SuperEvolveUnlockTurnFirstPlayer = I2(ru, "super_evolve_unlock_p0", "super_evolve_unlock_turn_first_player", rules.SuperEvolveUnlockTurnFirstPlayer),
                SuperEvolveUnlockTurnSecondPlayer = I2(ru, "super_evolve_unlock_p1", "super_evolve_unlock_turn_second_player", rules.SuperEvolveUnlockTurnSecondPlayer),
                EvolveBonusAttack = I(ru, "evolve_bonus_attack", rules.EvolveBonusAttack),
                EvolveBonusLife = I(ru, "evolve_bonus_life", rules.EvolveBonusLife),
                SuperEvolveBonusAttack = I(ru, "super_evolve_bonus_attack", rules.SuperEvolveBonusAttack),
                SuperEvolveBonusLife = I(ru, "super_evolve_bonus_life", rules.SuperEvolveBonusLife),
                MinDeckSize = I(ru, "min_deck_size", rules.MinDeckSize),
                MaxDeckSize = I(ru, "max_deck_size", rules.MaxDeckSize),
                MaxCopiesPerCard = I2(ru, "max_copies", "max_copies_per_card", rules.MaxCopiesPerCard),
                AllowCrossCraft = B(ru, "allow_cross_craft", rules.AllowCrossCraft),
                AllowTokensInConstructedDeck = B2(ru, "allow_tokens_in_deck", "allow_tokens_in_constructed_deck", rules.AllowTokensInConstructedDeck),
                // 这 9 项漏读会让 .pool.json 里写的规则被静默忽略（回落到默认值）。
                InitialEpFirstPlayer = I2(ru, "initial_ep_p0", "initial_ep_first_player", rules.InitialEpFirstPlayer),
                InitialEpSecondPlayer = I2(ru, "initial_ep_p1", "initial_ep_second_player", rules.InitialEpSecondPlayer),
                MaxEpCap = I(ru, "max_ep_cap", rules.MaxEpCap),
                InitialSepFirstPlayer = I2(ru, "initial_sep_p0", "initial_sep_first_player", rules.InitialSepFirstPlayer),
                InitialSepSecondPlayer = I2(ru, "initial_sep_p1", "initial_sep_second_player", rules.InitialSepSecondPlayer),
                MaxSepCap = I(ru, "max_sep_cap", rules.MaxSepCap),
                SuperEvolveOverflowDamage = I(ru, "super_evolve_overflow_damage", rules.SuperEvolveOverflowDamage),
                SkyboundArtThreshold = I(ru, "skybound_art_threshold", rules.SkyboundArtThreshold),
                SuperSkyboundArtThreshold = I(ru, "super_skybound_art_threshold", rules.SuperSkyboundArtThreshold),
            };
        }

        var enabled = new List<string>();
        if (r.TryGetProperty("enabled_packs", out var ep) && ep.ValueKind == JsonValueKind.Array)
            foreach (var x in ep.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) enabled.Add(x.GetString() ?? "");

        // 具名牌组：归属卡池配置（.pool.json 的 named_decks）。
        var namedDecks = new List<NamedDeckRawDef>();
        if (r.TryGetProperty("named_decks", out var nds) && nds.ValueKind == JsonValueKind.Array)
        {
            foreach (var nd in nds.EnumerateArray())
            {
                var entries = new List<NamedDeckEntryRaw>();
                if (nd.TryGetProperty("entries", out var es) && es.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in es.EnumerateArray())
                    {
                        entries.Add(new NamedDeckEntryRaw
                        {
                            CardId = I(e, "card_id", 0),
                            CardName = S(e, "card_name"),
                            Copies = I(e, "copies", 1),
                        });
                    }
                }
                namedDecks.Add(new NamedDeckRawDef
                {
                    DeckName = S(nd, "deck_name"),
                    DeckNameChs = S(nd, "deck_name_chs"),
                    Entries = entries,
                });
            }
        }

        // 卡池级职业与主战者计数器（165 专题二断链 A）。
        // 只存在于内存里、不落盘的话，界面上一改职业或删个计数器，
        // 一触发 ReloadCatalogAndPools 就全没了 —— 而且没有任何提示。
        var poolClans = new List<string>();
        if (r.TryGetProperty("pool_clans", out var pcs) && pcs.ValueKind == JsonValueKind.Array)
            foreach (var x in pcs.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                    poolClans.Add(x.GetString()!.Trim());

        var leaderCounters = new List<PoolCounterDef>();
        if (r.TryGetProperty("leader_counters", out var lcs) && lcs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in lcs.EnumerateArray())
            {
                Enum.TryParse<CounterLifecycle>(S(c, "lifecycle"), true, out var lc);
                string cn = S(c, "name");
                if (string.IsNullOrWhiteSpace(cn)) continue;
                leaderCounters.Add(new PoolCounterDef
                {
                    Name = cn, NameChs = S(c, "name_chs"),
                    Min = I(c, "min", 0), Max = I(c, "max", 99), Initial = I(c, "initial", 0),
                    Lifecycle = lc, IsBuiltin = false,
                    // 计数器的事件驱动规则（PyAST 文本），179 号文档 C6
                    RulesScript = S(c, "rules_script"),
                });
            }
        }

        return new CardPoolConfig
        {
            PoolId = S(r, "pool_id") is { Length: > 0 } pid ? pid : "standard",
            Name = S(r, "name") is { Length: > 0 } nm ? nm : "标准卡池",
            Rules = rules,
            EnabledPackIds = enabled,
            NamedDecks = namedDecks,
            PoolClans = poolClans,
            LeaderCounters = leaderCounters,
        };
    }

    /// <summary>从 .deck.json 读构筑存档。</summary>
    public static ConstructedDeck LoadDeck(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        var r = doc.RootElement;

        var cards = new List<(int, int)>();
        if (r.TryGetProperty("cards", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
                cards.Add((I(c, "card_id", 0), I(c, "count", 1)));
        }

        var chosen = new List<string>();
        if (r.TryGetProperty("chosen_extra_clans", out var ch) && ch.ValueKind == JsonValueKind.Array)
            foreach (var x in ch.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) chosen.Add(x.GetString() ?? "");

        return new ConstructedDeck
        {
            DeckId = S(r, "deck_id") is { Length: > 0 } did ? did : Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = S(r, "name") is { Length: > 0 } nm ? nm : "新建牌组",
            PoolId = S(r, "pool_id") is { Length: > 0 } pid ? pid : "standard",
            LeaderClan = S(r, "leader_clan") is { Length: > 0 } lc ? lc : "中立",
            ChosenExtraClans = chosen,
            Cards = cards,
        };
    }

    /// <summary>扫一个目录下的全部 *.pool.json。</summary>
    public static List<CardPoolConfig> LoadAllPools(string dir)
        => LoadAllPoolsWithPaths(dir).Select(x => x.cfg).ToList();

    /// <summary>
    /// 同 LoadAllPools，但把**每个卡池来自哪个文件**一并带出。
    ///
    /// 用途：保存规则时要写回原文件。文件名与 pool_id 未必同名
    /// （official-standard.pool.json 里的 pool_id 是 "standard"），
    /// 按 pool_id 拼文件名会在磁盘上造出两个 id 相同的文件互相打架。
    /// </summary>
    public static List<(CardPoolConfig cfg, string path)> LoadAllPoolsWithPaths(string dir)
    {
        var list = new List<(CardPoolConfig, string)>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.pool.json").OrderBy(x => x, StringComparer.Ordinal))
            list.Add((LoadPool(f), f));
        return list;
    }

    public static List<ConstructedDeck> LoadAllDecks(string dir)
    {
        var list = new List<ConstructedDeck>();
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.deck.json").OrderBy(x => x, StringComparer.Ordinal))
            list.Add(LoadDeck(f));
        return list;
    }

    private static string S(JsonElement e, string n)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int I(JsonElement e, string n, int d)
        => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;

    /// <summary>
    /// 双键名读取：先试缩写键（现有 .pool.json 的写法），再试完整 snake_case
    /// （GameSessionStore.SavePoolRules 按属性名反射写出的写法）。
    ///
    /// 少了这层兼容，玩家在「卡池管理」里改的 13 项规则——同名上限、进化解锁回合、
    /// 先手抽牌等——保存后全部因键名对不上而静默回落成默认值。
    /// </summary>
    private static int I2(JsonElement e, string shortKey, string longKey, int d)
        => e.TryGetProperty(shortKey, out var v1) && v1.ValueKind == JsonValueKind.Number ? v1.GetInt32()
         : e.TryGetProperty(longKey, out var v2) && v2.ValueKind == JsonValueKind.Number ? v2.GetInt32()
         : d;

    private static bool B2(JsonElement e, string shortKey, string longKey, bool d)
        => e.TryGetProperty(shortKey, out var v1) && (v1.ValueKind == JsonValueKind.True || v1.ValueKind == JsonValueKind.False) ? v1.GetBoolean()
         : e.TryGetProperty(longKey, out var v2) && (v2.ValueKind == JsonValueKind.True || v2.ValueKind == JsonValueKind.False) ? v2.GetBoolean()
         : d;

    private static bool B(JsonElement e, string n, bool d)
        => e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
           ? v.GetBoolean() : d;
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 卡包加载器：把 packs/<pack_id>/pack.json 读成 CardPackDef
//
// 加载器只负责「读」，不做任何校验 —— 校验属于 CardPoolBuilder 的活。
// 分开的理由：一个卡包单独看可能不完整（比如它引用了另一包的 Token），
// 只有在卡池层面才知道「这个引用是不是真的缺」。
// ============================================================================
public static class CardPackLoader
{
    /// <summary>从目录读一个卡包（目录下应有 pack.json）。</summary>
    public static CardPackDef LoadFromDirectory(string dir)
    {
        var file = Path.Combine(dir, "pack.json");
        if (!File.Exists(file)) throw new FileNotFoundException("卡包目录缺少 pack.json：" + dir);
        return LoadFromFile(file);
    }

    public static CardPackDef LoadFromFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
        return Parse(doc.RootElement);
    }

    public static CardPackDef Parse(JsonElement root)
    {
        var traits = ParseSymbols(root, "traits");
        var clans = ParseSymbols(root, "clans");
        var keywords = ParseSymbols(root, "keywords");
        var counters = new List<CounterRawEntry>();
        if (root.TryGetProperty("counters", out var cts) && cts.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cts.EnumerateArray())
            {
                counters.Add(new CounterRawEntry
                {
                    Name = Str(c, "name"),
                    NameChs = Str(c, "name_chs"),
                    Min = Int(c, "min", 0),
                    Max = Int(c, "max", 99),
                });
            }
        }

        var specs = new List<StackableAmuletSpec>();
        if (root.TryGetProperty("stackable_amulets", out var sms) && sms.ValueKind == JsonValueKind.Array)
        {
            foreach (var sm in sms.EnumerateArray())
            {
                specs.Add(new StackableAmuletSpec(
                    Str(sm, "counter_name"),
                    Int(sm, "default_token_card_id", 0),
                    Str(sm, "match_trait_name"),
                    Str(sm, "merge_policy") == "BanishOld" ? StackMergePolicy.BanishOld : StackMergePolicy.AbsorbQuietly,
                    Bool(sm, "protected_from_abilities", true)));
            }
        }

        var decks = new List<NamedDeckRawDef>();
        if (root.TryGetProperty("named_decks", out var nds) && nds.ValueKind == JsonValueKind.Array)
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
                            CardId = Int(e, "card_id", 0),
                            CardName = Str(e, "card_name"),
                            Copies = Int(e, "copies", 1),
                        });
                    }
                }
                decks.Add(new NamedDeckRawDef
                {
                    DeckName = Str(nd, "deck_name"),
                    DeckNameChs = Str(nd, "deck_name_chs"),
                    Entries = entries,
                });
            }
        }

        var cards = new List<CardRawEntry>();
        if (root.TryGetProperty("cards", out var cs) && cs.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in cs.EnumerateArray())
            {
                var tr = new List<string>();
                if (c.TryGetProperty("traits", out var ts) && ts.ValueKind == JsonValueKind.Array)
                    foreach (var t in ts.EnumerateArray()) tr.Add(t.GetString() ?? "");

                var alt = new List<AltModeRawEntry>();
                if (c.TryGetProperty("alt_modes", out var ams) && ams.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in ams.EnumerateArray())
                    {
                        alt.Add(new AltModeRawEntry
                        {
                            TypeEng = Str(m, "type_eng"),
                            Cost = Int(m, "cost", 0),
                            TextEng = Str(m, "text_eng"),
                        });
                    }
                }

                int kindNum = Int(c, "kind", 1);
                cards.Add(new CardRawEntry
                {
                    LocalId = Str(c, "local_id"),
                    CardId = Int(c, "card_id", 0),
                    // 卡包是编译产物：卡包里的 name 就是显示名（已本地化）。
                    // 老卡包可能还有 name_eng / name_chs 分列，按 name → name_chs → name_eng 回退，
                    // 这样新旧数据都能读，不需要两套加载器。
                    NameEng = PickName(c),
                    NameChs = Str(c, "name_chs"),
                    Kind = kindNum switch
                    {
                        2 => CardKind.Amulet,
                        3 => CardKind.Spell,
                        4 => CardKind.Crest,
                        5 => CardKind.Faith,
                        _ => CardKind.Follower,
                    },
                    IsToken = Bool(c, "is_token", false),
                    ArtCardId = Int(c, "art_card_id", 0),
                    Clan = Str(c, "clan") is { Length: > 0 } cl ? cl : "中立",
                    Traits = tr,
                    Cost = Int(c, "cost", 0),
                    // 「写了 cost: 0」（合法 0 费卡）与「根本没写 cost」必须区分，
                    // 否则 py_ast 卡的费用会被错误回退（179 号文档 暗坑三）。
                    HasExplicitCost = c.TryGetProperty("cost", out _),
                    Attack = Int(c, "attack", 0),
                    Life = Int(c, "life", 0),
                    SourceFormat = Str(c, "source_format") is { Length: > 0 } sf ? sf : "eng_cnl",
                    Text = Str(c, "text"),
                    Keywords = ParseStringList(c, "keywords"),
                    DeckModifiers = ParseDeckMods(c),
                    AltModes = alt,
                    Rarity = Int(c, "rarity", 0),
                });
            }
        }

        return new CardPackDef
        {
            PackId = Str(root, "pack_id"),
            Name = Str(root, "name"),
            Author = Str(root, "author"),
            Version = Str(root, "version") is { Length: > 0 } v ? v : "1.0.0",
            NativePoolId = Str(root, "native_pool_id") is { Length: > 0 } np ? np : "standard",
            Dependencies = ParseStringList(root, "dependencies"),
            Traits = traits,
            Clans = clans,
            Keywords = keywords,
            Counters = counters,
            StackableAmulets = specs,
            NamedDecks = decks,
            Cards = cards,
        };
    }

    /// <summary>扫描 packs 根目录，加载全部卡包（按目录名排序保证可复现）。</summary>
    public static List<CardPackDef> LoadAll(string packsRoot)
    {
        var result = new List<CardPackDef>();
        if (!Directory.Exists(packsRoot)) return result;
        foreach (var dir in Directory.GetDirectories(packsRoot).OrderBy(x => x, StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "pack.json");
            if (File.Exists(file)) result.Add(LoadFromFile(file));
        }
        return result;
    }

    /// <summary>找 packs 根目录（从起始目录向上找）。</summary>
    public static string ResolvePacksRoot(string startDir = null)
    {
        var dir = startDir ?? AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var p = Path.Combine(dir, "data", "packs");
            if (Directory.Exists(p)) return p;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return null;
    }

    // ---- 小工具 ----

    /// <summary>
    /// 取卡名：name → name_chs → name_eng。
    ///
    /// 编译产物的卡包只有 name（已换成本地化卡名）；
    /// 老格式分列 name_eng / name_chs，按中文优先回退即可，不必维护两套加载路径。
    /// </summary>
    private static string PickName(JsonElement c)
    {
        string n = Str(c, "name");
        if (!string.IsNullOrWhiteSpace(n)) return n;
        n = Str(c, "name_chs");
        if (!string.IsNullOrWhiteSpace(n)) return n;
        return Str(c, "name_eng");
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
           ? (v.GetString() ?? "") : "";

    private static int Int(JsonElement e, string name, int fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
           ? v.GetInt32() : fallback;

    private static bool Bool(JsonElement e, string name, bool fallback)
        => e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
           ? v.GetBoolean() : fallback;

    private static List<string> ParseStringList(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String) list.Add(x.GetString() ?? "");
        return list;
    }

    /// <summary>读卡包里的构筑期修正数组。</summary>
    private static List<DeckBuildModifier> ParseDeckMods(JsonElement e)
    {
        var list = new List<DeckBuildModifier>();
        if (!e.TryGetProperty("deck_modifiers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var x in arr.EnumerateArray())
        {
            var kindStr = Str(x, "kind");
            DeckModKind kind = kindStr switch
            {
                "size" => DeckModKind.ModifyDeckSize,
                "leader_life" => DeckModKind.ModifyLeaderStartLife,
                "hand" => DeckModKind.ModifyStartingHandSize,
                "ep" => DeckModKind.ModifyStartEvolvePoints,
                "sep" => DeckModKind.ModifyStartEvolvePoints,
                "self_copy" => DeckModKind.OverrideSelfCopyLimit,
                "allow_clan_fixed" => DeckModKind.AllowSpecificClan,
                "allow_clan_choose" => DeckModKind.AllowChosenClans,
                _ => DeckModKind.ModifyDeckSize,
            };
            bool isSep = kindStr == "sep";
            int v = Int(x, "value", 0);
            list.Add(new DeckBuildModifier
            {
                Kind = kind,
                Value = isSep ? 0 : v,
                Value2 = isSep ? v : Int(x, "value2", 0),
                TargetClan = Str(x, "clan"),
                StackPerCopy = Bool(x, "stack_per_copy", false),
                ExclusiveGroup = Str(x, "exclusive_group"),
            });
        }
        return list;
    }

    private static List<SymbolRawEntry> ParseSymbols(JsonElement e, string name)
    {
        var list = new List<SymbolRawEntry>();
        if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
            {
                list.Add(new SymbolRawEntry
                {
                    Name = Str(x, "name"),
                    NameChs = Str(x, "name_chs"),
                });
            }
        }
        return list;
    }
}

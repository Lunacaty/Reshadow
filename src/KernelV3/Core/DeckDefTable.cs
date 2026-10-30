using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 具名牌组表（DeckDefTable）
//
// 卡面上出现的「以某副牌库替换你的牌库」里的牌库，是一套预先定义好的卡组，
// 不是一张卡。例如 Apocalypse Deck：
//     3 × Silent Rider          (90004110)
//     3 × Servant of Cocytus    (90004120)
//     3 × Demon of Purgatory    (90004130)
//     1 × Astaroth's Reckoning  (90004310)
//
// 结构与 decks/deck2-v1.json 保持一致（cards[{card_id, copies}]），
// 这样同一份数据既能当预设牌组，也能当卡面上引用的具名牌组。
//
// 玩家 DIY 的具名牌组走 Register() 注册，与 GameSchema 的开放原则一致。
// ============================================================================

/// <summary>牌组中的一条：卡号 + 份数。</summary>
public readonly record struct DeckEntry(int CardId, int Copies);

public sealed class DeckDefTable
{
    private readonly Dictionary<string, List<DeckEntry>> _defs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册的具名牌组数量。</summary>
    public int Count => _defs.Count;

    /// <summary>注册（或覆盖）一个具名牌组。</summary>
    public void Register(string name, IEnumerable<DeckEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _defs[name.Trim()] = entries?.Where(e => e.CardId > 0 && e.Copies > 0).ToList()
                             ?? new List<DeckEntry>();
    }

    /// <summary>
    /// 按名字取牌组定义；未注册返回 null（调用方必须据此报错，不得静默生成空牌库）。
    ///
    /// 卡面里写的是 "the Apocalypse Deck"，冠词属于英文表面形式，
    /// 不属于牌组名本身。查找时统一剥掉前导 the/a/an，
    /// 否则每一条牌库替换都会因为一个冠词而失配。
    /// </summary>
    public IReadOnlyList<DeckEntry> TryGet(string name)
    {
        var key = Normalize(name);
        if (key.Length == 0) return null;
        return _defs.TryGetValue(key, out var list) ? list : null;
    }

    public bool Has(string name) => TryGet(name) != null;

    /// <summary>剥掉英文冠词与多余空白，得到真正的牌组名。</summary>
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        foreach (var art in new[] { "the ", "a ", "an " })
        {
            if (s.StartsWith(art, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(art.Length).Trim();
                break;
            }
        }
        return s;
    }

    /// <summary>把牌组展开成逐张的卡号序列（按份数重复）。</summary>
    public IReadOnlyList<int> Expand(string name)
    {
        var def = TryGet(name);
        if (def == null) return null;
        var list = new List<int>();
        foreach (var e in def)
            for (int i = 0; i < e.Copies; i++) list.Add(e.CardId);
        return list;
    }

    /// <summary>官方内置牌组。名称按卡面英文拼写，大小写不敏感。</summary>
    public static DeckDefTable CreateDefault()
    {
        var t = new DeckDefTable();
        t.Register("Apocalypse Deck", new[]
        {
            new DeckEntry(90004110, 3),   // Silent Rider
            new DeckEntry(90004120, 3),   // Servant of Cocytus
            new DeckEntry(90004130, 3),   // Demon of Purgatory
            new DeckEntry(90004310, 1),   // Astaroth's Reckoning
        });
        return t;
    }
}

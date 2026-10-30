using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// GameSchema —— 五张符号注册表的容器
//
// 这是「玩家可 DIY 兵种 / 资源 / 关键词」的落点：
//   · 内置词条由引擎在启动时注册（source = "builtin"）
//   · 玩家自造词条由 Mod 注册（source = "mod:<名字>"）
//   · 同名但来源不同 → 抛 SymbolConflictException，绝不静默合并
//
// ★ 关键约定：Lang 层**禁止**裸调 SymbolId<T>.FromName()。
//   FromName 产出的 Id 恒为 -1，会让所有计数器在运行时读写同一个槽位。
//   必须走 GameSchema 的 Register / Require 拿到真实正整数 Id。
// ============================================================================

public sealed class GameSchema
{
    /// <summary>计数器名（不区分大小写） -> (下限, 上限)。上限 -1 表示不限。</summary>
    public Dictionary<string, (int min, int max)> CounterLimits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把计数器值钳制到它自己的范围内。</summary>
    public int ClampCounter(SymbolId<CounterTag> ctr, int value)
    {
        if (ctr == null || !CounterLimits.TryGetValue(ctr.Name ?? "", out var lim)) return value;
        if (lim.max >= 0 && value > lim.max) value = lim.max;
        if (value < lim.min) value = lim.min;
        return value;
    }

    public SymbolRegistry<TraitTag> Traits { get; } = new();
    public SymbolRegistry<CounterTag> Counters { get; } = new();
    public SymbolRegistry<KeywordTag> Keywords { get; } = new();
    public SymbolRegistry<ClanTag> Clans { get; } = new();
    public SymbolRegistry<MacroTag> Macros { get; } = new();

    /// <summary>注册一个计数器并返回带真实 Id 的句柄（已在则复用）。</summary>
    public SymbolId<CounterTag> Counter(string name, string source = "builtin")
        => GetOrRegister(Counters, name, source);

    public SymbolId<KeywordTag> Keyword(string name, string source = "builtin")
        => GetOrRegister(Keywords, name, source);

    public SymbolId<TraitTag> Trait(string name, string source = "builtin")
        => GetOrRegister(Traits, name, source);

    public SymbolId<ClanTag> Clan(string name, string source = "builtin")
        => GetOrRegister(Clans, name, source);

    public SymbolId<MacroTag> Macro(string name, string source = "builtin")
        => GetOrRegister(Macros, name, source);

    private static SymbolId<T> GetOrRegister<T>(SymbolRegistry<T> reg, string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<T>.Invalid;
        var existing = reg.TryGet(name);
        return existing.IsValid ? existing : reg.Register(name, source);
    }

    /// <summary>引擎内置的全部资源计数器。</summary>
    /// <summary>
    /// 内置资源计数器的定义：名字 + 下限 + 上限（-1 表示不限）。
    /// 官方规则：能量点上限 10、进化点 2、超进化点 2。
    /// 没有上限约束时，效果会把 pp 写成负数或超量而不被发现。
    /// </summary>
    public static readonly (string name, int min, int max)[] BuiltinCounterDefs =
    {
        ("pp", 0, 10),
        // 打出这张卡之前的 PP。Enhance 分支靠它判定能走哪一档，
        // 因为基础费已经扣掉了。
        ("pp_at_play", 0, 10),
        ("max_pp", 0, 10),
        ("ep", 0, 2),
        ("sep", 0, 2),
        ("countdown", 0, 99),
        ("spellboost", 0, 99),
        ("earth_sigil", 0, 99),
        ("clues", 0, 99),
        ("crest", 0, 99),
        ("faith", 0, 99),
        ("shadow", 0, 999),
        ("X", 0, 999), ("Y", 0, 999), ("Z", 0, 999),
        ("turn_play", 0, 99),
        ("match_play", 0, 999),
        ("evolve_count", 0, 99),
        // 融合：fuse_count 记融合次数，fuse_cost 记累计材料费用
        ("fuse_count", 0, 99),
        ("fuse_cost", 0, 99),
        // 奥义槽 = 当前回合数 + 本卡在手时自己随从进化/超进化的次数。
        // 奥义 >= 10 可发动，解放奥义 >= 15。
        ("skybound_gauge", 0, 99),
    };

    public static readonly string[] BuiltinKeywords =
    {
        "ward", "rush", "storm", "bane", "drain", "ambush", "aura",
        "intimidate", "barrier", "cannot_attack", "cannot_destroy", "cannot_be_played",
        "attack_limit", "damage_cap", "ignores ward",
    };

    /// <summary>构造一份装好内置词条的 schema。</summary>
    public static GameSchema CreateDefault()
    {
        var s = new GameSchema();
        foreach (var (name, min, max) in BuiltinCounterDefs)
        {
            s.Counters.Register(name, "builtin");
            s.CounterLimits[name] = (min, max);
        }
        foreach (var k in BuiltinKeywords) s.Keywords.Register(k, "builtin");
        return s;
    }
}

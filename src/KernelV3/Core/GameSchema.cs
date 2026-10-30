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
    public static readonly string[] BuiltinCounters =
    {
        "pp", "max_pp", "ep", "sep", "countdown", "spellboost",
        "earth_sigil", "clues", "crest", "faith", "shadow",
        "X", "Y", "Z", "turn_play", "match_play", "evolve_count",
    };

    public static readonly string[] BuiltinKeywords =
    {
        "ward", "rush", "storm", "bane", "drain", "ambush", "aura",
        "intimidate", "barrier", "cannot_attack", "cannot_destroy",
        "attack_limit", "damage_cap", "ignores ward",
    };

    /// <summary>构造一份装好内置词条的 schema。</summary>
    public static GameSchema CreateDefault()
    {
        var s = new GameSchema();
        foreach (var c in BuiltinCounters) s.Counters.Register(c, "builtin");
        foreach (var k in BuiltinKeywords) s.Keywords.Register(k, "builtin");
        return s;
    }
}

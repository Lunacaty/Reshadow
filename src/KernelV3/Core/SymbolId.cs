using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 强类型符号 ID —— 解决「固定枚举 vs 玩家自由 DIY」的矛盾
//
// 设计依据：Gemini《54 Reshadow Kernel V3 蓝图》破局第一招。
//
// 核心思想：把「物理法则」与「内容词条」彻底分离。
//   · 物理法则（ZoneRef / CompareOp / CoreEvent / CoreOp）→ C# enum，写死，不可 DIY
//   · 内容词条（兵种 / 资源 / 关键词 / 职业）→ SymbolId<T>，由 GameSchema 注册
//
// 为什么不用 string：玩家把 "Officer" 拼成 "Oficer" 时无法静态报错。
// 为什么不用 enum：玩家无法 DIY 一个叫「高达」的新兵种。
//
// SymbolId<T> 两者兼得：
//   · 序列化时存名字（人可读、Mod 之间可合并）
//   · 加载时由 GameSchema 校验并分配整数 Id
//   · 类型参数 T 区分「兵种」「资源」「关键词」，编译期防止混用
// ============================================================================

/// <summary>符号种类标记：兵种 / 特性。</summary>
public sealed class TraitTag { }

/// <summary>符号种类标记：计数器 / 资源（PP、墓地、连击、玩家自定义的「剑意」…）。</summary>
public sealed class CounterTag { }

/// <summary>符号种类标记：关键词（守护、疾走、玩家自定义的「流血」…）。</summary>
public sealed class KeywordTag { }

/// <summary>符号种类标记：职业 / 阵营。</summary>
public sealed class ClanTag { }

/// <summary>符号种类标记：机制宏（葬送复活、魔力增幅、玩家自定义的复合动作）。</summary>
public sealed class MacroTag { }

/// <summary>
/// 一个强类型符号引用。
///
/// <see cref="Id"/> 是运行期分配的非负整数（0 保留表示 Invalid），
/// <see cref="Name"/> 是唯一真名（序列化时只存它）。
/// </summary>
public readonly record struct SymbolId<T>(int Id, string Name)
{
    /// <summary>空符号。任何使用空符号的 AST 节点在编译期就应被拒绝。</summary>
    public static SymbolId<T> Invalid => new(-1, "");

    public bool IsValid => Id >= 0 && !string.IsNullOrEmpty(Name);

    public override string ToString() => Name ?? "";

    /// <summary>反序列化时用：只有名字，Id 由 GameSchema 补齐。</summary>
    public static SymbolId<T> FromName(string name) => new(-1, name ?? "");

    public SymbolId<T> WithId(int id) => new(id, Name);
}

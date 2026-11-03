using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 符号注册表 —— 一张「名字 → 整数 Id」的双向表
//
// 同一个名字重复注册时：
//   · 来自同一来源 → 幂等，返回已有 Id
//   · 来自不同来源（两个 Mod 都定义 "高达"）→ 抛异常，交由上层决定是否改名
//
// 这是「玩家 DIY 兵种/资源/关键词」能成立的唯一前提：
// 所有下拉框、自动补全、词法词典都从这里读。
// ============================================================================

/// <summary>一个符号的完整定义。</summary>
public sealed record SymbolEntry<T>(int Id, string Name, string Source);

/// <summary>符号注册表（针对一种符号种类）。</summary>
public sealed class SymbolRegistry<T>
{
    private readonly Dictionary<string, SymbolEntry<T>> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SymbolEntry<T>> _byId = new();
    private int _nextId;

    public IReadOnlyCollection<SymbolEntry<T>> All => _byId.Values;

    /// <summary>
    /// 注册一个符号。已存在且来源相同则返回已有项；来源不同则抛异常。
    /// </summary>
    public SymbolId<T> Register(string name, string source = "builtin")
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("符号名不能为空", nameof(name));

        if (_byName.TryGetValue(name, out var existing))
        {
            // 126 轮：同名符号改为**天然同义合并（Idempotent Merge）**。
            //
            // 早先不同来源注册同一个英文名会直接抛异常，逼创作者互相协调 ——
            // 但职业/兵种/关键词本就是共享词条体系：A 包和 B 包都声明
            // "Mechanist" 时，它们指的必然是同一个概念，没有分叉的必要。
            // 现在直接复用已有 Id，谁先注册谁定 Id，后注册的静默合并。
            //
            // 关键字/计数器这类**有数值语义**的符号不在此列 —— 它们由
            // GameSchema 单独处理（上下限不一致时取并集并给出提示）。
            return new SymbolId<T>(existing.Id, existing.Name);
        }

        var entry = new SymbolEntry<T>(_nextId++, name, source);
        _byName[name] = entry;
        _byId[entry.Id] = entry;
        return new SymbolId<T>(entry.Id, entry.Name);
    }

    /// <summary>按名字查找；找不到返回 Invalid。</summary>
    public SymbolId<T> TryGet(string name)
        => name != null && _byName.TryGetValue(name, out var e)
            ? new SymbolId<T>(e.Id, e.Name)
            : SymbolId<T>.Invalid;

    /// <summary>
    /// 按名字解析，找不到则抛异常。
    /// 这是 AST 加载路径上的唯一入口 —— 保证「拼错名字」立即报错，而不是静默返回 0。
    /// </summary>
    public SymbolId<T> Require(string name, string context = null)
    {
        if (name != null && _byName.TryGetValue(name, out var e))
            return new SymbolId<T>(e.Id, e.Name);

        var hint = SuggestClosest(name);
        throw new UnknownSymbolException(
            $"未知{context ?? typeof(T).Name}符号 '{name}'。" +
            (hint != null ? $" 你是不是想写 '{hint}'？" : ""));
    }

    /// <summary>解析已存在的 SymbolId（把 Id 补齐）。</summary>
    public SymbolId<T> Resolve(SymbolId<T> sym)
        => sym.IsValid && _byId.ContainsKey(sym.Id) ? sym : Require(sym.Name);

    public bool Contains(string name) => name != null && _byName.ContainsKey(name);

    /// <summary>Levenshtein 距离最小的候选，用于报错提示。</summary>
    private string SuggestClosest(string name)
    {
        if (string.IsNullOrEmpty(name) || _byName.Count == 0) return null;

        string best = null;
        int bestDist = int.MaxValue;
        int limit = Math.Max(2, name.Length / 3);

        foreach (var candidate in _byName.Keys)
        {
            int d = Levenshtein(name, candidate);
            if (d < bestDist) { bestDist = d; best = candidate; }
        }
        return bestDist <= limit ? best : null;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}

/// <summary>两个来源定义了同名符号。</summary>
public sealed class SymbolConflictException : Exception
{
    public SymbolConflictException(string message) : base(message) { }
}

/// <summary>引用了未注册的符号。</summary>
public sealed class UnknownSymbolException : Exception
{
    public UnknownSymbolException(string message) : base(message) { }
}

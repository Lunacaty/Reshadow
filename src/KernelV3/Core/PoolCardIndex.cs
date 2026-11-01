using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 卡池卡片索引表（PoolCardIndex）
//
// 问题：引擎全靠 int CardId 认卡。作者 A 用 1001、作者 B 也用 1001，
// 两个包放进同一个卡池就互相覆盖。
//
// 解法：作者只写包内局部编号（local_id）或 "pack:local" 形式的命名空间键，
// 由**卡池**统一分配局内唯一的 int CardId，并维护双向映射。
//
// 分配策略是确定性的（按 PackId 排序 + LocalId 升序），
// 同一个卡池组合每次分配结果完全一致 —— 否则存档里的卡号会漂移。
// ============================================================================
public sealed class PoolCardIndex
{
    /// <summary>局内卡号起点。留出 0..999999 给引擎内置。</summary>
    public const int BaseOffset = 1_000_000;

    private readonly Dictionary<string, int> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _byId = new();
    private int _next = BaseOffset;

    /// <summary>命名空间键：pack_id:local_id。</summary>
    public static string Key(string packId, string localId) => packId + ":" + localId;

    /// <summary>登记一张卡并分配（或复用）局内卡号。</summary>
    public int Assign(string packId, string localId)
    {
        var key = Key(packId, localId ?? "");
        if (_byKey.TryGetValue(key, out var existed)) return existed;
        int id = _next++;
        _byKey[key] = id;
        _byId[id] = key;
        return id;
    }

    /// <summary>直接登记一个**已显式给出**的卡号（作者自己指定了 4 亿以下的号）。</summary>
    public void RegisterExplicit(string packId, string localId, int cardId)
    {
        var key = Key(packId, localId ?? "");
        _byKey[key] = cardId;
        _byId[cardId] = key;
    }

    public bool TryGetId(string packId, string localId, out int cardId)
        => _byKey.TryGetValue(Key(packId, localId), out cardId);

    public bool TryGetKey(int cardId, out string key) => _byId.TryGetValue(cardId, out key);

    /// <summary>这条卡号属于哪个卡包。</summary>
    public string PackOf(int cardId)
    {
        if (!_byId.TryGetValue(cardId, out var key)) return "";
        int i = key.IndexOf(':');
        return i < 0 ? key : key.Substring(0, i);
    }

    public int Count => _byId.Count;

    /// <summary>确定性分配：同一卡池组合每次结果一致。</summary>
    public IReadOnlyDictionary<string, int> Snapshot() => _byKey;
}

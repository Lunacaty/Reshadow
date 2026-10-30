using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 执行作用域 (VmScope)
//
// 这是对 Bloodymoon「五个上下文元素散落在参数里」的正面解法。
// Bloodymoon 的 AddEffect 签名：
//   AddEffect(group, source, manualTargets, EffectsIndex, log)
// 五个参数全是上下文的一个片段，没有一个是完整的上下文。
//
// V3 把它们统一成一个对象：
//   Registers   ← manualTargets / ctxEntities 的强类型版本（槽位寄存器）
//   TriggerEvent← log
//   Source/Trigger/Chosen ← 对应 SVLab 的 context 五字段
// ============================================================================

public sealed class VmScope
{
    /// <summary>谁在发起这次效果。</summary>
    public ZoneScope Owner { get; init; }

    /// <summary>来源卡（可能已离场，所以同时存快照）。</summary>
    public EntityRef? Source { get; set; }

    /// <summary>触发本次能力的实体。</summary>
    public EntityRef? Trigger { get; set; }

    /// <summary>触发事件本身（对应 Bloodymoon 的 PowerLogEntry）。</summary>
    public VmEvent? Event { get; set; }

    /// <summary>本次选择的结果（对应 SVLab 的 context.chosen）。</summary>
    public EntityRef? Chosen { get; set; }

    /// <summary>
    /// 槽位寄存器 —— 取代 V2 的单槽位 Scope.Previous。
    /// 键是槽位名（$self / $selected / $created / $trigger / $item / 玩家自定义）。
    /// </summary>
    public Dictionary<string, List<EntityRef>> Registers { get; } = new(StringComparer.Ordinal);

    /// <summary>当前循环的迭代次数（防死循环用）。</summary>
    public int LoopDepth { get; set; }

    // ---------- 槽位操作 ----------

    public void Bind(string slot, IEnumerable<RuntimeEntity> entities)
    {
        if (string.IsNullOrEmpty(slot)) return;
        Registers[slot] = entities?.Where(e => e != null).Select(EntityRef.From).ToList()
                          ?? new List<EntityRef>();
    }

    public void BindRefs(string slot, IEnumerable<EntityRef> refs)
    {
        if (string.IsNullOrEmpty(slot)) return;
        Registers[slot] = refs?.ToList() ?? new List<EntityRef>();
    }

    public IReadOnlyList<EntityRef> Read(string slot)
        => slot != null && Registers.TryGetValue(slot, out var list)
           ? list
           : Array.Empty<EntityRef>();

    public void Clear(string slot) => Registers.Remove(slot);

    /// <summary>派生一个子作用域（循环体 / 子帧）。寄存器独立，来源信息继承。</summary>
    public VmScope Derive()
    {
        var s = new VmScope
        {
            Owner = Owner,
            Source = Source,
            Trigger = Trigger,
            Event = Event,
            Chosen = Chosen,
            LoopDepth = LoopDepth + 1,
        };
        foreach (var kv in Registers) s.Registers[kv.Key] = new List<EntityRef>(kv.Value);
        return s;
    }
}

/// <summary>触发事件的描述（对应 Bloodymoon 的 PowerLogEntry）。</summary>
public sealed class VmEvent
{
    public CoreEvent Kind { get; init; }
    /// <summary>事件主体。</summary>
    public EntityRef? Subject { get; init; }
    /// <summary>事件副体（攻击目标 / 被弃的牌…）。</summary>
    public EntityRef? Object { get; init; }
    /// <summary>事件携带的数值（伤害量 / 抽牌数…）。</summary>
    public int Amount { get; init; }
}

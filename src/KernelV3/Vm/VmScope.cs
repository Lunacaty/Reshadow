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
    /// <summary>
    /// 谁在发起这次效果（AST 的相对视角：Self / Opponent）。
    /// 注意：这不是玩家编号，不能直接传给世界。传给世界前必须经 ResolvePlayer()。
    /// </summary>
    public ZoneScope Owner { get; init; }

    /// <summary>
    /// 绝对玩家编号：0 = 先手，1 = 后手。
    /// AST 是相对视角（这张卡的拥有者 vs 他的对手），战斗世界是绝对身份。
    /// 两者混用会导致「后手玩家打出的『伤害所有敌方随从』打在自己随从上」。
    /// </summary>
    public int Controller { get; init; }

    /// <summary>
    /// 上一次真正召唤成功的卡 ID。
    /// "Summon a X. ... summon 2 instead." 里第二句没有卡名，
    /// 靠这个记录补上前一句的卡（CardSourceKind.CopyOfPrevious）。
    /// </summary>
    public int LastSpawnedCardId { get; set; }

    /// <summary>把 AST 的相对视角翻译成世界的绝对玩家编号。</summary>
    public int ResolvePlayer(ZoneScope relative) => relative switch
    {
        ZoneScope.Self => Controller,
        ZoneScope.Opponent => 1 - Controller,
        _ => -1,   // Any：双方
    };

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
            Controller = Controller,
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

    /// <summary>
    /// 这次进化是不是玩家手动花 EP/SEP 触发的。
    /// 只有手动进化才发动卡面的 Evolve: / Super-Evolve: 冒号头；
    /// 能力效果自动进化只触发 When/Whenever ... evolves 类事件从句。
    /// </summary>
    public bool IsManualEvolve { get; init; }

    /// <summary>
    /// 待机列入列优先级（数值越小越先），见 R-3.3：
    ///   10 入场曲 / 进化时 / 超进化时 / 启动
    ///   20 场上出现时
    ///   30 攻击方 攻撃时 / 交戦时
    ///   40 被攻击方 交戦时
    ///   50 当前回合玩家 纹章 / 信仰
    ///   60 当前回合玩家 场上卡
    ///   70 非当前回合玩家 纹章
    ///   80 非当前回合玩家 场上卡
    ///   90 直接召唤
    /// </summary>
    public int Priority { get; init; } = -1;

    /// <summary>
    /// 事件的默认入列优先级（R-3.3）。
    /// 没有显式指定 Priority 时按这里取值。
    /// </summary>
    public int EffectivePriority => Priority >= 0 ? Priority : DefaultPriorityOf(Kind);

    private static int DefaultPriorityOf(CoreEvent k) => k switch
    {
        // 10：入场曲 / 进化时 / 超进化时 / 启动
        CoreEvent.OnPlay or CoreEvent.OnEvolve or CoreEvent.OnSuperEvolve
            or CoreEvent.OnEngage or CoreEvent.OnFuse => 10,

        // 20：场上出现时 / 离场与破坏 / 受伤回复
        CoreEvent.OnEnterField or CoreEvent.OnLeaveField or CoreEvent.OnDestroy
            or CoreEvent.OnDamaged or CoreEvent.OnHealed or CoreEvent.OnSummon => 20,

        // 30/40：攻撃时 / 交戦时
        CoreEvent.OnAttack => 30,
        CoreEvent.OnClash => 40,

        // 90：直接召唤
        _ => 60,
    };
}

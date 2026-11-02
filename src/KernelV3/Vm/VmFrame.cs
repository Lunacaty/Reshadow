using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 执行帧与挂起
//
// 这是 V3 对「同步 execute 卡死」的正面解法（Gemini 第二刀）。
//
// V2 的问题：
//   private void Execute(ActionNode node) { switch(node) { ... } }
// 是同步方法调用。遇到 "Draw 2 cards. Select a card in your hand and discard it."
// 根本停不下来 —— DoChoose 直接取前 N 个，玩家没有选择机会。
//
// V3 的做法：
//   把隐式的 C# 调用栈，换成显式的 Stack<VmFrame>（程序计数器 + 指令序列）。
//   遇到需要玩家输入，设 Pending 然后 return —— 主循环自然停止。
//   因为 VmFrame 和 PendingChoice 都是纯数据，所以可以序列化存档/断线重连。
// ============================================================================

/// <summary>一个执行帧。</summary>
public sealed class VmFrame
{
    /// <summary>本帧的作用域（含槽位寄存器）。</summary>
    public VmScope Scope { get; init; }

    /// <summary>本帧要执行的指令序列。</summary>
    public IReadOnlyList<EffectNode> Code { get; init; } = Array.Empty<EffectNode>();

    /// <summary>指令指针 —— 执行到第几条。</summary>
    public int Ip { get; set; }

    /// <summary>
    /// 刚刚由玩家 SubmitChoice 完成选择的指令下标（未完成时为 -1）。
    ///
    /// 用来区分「这条 Select/Choose 刚被玩家选完、重入」与
    /// 「这是同一条能力里的**另一条** Select」。
    /// 只靠「槽位里有没有东西」判断会串槽：第一句 Select 选完留下 $selected，
    /// 第二句 Select 一看非空就跳过选择，效果全打在第一个目标上。
    /// </summary>
    public int ResolvedChoiceIp { get; set; } = -1;

    /// <summary>本帧归属的能力（用于「复刻能力」回溯）。</summary>
    public AbilityDef Ability { get; init; }

    public bool IsDone => Ip >= (Code?.Count ?? 0);
}

/// <summary>等待玩家做出的选择。</summary>
public sealed class PendingChoice
{
    public int ChoiceId { get; init; }
    public ZoneScope Player { get; init; }

    /// <summary>
    /// 绝对玩家编号（0/1）。
    /// Player 是相对视角（Self/Opponent），UI 双人对战时必须知道该弹给谁。
    /// </summary>
    public int PlayerId { get; init; }
    public ChoiceKind Kind { get; init; }

    /// <summary>合法候选实体 id。</summary>
    public IReadOnlyList<int> Candidates { get; init; } = Array.Empty<int>();

    public int MinSelect { get; init; } = 1;
    public int MaxSelect { get; init; } = 1;

    /// <summary>选完之后写入哪个槽位的哪个位置（由帧恢复时使用）。</summary>
    public string Slot { get; init; } = TargetSpec.SlotSelected;

    /// <summary>用于 UI 提示的原始文本。</summary>
    public string Prompt { get; init; } = "";

    /// <summary>
    /// 这次选择能否被玩家撤回（点空白处 / 右键）。
    ///
    /// true  —— 发动前预选：出牌 / 进化 / 启动时的指向与选模式。
    ///          此刻卡尚未进场、PP/EP 尚未扣除，点空白处应整个取消、零损耗退回。
    /// false —— 结算中必选：已抽牌 / 已发现 / 被动触发。玩家已看到不可逆信息，
    ///          取消等于作弊。
    ///
    /// 默认 false：未显式标记为预选的挂起都不许撤回。
    /// </summary>
    public bool CanCancel { get; init; }
}

public enum ChoiceKind
{
    SelectTarget = 0,
    SelectMode,
    Mulligan,

    /// <summary>
    /// 从卡池抽样若干候选卡供玩家三选一（炉石式发现）。
    /// 与 SelectTarget 的区别：这里的 Candidates 是 **CardDef.CardId**，不是场上实体。
    /// </summary>
    DiscoverCard,
}

/// <summary>VM 状态。</summary>
public enum VmStatus
{
    Idle = 0,
    Running,
    WaitingForInput,
    GameOver,
    Faulted,

    /// <summary>
    /// 本次结算**正常跑完**（帧栈与触发队列都空了，且没有终局）。
    /// 与 Idle 的区别：Idle 是「还没开始」，Completed 是「刚跑完、可以读结果」。
    /// UI 与回归脚本靠它区分「等待下一次推进」和「这一批动作已全部结束」。
    /// </summary>
    Completed,
}

/// <summary>执行链过长（防玩家 DIY 出无限循环）。</summary>
public sealed class VmFaultException : Exception
{
    public VmFaultException(string message) : base(message) { }
}

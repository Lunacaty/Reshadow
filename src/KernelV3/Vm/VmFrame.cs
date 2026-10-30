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

    /// <summary>本帧归属的能力（用于「复刻能力」回溯）。</summary>
    public AbilityDef Ability { get; init; }

    public bool IsDone => Ip >= (Code?.Count ?? 0);
}

/// <summary>等待玩家做出的选择。</summary>
public sealed class PendingChoice
{
    public int ChoiceId { get; init; }
    public ZoneScope Player { get; init; }
    public ChoiceKind Kind { get; init; }

    /// <summary>合法候选实体 id。</summary>
    public IReadOnlyList<int> Candidates { get; init; } = Array.Empty<int>();

    public int MinSelect { get; init; } = 1;
    public int MaxSelect { get; init; } = 1;

    /// <summary>选完之后写入哪个槽位的哪个位置（由帧恢复时使用）。</summary>
    public string Slot { get; init; } = TargetSpec.SlotSelected;

    /// <summary>用于 UI 提示的原始文本。</summary>
    public string Prompt { get; init; } = "";
}

public enum ChoiceKind
{
    SelectTarget = 0,
    SelectMode,
    Mulligan,
}

/// <summary>VM 状态。</summary>
public enum VmStatus
{
    Idle = 0,
    Running,
    WaitingForInput,
    GameOver,
    Faulted,
}

/// <summary>执行链过长（防玩家 DIY 出无限循环）。</summary>
public sealed class VmFaultException : Exception
{
    public VmFaultException(string message) : base(message) { }
}

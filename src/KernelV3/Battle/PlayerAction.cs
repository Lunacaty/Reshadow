using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

/// <summary>主阶段可以主动执行的操作种类（76-c 2.3）。</summary>
public enum PlayerActionKind
{
    PlayCard,       // 打出手牌
    EngageAmulet,   // 启动护符
    FuseCards,      // 手牌融合
    Evolve,         // 手动普通进化
    SuperEvolve,    // 手动超进化
    UseExtraPp,     // 后手按额外能量点
    Attack,         // 随从攻击
    EndTurn,        // 结束回合
}

/// <summary>
/// 一个合法操作。
///
/// 之所以做成数据而不是「直接执行的方法」，是因为后端要能**枚举**再挑选：
/// Bot 对战、随机探索、以及「这一步能做哪些事」的 UI 提示，
/// 都需要在不改变状态的前提下把候选列出来。
/// </summary>
public sealed class PlayerAction
{
    public PlayerActionKind Kind { get; init; }

    /// <summary>主体：要打出的手牌 / 要启动的护符 / 要融合的主体卡 / 要进化的随从。</summary>
    public RuntimeEntity Subject { get; init; }

    /// <summary>目标：攻击目标、融合材料等。</summary>
    public RuntimeEntity Target { get; init; }

    /// <summary>融合材料（FuseCards 专用）。</summary>
    public IReadOnlyList<RuntimeEntity> Materials { get; init; }

    public override string ToString()
    {
        string subj = Subject?.Def?.Name ?? "-";
        string tgt = Target?.Def?.Name ?? "-";
        return Kind switch
        {
            PlayerActionKind.Attack => $"Attack({subj} -> {tgt})",
            PlayerActionKind.FuseCards => $"Fuse({subj} <- {Materials?.Count ?? 0})",
            PlayerActionKind.EndTurn => "EndTurn",
            _ => $"{Kind}({subj})",
        };
    }
}

using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

/// <summary>
/// 预施放事务：把「先选目标/模式 → 再扣费上场」拆成两步。
///
/// 原版手感是：出牌 / 进化 / 启动的一瞬间先拉瞄准线或弹模式框，
/// 玩家选完才扣 PP/EP、才播入场动画；期间点空白处可以整个取消、零消耗退回。
///
/// 会话托管在 MatchRunner 上，**不碰 CardVm.Pending** ——
/// 因为 CardVm.Block() 会重建 PendingChoice，外部设进去的值会被覆盖。
/// 答案答完后写进发起操作那张卡的 PreCommittedChoices，
/// 由 CardVm.FlushNextTrigger 只交给「事件主体自身的主动能力」那一帧。
/// </summary>
public sealed class PreActionSession
{
    public PlayerActionKind ActionKind { get; init; }
    public int PlayerId { get; init; }
    public RuntimeEntity Subject { get; init; }

    /// <summary>玩家在预选阶段依次选好的答案，按消费顺序排队。</summary>
    public List<IReadOnlyList<int>> Answers { get; } = new();

    /// <summary>当前等待玩家的预选请求；null 表示可以提交。</summary>
    public PendingChoice CurrentPrompt { get; set; }

    public bool IsComplete => CurrentPrompt == null;
}

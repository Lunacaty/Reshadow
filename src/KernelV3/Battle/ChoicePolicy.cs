using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

/// <summary>
/// 玩家做选择时的决策策略。
///
/// 引擎本身不知道「该选哪个」——那是 UI 或 Bot 的事。
/// 把决策抽成接口，后端才能在**没有界面**的情况下跑完整对局：
/// 自动化回归用确定性策略，UI 用一个把选择抛给界面的实现。
/// </summary>
public interface IChoicePolicy
{
    /// <summary>
    /// 为一次挂起的选择给出答案。
    /// 返回值语义与 <c>PendingChoice</c> 一致：
    ///   SelectTarget / Discard / …  → 实体 Id 列表
    ///   SelectMode                  → 模式下标列表
    /// </summary>
    IReadOnlyList<int> Select(BattleState state, PendingChoice pending);
}

/// <summary>
/// 确定性策略：永远按候选顺序取前 MinSelect 个。
///
/// 用于自动化回归与快速模拟 —— 同一副牌 + 同一个种子必须得到同一局，
/// 所以这里不允许任何随机性。
/// </summary>
public sealed class FirstLegalChoicePolicy : IChoicePolicy
{
    public static readonly FirstLegalChoicePolicy Instance = new();

    public IReadOnlyList<int> Select(BattleState state, PendingChoice p)
    {
        if (p == null) return Array.Empty<int>();
        int take = Math.Max(1, p.MinSelect);
        if (p.Candidates == null || p.Candidates.Count == 0) return Array.Empty<int>();
        if (take > p.Candidates.Count) take = p.Candidates.Count;
        return p.Candidates.Take(take).ToList();
    }
}

/// <summary>
/// 脚本化策略：按预设队列依次喂入选择，队列空时回退到确定性策略。
///
/// 用于「我想让这张卡选第 2 个模式」这类精确用例 ——
/// 单卡效用测试需要能指定选择，而不是碰运气。
/// </summary>
public sealed class ScriptedChoicePolicy : IChoicePolicy
{
    private readonly Queue<IReadOnlyList<int>> _script = new();

    public ScriptedChoicePolicy Enqueue(params int[] choices)
    {
        _script.Enqueue(choices ?? Array.Empty<int>());
        return this;
    }

    public int Remaining => _script.Count;

    public IReadOnlyList<int> Select(BattleState state, PendingChoice p)
        => _script.Count > 0
           ? _script.Dequeue()
           : FirstLegalChoicePolicy.Instance.Select(state, p);
}

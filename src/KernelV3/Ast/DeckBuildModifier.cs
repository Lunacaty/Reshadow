using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

/// <summary>构筑期修正的种类。</summary>
public enum DeckModKind
{
    /// <summary>修改构筑牌组的张数上下限（如 +20 → 60 张构筑）。</summary>
    ModifyDeckSize = 0,

    /// <summary>允许本构筑额外投入某个固定指定职业的卡牌。</summary>
    AllowSpecificClan,

    /// <summary>允许玩家在构筑时自由任选 N 个额外职业。</summary>
    AllowChosenClans,

    /// <summary>修改本卡自身的同名卡投入上限。</summary>
    OverrideSelfCopyLimit,

    /// <summary>修改该构筑对局开始时的主战者初始生命与上限。</summary>
    ModifyLeaderStartLife,

    /// <summary>修改该构筑对局开始时的起手抽牌数。</summary>
    ModifyStartingHandSize,

    /// <summary>修改该构筑对局开始时的初始进化点 / 超进化点增量。</summary>
    ModifyStartEvolvePoints,
}

/// <summary>
/// 卡牌携带的「构筑期规则修正词条」。
///
/// 这类能力在对局中**完全不生效**，只在组牌与开局那一刻参与计算。
/// 之前引擎里没有任何地方能表达它 —— 规则是死的，卡是活的，
/// 而「卡改变规则」这件事只能靠改引擎源码。
/// </summary>
public sealed class DeckBuildModifier
{
    public DeckModKind Kind { get; init; }

    /// <summary>数值参数（容量 +20、同名上限 10、生命 +10、任选 1 个职业）。</summary>
    public int Value { get; init; }

    /// <summary>次要参数（ModifyStartEvolvePoints：Value=EP 增量，Value2=SEP 增量）。</summary>
    public int Value2 { get; init; }

    /// <summary>指定职业名（Kind == AllowSpecificClan 时有效）。</summary>
    public string TargetClan { get; init; } = "";

    /// <summary>
    /// 是否按投入张数叠加。
    /// false（默认）= 同卡名或同互斥组只触发 1 次（带 3 张也只 +20）；
    /// true = 每投入 1 张叠加 1 次。
    /// </summary>
    public bool StackPerCopy { get; init; }

    /// <summary>
    /// 互斥组标识。非空时同一牌组内相同组的修正只生效第 1 个
    /// （防多张不同名的扩容卡无限叠加）。
    /// </summary>
    public string ExclusiveGroup { get; init; } = "";
}

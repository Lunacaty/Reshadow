using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

/// <summary>可堆叠护符的合并方式（进场时旧护符怎么处理）。</summary>
public enum StackMergePolicy
{
    /// <summary>土之印模式：旧护符安静移除，层数合入新护符。</summary>
    AbsorbQuietly = 0,

    /// <summary>线索模式：旧护符走 Banish（触发 OnBanish），层数合入新护符。</summary>
    BanishOld = 1,
}

/// <summary>
/// 「资源计数器 ↔ 场上可堆叠衍生护符」的绑定规格。
///
/// 土之印与线索在规则上有一条特殊物理行为：计数器的值寄宿在场上某个衍生护符上，
/// 同场多个同类护符自动合并层数，层数归 0 自动破坏，且不可被能力破坏或点选。
///
/// 过去这条行为是 BattleState 里写死的两个卡号常量；抽成规格数据后，
/// 引擎不再认识任何具体卡号，DIY 作者也能定义自己的可堆叠资源。
/// </summary>
public sealed record StackableAmuletSpec(
    string CounterName,
    int DefaultTokenCardId,
    string MatchTraitName,
    StackMergePolicy MergePolicy,
    bool ProtectedFromAbilities);

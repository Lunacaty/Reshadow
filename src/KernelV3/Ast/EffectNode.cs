using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

// ============================================================================
// 动作节点 —— 16 个正交原子指令 + 1 个宏节点
//
// V2 的 93 个 Verb 里实际只用了 9 个，其余 84 个都是从 SV1 枚举抄来、
// 与本引擎的语义完全无关（InvokeVoice / InvokeEmote / ForceAvarice …）。
//
// 更严重的是「正交维度污染」：
//   Damage / DamageAll / DamageRandom 是三个 Verb，
//   但 TargetSpec 里已经有 Mode = Choose / All / Random —— 两处都能表达「打全体」，会打架。
//
// V3 的收敛原则：一个物理变化 = 一个节点类型；如何选目标由 TargetSpec 决定。
// ============================================================================

public abstract class EffectNode
{
    /// <summary>本节点可选的前置条件（满足才执行）。</summary>
    public BoolExpr If { get; init; }
}

// ---------- 1. 选择目标 ----------

/// <summary>显式选择目标并写入槽位（Select X...）。</summary>
public sealed class SelectTargetNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public string OutSlot { get; init; } = TargetSpec.SlotSelected;
}

// ---------- 2. 伤害 ----------

/// <summary>造成伤害。分伤由 IsSplit 表达，群体由 Target.Mode 表达。</summary>
public sealed class DealDamageNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public NumExpr Amount { get; init; }
    /// <summary>true 表示把总伤害分配给目标们（Deal N damage split）。</summary>
    public bool IsSplit { get; init; }
    /// <summary>伤害来源是否为能力（影响「能力伤害免疫」类判定）。</summary>
    public bool IsAbility { get; init; } = true;
}

// ---------- 3. 恢复 ----------

/// <summary>恢复生命。完全恢复由 FullRestore 表达。</summary>
public sealed class RestoreLifeNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public NumExpr Amount { get; init; }
    public bool FullRestore { get; init; }
}

// ---------- 4. 属性修改 ----------

public enum StatModOp { Add = 0, Set, Swap }

public enum DurationKind
{
    Permanent = 0,
    ThisTurn,
    ThisMatch,
    UntilEndOfOpponentTurn,
}

/// <summary>修改攻防（+2/+2 / set to X/Y / swap）。</summary>
public sealed class ModifyStatsNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public StatModOp Op { get; init; } = StatModOp.Add;
    public NumExpr Attack { get; init; } = NumExpr.Const(0);
    public NumExpr Life { get; init; } = NumExpr.Const(0);
    public DurationKind Duration { get; init; } = DurationKind.Permanent;
}

// ---------- 5. 费用修改 ----------

public enum CostModOp { Add = 0, Set, Halve }

/// <summary>修改费用（reduce cost by 1 / set cost to 0）。</summary>
public sealed class ModifyCostNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public CostModOp Op { get; init; } = CostModOp.Add;
    public NumExpr Value { get; init; }
    public DurationKind Duration { get; init; } = DurationKind.Permanent;
}

// ---------- 6. 关键词 ----------

public enum KeywordOp { Grant = 0, Remove, SilenceAll }

/// <summary>授予 / 移除关键词（Give Ward / Remove all abilities）。</summary>
public sealed class ModifyKeywordNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public KeywordOp Op { get; init; } = KeywordOp.Grant;
    public Core.SymbolId<Core.KeywordTag> Keyword { get; init; } = Core.SymbolId<Core.KeywordTag>.Invalid;
    public DurationKind Duration { get; init; } = DurationKind.Permanent;

    /// <summary>
    /// 带参关键词的数值（可空）。
    /// 例：max_attacks_per_turn = 2 / damage_cap = 0 / damage_taken_bonus = 1 / mode_pick_bonus = 1。
    /// 没有数值时保持 null，避免把 "上限 0" 和 "没有上限" 混为一谈。
    /// </summary>
    public NumExpr Value { get; init; }
}

// ---------- 7. 授予能力 ----------

/// <summary>授予一段完整子能力（Give it "Last Words: ..."）。</summary>
public sealed class GrantAbilityNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public AbilityDef Granted { get; init; }
    public DurationKind Duration { get; init; } = DurationKind.Permanent;
}

/// <summary>
/// 给一个**资源计数器**挂载能力（而非给实体）。
///
/// 例：`give your faith "Whenever an allied follower evolves, deal 1 damage to the enemy leader."`
/// —— faith 是主战者持有的计数器，不是场上的实体，所以 GrantAbilityNode 表达不了。
/// 这张能力会在该计数器每次变化时由 Battle 层的计数器监听器触发。
/// </summary>
public sealed class GrantCounterAbilityNode : EffectNode
{
    public Core.SymbolId<Core.CounterTag> Counter { get; init; } = Core.SymbolId<Core.CounterTag>.Invalid;
    public Core.ZoneScope Owner { get; init; } = Core.ZoneScope.Self;
    public AbilityDef Granted { get; init; }
}

// ---------- 8. 生成卡 ----------

public enum CardSourceKind
{
    CardId = 0,
    /// <summary>从某个目标集合复制。</summary>
    CopyOfTarget,
    /// <summary>"Summon 2 instead" —— 重复上一个动作生成的卡，只改次数。</summary>
    CopyOfPrevious,
}

/// <summary>凭空生成（召唤衍生物 / 加手牌 / 复制体）。</summary>
public sealed class SpawnCardNode : EffectNode
{
    public CardSourceKind SourceKind { get; init; } = CardSourceKind.CardId;
    public int CardId { get; init; }
    public string CardName { get; init; } = "";
    /// <summary>SourceKind == CopyOfTarget 时，从哪个目标复制。</summary>
    public TargetSpec CopyFrom { get; init; }
    public Core.ZoneScope ToOwner { get; init; } = Core.ZoneScope.Self;
    public Core.ZoneRef ToZone { get; init; } = Core.ZoneRef.Board;
    public NumExpr Count { get; init; } = NumExpr.Const(1);
    public string OutSlot { get; init; } = TargetSpec.SlotCreated;
}

// ---------- 9. 移区 ----------

/// <summary>现有实体跨区域移动（破坏 / 放逐 / 弃牌 / 抽牌 / 回手 / 回牌库）。</summary>
public sealed class MoveZoneNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public Core.ZoneRef ToZone { get; init; }
    public string OutSlot { get; init; } = TargetSpec.SlotCreated;

    /// <summary>
    /// 这是否是「破坏」。
    /// Destroy 与弃牌 / 流放 / 回手都是「进墓场」，但只有 Destroy 会
    /// 触发谢幕曲、加墓场、记入本局被破坏池。
    /// 不分的话，弃牌会误触发谢幕曲，而法术破坏反而不触发。
    /// </summary>
    public bool IsDestroy { get; init; }
}

// ---------- 10. 变形 ----------

/// <summary>变形为另一张卡。</summary>
public sealed class TransformNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public int IntoCardId { get; init; }
    public string IntoCardName { get; init; } = "";

    /// <summary>
    /// 变形源不是具体卡名而是目标集合时使用。
    /// 例："transform all allied followers into exact copies of random followers in your deck"。
    /// </summary>
    public TargetSpec CopySource { get; init; }
}

// ---------- 11. 进化 ----------

/// <summary>进化 / 超进化。</summary>
public sealed class EvolveNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public bool IsSuperEvolve { get; init; }
}

// ---------- 12. 计数器 ----------

public enum CounterOp { Add = 0, Set, Fill }

/// <summary>修改计数器（PP / EP / 墓地 / 连击 / 增幅 / 玩家自定义资源）。</summary>
public sealed class ModifyCounterNode : EffectNode
{
    public Core.SymbolId<Core.CounterTag> Counter { get; init; } = Core.SymbolId<Core.CounterTag>.Invalid;
    public CounterOp Op { get; init; } = CounterOp.Add;
    public NumExpr Delta { get; init; } = NumExpr.Const(1);

    /// <summary>
    /// 计数器持有者：
    ///   null  -> 修改主战者级计数器（pp / ep / faith / combo…）
    ///   非 null -> 修改每个目标实体自己的计数器（倒计时 / 魔力增幅 / 单卡变量 X）
    /// </summary>
    public TargetSpec Target { get; init; }

    /// <summary>Target 为 null 时的玩家归属。</summary>
    public Core.ZoneScope Owner { get; init; } = Core.ZoneScope.Self;
}

// ---------- 13. 复刻能力 ----------

/// <summary>复刻本卡某时机的能力（Replicate the effects of this card Fanfare）。</summary>
public sealed class InvokeAbilityNode : EffectNode
{
    public TargetSpec Target { get; init; }
    public Core.CoreEvent Event { get; init; }
}

// ---------- 14. 条件 ----------

/// <summary>条件分支。</summary>
public sealed class ConditionalNode : EffectNode
{
    public BoolExpr Condition { get; init; }
    public IReadOnlyList<EffectNode> Then { get; init; } = Array.Empty<EffectNode>();
    public IReadOnlyList<EffectNode> Else { get; init; } = Array.Empty<EffectNode>();
}

// ---------- 15. 循环 ----------

public enum LoopKind { Times = 0, ForEach, RepeatUntil }

/// <summary>循环控制流（Do this N times / For each / Repeat until）。</summary>
public sealed class LoopNode : EffectNode
{
    public LoopKind Kind { get; init; }
    public NumExpr Count { get; init; }
    public TargetSpec Collection { get; init; }
    public BoolExpr Until { get; init; }
    public IReadOnlyList<EffectNode> Body { get; init; } = Array.Empty<EffectNode>();
    /// <summary>硬上限，防止玩家 DIY 出无限循环。默认 30。</summary>
    public int MaxIterations { get; init; } = 30;
}

// ---------- 16. 模式分支 ----------

public enum ModeDispatchKind { Choose = 0, Random, Sequence }

/// <summary>模式抉择 / 随机分支 / 顺序分支（Select a Mode）。</summary>
public sealed class BranchModeNode : EffectNode
{
    public ModeDispatchKind Kind { get; init; } = ModeDispatchKind.Choose;
    public NumExpr PickCount { get; init; } = NumExpr.Const(1);
    public IReadOnlyList<IReadOnlyList<EffectNode>> Options { get; init; } = Array.Empty<IReadOnlyList<EffectNode>>();

    /// <summary>
    /// 模式选项作用的目标（"give this follower 3 random abilities from the following"）。
    /// null 表示选项自己携带目标。
    /// </summary>
    public TargetSpec GrantTarget { get; init; }
}

// ---------- 17. 牌库替换 ----------

/// <summary>
/// 整副牌库替换（"Replace your deck with the Apocalypse Deck"）。
///
/// 这不是"生成一张卡"，而是一次原子操作：
///   · 丢弃当前牌库的全部内容（**不进任何区域**，不污染 BanishedCount）
///   · 装入具名牌组定义的整套卡
///
/// 旧实现把它拆成 MoveZone(Banished) + SpawnCard(单卡)，两个后果：
///   1. "Apocalypse Deck" 是全库 0 命中的名字，实际生成一张查不到的卡
///   2. 旧牌库进 Banished，BanishedCount 一次性 +30，此后所有读它的条件全错
/// </summary>
public sealed class ReplaceDeckNode : EffectNode
{
    /// <summary>具名牌组名（"Apocalypse Deck"）。由 DeckDefTable 解析成卡表。</summary>
    public string DeckName { get; init; } = "";
    public Core.ZoneScope Owner { get; init; } = Core.ZoneScope.Self;
}

// ---------- 18. 宏 ----------

/// <summary>机制宏（Reanimate(4) / SpellboostHand(2) / 玩家自定义复合动作）。</summary>
public sealed class MacroNode : EffectNode
{
    public Core.SymbolId<Core.MacroTag> Macro { get; init; } = Core.SymbolId<Core.MacroTag>.Invalid;
    public IReadOnlyList<NumExpr> Args { get; init; } = Array.Empty<NumExpr>();
}

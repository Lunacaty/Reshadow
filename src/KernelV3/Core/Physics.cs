using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 物理法则层 —— 这些是引擎的基本法则，写死为 enum，玩家不可 DIY
//
// 与「内容词条」（兵种/资源/关键词/职业）的分界线：
//   这里定义的是「宇宙怎么运转」：有哪些区域、怎么比较、有哪些基本事件
//   兵种/资源/关键词是「宇宙里有什么东西」，由 GameSchema 注册
//
// 这条分界线是 V3 的基石。一旦把 ZoneRef 做成可 DIY，
// 引擎就再也无法静态检查「这个动作会不会把卡移到一个不存在的区域」。
// ============================================================================

/// <summary>实体所处的区域。</summary>
public enum ZoneRef
{
    None = 0,
    Board,
    Hand,
    Deck,
    Cemetery,
    Banished,
    ExArea,
    CrestArea,
    Leader,
}

/// <summary>归属方。</summary>
public enum ZoneScope { Self = 0, Opponent = 1, Any = 2 }

/// <summary>比较算子。</summary>
public enum CompareOp { Eq = 0, Ne, Gt, Ge, Lt, Le }

/// <summary>排序依据。</summary>
public enum SortKey { None = 0, Attack, Life, Cost, BaseCost, AttackDiff }

/// <summary>选取模式。</summary>
public enum SelectMode { All = 0, Choose, Random, First, Last, Distinct }

/// <summary>
/// 核心事件 —— 从 85 个 Timing 收敛而来。
///
/// 原 Timing 里 Play / PlayOther、Draw / DrawOther 这类
/// 「同一事件 + 不同触发者」被拆成了两个枚举值，才膨胀到 85 个。
/// 这里把「事件」与「谁触发的」分离：
///   事件由 CoreEvent 表达
///   触发者由 TriggerSpec.SubjectFilter 表达
/// </summary>
public enum CoreEvent
{
    PassiveAura = 0,

    // 对局与回合
    BattleStart,
    TurnStart,
    TurnEnd,

    // 卡牌进出场
    OnPlay,
    OnEnterField,
    OnLeaveField,
    OnDestroy,
    OnBanish,
    OnSummon,

    // 手牌与牌库
    OnDraw,
    OnDiscard,
    OnReturnToHand,
    OnAddToHand,

    // 战斗与伤害
    OnAttack,
    OnClash,
    OnDamaged,
    OnHealed,

    // 进化与变身
    OnEvolve,
    OnSuperEvolve,
    OnTransform,

    // WB 特有
    OnEngage,
    OnFuse,
    OnSpellboost,
    OnCountdownExpire,

    // 判负条件
    OnDeckOut,

    // 计数变化
    OnCounterChange,
}

/// <summary>触发者在哪个区域监听。</summary>
public enum ActiveZoneScope { Board = 0, Hand, Deck, ExArea }

/// <summary>在谁的回合生效。</summary>
public enum TurnOwnerScope { Any = 0, Self, Opponent }

/// <summary>
/// 可读取的实体属性键 —— 这是「引擎物理」，不是「内容词条」。
/// 玩家自定义的计数器走 SymbolId&lt;CounterTag&gt;，不在这里。
/// </summary>
public enum StatKey
{
    None = 0,

    // 卡面基础值
    Attack, Life, MaxLife, Cost, BaseAttack, BaseLife, BaseCost,

    // 计数
    Countdown, ChantCount,

    // 玩家与区域计数
    HandCount, DeckCount, BoardCount, CemeteryCount, BanishedCount,
    PlayPoint, MaxPlayPoint, EvolvePoint, MaxEvolvePoint,

    // 本局累计
    TurnIndex, DamageDealtThisTurn, CardsPlayedThisTurn, CardsPlayedThisMatch,
}

/// <summary>实体状态（用于 HasState 谓词）。</summary>
public enum UnitState
{
    None = 0,
    Damaged,        // 生命值低于上限
    Evolved,        // 已进化
    SuperEvolved,   // 已超进化
    Silenced,       // 被沉默
    AttackedThisTurn,
    EnteredThisTurn,
    CostModified,
    HasLastWords,
    HasOnSpellboost,
    Destroyed,
    AttackedLeaderLastTurn,
}

/// <summary>历史池作用域（用于"本局/本回合被破坏的卡"这类统计）。</summary>
public enum HistoryScope
{
    None = 0,
    DestroyedThisTurn,
    DestroyedThisMatch,
    EnteredThisMatch,
    FusedToSelf,
    AffectedByThisAbility,
}

/// <summary>牌库规则（用于 DeckRule 谓词）。</summary>
public enum DeckRule
{
    None = 0,
    NoDuplicates,
    EvenCostOnly,
    OddCostOnly,
}

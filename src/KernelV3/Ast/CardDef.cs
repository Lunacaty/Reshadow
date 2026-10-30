using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

// ============================================================================
// 顶层结构：卡片与能力
//
// V2 的 Ability 有 85 个 Timing 枚举值和 93 个 Verb；
// V3 把「什么事件」与「谁触发的」拆开：
//   TriggerSpec(event, SubjectFilter, ActiveZone, DuringTurn, MaxPerTurn)
// 于是 Play / PlayOther 变成同一个 OnPlay + 不同的 SubjectFilter。
// ============================================================================

/// <summary>触发规格 —— 取代 V2 的 Timings 数组。</summary>
public sealed class TriggerSpec
{
    public Core.CoreEvent Event { get; init; } = Core.CoreEvent.OnPlay;

    /// <summary>谁触发才算数？null 表示「本卡自己」（Self）。</summary>
    public TargetSpec SubjectFilter { get; init; }

    /// <summary>事件副目标过滤（如 Follower Strike 要求攻击目标是随从）。</summary>
    public TargetSpec SecondaryFilter { get; init; }

    /// <summary>该能力在哪个区域监听。</summary>
    public Core.ActiveZoneScope ActiveZone { get; init; } = Core.ActiveZoneScope.Board;

    /// <summary>限制在谁的回合。</summary>
    public Core.TurnOwnerScope DuringTurn { get; init; } = Core.TurnOwnerScope.Any;

    /// <summary>每回合最多次数。0 表示不限；1 对应 Once on each of your turns。</summary>
    public int MaxTriggersPerTurn { get; init; }

    /// <summary>
    /// 只用「手动进化」触发。
    /// 卡面上的 Evolve: / Super-Evolve: 冒号头只在你真的花 EP/SEP 进化时发动；
    /// 由能力效果自动进化（如 "Evolve this follower"）**不触发**这两类头，
    /// 但会触发 When/Whenever ... evolves 这类事件从句。
    /// </summary>
    public bool ManualEvolveOnly { get; init; }

    public static TriggerSpec Of(Core.CoreEvent ev) => new() { Event = ev };
}

/// <summary>代价规格。</summary>
public sealed class CostSpec
{
    public CostKind Kind { get; init; }
    public NumExpr Amount { get; init; } = NumExpr.Const(1);
    /// <summary>原始文本（Enhance (4) 的 "Enhance"），仅供展示与校验。</summary>
    public string Label { get; init; } = "";
}

public enum CostKind
{
    None = 0,
    UsePp,         // Enhance (N) / Engage (N)
    Necromance,    // Necromancy (N)
    DestroyAlly,   // Earth Rite (N)
    TurnPlayCount, // Combo (N) —— 条件型代价
    MatchPlayCount,// Rally (N)
    Countdown,     // Countdown (N)
}

/// <summary>一条能力 = 触发规格 + 条件 + 代价 + 动作。</summary>
public sealed class AbilityDef
{
    public TriggerSpec Trigger { get; init; }
    public BoolExpr Condition { get; init; } = BoolExpr.True;
    public IReadOnlyList<CostSpec> Costs { get; init; } = Array.Empty<CostSpec>();
    public IReadOnlyList<EffectNode> Body { get; init; } = Array.Empty<EffectNode>();
}

/// <summary>卡种。</summary>
public enum CardKind { Follower = 0, Spell, Amulet, Crest, Leader }

/// <summary>一张卡。</summary>
public sealed class CardDef
{
    public int CardId { get; init; }
    public string Name { get; init; } = "";
    public string NameChs { get; init; } = "";
    public CardKind Kind { get; init; }

    public Core.SymbolId<Core.ClanTag> Clan { get; init; } = Core.SymbolId<Core.ClanTag>.Invalid;
    public IReadOnlyList<Core.SymbolId<Core.TraitTag>> Traits { get; init; } = Array.Empty<Core.SymbolId<Core.TraitTag>>();

    public int BaseCost { get; init; }
    public int BaseAttack { get; init; }
    public int BaseLife { get; init; }

    /// <summary>卡牌自带的常驻关键词（Ward / Rush / Storm）。
    /// V2 把裸关键词塞进 Ability.Body 冒充动作，V3 提到顶层。</summary>
    public IReadOnlyList<Core.SymbolId<Core.KeywordTag>> InnateKeywords { get; init; }
        = Array.Empty<Core.SymbolId<Core.KeywordTag>>();

    /// <summary>卡牌自带的初始计数器（Countdown(3) / "X starts at 0"）。</summary>
    public IReadOnlyDictionary<Core.SymbolId<Core.CounterTag>, int> InnateCounters { get; init; }
        = new Dictionary<Core.SymbolId<Core.CounterTag>, int>();

    public IReadOnlyList<AbilityDef> Abilities { get; init; } = Array.Empty<AbilityDef>();

    /// <summary>原始英文文本（仅诊断用，不参与执行）。</summary>
    public string SourceText { get; init; } = "";
}

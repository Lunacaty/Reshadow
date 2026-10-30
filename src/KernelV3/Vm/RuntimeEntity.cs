using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 运行时实体与快照
//
// 关键设计（来自 SVLab 的 entry_serial，Bloodymoon 没有的东西）：
//   EntityId    实体身份（同一张卡的逻辑 id）
//   EntrySerial 入场流水号 —— 每次改变区域立即 +1
//
// 为什么需要 EntrySerial：
//   玩家把随从回手再打出，EntityId 可能复用，但它已经不是「当初那个」了。
//   没有流水号，「每当这个随从入场时」会在回手再打出时误触发。
//
// 为什么需要快照：
//   `Select an enemy, destroy it, add a copy of it to your hand`
//   读到「它」时它可能已经死了 —— 必须从快照里读 CardId / Attack，而不是活体。
// ============================================================================

/// <summary>一份拷下来的实体状态。</summary>
public sealed class EntitySnapshot
{
    public int EntityId { get; init; }
    public int EntrySerial { get; init; }
    public int CardId { get; init; }
    public string Name { get; init; } = "";
    public ZoneRef Zone { get; init; }
    /// <summary>绝对玩家编号（0 = 先手，1 = 后手）。AST 的 ZoneScope 是相对视角，绝不能混用。</summary>
    public int PlayerId { get; init; }
    public int Attack { get; init; }
    public int Life { get; init; }
    public int MaxLife { get; init; }
    public int Cost { get; init; }
    public int BaseAttack { get; init; }
    public int BaseLife { get; init; }
    public int BaseCost { get; init; }
    public bool IsEvolved { get; init; }
    public bool IsSuperEvolved { get; init; }
    public bool IsSilenced { get; init; }
    public IReadOnlyCollection<SymbolId<KeywordTag>> Keywords { get; init; }
        = Array.Empty<SymbolId<KeywordTag>>();
    public IReadOnlyCollection<SymbolId<TraitTag>> Traits { get; init; }
        = Array.Empty<SymbolId<TraitTag>>();
    public IReadOnlyDictionary<SymbolId<CounterTag>, int> Counters { get; init; }
        = new Dictionary<SymbolId<CounterTag>, int>();
}

/// <summary>运行时实体。</summary>
public sealed class RuntimeEntity
{
    public int EntityId { get; init; }
    public int EntrySerial { get; set; }
    public Ast.CardDef Def { get; set; }
    /// <summary>绝对玩家编号（0 = 先手，1 = 后手）。</summary>
    public int PlayerId { get; set; }
    public ZoneRef Zone { get; set; }

    public int Attack { get; set; }
    public int Life { get; set; }
    public int MaxLife { get; set; }
    public int Cost { get; set; }

    public bool IsEvolved { get; set; }
    public bool IsSuperEvolved { get; set; }
    public bool IsSilenced { get; set; }
    public bool AttackedThisTurn { get; set; }

    /// <summary>本回合已攻击次数（受 max_attacks_per_turn 限制）。</summary>
    public int AttacksThisTurn { get; set; }

    /// <summary>上一次攻击是否打的是主战者（部分卡牌以此为条件）。</summary>
    public bool AttackedLeaderLastTurn { get; set; }
    public int EnteredTurn { get; set; }

    /// <summary>本回合是否已启动过（护符 Engage）。每回合上限 1 次。</summary>
    public bool EngagedThisTurn { get; set; }

    /// <summary>本回合是否已被融合（Fuse）。每个融合目标每回合上限 1 次。</summary>
    public bool FusedThisTurn { get; set; }

    public HashSet<SymbolId<KeywordTag>> Keywords { get; } = new();

    /// <summary>带参关键词的数值（max_attacks_per_turn=2 / damage_cap=0 …）。</summary>
    public Dictionary<SymbolId<KeywordTag>, int> KeywordValues { get; } = new();
    public HashSet<SymbolId<TraitTag>> Traits { get; } = new();
    public Dictionary<SymbolId<CounterTag>, int> Counters { get; } = new();

    /// <summary>
    /// 临时修正登记（官方术语表：until the end of the turn）。
    /// 每条记录一份「加了多少」，回合结束或对手回合结束时按 DurationKind 撤销。
    /// 没有这个列表，所有限时效果都会永久生效。
    /// </summary>
    public List<TemporaryModifier> TempModifiers { get; } = new();

    public EntitySnapshot TakeSnapshot() => new()
    {
        EntityId = EntityId,
        EntrySerial = EntrySerial,
        CardId = Def?.CardId ?? 0,
        Name = Def?.Name ?? "",
        Zone = Zone,
        PlayerId = PlayerId,
        Attack = Attack,
        Life = Life,
        MaxLife = MaxLife,
        Cost = Cost,
        BaseAttack = Def?.BaseAttack ?? 0,
        BaseLife = Def?.BaseLife ?? 0,
        BaseCost = Def?.BaseCost ?? 0,
        IsEvolved = IsEvolved,
        IsSuperEvolved = IsSuperEvolved,
        IsSilenced = IsSilenced,
        Keywords = new List<SymbolId<KeywordTag>>(Keywords),
        Traits = new List<SymbolId<TraitTag>>(Traits),
        Counters = new Dictionary<SymbolId<CounterTag>, int>(Counters),
    };
}

/// <summary>
/// 槽位里存的引用 —— 不是活体对象，而是「身份 + 当时的快照」。
/// </summary>
public readonly record struct EntityRef(int EntityId, int EntrySerial, EntitySnapshot Snapshot)
{
    public static EntityRef From(RuntimeEntity e) => new(e.EntityId, e.EntrySerial, e.TakeSnapshot());
}

/// <summary>一条可撤销的临时修正。</summary>
public sealed class TemporaryModifier
{
    public Ast.DurationKind Duration { get; init; }
    public int Attack { get; init; }
    public int Life { get; init; }
    public Core.SymbolId<Core.KeywordTag> Keyword { get; init; }
    public bool IsKeywordGrant { get; init; }
    public int Cost { get; init; }
    public Core.SymbolId<Core.CounterTag> Counter { get; init; }
    public int CounterDelta { get; init; }
    public bool IsCounterMod { get; init; }
}
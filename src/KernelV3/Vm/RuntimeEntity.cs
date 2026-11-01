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

    /// <summary>
    /// 拍快照那一刻的**卡种**。
    ///
    /// 必须存下来，不能靠「回查墓地里那个实体的 Def」：
    /// 结晶护符死进墓地后 Def 会还原成随从本体，
    /// 于是「死掉的护符」在回查时变成了「死掉的随从」，
    /// 亡者召还就会把一张护符当随从复活上场。
    /// 快照是那一瞬间的事实，卡种同理。
    /// </summary>
    public Ast.CardKind Kind { get; init; }

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

    /// <summary>
    /// 这张卡历史上融合过的**材料卡号**集合（同卡号只记一次）。
    ///
    /// 对应卡面「the number of differently named cards Fused to this card」
    /// 与「When you've Fused both to this card」。
    /// 必须存集合而不是只留计数：喂两张同名材料算 1 种，不是 2 种。
    /// 实体进出区域时清空（与 fuse_count / fuse_cost / fuse_kinds 一起），
    /// 但三个计数器本身保留在卡上，供入场曲读取。
    /// </summary>
    public HashSet<int> FusedKinds { get; } = new();

    /// <summary>
    /// 这块材料是被哪张卡吞掉的（融合主体的 EntityId，0 表示不是融合材料）。
    /// 用于在主体离场时精确清空它名下的素材，而不误伤别的卡的材料。
    /// </summary>
    public int FusedIntoId { get; set; }

    /// <summary>
    /// 激奏 / 结晶打出前的本体卡面定义。
    ///
    /// 结晶护符在场上必须**保持** CrystallizeDef —— 那是它有吟唱与谢幕曲的依据。
    /// 只有回到手牌（洗白为印刷值）时才还原成本体，手牌里必须显示本体卡面。
    /// </summary>
    public Ast.CardDef OriginalDef { get; set; }

    /// <summary>
    /// 离场那一刻的卡面定义（激奏/结晶卡专用）。
    ///
    /// 结晶护符的谢幕曲挂在 CrystallizeDef 上，而它一离场就要把 Def 还原成随从本体
    /// （否则墓地/牌库里那张卡认不出自己是谁、亡者召还会把护符当随从复活）。
    /// 还原之后，稍后才执行的 FlushNextTrigger 就再也读不到它的谢幕曲了。
    /// 把离场时的定义留在这里，让触发扫描仍有据可查。
    /// </summary>
    public Ast.CardDef LastFieldDef { get; set; }

    /// <summary>
    /// 「Remove Last Words」是否已生效。
    /// 谢幕曲是一条 AbilityDef（OnDestroy），不是关键词，
    /// 单纯 Keywords.Remove("last words") 拦不住它 —— 必须单独记一个事实位。
    /// </summary>
    public bool LastWordsRemoved { get; set; }

    /// <summary>
    /// 这是否是一个信仰实体（住在纹章区，但不算纹章）。
    /// 纹章相关的效果必须按这个标志排除信仰。
    /// </summary>
    public bool IsFaith { get; set; }

    /// <summary>信仰的持有者（绝对玩家编号）。</summary>
    public int FaithOwner { get; set; } = -1;

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
        Kind = Def?.Kind ?? Ast.CardKind.Follower,
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
    /// <summary>临时费用变化的增量（到期时按 -Cost 撤销）。</summary>
    public int Cost { get; init; }
    /// <summary>这条记录是费用变化而不是攻防变化。</summary>
    public bool IsCostMod { get; init; }
    public Core.SymbolId<Core.CounterTag> Counter { get; init; }
    public int CounterDelta { get; init; }
    public bool IsCounterMod { get; init; }
}
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 单个玩家的绝对状态
//
// 七大区域严格分开：Board / Hand / Deck / Cemetery / Banished / ExArea / CrestArea
// 每个玩家一份，绝不共享。这是「敌方回合涨我方 PP」这类错误的根本防线。
// ============================================================================

public sealed class PlayerState
{
    /// <summary>0 = 先手，1 = 后手。绝对身份，不随视角变化。</summary>
    public int PlayerId { get; init; }

    /// <summary>主战者实体（生命值、最大生命值、状态）。</summary>
    public RuntimeEntity Leader { get; set; }

    /// <summary>场上：随从 + 护符，上限 5。</summary>
    public List<RuntimeEntity> Board { get; } = new();

    /// <summary>手牌，上限 9。</summary>
    public List<RuntimeEntity> Hand { get; } = new();

    /// <summary>牌库。</summary>
    public List<RuntimeEntity> Deck { get; } = new();

    /// <summary>墓地。</summary>
    public List<RuntimeEntity> Cemetery { get; } = new();

    /// <summary>放逐区（不算「被破坏」，不触发谢幕曲）。</summary>
    public List<RuntimeEntity> Banished { get; } = new();

    /// <summary>EX 区（暂存 / 消耗区）。</summary>
    public List<RuntimeEntity> ExArea { get; } = new();

    /// <summary>纹章区，上限 5，独立于 Board，不占用场上格子。</summary>
    public List<RuntimeEntity> CrestArea { get; } = new();

    /// <summary>玩家级计数器：pp / max_pp / ep / sep / shadow / combo / rally / faith / clues …</summary>
    public Dictionary<int, int> Counters { get; } = new();

    /// <summary>本局被破坏的卡快照（供 Reanimate 与「本局被破坏」统计）。</summary>
    public List<EntitySnapshot> DestroyedThisMatch { get; } = new();

    /// <summary>本回合被破坏的卡快照。</summary>
    public List<EntitySnapshot> DestroyedThisTurn { get; } = new();

    /// <summary>本局入场过的卡快照（「本局入场过的随从」类效果）。</summary>
    public List<EntitySnapshot> EnteredThisMatch { get; } = new();

    // ---------- 回合内限额 ----------

    /// <summary>本回合是否已手动进化过（每回合限 1 次）。</summary>
    /// <summary>牌组抽空判负。官方：牌组为 0 张时抽牌即失败。</summary>
    public bool LostByDeckOut { get; set; }

    /// <summary>牌组底部已有「胜利的卡牌」。</summary>
    public bool HasVictoryCard { get; set; }

    /// <summary>抽空牌组时持有胜利的卡牌 —— 改为胜利。</summary>
    public bool WonByVictoryCard { get; set; }

    public bool ManualEvolvedThisTurn { get; set; }

    /// <summary>本回合已启动过的护符（Engage 每回合限 1 次）。</summary>
    public HashSet<int> EngagedEntityIdsThisTurn { get; } = new();

    /// <summary>本回合已融合过的卡。</summary>
    public HashSet<int> FusedEntityIdsThisTurn { get; } = new();

    // ---------- 区域容量 ----------

    public const int MaxBoard = 5;
    public const int MaxHand = 9;
    public const int MaxCrest = 5;

    /// <summary>按区域取容器。ZoneRef 与容器的唯一映射点。</summary>
    public List<RuntimeEntity> Zone(ZoneRef z) => z switch
    {
        ZoneRef.Board => Board,
        ZoneRef.Hand => Hand,
        ZoneRef.Deck => Deck,
        ZoneRef.Cemetery => Cemetery,
        ZoneRef.Banished => Banished,
        ZoneRef.ExArea => ExArea,
        ZoneRef.CrestArea => CrestArea,
        _ => null,
    };

    /// <summary>该区域的容量上限（-1 表示不限）。</summary>
    public static int Capacity(ZoneRef z) => z switch
    {
        ZoneRef.Board => MaxBoard,
        ZoneRef.Hand => MaxHand,
        ZoneRef.CrestArea => MaxCrest,
        _ => -1,
    };
}

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

    /// <summary>
    /// 这是不是真正的【入场曲】（Fanfare）。
    ///
    /// 关键区别：随从/护符写了 `Fanfare:` 冒号头才算入场曲；
    /// 法术从手牌施放时触发的同样是 OnPlay，但**不是入场曲** ——
    /// 既不该在卡面印「【入场曲】」，也不受「入场曲能力不发动」类光环影响。
    /// 以前两者混为一谈，导致所有法术都被印成【入场曲】。
    /// </summary>
    public bool IsFanfare { get; init; }

    /// <summary>
    /// 第二个可触发的事件。用于「Whenever you play **or Fuse** a Loot card」
    /// 这类一句话里并列两个事件的写法：主语与效果完全相同，只差事件种类。
    /// 拆成两条能力会让效果执行两次（同一张牌打出时既算 play 又算 fuse 以外的重复）。
    /// </summary>
    public Core.CoreEvent? SecondaryEvent { get; init; }

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

    /// <summary>
    /// 这条能力所属的卡出现过 `Fuse: ...` 材料声明。
    ///
    /// 材料声明段本身不产出能力，所以「with Fuse」这类谓词
    /// 拿不到任何 Trigger.Event == OnFuse 的能力可查 —— 必须由编译期
    /// 把事实贴到这张卡的某条能力上，运行期才查得到。
    /// </summary>
    public bool HasFuse { get; init; }
}

/// <summary>卡种。</summary>
/// <summary>
/// 卡牌种类。
/// Faith 与 Crest 都住在纹章区，但二者互相独立：
/// 纹章相关的效果（如「纹章造成伤害」）不得把信仰算进去。
/// </summary>
public enum CardKind { Follower = 0, Spell, Amulet, Crest, Leader, Faith }

/// <summary>一张卡。</summary>
public sealed class CardDef
{
    public int CardId { get; init; }

    /// <summary>
    /// 立绘用的卡号。0 表示用 CardId 本身。
    ///
    /// 纹章/信仰是合成号段（4xxxxxxxx），没有对应立绘文件；
    /// 它们要回到主人卡的立绘，否则来源溯源条目加载不出图。
    /// </summary>
    public int ArtCardId { get; init; }

    /// <summary>定义这张卡的卡包 ID（空 = 引擎内置）。跨包冲突检测与来源追溯用。</summary>
    public string PackId { get; init; } = "";

    /// <summary>
    /// true = 衍生卡（Token）：仅供召唤/变身/加手牌/替换牌组使用，
    /// 不可放进局外构筑。
    /// </summary>
    public bool IsToken { get; init; }

    /// <summary>
    /// 构筑期修正词条（对局中不生效）。
    /// 为空表示这张卡不改变任何构筑规则。
    /// </summary>
    public List<DeckBuildModifier> DeckModifiers { get; init; } = new();
    public string Name { get; init; } = "";
    public string NameChs { get; init; } = "";
    public CardKind Kind { get; init; }

    public Core.SymbolId<Core.ClanTag> Clan { get; init; } = Core.SymbolId<Core.ClanTag>.Invalid;
    public IReadOnlyList<Core.SymbolId<Core.TraitTag>> Traits { get; init; } = Array.Empty<Core.SymbolId<Core.TraitTag>>();

    /// <summary>
    /// 稀有度：1=青铜 2=白银 3=黄金 4=虹彩 0=未标注。
    ///
    /// 只有卡框渲染读它（铜/银/金/虹四种边色），不参与任何规则判定 ——
    /// 名字相近的 RarityTag 是卡面文案用的，两者不要混。
    /// </summary>
    public int Rarity { get; init; }

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

    /// <summary>
    /// 这张卡是否有 `Fuse: ...` 材料声明。
    ///
    /// 材料声明段本身不产出能力（它只是告诉玩家能喂什么），
    /// 但「with Fuse」这类**谓词**问的正是「这张卡能不能融合」。
    /// 只查 OnFuse 能力会把纯材料声明卡误判成不能融合。
    /// </summary>
    public bool HasFuse { get; init; }

    /// <summary>
    /// `Fuse: <材料>` 声明里的材料过滤条件。
    ///
    /// 例：`Fuse: Loot cards` → 只看带 Loot 兵种的卡；
    ///     `Fuse: Artifact amulets` → 只看带 Artifact 兵种的护符。
    /// 为空表示「不限材料」。融合前必须用它逐张校验，
    /// 否则把任意一张手牌喂进去都能融合。
    /// </summary>
    public TargetSpec FuseMaterialFilter { get; init; }

    /// <summary>原始英文文本（仅诊断用，不参与执行）。</summary>
    public string SourceText { get; init; } = "";

    /// <summary>
    /// 这张卡自己会生成的那个**纹章**的完整定义（含 Abilities）。
    ///
    /// 纹章与信仰在局内是独立实体，但在**效果定义上不是独立卡片**：
    /// 它们的能力文本来自主人卡的 alt_modes（「Gain Crest: <同名>」那条），
    /// 必须随主人卡一起编译进 AST。
    ///
    /// 不内嵌的话，运行时只能按**卡名**去卡池里现造一个空壳 CardDef ——
    /// 名字对得上，能力全空；更糟的是若卡池里恰好有同名随从，
    /// 还会把那个随从卡当成纹章塞进纹章区。
    /// </summary>
    public CardDef CrestDef { get; init; }

    /// <summary>这张卡自己生成的那个**信仰**的完整定义（含 Abilities）。</summary>
    public CardDef FaithDef { get; init; }

    /// <summary>
    /// 激奏（Accelerate）：本体费用不够时可以按这个**法术**打出。
    /// 与 Crest/Faith 不同，它不是独立实体，而是同一张卡的另一张脸。
    /// </summary>
    public CardDef AccelerateDef { get; init; }

    /// <summary>
    /// 结晶（Crystallize）：本体费用不够时可以按这个**护符**打出。
    /// </summary>
    public CardDef CrystallizeDef { get; init; }
}

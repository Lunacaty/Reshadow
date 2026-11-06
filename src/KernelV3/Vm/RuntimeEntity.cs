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

    /// <summary>
    /// 快照时的卡定义。
    ///
    /// 存在的理由：纯内存探针（发现的候选筛选）不在 _world 里，
    /// 靠 EntityId 回查拿不到 Def，于是职业/兵种/关键词条件全部失效。
    /// 把定义直接挂进快照，探针不碰世界也能跑完整的条件求值。
    /// </summary>
    public Ast.CardDef Def { get; init; }

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
    public bool PlayedWithEnhance { get; init; }
    public IReadOnlyCollection<SymbolId<KeywordTag>> Keywords { get; init; }
        = Array.Empty<SymbolId<KeywordTag>>();
    public IReadOnlyCollection<SymbolId<TraitTag>> Traits { get; init; }
        = Array.Empty<SymbolId<TraitTag>>();
    public IReadOnlyDictionary<SymbolId<CounterTag>, int> Counters { get; init; }
        = new Dictionary<SymbolId<CounterTag>, int>();

    /// <summary>
    /// 带参关键词的数值（max_attacks_per_turn=3 / damage_cap=2 / damage_taken_bonus=1 …）。
    ///
    /// 复制必须带上它：关键词本身在 Keywords 里只说明「有这回事」，
    /// 具体几次、上限多少全在这张表。漏了的话，
    /// 从对手场上复制一个「可攻击 3 次」的随从，复制体退化回 1 次。
    /// </summary>
    public IReadOnlyDictionary<SymbolId<KeywordTag>, int> KeywordValues { get; init; }
        = new Dictionary<SymbolId<KeywordTag>, int>();

    /// <summary>
    /// 限时修正（本回合 +2/+2、「到下回合结束前获得疾驰」等）。
    ///
    /// exact copy 继承的是**此刻的实际状态**，这些修正必须一起带走。
    /// </summary>
    public IReadOnlyList<TemporaryModifier> TempModifiers { get; init; }
        = Array.Empty<TemporaryModifier>();

    /// <summary>快照时刻的能力影响历史（复制体继承它，便于溯源）。</summary>
    public IReadOnlyList<AppliedEffectRecord> EffectHistory { get; init; }
        = Array.Empty<AppliedEffectRecord>();
}

/// <summary>
/// 本实体曾受到的一条能力影响记录（供战斗左上角详情窗溯源展示）。
/// </summary>
public sealed record AppliedEffectRecord
{
    /// <summary>施加该效果的来源卡定义（提供立绘 CardId、卡名与完整效果文本）。</summary>
    public Ast.CardDef SourceCard { get; init; }

    /// <summary>来源卡上具体生效的那条能力（用于在二级弹窗里精准高亮那一段）。</summary>
    public Ast.AbilityDef SourceAbility { get; init; }

    /// <summary>简短标签，如 "+2/+2"、"费用-1"、"【疾驰】"。</summary>
    public string Tag { get; init; } = "";
}

/// <summary>运行时实体。</summary>
public sealed class RuntimeEntity
{
    /// <summary>
    /// 本实体最近一次通过 SpawnCard 生成的实体引用。
    ///
    /// 用途：同一张卡跨能力域的 them/it 回指。
    ///   "&lt;ev&gt;Evolve: Summon 2 copies of Ghost.&lt;/ev&gt;"
    ///   "&lt;sev&gt;Super-Evolve: Give them Drain.&lt;/sev&gt;"
    /// 超进化时 OnEvolve 与 OnSuperEvolve 是两个事件、各自新建 VmScope，
    /// 前者的 $created 在后者里是空的 —— 幽灵拿不到 Drain。
    /// 把召唤结果留一份在源实体上，VmEval 解析 $created 时回退到这里。
    ///
    /// 同时解决「Summon A and B」时后一个覆盖前一个的问题：
    /// 连续 SpawnCardNode 要**追加**而不是覆盖。
    /// </summary>
    public List<EntityRef> LastCreatedRefs { get; } = new();

    /// <summary>
    /// 本实体最近一次通过 Select 选中的实体引用。
    ///
    /// 供跨时机回指："Evolve: Select an enemy follower and banish it.
    /// Super-Evolve: Banish all enemy copies of it from the field."（10334110）
    /// 超级进化是另一条能力，$selected 在那个作用域里已经空了，
    /// 只能回到**源实体**身上取它上一次选了什么。
    /// </summary>
    public List<EntityRef> LastSelectedRefs { get; } = new();

    /// <summary>
    /// 跨时机的序数选择记录：LastChooseRefs[k-1] 就是 $chooseK 的内容。
    /// 与 LastSelectedRefs 同源，供超进化 / 谢幕曲这类另起能力的作用域回指。
    /// </summary>
    public List<List<EntityRef>> LastChooseRefs { get; } = new();

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

    /// <summary>
    /// 本卡最近一次召唤出的卡 ID（跨时机 CopyOfPrevious 用）。
    /// 必须存在实体上："入场曲召唤 X，超进化时改为召唤 2 个" 的第二句
    /// 跑在几回合后的新 VmScope 里，只存 scope 就永远是 0（174 号文档 四.11）。
    /// </summary>
    public int LastSpawnedCardId { get; set; }

    /// <summary>当前已计入 Attack 的常驻光环攻防（PassiveAura）。</summary>
    public int AppliedAuraAtk { get; set; }
    /// <summary>当前已计入 Life / MaxLife 的常驻光环生命。</summary>
    public int AppliedAuraLife { get; set; }

    /// <summary>本回合已攻击次数（受 max_attacks_per_turn 限制）。</summary>
    public int AttacksThisTurn { get; set; }

    /// <summary>上一次攻击是否打的是主战者（部分卡牌以此为条件）。</summary>
    public bool CurrentAttackHitLeader { get; set; }

    /// <summary>本卡本次是以【爆能强化】打出的。用于 "Whenever you play an Enhanced card" 这类条件。</summary>
    public bool PlayedWithEnhance { get; set; }
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

    /// <summary>
    /// 玩家在发动前预选阶段已选好的答案队列。
    ///
    /// 暂存在**发起操作的那张卡**身上，由 FlushNextTrigger 只交给
    /// 「事件主体自身的主动能力」那一帧。这样旁观卡与入场初始化事件
    /// 永远不可能偷吃答案。
    /// </summary>
    public Queue<IReadOnlyList<int>> PreCommittedChoices { get; set; }

    /// <summary>
    /// 本实体曾受到的能力影响历史，按发生顺序追加。
    /// 只用于 UI 溯源展示，不参与任何规则判定。
    /// </summary>
    public List<AppliedEffectRecord> EffectHistory { get; } = new();

    /// <summary>
    /// 这张牌是否被公开（手牌里显示「👁 公开」标识）。
    ///
    /// 纯可见性标记，不参与任何数值或区域判定。
    /// </summary>
    public bool IsRevealed { get; set; }

    /// <summary>
    /// 拍快照。<paramref name="extraTraits"/> 用于「先贴兵种再拍快照」的场景 ——
    /// 亡者召还要在 CreateEntity 之后才能确定「亡者」兵种，
    /// 但 EnteredThisMatch 的快照在 CreateEntity 中间就拍下了。
    /// 与其事后重拍整份快照，不如让调用方在拍的那一刻就把附加兵种带上。
    /// </summary>
    public EntitySnapshot TakeSnapshot(params SymbolId<TraitTag>[] extraTraits)
    {
        var traits = new List<SymbolId<TraitTag>>(Traits);
        if (extraTraits != null)
            foreach (var t in extraTraits)
                if (!traits.Contains(t)) traits.Add(t);

        return new EntitySnapshot
        {
            EntityId = EntityId,
            EntrySerial = EntrySerial,
            CardId = Def?.CardId ?? 0,
            Def = Def,
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
            PlayedWithEnhance = PlayedWithEnhance,
            Keywords = new List<SymbolId<KeywordTag>>(Keywords),
            Traits = traits,
            Counters = new Dictionary<SymbolId<CounterTag>, int>(Counters),
            KeywordValues = new Dictionary<SymbolId<KeywordTag>, int>(KeywordValues),
            TempModifiers = new List<TemporaryModifier>(TempModifiers),
            EffectHistory = new List<AppliedEffectRecord>(EffectHistory),
        };
    }
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

    // ---- 以下只用于「设为某值」型的限时攻防修正 ----
    // Add 型存增量（到期减回去）；Set 型存改之前的原值（到期还原）。
    // 混用会让限时 Set 在回合结束时被减 0 = 永久生效。
    /// <summary>这条修正是 Set 型（到期还原原值，而不是减增量）。</summary>
    public bool IsStatSet { get; init; }
    /// <summary>Set 型：攻击是否被改过（false = 没改，到期不还原）。</summary>
    public bool SetAttack { get; init; }
    /// <summary>Set 型：生命是否被改过。</summary>
    public bool SetLife { get; init; }
    /// <summary>Set 型：改之前的生命上限（Life 被改时同步还原）。</summary>
    public int PrevMaxLife { get; init; }
    /// <summary>施法者编号。UntilEndOfOpponentTurn 的「对手」是相对施法者而言的。</summary>
    public int CasterPlayerId { get; init; } = -1;
}
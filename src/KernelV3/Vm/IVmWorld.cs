using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 世界接口 —— VM 与具体战斗状态之间的契约
//
// VM 只依赖这个接口，不依赖任何具体的 BattleState 实现。
// 这样做的三个好处：
//   · 单测时可以塞一个假世界，不需要跑完整对局
//   · 联机时服务端只要实现同一接口即可
//   · 未来换状态存储（内存 / 数据库）不需要动 VM
// ============================================================================

/// <remarks>
/// 所有玩家归属一律用 int playerId（0 = 先手，1 = 后手，-1 = 双方）。
/// ZoneScope 是 AST 的相对视角，同一个 Self 在不同卡上指向不同的真实玩家，
/// 绝不能直接传给世界层。转换只在 VmScope.ResolvePlayer() 一处发生。
/// </remarks>
public interface IVmWorld
{
    /// <summary>随机源。联机时必须由服务端播种，不能各自 new Random()。</summary>
    Random Rng { get; }

    /// <summary>
    /// 按「身份 + 流水号」取活体实体。
    /// 已离场或流水号已变（回手再打出）则返回 null。
    /// 这是「不鞭尸」的唯一开关。
    /// </summary>
    RuntimeEntity GetLive(int entityId, int entrySerial);

    /// <summary>
    /// 枚举历史池（本回合/本局被破坏、入场过的卡）。
    /// 「Reanimate (N)」与「本局被破坏的随从」这类效果必须走这里 ——
    /// 直接查 Cemetery 会把后来被复活或消失的卡算错。
    /// playerId = -1 表示双方。
    /// </summary>
    IEnumerable<EntitySnapshot> EnumerateHistory(HistoryScope scope, int playerId);

    /// <summary>枚举某区域的实体。</summary>
    IEnumerable<RuntimeEntity> EnumerateZone(ZoneRef zone, int playerId);

    /// <summary>按卡号找实体（用于「召唤某张具体卡」）。</summary>
    IEnumerable<RuntimeEntity> FindByCardId(ZoneRef zone, int playerId, int cardId);

    /// <summary>
    /// 本局使用的符号表。
    /// 计数器 / 关键词 / 兵种都靠它把名字解析成稳定 Id ——
    /// 求值器需要它才能把 StatKey 映射到具体的计数器，
    /// 而不是自己再 new 一份（那会造成 SymbolId 串号）。
    /// </summary>
    GameSchema Schema { get; }

    /// <summary>读计数器（PP / 墓地 / 连击 / 玩家自定义资源）。</summary>
    int ReadCounter(int playerId, SymbolId<CounterTag> counter);

    /// <summary>写计数器。</summary>
    void WriteCounter(int playerId, SymbolId<CounterTag> counter, int value);

    /// <summary>读实体自己的计数器（倒计时 / 魔力增幅 / 单卡变量）。</summary>
    int ReadEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter);

    /// <summary>写实体自己的计数器。</summary>
    void WriteEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter, int value);

    /// <summary>检查牌库规则。</summary>
    bool CheckDeckRule(int playerId, DeckRule rule);

    /// <summary>分配一个新的实体 id。</summary>
    int AllocateEntityId();

    /// <summary>把实体放进某区域（会触发 EntrySerial++）。</summary>
    void MoveEntity(RuntimeEntity e, ZoneRef toZone, int toPlayerId);

    /// <summary>
    /// 抽牌（牌库顶 → 手牌）—— **唯一**的抽牌管线。
    ///
    /// 语义契约（实现必须全部满足）：
    ///   1. 每张成功抽到的牌发一条 OnDraw，Subject = 那张牌
    ///   2. 牌库为空仍要抽时：发一条 OnDeckOut 并立即停止，剩余张数不再抽；
    ///      抽空判负由实现层记录（BattleState: LostByDeckOut / WonByVictoryCard）
    ///   3. 手牌溢出（≥ 上限）的那张按爆牌规则处理：进墓地 + shadow+1 + 不发 OnDiscard
    ///
    /// 能力抽牌必须走这里。VM 自己 MoveEntity 会同时绕开上面三条规则 ——
    /// 「每当抽牌时」永远不响应、牌库抽空也不会判负。
    /// </summary>
    void DrawCards(int playerId, int count);

    /// <summary>破坏（进墓场 + 触发 OnDestroy + 墓场 +1）。与 MoveEntity 不同，后者只移动不触发。</summary>
    void DestroyEntity(RuntimeEntity e);
    /// <summary>统一伤害管线（屏障 / 超进化免伤 / 伤害上限）。能力伤害必须走这里，不能直接扣血。</summary>
    void ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId);

    /// <summary>把实体移出游戏（破坏 / 放逐后的最终去向）。</summary>
    void RemoveEntity(RuntimeEntity e);

    /// <summary>产生一个事件，供触发器系统收集。</summary>
    void EmitEvent(VmEvent ev);

    /// <summary>
    /// 事件接收者 —— EmitEvent 写完 EventLog 之后，由它把**同一条**事件交给 VM。
    ///
    /// 没有这个出口时，BattleState 与 VM 之间没有任何连接：
    /// 回合初抽牌、能力伤害、能力破坏发出的事件全部只进了 EventLog，
    /// 于是「每当抽牌时」「当受到伤害时」「谢幕曲」对所有非战斗来源一律失明。
    ///
    /// 语义契约（实现必须全部满足）：
    ///   1. 先写 EventLog，再转发 —— 记账不因转发失败而缺漏
    ///   2. 每条事件只转发一次，且与入日志的顺序一致
    ///   3. null 表示只记录不转发（没有 VM 的单测就是这种状态）
    /// 转发**只入队**，不在这里推进结算；结算仍由 CardVm.StepUntilBlocked 独占。
    /// </summary>
    Action<VmEvent> TriggerSink { get; set; }

    /// <summary>按卡号生成一张新卡并放进指定区域。</summary>
    RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, int toPlayerId);

    /// <summary>
    /// 通知「一个实体刚刚进入战场」。
    ///
    /// 必须在**所有**进场路径的末端调用一次：SpawnCard、MoveZone 到战场、
    /// 复制上场、Invoke 都算。早期只在 TurnRunner.PlayCard 里手写发事件，
    /// 于是召唤 / 复活 / 复制进场的随从既不触发「进入战场时」，也不加连携。
    ///
    /// 实现方的契约：
    ///   1. e 不在战场时直接返回（幂等，允许重复调用）
    ///   2. 先加连携（仅随从），再发 OnEnterField 与 OnSummon
    ///   3. 不推进 VM —— 结算时机仍由 StepUntilBlocked 独占
    /// </summary>
    void NotifySummoned(RuntimeEntity e);

    /// <summary>
    /// 用一副具名牌组替换某玩家的整个牌库（"Replace your deck with ..."）。
    ///
    /// 语义契约（三条，实现必须全部满足）：
    ///   1. 旧牌库的全部卡直接离场，**不进入任何区域**
    ///      —— 特别是不能进 Banished，否则 BanishedCount 被永久污染
    ///   2. 新牌库按牌组定义逐张生成，顺序固定
    ///   3. 牌组名无法解析时由调用方抛错，不得静默装成空牌库
    /// </summary>
    void ReplaceDeck(int playerId, string deckName, IReadOnlyList<int> cardIds);

    /// <summary>把某玩家的牌库标记为「含胜利的卡牌」（Mjerrabaine 纹章机制）。</summary>
    void SetVictoryCard(int playerId) { }

    /// <summary>
    /// 实体是否有效持有某关键词（含光环授予）。
    ///
    /// 默认实现只看实体自身的 Keyword 集合；BattleState 会连光环一起算。
    /// 用于「入场曲/爆能强化无法发动」这类封印判定。
    /// </summary>
    bool HasEffectiveKeyword(RuntimeEntity target, Core.SymbolId<Core.KeywordTag> kw)
        => target != null && target.Keywords.Contains(kw);

    /// <summary>按快照生成一个复制体（用于「复制一份」）。</summary>
    RuntimeEntity SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId);

    /// <summary>把实体变形为另一张卡。</summary>
    void TransformEntity(RuntimeEntity e, int intoCardId);

    /// <summary>给实体附加一段能力。</summary>
    void GrantAbility(RuntimeEntity e, Ast.AbilityDef ability);
    /// <summary>带期限的授予：期限到了要撤销。</summary>
    void GrantAbility(RuntimeEntity e, Ast.AbilityDef ability, Ast.DurationKind duration);

    /// <summary>运行时授予的能力（不写在 CardDef 上），触发器扫描时必须一并检查。</summary>
    IReadOnlyList<(RuntimeEntity e, Ast.AbilityDef ab)> GrantedAbilities { get; }

    /// <summary>给一个资源计数器挂载能力（如 faith 的"每当友方随从进化"）。</summary>
    void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, Ast.AbilityDef ability);

    /// <summary>
    /// 运行时挂在资源计数器上的能力（如信仰）。
    /// 这些能力不属于任何卡，必须单独扫描才能触发。
    /// </summary>
    IReadOnlyList<(int playerId, SymbolId<CounterTag> ctr, Ast.AbilityDef ab)> GrantedCounterAbilities { get; }

    /// <summary>
    /// 状态结算（State-Based Actions）：把场上 Life &lt;= 0 的实体移入墓地并触发 OnDestroy。
    /// 必须在每条原子指令执行后调用，否则死亡随从会继续留在场上挨刀。
    /// 返回本次结算产生的事件数（便于循环直到稳定）。
    /// </summary>
    int ProcessStateBasedActions();

    /// <summary>
    /// 撤销到期的限时修正（until the end of the turn）。
    /// 返回撤销的条目数。
    /// </summary>
    int ExpireTemporaryModifiers(int endingPlayer);

    /// <summary>
    /// 展开机制宏为动作序列（Reanimate / Spellboost / 玩家自定义）。
    /// </summary>
    IReadOnlyList<Ast.EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope);
}

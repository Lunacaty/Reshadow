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

    /// <summary>把实体移出游戏（破坏 / 放逐后的最终去向）。</summary>
    void RemoveEntity(RuntimeEntity e);

    /// <summary>产生一个事件，供触发器系统收集。</summary>
    void EmitEvent(VmEvent ev);

    /// <summary>按卡号生成一张新卡并放进指定区域。</summary>
    RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, int toPlayerId);

    /// <summary>按快照生成一个复制体（用于「复制一份」）。</summary>
    RuntimeEntity SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId);

    /// <summary>把实体变形为另一张卡。</summary>
    void TransformEntity(RuntimeEntity e, int intoCardId);

    /// <summary>给实体附加一段能力。</summary>
    void GrantAbility(RuntimeEntity e, Ast.AbilityDef ability);

    /// <summary>运行时授予的能力（不写在 CardDef 上），触发器扫描时必须一并检查。</summary>
    IReadOnlyList<(RuntimeEntity e, Ast.AbilityDef ab)> GrantedAbilities { get; }

    /// <summary>给一个资源计数器挂载能力（如 faith 的"每当友方随从进化"）。</summary>
    void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, Ast.AbilityDef ability);

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

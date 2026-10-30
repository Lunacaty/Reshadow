using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 战斗状态 —— IVmWorld 的真实实现
//
// 它持有两个绝对对等的 PlayerState，是唯一的规则权威。
// MemoryWorld 只是自测用的假世界，这个才是真正跑对局的世界。
//
// 三条不变量（写死在代码里，不是文档）：
//   1. 任何操作只影响 PlayerId 指定的那个玩家，绝不「顺带」改对手
//   2. 区域容量在进入时检查，超限直接去墓地/消失，不触发入场事件
//   3. 只有被「破坏」才触发 OnDestroy 并让 shadow+1；放逐/回手/变形都不算
// ============================================================================

public sealed class BattleState : IVmWorld
{
    public PlayerState[] Players { get; } = new PlayerState[2];

    /// <summary>当前回合玩家（0 或 1）。</summary>
    public int ActivePlayer { get; set; }

    /// <summary>回合数，先手第 1 回合开始计。</summary>
    public int TurnNumber { get; set; }

    public GameSchema Schema { get; }
    public Random Rng { get; }

    private readonly Dictionary<int, CardDef> _cardPool = new();
    private readonly List<RuntimeEntity> _all = new();
    private readonly List<(RuntimeEntity e, AbilityDef ab)> _granted = new();
    private readonly List<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> _grantedCounters = new();
    private int _nextEntityId = 1;

    public BattleState(GameSchema schema = null, int seed = 0)
    {
        Schema = schema ?? GameSchema.CreateDefault();
        Rng = seed == 0 ? new Random() : new Random(seed);
        Players[0] = new PlayerState { PlayerId = 0 };
        Players[1] = new PlayerState { PlayerId = 1 };
    }

    // ---------- 卡池 ----------

    public void RegisterCard(CardDef def) => _cardPool[def.CardId] = def;

    public CardDef CardDefOf(int cardId)
        => _cardPool.TryGetValue(cardId, out var d) ? d : null;

    // ---------- 计数器：一律按绝对玩家编号 ----------

    public int ReadCounter(int playerId, SymbolId<CounterTag> counter)
    {
        if (playerId < 0 || playerId > 1) return 0;
        return Players[playerId].Counters.TryGetValue(counter.Id, out var v) ? v : 0;
    }

    public void WriteCounter(int playerId, SymbolId<CounterTag> counter, int value)
    {
        if (playerId < 0 || playerId > 1) return;
        Players[playerId].Counters[counter.Id] = value;
    }

    public int ReadEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter)
        => e != null && e.Counters.TryGetValue(counter, out var v) ? v : 0;

    public void WriteEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter, int value)
    {
        if (e != null) e.Counters[counter] = value;
    }

    // ---------- 区域查询 ----------

    public IEnumerable<RuntimeEntity> EnumerateZone(ZoneRef zone, int playerId)
    {
        if (zone == ZoneRef.Leader)
        {
            if (playerId < 0) return Players.Select(p => p.Leader).Where(x => x != null);
            return playerId is 0 or 1 && Players[playerId].Leader != null
                ? new[] { Players[playerId].Leader }
                : Array.Empty<RuntimeEntity>();
        }

        if (playerId < 0)
            return Players.SelectMany(p => p.Zone(zone) ?? new List<RuntimeEntity>()).ToList();

        if (playerId > 1) return Array.Empty<RuntimeEntity>();
        return Players[playerId].Zone(zone) ?? (IEnumerable<RuntimeEntity>)Array.Empty<RuntimeEntity>();
    }

    public IEnumerable<RuntimeEntity> FindByCardId(ZoneRef zone, int playerId, int cardId)
        => EnumerateZone(zone, playerId).Where(e => (e.Def?.CardId ?? 0) == cardId);

    public RuntimeEntity GetLive(int entityId, int entrySerial)
    {
        var e = _all.FirstOrDefault(x => x.EntityId == entityId);
        if (e == null) return null;
        if (e.EntrySerial != entrySerial) return null;   // 换过区，旧引用失效
        return e;
    }

    public int AllocateEntityId() => _nextEntityId++;

    public bool CheckDeckRule(int playerId, DeckRule rule)
    {
        if (playerId is < 0 or > 1) return true;
        var deck = Players[playerId].Deck;
        return rule switch
        {
            DeckRule.NoDuplicates => deck.Select(e => e.Def?.CardId ?? 0).Distinct().Count() == deck.Count,
            DeckRule.EvenCostOnly => deck.All(e => e.Cost % 2 == 0),
            DeckRule.OddCostOnly => deck.All(e => e.Cost % 2 == 1),
            _ => true,
        };
    }

    // ---------- 事件 ----------

    /// <summary>事件出口。VM 会把事件转成触发器，真正的入队由 CardVm 负责。</summary>
    public List<VmEvent> EventLog { get; } = new();
    public void EmitEvent(VmEvent ev) { if (ev != null) EventLog.Add(ev); }

    public void ClearEvents() => EventLog.Clear();

    // ---------- 实体生命周期 ----------

    /// <summary>造一个实体放进指定区域。这是所有实体的唯一出生点。</summary>
    public RuntimeEntity CreateEntity(int cardId, ZoneRef zone, int playerId, int atk = 0, int life = 0, int cost = -1)
    {
        if (playerId is < 0 or > 1) throw new ArgumentException("playerId 必须是 0 或 1");

        _cardPool.TryGetValue(cardId, out var def);
        var e = new RuntimeEntity
        {
            EntityId = AllocateEntityId(),
            EntrySerial = 1,
            Def = def,
            PlayerId = playerId,
            Zone = zone,
            Attack = atk != 0 ? atk : (def?.BaseAttack ?? 0),
            Life = life != 0 ? life : (def?.BaseLife ?? 0),
            Cost = cost >= 0 ? cost : (def?.BaseCost ?? 0),
        };
        e.MaxLife = e.Life;
        _all.Add(e);
        PlaceInZone(e, zone, playerId);
        return e;
    }

    /// <summary>把实体放进区域，并执行容量铁律。</summary>
    private void PlaceInZone(RuntimeEntity e, ZoneRef zone, int playerId)
    {
        var p = Players[playerId];
        var list = p.Zone(zone);
        if (list == null) return;

        int cap = PlayerState.Capacity(zone);
        if (cap >= 0 && list.Count >= cap)
        {
            // 铁律 2：满场/满手时新实体直接销毁，不触发入场与破坏事件
            if (zone == ZoneRef.Board)
            {
                // 场上已满 -> 直接进墓地，不触发 OnEnterField / OnDestroy
                e.Zone = ZoneRef.Cemetery;
                p.Cemetery.Add(e);
                return;
            }
            if (zone == ZoneRef.Hand)
            {
                // 手牌已满 -> 直接进墓地，不触发 OnDraw
                e.Zone = ZoneRef.Cemetery;
                p.Cemetery.Add(e);
                return;
            }
        }

        e.Zone = zone;
        e.PlayerId = playerId;
        list.Add(e);

        if (zone == ZoneRef.Board)
            p.EnteredThisMatch.Add(e.TakeSnapshot());
    }

    /// <summary>把实体从当前区域移除。</summary>
    private void Detach(RuntimeEntity e)
    {
        foreach (var p in Players)
        {
            if (p.Zone(e.Zone)?.Remove(e) == true) return;
            if (p.Leader == e) { p.Leader = null; return; }
        }
    }

    /// <summary>
    /// 换区。EntrySerial++ 让旧引用失效（回手再打出的识别依据）。
    /// 离开场上只发 OnLeaveField；是否发 OnDestroy 由 DestroyEntity 决定。
    /// </summary>
    public void MoveEntity(RuntimeEntity e, ZoneRef toZone, int toPlayerId)
    {
        if (e == null) return;
        if (e.Zone == toZone && e.PlayerId == toPlayerId) return;

        bool leftBoard = e.Zone == ZoneRef.Board && toZone != ZoneRef.Board;
        Detach(e);
        e.EntrySerial++;
        PlaceInZone(e, toZone, toPlayerId);

        if (leftBoard)
            EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
    }

    /// <summary>
    /// 破坏（Destroy）：进墓地 + 触发 OnDestroy + shadow+1。
    /// 只有真正的「被破坏」才调用它；放逐 / 回手 / 变形一律不要用。
    /// </summary>
    public void DestroyEntity(RuntimeEntity e)
    {
        if (e == null) return;
        if (e.Zone != ZoneRef.Board) return;

        int owner = e.PlayerId;
        var snap = e.TakeSnapshot();

        Detach(e);
        e.EntrySerial++;
        PlaceInZone(e, ZoneRef.Cemetery, owner);

        Players[owner].DestroyedThisMatch.Add(snap);
        Players[owner].DestroyedThisTurn.Add(snap);

        var shadow = Schema.Counter("shadow");
        WriteCounter(owner, shadow, ReadCounter(owner, shadow) + 1);

        EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
    }

    /// <summary>放逐（Banish）：进放逐区，不触发 OnDestroy，不加 shadow。</summary>
    public void BanishEntity(RuntimeEntity e)
    {
        if (e == null) return;
        if (e.Zone == ZoneRef.Board)
            EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(e) });
        MoveEntity(e, ZoneRef.Banished, e.PlayerId);
    }

    public void RemoveEntity(RuntimeEntity e) => _all.Remove(e);

    // ---------- 状态结算 SBA ----------

    /// <summary>
    /// 把 Life &lt;= 0 的场上实体破坏掉。
    /// 必须在每条原子指令后调用，否则死亡随从会留在场上继续挨刀。
    /// </summary>
    public int ProcessStateBasedActions()
    {
        int n = 0;
        foreach (var p in Players)
        {
            foreach (var e in p.Board.ToList())
            {
                if (e.Life > 0) continue;
                DestroyEntity(e);
                n++;
            }
        }
        return n;
    }

    // ---------- 生成 / 变形 / 授予 ----------

    public RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, int toPlayerId)
        => CreateEntity(cardId, toZone, toPlayerId);

    public RuntimeEntity SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId)
    {
        var s = source.Snapshot;
        if (s == null) return null;
        var e = CreateEntity(s.CardId, toZone, toPlayerId, s.Attack, s.Life, s.Cost);
        return e;
    }

    public void TransformEntity(RuntimeEntity e, int intoCardId)
    {
        if (e == null) return;
        if (_cardPool.TryGetValue(intoCardId, out var d))
        {
            e.Def = d;
            e.Attack = d.BaseAttack;
            e.Life = d.BaseLife;
            e.MaxLife = d.BaseLife;
            e.Cost = d.BaseCost;
        }
        // 变形不算破坏：不触发 OnDestroy，不加 shadow
    }

    public void GrantAbility(RuntimeEntity e, AbilityDef ability)
        => _granted.Add((e, ability));

    public IReadOnlyList<(RuntimeEntity e, AbilityDef ab)> GrantedAbilities => _granted;

    public void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, AbilityDef ability)
        => _grantedCounters.Add((playerId, counter, ability));

    public IReadOnlyList<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> GrantedCounterAbilities => _grantedCounters;

    public IReadOnlyList<EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope)
        => Array.Empty<EffectNode>();

    // ---------- 抽牌 ----------

    public void DrawCards(int playerId, int count)
    {
        var p = Players[playerId];
        for (int i = 0; i < count; i++)
        {
            if (p.Deck.Count == 0) continue;
            var card = p.Deck[0];
            MoveEntity(card, ZoneRef.Hand, playerId);
            EmitEvent(new VmEvent { Kind = CoreEvent.OnDraw, Subject = EntityRef.From(card) });
        }
    }
}

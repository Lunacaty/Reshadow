using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 内存世界 —— IVmWorld 的最小实现，用于 VM 自测
//
// 它不是真正的战斗引擎，只提供 VM 需要的存储与事件出口。
// 真正的 BattleState 会实现同一个接口，VM 代码不需要改一行。
// ============================================================================

public sealed class MemoryWorld : IVmWorld
{
    private readonly List<RuntimeEntity> _entities = new();
    private readonly Dictionary<(ZoneScope, int), int> _counters = new();
    private int _nextEntityId = 1;
    private readonly Dictionary<int, CardDef> _cardPool = new();

    public List<VmEvent> EventLog { get; } = new();
    public Random Rng { get; }

    public MemoryWorld(int seed = 12345) => Rng = new Random(seed);

    public void RegisterCard(CardDef def) => _cardPool[def.CardId] = def;

    public IReadOnlyList<RuntimeEntity> AllEntities => _entities;

    /// <summary>直接造一个实体放进指定区域（测试用）。</summary>
    public RuntimeEntity Create(int cardId, ZoneRef zone, ZoneScope owner,
        int atk = 0, int life = 0, int cost = 0)
    {
        _cardPool.TryGetValue(cardId, out var def);
        var e = new RuntimeEntity
        {
            EntityId = _nextEntityId++,
            EntrySerial = 1,
            Def = def,
            Owner = owner,
            Zone = zone,
            Attack = atk,
            Life = life,
            MaxLife = life,
            Cost = cost,
        };
        _entities.Add(e);
        return e;
    }

    public RuntimeEntity GetLive(int entityId, int entrySerial)
    {
        var e = _entities.FirstOrDefault(x => x.EntityId == entityId);
        if (e == null) return null;
        if (e.EntrySerial != entrySerial) return null;   // 回手再打出 → 不是同一个
        return e;
    }

    public IEnumerable<RuntimeEntity> EnumerateZone(ZoneRef zone, ZoneScope owner)
        => _entities.Where(e => e.Zone == zone && (owner == ZoneScope.Any || e.Owner == owner));

    public IEnumerable<RuntimeEntity> FindByCardId(ZoneRef zone, ZoneScope owner, int cardId)
        => EnumerateZone(zone, owner).Where(e => (e.Def?.CardId ?? 0) == cardId);

    public int ReadCounter(ZoneScope owner, SymbolId<CounterTag> counter)
        => _counters.TryGetValue((owner, counter.Id), out var v) ? v : 0;

    public void WriteCounter(ZoneScope owner, SymbolId<CounterTag> counter, int value)
        => _counters[(owner, counter.Id)] = value;

    public bool CheckDeckRule(ZoneScope owner, DeckRule rule)
    {
        var deck = EnumerateZone(ZoneRef.Deck, owner).ToList();
        return rule switch
        {
            DeckRule.NoDuplicates => deck.Select(e => e.Def?.CardId ?? 0).Distinct().Count() == deck.Count,
            DeckRule.EvenCostOnly => deck.All(e => e.Cost % 2 == 0),
            DeckRule.OddCostOnly => deck.All(e => e.Cost % 2 == 1),
            _ => true,
        };
    }

    public int AllocateEntityId() => _nextEntityId++;

    public void MoveEntity(RuntimeEntity e, ZoneRef toZone, ZoneScope toOwner)
    {
        if (e.Zone == toZone && e.Owner == toOwner) return;
        e.Zone = toZone;
        e.Owner = toOwner;
        e.EntrySerial++;      // ★ 每次换区流水号 +1
    }

    public void RemoveEntity(RuntimeEntity e) => _entities.Remove(e);

    public void EmitEvent(VmEvent ev) => EventLog.Add(ev);

    public RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, ZoneScope toOwner)
    {
        var e = Create(cardId, toZone, toOwner);
        if (_cardPool.TryGetValue(cardId, out var d))
        {
            e.Attack = d.BaseAttack;
            e.Life = d.BaseLife;
            e.MaxLife = d.BaseLife;
            e.Cost = d.BaseCost;
        }
        return e;
    }

    public RuntimeEntity SpawnCopy(EntityRef src, ZoneRef toZone, ZoneScope toOwner)
    {
        var s = src.Snapshot;
        if (s == null) return null;
        var e = Create(s.CardId, toZone, toOwner, s.Attack, s.Life, s.Cost);
        return e;
    }

    public void TransformEntity(RuntimeEntity e, int intoCardId)
    {
        if (_cardPool.TryGetValue(intoCardId, out var d))
        {
            e.Def = d;
            e.Attack = d.BaseAttack;
            e.Life = d.BaseLife;
            e.MaxLife = d.BaseLife;
            e.Cost = d.BaseCost;
        }
    }

    public readonly List<(RuntimeEntity e, AbilityDef ab)> GrantedAbilities = new();

    public void GrantAbility(RuntimeEntity e, AbilityDef ability)
        => GrantedAbilities.Add((e, ability));

    public readonly List<(ZoneScope owner, SymbolId<CounterTag> ctr, AbilityDef ab)> GrantedCounterAbilities = new();

    public void GrantCounterAbility(ZoneScope owner, SymbolId<CounterTag> counter, AbilityDef ability)
        => GrantedCounterAbilities.Add((owner, counter, ability));

    public IReadOnlyList<EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope)
        => Array.Empty<EffectNode>();   // 宏由 Battle 层注册，自测里不展开
}

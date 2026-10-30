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
    private readonly Dictionary<(int playerId, int counterId), int> _counters = new();
    private int _nextEntityId = 1;
    private readonly Dictionary<int, CardDef> _cardPool = new();

    public List<VmEvent> EventLog { get; } = new();
    /// <summary>符号表：计数器上下限等元数据来自这里。</summary>
    public GameSchema Schema { get; } = GameSchema.CreateDefault();

    public Random Rng { get; }

    public MemoryWorld(int seed = 12345) => Rng = new Random(seed);

    public void RegisterCard(CardDef def) => _cardPool[def.CardId] = def;

    public IReadOnlyList<RuntimeEntity> AllEntities => _entities;

    /// <summary>直接造一个实体放进指定区域（测试用）。</summary>
    public RuntimeEntity Create(int cardId, ZoneRef zone, int playerId,
        int atk = 0, int life = 0, int cost = 0)
    {
        _cardPool.TryGetValue(cardId, out var def);
        var e = new RuntimeEntity
        {
            EntityId = _nextEntityId++,
            EntrySerial = 1,
            Def = def,
            PlayerId = playerId,
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

    /// <summary>测试用历史池：把墓地当作被破坏记录。</summary>
    public IEnumerable<EntitySnapshot> EnumerateHistory(HistoryScope scope, int playerId)
    {
        if (scope == HistoryScope.None) return Array.Empty<EntitySnapshot>();
        return _entities.Where(e => e.Zone == ZoneRef.Cemetery
                                    && (playerId < 0 || e.PlayerId == playerId))
                        .Select(e => e.TakeSnapshot())
                        .ToList();
    }

    public IEnumerable<RuntimeEntity> EnumerateZone(ZoneRef zone, int playerId)
        => _entities.Where(e => e.Zone == zone && (playerId < 0 || e.PlayerId == playerId));

    public IEnumerable<RuntimeEntity> FindByCardId(ZoneRef zone, int playerId, int cardId)
        => EnumerateZone(zone, playerId).Where(e => (e.Def?.CardId ?? 0) == cardId);

    public int ReadCounter(int playerId, SymbolId<CounterTag> counter)
        => _counters.TryGetValue((playerId, counter.Id), out var v) ? v : 0;

    /// <summary>测试用：计数器写入回调（验证执行顺序）。</summary>
    public Action<string, int> OnCounterWrite;

    public int ReadEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter)
        => e != null && e.Counters.TryGetValue(counter, out var v) ? v : 0;

    public void WriteEntityCounter(RuntimeEntity e, SymbolId<CounterTag> counter, int value)
    {
        if (e == null) return;
        e.Counters[counter] = value;
        OnCounterWrite?.Invoke(counter.Name, value);
    }

    public void WriteCounter(int playerId, SymbolId<CounterTag> counter, int value)
    {
        int clamped = Schema.ClampCounter(counter, value);
        _counters[(playerId, counter.Id)] = clamped;
        OnCounterWrite?.Invoke(counter.Name, clamped);
    }

    public bool CheckDeckRule(int playerId, DeckRule rule)
    {
        var deck = EnumerateZone(ZoneRef.Deck, playerId).ToList();
        return rule switch
        {
            DeckRule.NoDuplicates => deck.Select(e => e.Def?.CardId ?? 0).Distinct().Count() == deck.Count,
            DeckRule.EvenCostOnly => deck.All(e => e.Cost % 2 == 0),
            DeckRule.OddCostOnly => deck.All(e => e.Cost % 2 == 1),
            _ => true,
        };
    }

    public int AllocateEntityId() => _nextEntityId++;

    public void MoveEntity(RuntimeEntity e, ZoneRef toZone, int toPlayerId)
    {
        if (e.Zone == toZone && e.PlayerId == toPlayerId) return;
        e.Zone = toZone;
        e.PlayerId = toPlayerId;
        e.EntrySerial++;      // ★ 每次换区流水号 +1
    }

    public void RemoveEntity(RuntimeEntity e) => _entities.Remove(e);

    /// <summary>统一伤害管线：屏障先吸收，再扣血。</summary>
    public void ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId)
    {
        if (target == null || amount <= 0) return;
        var barrier = Schema.Keyword("barrier");
        if (target.Keywords.Contains(barrier))
        {
            target.Keywords.Remove(barrier);
            amount = 0;
        }
        if (amount > 0) target.Life -= amount;
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDamaged, Subject = EntityRef.From(target), Amount = amount });
    }

    /// <summary>
    /// 破坏：进墓场，并补上 MoveEntity 不会做的三件事：
    /// · 发 OnDestroy（谢幕曲）
    /// · 墓场 +1（Necromancy 与 Reanimate 都靠它）
    /// · 记入本局被破坏池（这就是为什么本场景要存在）
    /// 只有真正的「被破坏」才调它；弃牌 / 流放 / 回手一律用 MoveEntity。
    /// </summary>
    public void DestroyEntity(RuntimeEntity e)
    {
        if (e == null || e.Zone != ZoneRef.Board) return;
        MoveEntity(e, ZoneRef.Cemetery, e.PlayerId);
        EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
    }

    public void EmitEvent(VmEvent ev) => EventLog.Add(ev);

    public RuntimeEntity SpawnCard(int cardId, ZoneRef toZone, int toPlayerId)
    {
        var e = Create(cardId, toZone, toPlayerId);
        if (_cardPool.TryGetValue(cardId, out var d))
        {
            e.Attack = d.BaseAttack;
            e.Life = d.BaseLife;
            e.MaxLife = d.BaseLife;
            e.Cost = d.BaseCost;
        }
        return e;
    }

    public void ReplaceDeck(int playerId, string deckName, IReadOnlyList<int> cardIds)
    {
        // 旧牌库直接离场：不进 Banished、不进 Cemetery，否则计数器被污染
        for (int i = _entities.Count - 1; i >= 0; i--)
        {
            var e = _entities[i];
            if (e.PlayerId == playerId && e.Zone == ZoneRef.Deck) _entities.RemoveAt(i);
        }
        foreach (var id in cardIds) SpawnCard(id, ZoneRef.Deck, playerId);
    }

    public RuntimeEntity SpawnCopy(EntityRef src, ZoneRef toZone, int toPlayerId)
    {
        var s = src.Snapshot;
        if (s == null) return null;
        var e = Create(s.CardId, toZone, toPlayerId, s.Attack, s.Life, s.Cost);
        return e;
    }

    public int ExpireTemporaryModifiers(int endingPlayer)
    {
        int n = 0;
        foreach (var e in _entities) n += ExpireOn(e, endingPlayer);
        return n;
    }

    private static int ExpireOn(RuntimeEntity e, int endingPlayer)
    {
        int n = 0;
        for (int i = e.TempModifiers.Count - 1; i >= 0; i--)
        {
            var m = e.TempModifiers[i];
            bool thisTurn = m.Duration == Ast.DurationKind.ThisTurn;
            bool oppTurn = m.Duration == Ast.DurationKind.UntilEndOfOpponentTurn && endingPlayer != e.PlayerId;
            if (!thisTurn && !oppTurn) continue;
            if (m.IsKeywordGrant)
            {
                e.Keywords.Remove(m.Keyword);
                e.KeywordValues.Remove(m.Keyword);
            }
            else if (!m.IsCounterMod)
            {
                e.Attack -= m.Attack;
                e.Life -= m.Life;
            }
            e.TempModifiers.RemoveAt(i);
            n++;
        }
        return n;
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

    IReadOnlyList<(RuntimeEntity e, AbilityDef ab)> IVmWorld.GrantedAbilities => GrantedAbilities;

    public void GrantAbility(RuntimeEntity e, AbilityDef ability)
        => GrantedAbilities.Add((e, ability));

    public readonly List<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> GrantedCounterAbilities = new();

    public void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, AbilityDef ability)
        => GrantedCounterAbilities.Add((playerId, counter, ability));

    public int ProcessStateBasedActions()
    {
        int n = 0;
        var dead = _entities.Where(e => e.Zone == ZoneRef.Board && e.Life <= 0).ToList();
        foreach (var e in dead)
        {
            e.Zone = ZoneRef.Cemetery;
            e.EntrySerial++;   // 换区，旧引用失效
            EmitEvent(new VmEvent { Kind = CoreEvent.OnDestroy, Subject = EntityRef.From(e) });
            n++;
        }
        return n;
    }

    public IReadOnlyList<EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope)
        => Array.Empty<EffectNode>();   // 宏由 Battle 层注册，自测里不展开
}

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
        // 钳制到该计数器自己的范围（pp 0..10 / ep 0..2 …）
        Players[playerId].Counters[counter.Id] = Schema.ClampCounter(counter, value);
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

    /// <summary>
    /// 历史池查询。BattleState 维护的是快照列表（PlayerState 里的 DestroyedThisMatch 等），
    /// 不是当前的 Cemetery —— 被复活或消失的卡不应该再算进去。
    /// </summary>
    public IEnumerable<EntitySnapshot> EnumerateHistory(HistoryScope scope, int playerId)
    {
        IEnumerable<PlayerState> ps = playerId < 0 ? Players : new[] { Players[playerId] };
        return scope switch
        {
            HistoryScope.DestroyedThisTurn => ps.SelectMany(p => p.DestroyedThisTurn).ToList(),
            HistoryScope.DestroyedThisMatch => ps.SelectMany(p => p.DestroyedThisMatch).ToList(),
            HistoryScope.EnteredThisMatch => ps.SelectMany(p => p.EnteredThisMatch).ToList(),
            _ => Array.Empty<EntitySnapshot>(),
        };
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

        // 装配卡面自带的常驻属性。
        // 裸关键词（Ward / Storm / Rush）与 Countdown (N) 在编译器里被编成
        // PassiveAura 能力，但之前从来没有人在实体创建时把它们装进去，
        // 所以一张印着 Ward 的随从被召唤上场后 Keywords 是空的。
        if (def != null)
        {
            foreach (var ab in def.Abilities)
            {
                if (ab?.Trigger == null) continue;
                if (ab.Trigger.Event != Core.CoreEvent.PassiveAura) continue;

                foreach (var n in ab.Body ?? Array.Empty<Ast.EffectNode>())
                {
                    if (n is Ast.ModifyKeywordNode mk && mk.Op == Ast.KeywordOp.Grant)
                        if (!e.Keywords.Contains(mk.Keyword)) e.Keywords.Add(mk.Keyword);
                }

                foreach (var c in ab.Costs ?? Array.Empty<Ast.CostSpec>())
                {
                    if (c.Kind != Ast.CostKind.Countdown) continue;
                    if (c.Amount?.Op != Ast.NumOp.Const) continue;
                    e.Counters[Schema.Counter("countdown")] = c.Amount.Value;
                }
            }

            foreach (var k in def.InnateKeywords) if (!e.Keywords.Contains(k)) e.Keywords.Add(k);
            foreach (var kv in def.InnateCounters) e.Counters[kv.Key] = kv.Value;
        }

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

        // 纹章唯一铁律：同名纹章不可重复持有。
        // 已有纹章 A 时再获得 A，直接忽略（连倒计时也不刷新）。
        if (zone == ZoneRef.CrestArea && e.Def != null)
        {
            bool exists = p.CrestArea.Any(c => c.Def != null && c.Def.CardId == e.Def.CardId);
            if (exists) { e.Zone = ZoneRef.None; _all.Remove(e); return; }
        }

        int cap = PlayerState.Capacity(zone);
        if (cap >= 0 && list.Count >= cap)
        {
            // 铁律 2：满场/满手时新实体直接消失，不触发入场与破坏事件。
            // 它既不是被破坏也不是被弃牌，所以不能进 Cemetery，
            // 否则墓地、shadow、DestroyedThisMatch 都会被凭空打爆。
            // 用 ZoneRef.None 表示「从未存在于任何区域」。
            if (zone == ZoneRef.Board || zone == ZoneRef.Hand)
            {
                e.Zone = ZoneRef.None;
                _all.Remove(e);
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
    /// <summary>
    /// 统一伤害管线。能力伤害必须走这里，
    /// 否则屏障、超进化免伤、伤害上限全部被绕过。
    /// </summary>
    public void ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId)
        => new CombatResolver(this, Schema).ApplyDamage(target, amount, sourcePlayerId);

    public void DestroyEntity(RuntimeEntity e)
    {
        if (e == null) return;
        if (e.Zone != ZoneRef.Board) return;

        // 超进化铁律：自己的回合内，超进化随从不能被任何能力或效果破坏。
        // 这条规则之前完全没有实现，法术可以直接炸死刚超进化的随从。
        if (e.IsSuperEvolved && e.PlayerId == ActivePlayer) return;

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

    public void ReplaceDeck(int playerId, string deckName, IReadOnlyList<int> cardIds)
    {
        var p = Players[playerId];
        // 旧牌库直接离场：既不进 Banished 也不进 Cemetery，
        // 否则 BanishedCount / CemeteryCount 会被一次性打爆。
        p.Deck.Clear();
        foreach (var id in cardIds) CreateEntity(id, ZoneRef.Deck, playerId);
    }

    public RuntimeEntity SpawnCopy(EntityRef source, ZoneRef toZone, int toPlayerId)
    {
        var s = source.Snapshot;
        if (s == null) return null;
        var e = CreateEntity(s.CardId, toZone, toPlayerId, s.Attack, s.Life, s.Cost);
        return e;
    }

    /// <summary>
    /// 变身：把实体换成另一张卡。
    ///
    /// 官方术语表有独立的「变身」条目，语义是**变成另一张卡**，
    /// 而不只是换个名字 —— 旧卡的关键词、计数器、进化状态、
    /// 临时修正都必须清掉，否则新卡会带着旧卡的全部状态。
    ///
    /// 变形不算破坏：不触发 OnDestroy，不加 shadow。
    /// </summary>
    public void TransformEntity(RuntimeEntity e, int intoCardId)
    {
        if (e == null) return;
        if (!_cardPool.TryGetValue(intoCardId, out var d)) return;

        e.Def = d;
        e.Attack = d.BaseAttack;
        e.Life = d.BaseLife;
        e.MaxLife = d.BaseLife;
        e.Cost = d.BaseCost;

        // 旧卡的状态一律不继承
        e.Keywords.Clear();
        e.KeywordValues.Clear();
        e.Counters.Clear();
        e.TempModifiers.Clear();
        e.IsEvolved = false;
        e.IsSuperEvolved = false;
        e.IsSilenced = false;
        e.AttacksThisTurn = 0;
        e.AttackedThisTurn = false;
        e.AttackedLeaderLastTurn = false;
    }

    public void GrantAbility(RuntimeEntity e, AbilityDef ability)
        => _granted.Add((e, ability));

    public IReadOnlyList<(RuntimeEntity e, AbilityDef ab)> GrantedAbilities => _granted;

    public void GrantCounterAbility(int playerId, SymbolId<CounterTag> counter, AbilityDef ability)
        => _grantedCounters.Add((playerId, counter, ability));

    public IReadOnlyList<(int playerId, SymbolId<CounterTag> ctr, AbilityDef ab)> GrantedCounterAbilities => _grantedCounters;

    /// <summary>
    /// 机制宏展开。返回空表示未实现 —— VM 会因此报错，
    /// 而不是让效果凭空消失。
    /// </summary>
    public IReadOnlyList<EffectNode> ExpandMacro(SymbolId<MacroTag> macro, IReadOnlyList<int> args, VmScope scope)
        => Array.Empty<EffectNode>();

    // ---------- 抽牌 ----------

    /// <summary>
    /// 抽牌。
    ///
    /// 官方规则：牌组为 0 张时抽牌即失败 ——
    /// 但若自己牌组底部已有「胜利的卡牌」，则改为胜利。
    /// 之前这里只是 continue，抽空牌组不会有任何后果。
    /// </summary>
    public void DrawCards(int playerId, int count)
    {
        var p = Players[playerId];
        for (int i = 0; i < count; i++)
        {
            if (p.Deck.Count == 0)
            {
                if (p.HasVictoryCard)
                    p.WonByVictoryCard = true;
                else
                    p.LostByDeckOut = true;
                EmitEvent(new VmEvent { Kind = CoreEvent.OnDeckOut });
                return;
            }

            var card = p.Deck[0];
            MoveEntity(card, ZoneRef.Hand, playerId);
            EmitEvent(new VmEvent { Kind = CoreEvent.OnDraw, Subject = EntityRef.From(card) });
        }
    }

    /// <summary>
    /// 撤销到期的临时修正。
    ///
    /// 官方术语表: until the end of the turn = 本回合结束撤销;
    /// until the end of your opponents turn = 到对手回合结束才撤销。
    ///
    /// 之前 Duration 只写进 AST, 运行时从不处理, 所有限时效果都永久生效。
    /// </summary>
    public int ExpireTemporaryModifiers(int endingPlayer)
    {
        int n = 0;
        foreach (var p in Players)
        {
            foreach (var list in new[] { p.Board, p.Hand, p.CrestArea, p.ExArea })
            {
                foreach (var e in list) n += ExpireOn(e, endingPlayer);
            }
        }
        return n;
    }

    private static int ExpireOn(RuntimeEntity e, int endingPlayer)
    {
        int n = 0;
        for (int i = e.TempModifiers.Count - 1; i >= 0; i--)
        {
            var m = e.TempModifiers[i];
            bool due = m.Duration switch
            {
                DurationKind.ThisTurn => true,
                // 到对手回合结束才到期
                DurationKind.UntilEndOfOpponentTurn => endingPlayer != e.PlayerId,
                DurationKind.ThisMatch => false,
                _ => false,
            };
            if (!due) continue;

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

    /// <summary>把牌组底部的卡变成「胜利的卡牌」（官方「胜利的卡牌」机制）。</summary>
    public void SetVictoryCard(int playerId)
    {
        if (playerId is < 0 or > 1) return;
        Players[playerId].HasVictoryCard = true;
    }
}

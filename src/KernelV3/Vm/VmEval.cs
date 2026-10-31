using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 求值器 —— 把 AST 的表达式树跑成具体值
//
// 三个求值入口：
//   EvalNum   : NumExpr  → int
//   EvalBool  : BoolExpr → bool
//   Resolve   : TargetSpec → List<RuntimeEntity>（活体）
//   ResolveRef: TargetSpec → List<EntityRef>（含快照，用于复制/读属性）
// ============================================================================

public sealed class VmEval
{
    private readonly IVmWorld _world;

    public VmEval(IVmWorld world) => _world = world;

    // ---------- 数值求值 ----------

    public int EvalNum(NumExpr e, VmScope scope)
    {
        if (e == null) return 0;
        switch (e.Op)
        {
            case NumOp.Const:
                return e.Value;

            case NumOp.Property:
            {
                var t = e.Of ?? TargetSpec.Self;
                // 优先走快照：实体已离场/死亡时仍能读到它生前的数值（LKI）
                var refsP = ResolveRef(t, scope);
                if (refsP.Count > 0) return ReadStatFromSnapshot(refsP[0].Snapshot, e.Stat);
                var ents = Resolve(t, scope);
                if (ents.Count == 0) return 0;
                return ReadStat(ents[0], e.Stat);
            }

            case NumOp.Count:
            {
                var t = e.Of ?? TargetSpec.Self;
                return Resolve(t, scope).Count;
            }

            case NumOp.Sum:
            {
                var t = e.Of ?? TargetSpec.Self;
                var pool = Resolve(t, scope);

                // "the sum of the 3 highest base costs"：先按 Sort 排好，再取前 TopN 项。
                // Resolve 已经应用过 Sort / SortDescending，这里只需要截断。
                if (t.TopN > 0 && pool.Count > t.TopN)
                    pool = pool.Take(t.TopN).ToList();

                return pool.Sum(x => ReadStat(x, e.Stat));
            }

            case NumOp.Arith:
            {
                if (e.Args == null || e.Args.Count == 0) return 0;
                int acc = EvalNum(e.Args[0], scope);
                for (int i = 1; i < e.Args.Count; i++)
                {
                    int v = EvalNum(e.Args[i], scope);
                    acc = e.Arith switch
                    {
                        ArithOp.Add => acc + v,
                        ArithOp.Sub => acc - v,
                        ArithOp.Mul => acc * v,
                        ArithOp.Div => v == 0
                            ? throw new VmFaultException("表达式除以 0")
                            : acc / v,
                        ArithOp.Mod => v == 0
                            ? throw new VmFaultException("表达式对 0 取模")
                            : acc % v,
                        _ => acc,
                    };
                }
                return acc;
            }

            case NumOp.Cond:
                return EvalBool(e.Cond, scope)
                    ? EvalNum(e.Then, scope)
                    : EvalNum(e.Else, scope);

            case NumOp.Extreme:
            {
                if (e.Args == null || e.Args.Count == 0) return 0;
                var vals = e.Args.Select(a => EvalNum(a, scope)).ToList();
                return e.TakeMax ? vals.Max() : vals.Min();
            }

            case NumOp.Clamp:
            {
                if (e.Args == null || e.Args.Count < 3) return 0;
                int v = EvalNum(e.Args[0], scope);
                int lo = EvalNum(e.Args[1], scope);
                int hi = EvalNum(e.Args[2], scope);
                return Math.Clamp(v, lo, hi);
            }

            case NumOp.Counter:
                // 显式指定了取值玩家时（"both players have N max play points" 的对手侧
                // 就是 NumExpr.Ctr(max_pp, Of=OpponentLeader)），必须直接读那个玩家的
                // 玩家级计数器。不能落回事件源实体：源实体身上同名计数器不存在，
                // 而且同一张卡的自我侧与对手侧会被读成同一个值 —— 那就又变回单侧条件了。
                if (e.Of != null)
                {
                    if (e.Of.Owner != ZoneScope.Self && e.Of.Owner != ZoneScope.Opponent)
                        throw new VmFaultException(
                            "计数器读取的取值玩家必须是 Self 或 Opponent，实际 " + e.Of.Owner);
                    return _world.ReadCounter(scope.ResolvePlayer(e.Of.Owner), e.Counter);
                }
                // 奥义槽是**每张手牌各自**的数值，不能读玩家级计数器。
                // 先查事件源实体自己身上的计数器，没有才退回玩家级。
                if (scope.Source != null)
                {
                    var src = _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial);
                    if (src != null && src.Counters.TryGetValue(e.Counter, out var ev2))
                        return ev2;
                }
                return _world.ReadCounter(scope.ResolvePlayer(scope.Owner), e.Counter);

            case NumOp.DistinctCount:
            {
                var t = e.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Where(x => x.Def != null).Select(x => x.Def.CardId).Distinct().Count();
            }

            case NumOp.MaxGroupSize:
            {
                var t = e.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                var groups = ents.GroupBy(x => x.Cost).Select(g => g.Count()).ToList();
                return groups.Count > 0 ? groups.Max() : 0;
            }

            case NumOp.SplitOf:
                // 把计数器随机拆成 3 份，同一帧内 X/Y/Z 之和恒等于计数器值
                return GetSplitPart(scope, e.Counter, e.Value);

            default:
                throw new VmFaultException("未知的数值运算: " + e.Op);
        }
    }

    // ---------- 布尔求值 ----------

    public bool EvalBool(BoolExpr e, VmScope scope)
    {
        if (e == null) return true;
        switch (e)
        {
            case ConstBoolExpr c: return c.Value;
            case AndExpr a: return a.Items.All(x => EvalBool(x, scope));
            case OrExpr o: return o.Items.Any(x => EvalBool(x, scope));
            case NotExpr n: return !EvalBool(n.Inner, scope);

            case CompareExpr c:
            {
                int l = EvalNum(c.Left, scope);
                int r = EvalNum(c.Right, scope);
                return Compare(l, c.Op, r);
            }

            case ExistsExpr ex:
            {
                int n = Resolve(ex.Target, scope).Count;
                return Compare(n, ex.Op, EvalNum(ex.Count, scope));
            }

            // 属性谓词一律走引用池而不是活体池：
            // 历史池（墓地 / 本局被破坏）里的候选没有活体，只查活体会让
            // 「被破坏的随从里攻击力最高的那个」这类条件全部恒假。
            // 取值规则：活体优先，离场退回快照。
            case HasKeywordExpr hk:
            {
                var t = hk.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefHasKeyword(r, hk.Keyword));
            }

            case HasStateExpr hs:
            {
                var t = hs.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefMatchState(r, hs.State));
            }

            case HasTraitExpr ht:
            {
                var t = ht.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefHasTrait(r, ht.Trait));
            }

            case HasClanExpr hc:
            {
                var t = hc.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefDef(r)?.Clan.Id == hc.Clan.Id);
            }

            case HasKindExpr hk:
            {
                var t = hk.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefDef(r)?.Kind == hk.Kind);
            }

            case NotSameEntityExpr ne:
            {
                var t = ne.Of ?? TargetSpec.Item;
                var ents = Resolve(t, scope);
                // 排除槽位可能是「待定」的（见 TargetSpec.SlotExcludeTriggerOrSelf）：
                // 编译期说不出这句 other 落在触发上下文还是普通动作上下文，
                // 只有执行时才知道。判据是本次作用域有没有绑定事件主体。
                //   触发上下文（scope.Trigger 非空）→ 排除事件主体；
                //   普通动作上下文（scope.Trigger 为空）→ 退回 $self。
                // 为什么不在编译期定死：同一条效果文本在两种上下文里都会被用到，
                // 而 TargetParser 拿到 DefaultPronounSlot=$trigger 只说明「本句位于触发器从句下」，
                // 真正的事件主体属于运行期事实。
                var slot = ne.ExcludeSlot == TargetSpec.SlotExcludeTriggerOrSelf
                    ? (scope.Trigger != null ? TargetSpec.SlotTrigger : TargetSpec.SlotSelf)
                    : ne.ExcludeSlot;
                // 必须走 ResolveRef 而不是 scope.Read：寄存器里通常没有这两枚锚点
                // （CardVm 只设了 scope.Source / scope.Trigger 两个属性），
                // ResolveRef 的槽位分支才带「$self 回退 Source / $trigger 回退 Trigger」
                // —— 直接 Read 会拿到空集合，于是 "other" 静默退化成「不排除任何人」。
                var excluded = ResolveRef(new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = slot,
                    Mode = Core.SelectMode.All,
                }, scope);
                if (excluded.Count == 0) return ents.Count > 0;
                // 排除自己：候选里至少有一个 EntityId 不在排除集合中
                var bad = new HashSet<int>();
                foreach (var ex in excluded) bad.Add(ex.EntityId);
                return ents.Any(x => !bad.Contains(x.EntityId));
            }

            case DeckRuleExpr dr:
                return _world.CheckDeckRule(scope.ResolvePlayer(dr.Owner), dr.Rule);

            case IsPreviousExpr ip:
                return scope.Read(ip.Slot).Count > 0;

            default:
                throw new VmFaultException("未知的布尔运算: " + e.GetType().Name);
        }
    }

    private static bool Compare(int l, CompareOp op, int r) => op switch
    {
        CompareOp.Eq => l == r,
        CompareOp.Ne => l != r,
        CompareOp.Gt => l > r,
        CompareOp.Ge => l >= r,
        CompareOp.Lt => l < r,
        CompareOp.Le => l <= r,
        _ => false,
    };

    /// <summary>
    /// 每个 VmScope 缓存一次拆分结果，保证同一次结算中 X+Y+Z 恒定。
    /// key = 计数器 Id，value = [x, y, z]。
    /// </summary>
    private int GetSplitPart(VmScope scope, SymbolId<CounterTag> counter, int index)
    {
        if (!_splits.TryGetValue(scope, out var map))
        {
            map = new Dictionary<int, int[]>();
            _splits[scope] = map;
        }
        if (!map.TryGetValue(counter.Id, out var parts))
        {
            int total = _world.ReadCounter(scope.ResolvePlayer(scope.Owner), counter);
            if (total < 0) total = 0;
            // 在 [0, total] 上取两个切点，切成三段
            int a = total > 0 ? _world.Rng.Next(0, total + 1) : 0;
            int b = total > 0 ? _world.Rng.Next(0, total + 1) : 0;
            int lo = Math.Min(a, b), hi = Math.Max(a, b);
            parts = new[] { lo, hi - lo, total - hi };
            map[counter.Id] = parts;
        }
        int i = Math.Clamp(index, 0, 2);
        return parts[i];
    }

    private readonly Dictionary<VmScope, Dictionary<int, int[]>> _splits = new();

    private static bool MatchState(RuntimeEntity e, UnitState s) => s switch
    {
        UnitState.Damaged => e.Life < e.MaxLife,
        UnitState.Evolved => e.IsEvolved,
        UnitState.SuperEvolved => e.IsSuperEvolved,
        UnitState.Silenced => e.IsSilenced,
        UnitState.AttackedThisTurn => e.AttackedThisTurn,
        // 「本次攻击打的是主战者」。
        // CombatResolver 在发起 OnAttack **之前**就写定这个字段，
        // 因此它是「攻击目标是不是随从」在触发阶段唯一可读的判据；
        // 缺了它，「Whenever X attacks a follower」只能退化成恒假谓词。
        UnitState.AttackedLeaderLastTurn => e.AttackedLeaderLastTurn,
        _ => false,
    };

    /// <summary>
    /// 从快照判状态。历史池（墓地/本局被破坏）里的条目只能落到这里 ——
    /// 它们的活体早已不存在，只凭活体判定会让所有「离场后的属性判定」恒假。
    /// AttackedThisTurn 不在快照里，快照一律判否。
    /// </summary>
    private static bool MatchState(EntitySnapshot sn, UnitState s) => s switch
    {
        UnitState.Damaged => sn.Life < sn.MaxLife,
        UnitState.Evolved => sn.IsEvolved,
        UnitState.SuperEvolved => sn.IsSuperEvolved,
        UnitState.Silenced => sn.IsSilenced,
        _ => false,
    };

    /// <summary>从快照读属性（实体已死后仍有效）。</summary>
    private static int ReadStatFromSnapshot(EntitySnapshot sn, StatKey k)
    {
        if (sn == null) return 0;
        return k switch
        {
            StatKey.Attack => sn.Attack,
            StatKey.Life => sn.Life,
            StatKey.MaxLife => sn.MaxLife,
            StatKey.Cost => sn.Cost,
            StatKey.BaseAttack => sn.BaseAttack,
            StatKey.BaseLife => sn.BaseLife,
            StatKey.BaseCost => sn.BaseCost,
            _ => 0,
        };
    }

    private static int ReadStat(RuntimeEntity e, StatKey k) => k switch
    {
        StatKey.Attack => e.Attack,
        StatKey.Life => e.Life,
        StatKey.MaxLife => e.MaxLife,
        StatKey.Cost => e.Cost,
        StatKey.BaseAttack => e.Def?.BaseAttack ?? 0,
        StatKey.BaseLife => e.Def?.BaseLife ?? 0,
        StatKey.BaseCost => e.Def?.BaseCost ?? 0,
        _ => 0,
    };

    // ---------- 引用级读取：活体优先，离场退回快照 ----------
    //
    // 历史池里的引用没有活体，但快照里带着离场那一刻的全部数值。
    // 所有「读属性 / 判状态」的点都必须经过这里，
    // 否则墓地里的候选在条件与排序里全部读成 0 / false。

    private RuntimeEntity RefLive(EntityRef r) => _world.GetLive(r.EntityId, r.EntrySerial);

    /// <summary>
    /// 找引用的「当前载体」：活体优先；流水号已变（离场 / 回手）时按 EntityId 在任意区域找回。
    /// EntityId 永不复用，所以这一步不会串到别的卡上。
    /// 载体只用于读 CardDef（卡号 / 兵种 / 职业），数值与状态仍以快照为准。
    /// </summary>
    private RuntimeEntity RefCarrier(EntityRef r)
    {
        var live = RefLive(r);
        if (live != null) return live;
        foreach (var zone in CarrierZones)
        {
            foreach (var e in _world.EnumerateZone(zone, -1))
                if (e != null && e.EntityId == r.EntityId) return e;
        }
        return null;
    }

    private static readonly ZoneRef[] CarrierZones =
    {
        ZoneRef.Cemetery, ZoneRef.Banished, ZoneRef.Hand, ZoneRef.Deck,
        ZoneRef.Board, ZoneRef.ExArea, ZoneRef.CrestArea, ZoneRef.Leader,
    };

    private CardDef RefDef(EntityRef r) => RefCarrier(r)?.Def;

    private int RefStat(EntityRef r, StatKey k)
    {
        var live = RefLive(r);
        if (live != null) return ReadStat(live, k);
        return ReadStatFromSnapshot(r.Snapshot, k);
    }

    private bool RefHasKeyword(EntityRef r, SymbolId<KeywordTag> kw)
    {
        var live = RefLive(r);
        if (live != null) return live.Keywords.Contains(kw);
        return r.Snapshot != null && r.Snapshot.Keywords.Contains(kw);
    }

    private bool RefHasTrait(EntityRef r, SymbolId<TraitTag> trait)
    {
        var live = RefLive(r);
        if (live != null) return live.Traits.Contains(trait);
        return r.Snapshot != null && r.Snapshot.Traits.Contains(trait);
    }

    private bool RefMatchState(EntityRef r, UnitState s)
    {
        var live = RefLive(r);
        if (live != null) return MatchState(live, s);
        return r.Snapshot != null && MatchState(r.Snapshot, s);
    }

    // ---------- 目标解析 ----------

    /// <summary>解析目标为活体实体（用于改变状态）。已离场的会被过滤掉。</summary>
    public List<RuntimeEntity> Resolve(TargetSpec t, VmScope scope)
    {
        var refs = ResolveRef(t, scope);
        var result = new List<RuntimeEntity>();
        foreach (var r in refs)
        {
            var live = _world.GetLive(r.EntityId, r.EntrySerial);
            if (live != null) result.Add(live);
        }
        return result;
    }

    /// <summary>解析目标为引用（含快照，用于复制 / 读属性）。</summary>
    public List<EntityRef> ResolveRef(TargetSpec t, VmScope scope)
    {
        if (t == null) return new List<EntityRef>();

        List<EntityRef> pool;
        switch (t.SourceKind)
        {
            case TargetSourceKind.Slot:
                pool = new List<EntityRef>(scope.Read(t.Slot));
                // $self 未显式绑定时回退到 Source —— 引擎自动语义
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotSelf && scope.Source != null)
                    pool.Add(scope.Source.Value);
                // $trigger 同理回退到 Trigger。
                // 它此前**从未被写进槽位表**：CardVm 只设了 scope.Trigger 这个属性，
                // 而槽位解析读的是 Registers —— 于是任何以 $trigger 表达的条件
                // （"not is AttackedLeaderLastTurn" / "prop:Life($trigger) > 0"）
                // 都在空池上求值，恒为 false，触发器下的 "give it ..." 也一律空转。
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotTrigger && scope.Trigger != null)
                    pool.Add(scope.Trigger.Value);
                // $opposing 未绑定 → 回退到事件的另一方（ev.Object）。
                // CardVm 已在构造 scope 时显式绑定；这条兜底用于
                // 没有走那条路径的求值场景（条件求值、测试直调等）。
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotOpposing
                    && scope.Event?.Object != null)
                    pool.Add(scope.Event.Object.Value);
                break;

            case TargetSourceKind.CardId:
                pool = _world.FindByCardId(t.Zone, scope.ResolvePlayer(t.Owner), t.CardId)
                             .Select(EntityRef.From).ToList();
                break;

            default:
                if (t.History != HistoryScope.None)
                {
                    // 历史池：候选来自「本局/本回合被破坏」的记录，不是当前区域
                    pool = _world.EnumerateHistory(t.History, scope.ResolvePlayer(t.Owner))
                                 .Select(sn => new EntityRef(sn.EntityId, sn.EntrySerial, sn))
                                 .ToList();
                }
                else
                {
                    pool = _world.EnumerateZone(t.Zone, scope.ResolvePlayer(t.Owner))
                                 .Select(EntityRef.From).ToList();
                }
                break;
        }

        // Where 过滤（槽位来源同样适用 —— 这是「对刚抽到的牌再筛」的关键）
        if (t.Where != null && !(t.Where is ConstBoolExpr cb && cb.Value))
        {
            var filtered = new List<EntityRef>();
            foreach (var r in pool)
            {
                // 只有「既没有活体、又没有快照」的引用才是废引用。
                // 历史池（墓地 / 本局被破坏）的 EntrySerial 在 DestroyEntity 时已 +1，
                // GetLive 必然返回 null —— 之前在这里直接 continue，
                // 于是 Reanimate 的候选池永远是空的，召回来的是施放者自己。
                if (_world.GetLive(r.EntityId, r.EntrySerial) == null && r.Snapshot == null) continue;
                var sub = scope.Derive();
                sub.BindRefs(TargetSpec.SlotItem, new[] { r });
                if (EvalBool(t.Where, sub)) filtered.Add(r);
            }
            pool = filtered;
        }

        // IncludeLeader："all enemies" / "all allies" 要同时覆盖随从与主战者。
        //
        // 以前这条展开写在 ActionParser.ParseDeal 里（手工补一条打主战者的节点），
        // 只对「造成伤害」生效，而且会让主战者被打两次（原 Target 自己已带标记）。
        // 放到求值层后，伤害 / 回复 / 修属性等一切按 Target 结算的动作都自动正确，
        // 且 AST 上只有**一个**节点。
        if (t.IncludeLeader && t.Zone == ZoneRef.Board && t.SourceKind == TargetSourceKind.Zone)
        {
            // 场上的护符没有生命值，不该被「全体随从+主战者」类效果选中。
            pool = pool.Where(r => RefDef(r)?.Kind == Ast.CardKind.Follower).ToList();
            foreach (var ld in _world.EnumerateZone(ZoneRef.Leader, scope.ResolvePlayer(t.Owner)))
                pool.Add(EntityRef.From(ld));
        }

        // 名称去重
        if (t.DistinctByName)
        {
            var seen = new HashSet<int>();
            pool = pool.Where(r => seen.Add(r.Snapshot?.CardId ?? 0)).ToList();
        }

        // 排序 —— 同样活体优先、离场退回快照（Reanimate 挑最高费时池子里全是快照）
        Func<EntityRef, int> sortKey = r => t.Sort switch
        {
            SortKey.Attack => RefStat(r, StatKey.Attack),
            SortKey.Life => RefStat(r, StatKey.Life),
            SortKey.Cost => RefStat(r, StatKey.Cost),
            SortKey.BaseCost => RefStat(r, StatKey.BaseCost),
            SortKey.AttackDiff => RefStat(r, StatKey.Attack) - RefStat(r, StatKey.BaseAttack),
            _ => 0,
        };

        if (t.Sort != SortKey.None)
        {
            pool = t.SortDescending ? pool.OrderByDescending(sortKey).ToList() : pool.OrderBy(sortKey).ToList();
        }

        // 数量与模式
        int n = t.Count != null ? EvalNum(t.Count, scope) : 1;
        switch (t.Mode)
        {
            case SelectMode.All: return pool;
            case SelectMode.First: return pool.Take(Math.Max(1, n)).ToList();
            case SelectMode.Last: return pool.Skip(Math.Max(0, pool.Count - Math.Max(1, n))).ToList();
            case SelectMode.Random:
                // 「随机取 1 个攻击力最高的」= 先收缩到极值并列子集再随机。
                // 之前直接 OrderBy(Rng) 把上面的排序整个洗掉，
                // 于是 Random 模式下的 Sort 完全是装饰品。
                if (t.Sort != SortKey.None && t.TopN == 0 && pool.Count > 1)
                {
                    int best = t.SortDescending ? pool.Max(sortKey) : pool.Min(sortKey);
                    pool = pool.Where(r => sortKey(r) == best).ToList();
                }
                return pool.OrderBy(_ => _world.Rng.Next()).Take(Math.Max(0, n)).ToList();
            case SelectMode.Distinct: return pool.Take(Math.Max(1, n)).ToList();
            case SelectMode.Choose: return pool;   // ← 由调用方挂起等玩家
            default: return pool;
        }
    }
}

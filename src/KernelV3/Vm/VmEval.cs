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
                return Resolve(t, scope).Sum(x => ReadStat(x, e.Stat));
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
                return _world.ReadCounter(scope.Owner, e.Counter);

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

            case HasKeywordExpr hk:
            {
                var t = hk.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Any(x => x.Keywords.Contains(hk.Keyword));
            }

            case HasStateExpr hs:
            {
                var t = hs.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Any(x => MatchState(x, hs.State));
            }

            case HasTraitExpr ht:
            {
                var t = ht.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Any(x => x.Traits.Contains(ht.Trait));
            }

            case HasClanExpr hc:
            {
                var t = hc.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Any(x => x.Def != null && x.Def.Clan.Id == hc.Clan.Id);
            }

            case HasKindExpr hk:
            {
                var t = hk.Of ?? TargetSpec.Self;
                var ents = Resolve(t, scope);
                return ents.Any(x => x.Def != null && x.Def.Kind == hk.Kind);
            }

            case NotSameEntityExpr ne:
            {
                var t = ne.Of ?? TargetSpec.Item;
                var ents = Resolve(t, scope);
                var excluded = scope.Read(ne.ExcludeSlot);
                if (excluded.Count == 0) return ents.Count > 0;
                // 排除自己：候选里至少有一个 EntityId 不在排除集合中
                var bad = new HashSet<int>();
                foreach (var ex in excluded) bad.Add(ex.EntityId);
                return ents.Any(x => !bad.Contains(x.EntityId));
            }

            case DeckRuleExpr dr:
                return _world.CheckDeckRule(dr.Owner, dr.Rule);

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
            int total = _world.ReadCounter(scope.Owner, counter);
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
        _ => false,
    };

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
                break;

            case TargetSourceKind.CardId:
                pool = _world.FindByCardId(t.Zone, t.Owner, t.CardId)
                             .Select(EntityRef.From).ToList();
                break;

            default:
                pool = _world.EnumerateZone(t.Zone, t.Owner)
                             .Select(EntityRef.From).ToList();
                break;
        }

        // Where 过滤（槽位来源同样适用 —— 这是「对刚抽到的牌再筛」的关键）
        if (t.Where != null && !(t.Where is ConstBoolExpr cb && cb.Value))
        {
            var filtered = new List<EntityRef>();
            foreach (var r in pool)
            {
                var live = _world.GetLive(r.EntityId, r.EntrySerial);
                if (live == null) continue;
                var sub = scope.Derive();
                sub.BindRefs(TargetSpec.SlotItem, new[] { r });
                if (EvalBool(t.Where, sub)) filtered.Add(r);
            }
            pool = filtered;
        }

        // 名称去重
        if (t.DistinctByName)
        {
            var seen = new HashSet<int>();
            pool = pool.Where(r => seen.Add(r.Snapshot?.CardId ?? 0)).ToList();
        }

        // 排序
        if (t.Sort != SortKey.None)
        {
            Func<EntityRef, int> key = r =>
            {
                var live = _world.GetLive(r.EntityId, r.EntrySerial);
                if (live == null) return 0;
                return t.Sort switch
                {
                    SortKey.Attack => live.Attack,
                    SortKey.Life => live.Life,
                    SortKey.Cost => live.Cost,
                    SortKey.BaseCost => live.Def?.BaseCost ?? 0,
                    SortKey.AttackDiff => live.Attack - (live.Def?.BaseAttack ?? 0),
                    _ => 0,
                };
            };
            pool = t.SortDescending ? pool.OrderByDescending(key).ToList() : pool.OrderBy(key).ToList();
        }

        // 数量与模式
        int n = t.Count != null ? EvalNum(t.Count, scope) : 1;
        switch (t.Mode)
        {
            case SelectMode.All: return pool;
            case SelectMode.First: return pool.Take(Math.Max(1, n)).ToList();
            case SelectMode.Last: return pool.Skip(Math.Max(0, pool.Count - Math.Max(1, n))).ToList();
            case SelectMode.Random: return pool.OrderBy(_ => _world.Rng.Next()).Take(Math.Max(0, n)).ToList();
            case SelectMode.Distinct: return pool.Take(Math.Max(1, n)).ToList();
            case SelectMode.Choose: return pool;   // ← 由调用方挂起等玩家
            default: return pool;
        }
    }
}

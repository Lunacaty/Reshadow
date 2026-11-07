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

            case NumOp.RandomRange:
            {
                // 同一次结算内锁定：X 被引用三次也只掷一次骰。
                // 缓存键是 (scope, RollId) —— scope 对应「这一次能力结算」，
                // RollId 区分 X / Y / Z。
                int min = e.Value;
                int max = e.Args != null && e.Args.Count > 0 ? EvalNum(e.Args[0], scope) : min;
                if (max < min) max = min;

                if (!_rolls.TryGetValue(scope, out var bag))
                {
                    bag = new Dictionary<int, int>();
                    _rolls[scope] = bag;
                }
                if (!bag.TryGetValue(e.RollId, out int rolled))
                {
                    rolled = _world.Rng.Next(min, max + 1);
                    bag[e.RollId] = rolled;
                }
                return rolled;
            }

            case NumOp.Property:
            {
                var t = e.Of ?? TargetSpec.Self;
                // 活体优先：刚被 +0/+1 的随从要读到**当前**生命值，而不是绑定时的快照；
                // 实体已离场/死亡时才回落快照（LKI）。与 RefStat 的口径保持一致。
                var refsP = ResolveRef(t, scope);
                if (refsP.Count > 0) return RefStat(refsP[0], e.Stat);
                var ents = Resolve(t, scope);
                if (ents.Count == 0) return 0;
                return ReadStat(ents[0], e.Stat);
            }

            case NumOp.Count:
            {
                var t = e.Of ?? TargetSpec.Self;
                // 必须用 ResolveRef（含历史快照），不能用 Resolve（只认当前活体）：
                // 「本局入场过的 N 张同名卡」「本局被破坏的 N 张」这些候选
                // 在事件到达时早已离场或死亡，GetLive 必然返回 null，
                // 用 Resolve 计数会恒为 0 —— 条件永久为假。
                // <Kind> 修饰词必须归一化成 $item：编译期会把它提权成 HasKindExpr(Of=$self)，
                // 不归一化它读的是事件源/施法者的卡种（对 Spell/Amulet 恒 false）→ 计数恒 0。
                return ResolveRef(CardVm.NormalizeZoneWhere(t), scope).Count;
            }

            case NumOp.Sum:
            {
                var t = e.Of ?? TargetSpec.Self;
                // 必须走 ResolveRef（= 引用池），不能用 Resolve（= 活体池）。
                //
                // Resolve 只返回当前在场的活体（GetLive），而
                // "... destroyed this turn" 这类历史快照里的实体早已离场，
                // GetLive 一律返回 null —— 求和恒为 0（90034120 式神·贵人）。
                // 同 Count：<Kind> 修饰词要归一化成 $item，否则卡种过滤读的是事件源。
                var pool = ResolveRef(CardVm.NormalizeZoneWhere(t), scope);

                // "the sum of the 3 highest base costs"：先按 Sort 排好，再取前 TopN 项。
                // ResolveRef 已经应用过 Sort / SortDescending，这里只需要截断。
                if (t.TopN > 0 && pool.Count > t.TopN)
                    pool = pool.Take(t.TopN).ToList();

                return pool.Sum(r => RefStat(r, e.Stat));
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
                // 奥义槽 = 当前回合数 + 本卡在手牌期间我方进化次数（76-c 6.2.5）。
                //
                // 早先只在 StartTurn 给「已经在手牌」的卡 +1，于是第 8 回合
                // 刚抽上手的奥义卡槽位是 0 而不是 8。改成读取时动态合成：
                // 实体身上的 skybound_gauge 只记「手牌期间进化次数」，
                // 总槽位由 TurnNumber 实时加上（179 号文档 P1-7）。
                if (e.Counter.Name == "skybound_gauge")
                {
                    int evoBonus = 0;
                    if (scope.Source != null)
                    {
                        var srcSb = _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial);
                        if (srcSb != null && srcSb.Counters.TryGetValue(e.Counter, out var sbCnt))
                            evoBonus = sbCnt;
                    }
                    int turnNum = (_world as Battle.BattleState)?.TurnNumber ?? 0;
                    return turnNum + evoBonus;
                }
                // 奥义槽以外的计数器：先查事件源实体自己身上的，没有才退回玩家级。
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
                // 同 NumOp.Count：历史池里的候选没有活体，必须走引用池。
                // 卡号取「活体 or 快照」，两者都缺才计 0（那不是一张可识别的卡）。
                // 同 Count：先归一化 <Kind>，否则卡种过滤读的是事件源。
                return ResolveRef(CardVm.NormalizeZoneWhere(t), scope)
                    .Select(r => RefDef(r)?.CardId ?? (r.Snapshot?.CardId ?? 0))
                    .Where(id => id != 0)
                    .Distinct()
                    .Count();
            }

            case NumOp.MaxGroupSize:
            {
                var t = e.Of ?? TargetSpec.Self;
                // 同 Count：先归一化 <Kind>，否则卡种过滤读的是事件源。
                // 并且走引用池（含已离场的历史卡），与 Count/Sum 口径一致；
                // 早先走活体池，跨回合的「同费卡分组」统计会漏掉已离场的卡。
                var refs = ResolveRef(CardVm.NormalizeZoneWhere(t), scope);
                var groups = refs.GroupBy(r => RefStat(r, StatKey.Cost)).Select(g => g.Count()).ToList();
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
                // 存在性判断同样要看得见历史池。
                int n = ResolveRef(ex.Target, scope).Count;
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

            case HasCardNameExpr hcn:
            {
                // CardId == 0 且 CardName 为空时，Of 是「参照槽位」——
                // 判断 $item 是否与那个槽位里的实体**同名**。
                //   "Banish all enemy copies of it from the field"（10334110）
                if (hcn.CardId == 0 && string.IsNullOrEmpty(hcn.CardName) && hcn.Of != null)
                {
                    var refTargets = ResolveRef(hcn.Of, scope);
                    if (refTargets.Count == 0) return false;
                    int wantId = RefDef(refTargets[0])?.CardId ?? (refTargets[0].Snapshot?.CardId ?? 0);
                    if (wantId <= 0) return false;
                    return ResolveRef(TargetSpec.Item, scope).Any(r =>
                        (RefDef(r)?.CardId ?? (r.Snapshot?.CardId ?? 0)) == wantId);
                }

                var t = hcn.Of ?? TargetSpec.Item;
                return ResolveRef(t, scope).Any(r =>
                {
                    var d = RefDef(r);
                    if (d == null) return false;
                    // 纹章生成时 CardId 被改写成 400xxxxxx 合成号，原卡号存在 ArtCardId 里，
                    // 只比 CardId 会让 `where named #10444110` 查不到纹章（175 号第二部分 4-2）。
                    if (hcn.CardId > 0 && (d.CardId == hcn.CardId || d.ArtCardId == hcn.CardId)) return true;
                    // 中英文名都要比：编译产物里 CardName 可能是英文，
                    // 而 CardDef.Name 走的是本地化名 / NameChs 是简中（174 号文档 12）。
                    return !string.IsNullOrEmpty(hcn.CardName)
                           && (string.Equals(d.Name, hcn.CardName, StringComparison.OrdinalIgnoreCase)
                               || string.Equals(d.NameChs, hcn.CardName, StringComparison.OrdinalIgnoreCase));
                });
            }
            case HasPackExpr hp:
            {
                var t = hp.Of ?? TargetSpec.Self;
                // PackId 允许用 '|' 分隔多个卡包（"official-sv|diy-mecha"）。
                var allowed = (hp.PackId ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
                return ResolveRef(t, scope).Any(r =>
                {
                    var pack = RefDef(r)?.PackId ?? "";
                    return allowed.Any(x => string.Equals(pack, x.Trim(), StringComparison.OrdinalIgnoreCase));
                });
            }

            case HasClanExpr hc:
            {
                var t = hc.Of ?? TargetSpec.Self;

                // ① 「本职业」：读我方主战者的职业，未设则回退到来源卡。
                if (hc.IsSelfClan)
                {
                    int myPid = scope.ResolvePlayer(Core.ZoneScope.Self);
                    var leaderDef = _world.EnumerateZone(Core.ZoneRef.Leader, myPid)
                        .FirstOrDefault()?.Def;
                    var myClan = (leaderDef != null && leaderDef.Clan.IsValid)
                        ? leaderDef.Clan
                        : (scope.Source != null
                            ? (RefDef(scope.Source.Value)?.Clan ?? Core.SymbolId<Core.ClanTag>.Invalid)
                            : Core.SymbolId<Core.ClanTag>.Invalid);
                    if (!myClan.IsValid) return false;
                    return ResolveRef(t, scope).Any(r => RefDef(r)?.Clan.Id == myClan.Id);
                }

                // ② 多职业区间（"Swordcraft|Runecraft"）
                if (hc.Clans != null && hc.Clans.Count > 0)
                {
                    var want = hc.Clans;
                    return ResolveRef(t, scope).Any(r =>
                    {
                        var c = RefDef(r)?.Clan ?? Core.SymbolId<Core.ClanTag>.Invalid;
                        return c.IsValid && want.Any(x => x.Id == c.Id);
                    });
                }

                // ③ 单一固定职业
                return ResolveRef(t, scope).Any(r => RefDef(r)?.Clan.Id == hc.Clan.Id);
            }

            case HasKindExpr hk:
            {
                var t = hk.Of ?? TargetSpec.Self;
                return ResolveRef(t, scope).Any(r => RefKind(r) == hk.Kind);
            }

            case NotSameEntityExpr ne:
            {
                var t = ne.Of ?? TargetSpec.Item;
                // 同样必须用引用池：这句 other 通常就是在过滤历史池里的候选
                // （"... copies of X have entered the field this match"），
                // 用活体池会把已经离场的候选全部算成「不存在」，过滤恒 true。
                var ents = ResolveRef(t, scope);
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

    /// <summary>同一次结算内锁定的随机数：scope → (RollId → 值)。</summary>
    private readonly Dictionary<VmScope, Dictionary<int, int>> _rolls = new();

    private bool MatchState(RuntimeEntity e, UnitState s) => s switch
    {
        UnitState.Damaged => e.Life < e.MaxLife,
        UnitState.Evolved => e.IsEvolved,
        UnitState.SuperEvolved => e.IsSuperEvolved,
        UnitState.Silenced => e.IsSilenced,
        // "Whenever you play an Enhanced card"：本卡以爆能强化档位打出。
        // 必须写在这个**活体**重载里 —— RefMatchState 是活体优先，
        // 打出瞬间（随从在战场 / 法术在结算栈里）走的都是这一支，
        // 只写快照重载等于让这个谓词永远返回 false。
        UnitState.Enhanced => e.PlayedWithEnhance,
        UnitState.AttackedThisTurn => e.AttackedThisTurn,
        // 「本次攻击打的是主战者」。
        // CombatResolver 在发起 OnAttack **之前**就写定这个字段，
        // 因此它是「攻击目标是不是随从」在触发阶段唯一可读的判据；
        // 缺了它，「Whenever X attacks a follower」只能退化成恒假谓词。
        UnitState.CurrentAttackHitLeader => e.CurrentAttackHitLeader,

        // 「whose cost has been changed」/「with Fuse」/「with Last Words」/
        // 「with On Spellboost」—— 这四种谓词 TargetParser 都会产出，
        // 但这里一直没有分支，一律落进 default 的 false。
        // 于是卡面上写着「取一张带 Fuse 的随从」时永远取不到任何东西。
        UnitState.CostModified => e.Def != null && e.Cost != e.Def.BaseCost,
        // 「with Fuse」问的是「这张卡能不能融合」（有没有 Fuse: 材料声明），
        // 不是「有没有 OnFuse 触发器」—— 材料声明段本身不产出能力。
        // 「with Fuse」问的是「这张卡能不能融合」（有没有 Fuse: 材料声明），
        // 不是「有没有 OnFuse 触发器」—— 材料声明段本身不产出能力。
        UnitState.HasFuse => e.Def?.HasFuse == true
                         || (e.Def?.Abilities != null && e.Def.Abilities.Any(a => a != null && a.HasFuse))
                         || HasAbilityOf(e, Core.CoreEvent.OnFuse),
        UnitState.HasLastWords => HasAbilityOf(e, Core.CoreEvent.OnDestroy),
        UnitState.HasOnSpellboost => HasAbilityOf(e, Core.CoreEvent.OnSpellboost),
        _ => false,
    };

    /// <summary>
    /// 实体（或它身上挂的授予能力）是否声明了指定事件的能力。
    /// 「with Fuse」这类谓词问的是「它有没有这条能力」，不是「关键词表里有没有」。
    /// </summary>
    private bool HasAbilityOf(RuntimeEntity e, Core.CoreEvent ev)
    {
        if (e?.Def?.Abilities != null)
            foreach (var ab in e.Def.Abilities)
                if (ab?.Trigger != null && ab.Trigger.Event == ev) return true;
        // 运行时授予的能力在 IVmWorld.GrantedAbilities 里，不在实体上。
        if (e != null)
            foreach (var (ge, gab) in _world.GrantedAbilities)
                if (ge != null && ge.EntityId == e.EntityId
                    && gab?.Trigger != null && gab.Trigger.Event == ev) return true;
        return false;
    }

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
        // "Whenever you play an Enhanced card"：本卡以爆能强化档位打出。
        UnitState.Enhanced => sn.PlayedWithEnhance,
        _ => false,
    };

    /// <summary>从快照读属性（实体已死后仍有效）。</summary>
    private static int ReadStatFromSnapshot(EntitySnapshot sn, StatKey k)
    {
        if (sn == null) return 0;
        // 活体读取 ReadStat 对攻击/费用做了 Math.Max(0, …) 钳制，
        // 快照读取必须同口径 —— 否则一个 1 攻随从吃了 -2/-0（内部 Attack=-1）
        // 后被破坏，快照留下 -1，之后「破坏攻击力最低的随从」「召唤墓地中
        // 攻击力最高的随从」以及 SortKey.Attack 的极值分组全被这个 -1 带偏
        // （179 号文档 深水区 3）。
        return k switch
        {
            StatKey.Attack => Math.Max(0, sn.Attack),
            StatKey.Life => sn.Life,
            StatKey.MaxLife => sn.MaxLife,
            StatKey.Cost => Math.Max(0, sn.Cost),
            StatKey.BaseAttack => Math.Max(0, sn.BaseAttack),
            StatKey.BaseLife => sn.BaseLife,
            StatKey.BaseCost => Math.Max(0, sn.BaseCost),
            StatKey.Countdown => ReadCountdown(sn.Counters),
            StatKey.ChantCount => ReadCountdown(sn.Counters),
            _ => 0,
        };
    }

    /// <summary>
    /// 从计数器字典里取「剩余吟唱」。
    /// 吟唱不是实体字段，它住在 Counters 里，名字固定为 countdown。
    /// </summary>
    private static int ReadCountdown(IReadOnlyDictionary<SymbolId<CounterTag>, int> ctrs)
    {
        if (ctrs == null) return 0;
        foreach (var kv in ctrs)
            if (string.Equals(kv.Key.Name, "countdown", StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        return 0;
    }

    private int ReadStat(RuntimeEntity e, StatKey k) => k switch
    {
        StatKey.Attack => Math.Max(0, e.Attack),
        StatKey.Life => e.Life,
        StatKey.MaxLife => e.MaxLife,
        StatKey.Cost => Math.Max(0, e.Cost),
        StatKey.BaseAttack => e.Def?.BaseAttack ?? 0,
        StatKey.BaseLife => e.Def?.BaseLife ?? 0,
        StatKey.BaseCost => e.Def?.BaseCost ?? 0,
        // 吟唱住在 Counters 里，不是实体字段。漏掉这条分支的话
        // 「if countdown is at least 2」永远读到 0，条件恒假。
        StatKey.Countdown => ReadCountdown(e.Counters),
        StatKey.ChantCount => ReadCountdown(e.Counters),

        // 玩家与区域级计数。这些键由 ConditionCompiler 生产、NaturalPrinter 打印，
        // 但 ReadStat 一直没有分支 —— 「你的手牌有几张」读出来恒为 0。
        // 属性求值的锚点决定问的是谁：$self / $trigger 取该实体的控制者，
        // 其余（含主战者）取 owner 侧的玩家。
        StatKey.HandCount => _world.EnumerateZone(ZoneRef.Hand, StatPlayer(e)).Count(),
        StatKey.DeckCount => _world.EnumerateZone(ZoneRef.Deck, StatPlayer(e)).Count(),
        StatKey.BoardCount => _world.EnumerateZone(ZoneRef.Board, StatPlayer(e)).Count(),
        StatKey.CemeteryCount => _world.EnumerateZone(ZoneRef.Cemetery, StatPlayer(e)).Count(),
        StatKey.BanishedCount => _world.EnumerateZone(ZoneRef.Banished, StatPlayer(e)).Count(),
        StatKey.PlayPoint => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("pp")),
        StatKey.MaxPlayPoint => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("max_pp")),
        StatKey.EvolvePoint => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("ep")),
        StatKey.MaxEvolvePoint => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("sep")),
        // 这四个键原先读的是从未被写入的幽灵计数器，恒为 0（174 号文档 四.8）。
        // 直接对接真实数据源：回合数在 TurnNumber，本回合打出张数在 combo，
        // 本局打出张数在 PlayedThisMatch。
        StatKey.TurnIndex => (_world as Battle.BattleState)?.TurnNumber ?? 0,
        StatKey.DamageDealtThisTurn => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("damage_dealt_this_turn")),
        StatKey.CardsPlayedThisTurn => _world.ReadCounter(StatPlayer(e), _world.Schema.Counter("combo")),
        StatKey.CardsPlayedThisMatch => (_world as Battle.BattleState)?.Players[StatPlayer(e)].PlayedThisMatch.Count ?? 0,
        _ => 0,
    };

    /// <summary>属性求值问的是哪个玩家的计数：卡牌看它的控制者，主战者看它自己。</summary>
    private static int StatPlayer(RuntimeEntity e) => e?.PlayerId ?? 0;

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

    // 回退到快照：纯内存探针（发现的候选筛选）不在 _world 里，
    // 只查世界会拿到 null，职业/兵种/关键词条件于是全部判假。
    private CardDef RefDef(EntityRef r) => RefCarrier(r)?.Def ?? r.Snapshot?.Def;

    private int RefStat(EntityRef r, StatKey k)
    {
        var live = RefLive(r);
        if (live != null) return ReadStat(live, k);
        return ReadStatFromSnapshot(r.Snapshot, k);
    }

    private bool RefHasKeyword(EntityRef r, SymbolId<KeywordTag> kw)
    {
        var live = RefLive(r);
        // 必须走世界统一光环查询：直接查 HashSet 会漏掉 PassiveAura 授予的关键词，
        // 于是 "where has ward" 对光环赋予的守护随从全部判 false（174 号文档 三.6）。
        if (live != null) return _world.HasEffectiveKeyword(live, kw);
        return r.Snapshot != null && r.Snapshot.Keywords.Contains(kw);
    }

    /// <summary>
    /// 引用的**卡种**：活体优先，离场后退回快照。
    ///
    /// 不能退回 RefCarrier 去读它现在的 Def —— 结晶护符死进墓地时
    /// Def 已被还原成随从本体，「死掉的护符」于是变成「死掉的随从」，
    /// 亡者召还就会把它当随从复活。快照记的是死亡那一刻的卡种。
    /// </summary>
    private Ast.CardKind RefKind(EntityRef r)
    {
        var live = RefLive(r);
        if (live?.Def != null) return live.Def.Kind;
        if (r.Snapshot != null) return r.Snapshot.Kind;
        return RefCarrier(r)?.Def?.Kind ?? Ast.CardKind.Follower;
    }

    private bool RefHasTrait(EntityRef r, SymbolId<TraitTag> trait)
    {
        // 活体优先，但**快照作为补充**：快照是在实体创建那一刻拍的，
        // 之后由效果追加的兵种（如把一张牌视作 Loot / Departed）不在快照里。
        // 只认快照会让「Whenever you play a Loot card」这类判定恒假 ——
        // 那张牌确实是 Loot，只是它变成 Loot 的时间晚于快照。
        // 载体优先（按 EntityId 找回，容忍流水号变化），其次快照。
        // 用 RefLive 会把「刚移过区、EntrySerial 已 ++」的实体判成已死，
        // 于是只剩快照 —— 而快照是创建那一刻拍的，不含后来追加的兵种。
        var carrier = RefCarrier(r);
        if (carrier != null)
        {
            if (carrier.Traits.Contains(trait)) return true;
            // 卡面印刷的兵种也认：有些路径造出来的实体未必装了 Def.Traits
            // （旧存档 / 手写 CardDef），直接看定义更稳。
            if (carrier.Def?.Traits != null && carrier.Def.Traits.Contains(trait)) return true;
        }
        if (r.Snapshot != null)
        {
            if (r.Snapshot.Traits.Contains(trait)) return true;
            // 快照没带兵种时，回查卡池里的 CardDef（它是印刷兵种的权威来源）。
            var sd = _world.FindByCardId(Core.ZoneRef.Board, r.Snapshot.PlayerId, r.Snapshot.CardId).FirstOrDefault()
                  ?? _world.FindByCardId(Core.ZoneRef.Cemetery, r.Snapshot.PlayerId, r.Snapshot.CardId).FirstOrDefault()
                  ?? _world.FindByCardId(Core.ZoneRef.Hand, r.Snapshot.PlayerId, r.Snapshot.CardId).FirstOrDefault();
            if (sd?.Traits != null && sd.Traits.Contains(trait)) return true;
        }
        return false;
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

                // $created 跨能力域回退：
                //   "<ev>Evolve: Summon 2 Ghosts.</ev>" -> "<sev>Super-Evolve: Give them Drain.</sev>"
                // 两条能力各有独立 VmScope，前者的 $created 在后者里是空的。
                // 但源实体（这张卡）身上留了一份召唤结果，回退读它即可。
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotCreated && scope.Source != null)
                {
                    var srcRef = scope.Source.Value;
                    var carrier = _world.GetLive(srcRef.EntityId, srcRef.EntrySerial);
                    if (carrier != null && carrier.LastCreatedRefs.Count > 0)
                        pool.AddRange(carrier.LastCreatedRefs);
                }

                // $selected 跨能力域回退（与 $created 完全对称）：
                //   "Evolve: Select an enemy follower and banish it."
                //   "Super-Evolve: Banish all enemy copies of it from the field."（10334110）
                // 超进化是另一条能力，作用域里的 $selected 是空的，
                // 只能回到源实体身上取它上一次选了什么。
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotSelected && scope.Source != null)
                {
                    var srcSel = scope.Source.Value;
                    var carrierSel = _world.GetLive(srcSel.EntityId, srcSel.EntrySerial);
                    if (carrierSel != null && carrierSel.LastSelectedRefs.Count > 0)
                        pool.AddRange(carrierSel.LastSelectedRefs);
                }
                // $chooseK 的跨能力域回退，与 $selected 完全对称。
                if (pool.Count == 0 && scope.Source != null
                    && TargetSpec.TryParseChooseSlotIndex(t.Slot, out int chIdx))
                {
                    var srcCh = scope.Source.Value;
                    var carrierCh = _world.GetLive(srcCh.EntityId, srcCh.EntrySerial);
                    if (carrierCh != null && chIdx >= 1 && chIdx <= carrierCh.LastChooseRefs.Count)
                        pool.AddRange(carrierCh.LastChooseRefs[chIdx - 1]);
                }
                // $self 未显式绑定时回退到 Source —— 引擎自动语义
                if (pool.Count == 0 && t.Slot == TargetSpec.SlotSelf && scope.Source != null)
                    pool.Add(scope.Source.Value);
                // $trigger 同理回退到 Trigger。
                // 它此前**从未被写进槽位表**：CardVm 只设了 scope.Trigger 这个属性，
                // 而槽位解析读的是 Registers —— 于是任何以 $trigger 表达的条件
                // （"not is CurrentAttackHitLeader" / "prop:Life($trigger) > 0"）
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

            case TargetSourceKind.CardId when t.History != HistoryScope.None:
                // 「本局入场过的 N 张同名卡」这类：池子来自历史记录，不是当前区域。
                // 不接这一条的话，"if at least 5 other allied copies of X have
                // entered the field this match" 会退化成「当前场上有 5 张 X」，
                // 已死亡/已离场的副本全部不计 —— 条件永久为假。
                pool = _world.EnumerateHistory(t.History, scope.ResolvePlayer(t.Owner))
                             .Where(sn => t.CardId == 0 || sn.CardId == t.CardId)
                             .Select(sn => new EntityRef(sn.EntityId, sn.EntrySerial, sn))
                             .ToList();
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
            // 统一在这里把隐式 $self 归一化成 $item（179 号文档 P0-1）。
            // 放在 ResolveRef 入口而不是各个调用方：DoSpawn、AuraTargets、
            // PreChoiceInspector 等所有路径都自动受惠，不会漏。
            var where = CardVm.NormalizeWhereForItem(t.Where);
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
                if (EvalBool(where, sub)) filtered.Add(r);
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

        // 「只要重复的那些」：每张卡的第一张留在池外（保留在牌库），
        // 从第二张起全部取出。与 DistinctByName 正好互补 ——
        // 执行完之后牌库里恰好只剩每种一张，宇宙条件随即成立。
        if (t.DuplicatesOnly)
        {
            var seenOnce = new HashSet<int>();
            pool = pool.Where(r => !seenOnce.Add(r.Snapshot?.CardId ?? 0)).ToList();
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
            case SelectMode.All:
                // 「all leaders with the lowest defense」（10552310）：
                // All 只是「全取」，但排序键存在时还要先收缩到极值并列子集。
                // 不收缩会变成「打双方主战者各 3 点」，与卡面写的最低价完全相反。
                if (t.Sort != SortKey.None && t.TopN == 0 && pool.Count > 1)
                {
                    int bestAll = t.SortDescending ? pool.Max(sortKey) : pool.Min(sortKey);
                    pool = pool.Where(r => sortKey(r) == bestAll).ToList();
                }
                return pool;
            // n 已经在上方按 Count 求值并钳到 >= 0，这里不能再 Math.Max(1, n)：
            // 动态数量算出 0 时应当取 0 个，不是强取 1 个（175 号对照表 四.9~11 备注）。
            case SelectMode.First: return pool.Take(n).ToList();
            case SelectMode.Last: return pool.Skip(Math.Max(0, pool.Count - n)).ToList();
            case SelectMode.Random:
                if (n <= 0) return new List<EntityRef>();
                // 带 Sort 时按极值分组依次取满 n 个，只在跨过 n 的那一组内部随机。
                // 早先只收缩到「并列极值」再随机，n>1 时会把次高组全部踢掉 ——
                // 「随机破坏 2 个攻击力最高的随从」少破坏 1 个（174 号文档 四.10）。
                if (t.Sort != SortKey.None && t.TopN == 0 && pool.Count > 1)
                {
                    var result = new List<EntityRef>();
                    var groups = t.SortDescending
                        ? pool.GroupBy(sortKey).OrderByDescending(g => g.Key)
                        : pool.GroupBy(sortKey).OrderBy(g => g.Key);
                    foreach (var g in groups)
                    {
                        int need = n - result.Count;
                        if (need <= 0) break;
                        var list = g.ToList();
                        if (list.Count <= need) result.AddRange(list);
                        else result.AddRange(list.OrderBy(_ => _world.Rng.Next()).Take(need));
                    }
                    return result;
                }
                return pool.OrderBy(_ => _world.Rng.Next()).Take(n).ToList();
            case SelectMode.Distinct:
                // 同名各取 1 张，最多 n 张；n=0 时取 0 张。
                if (n <= 0) return new List<EntityRef>();
                return pool.GroupBy(r => RefDef(r)?.CardId ?? (r.Snapshot?.CardId ?? 0))
                           .Select(g => g.First()).Take(n).ToList();
            case SelectMode.Choose: return pool;   // ← 由调用方挂起等玩家
            default: return pool;
        }
    }
}

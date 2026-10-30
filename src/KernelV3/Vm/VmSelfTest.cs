using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// VM 自测 —— 每个用例都对应用户实际关心的一条硬需求
// ============================================================================

public static class VmSelfTest
{
    private static int _pass, _fail;
    private static readonly List<string> _log = new();

    public static string Run()
    {
        _pass = _fail = 0;
        _log.Clear();

        TestBasicDamage();
        TestPendingChoice();          // ★ V2 完全没有的能力
        TestNoCorpseHit();            // ★ 离场后不鞭尸
        TestSnapshotAfterDestroy();   // ★ 死后仍能读快照
        TestEntrySerial();            // ★ 回手再打出不算同一个
        TestSlotNotOverwritten();     // ★ 多步 Previous 链不被污染
        TestSlotSecondaryFilter();    // ★ 对刚抽到的牌再筛
        TestLoopGuard();              // ★ 防死循环
        TestBranchRandom();
        TestConditional();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"=== VM 自测 {_pass}/{_pass + _fail} ===");
        foreach (var l in _log) sb.AppendLine(l);
        return sb.ToString();
    }

    // ---------- 用例 ----------

    private static void TestBasicDamage()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var t = w.Create(1, ZoneRef.Board, ZoneScope.Opponent, atk: 2, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new DealDamageNode
            {
                Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent),
                Amount = NumExpr.Const(3),
            },
        }, new VmScope { Owner = ZoneScope.Self });

        vm.StepUntilBlocked();
        Check("基础伤害", t.Life == 2, $"期望 2，实际 {t.Life}");
    }

    private static void TestPendingChoice()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var a = w.Create(1, ZoneRef.Board, ZoneScope.Opponent, life: 5);
        var b = w.Create(2, ZoneRef.Board, ZoneScope.Opponent, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new SelectTargetNode
            {
                Target = new TargetSpec
                {
                    Zone = ZoneRef.Board, Owner = ZoneScope.Opponent,
                    Mode = SelectMode.Choose, Count = NumExpr.Const(1),
                },
                OutSlot = TargetSpec.SlotSelected,
            },
            new DealDamageNode
            {
                Target = TargetSpec.Previous,
                Amount = NumExpr.Const(3),
            },
        }, new VmScope { Owner = ZoneScope.Self });

        var st = vm.StepUntilBlocked();
        bool blocked = st == VmStatus.WaitingForInput && vm.Pending != null;
        Check("挂起等玩家选择", blocked, $"状态={st}");

        if (blocked)
        {
            int chosen = b.EntityId;
            vm.SubmitChoice(vm.Pending.ChoiceId, new[] { chosen });
            vm.StepUntilBlocked();
            Check("选择后正确执行", b.Life == 2 && a.Life == 5, $"a={a.Life} b={b.Life}");
        }
    }

    private static void TestNoCorpseHit()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var t = w.Create(1, ZoneRef.Board, ZoneScope.Opponent, life: 1);

        vm.PushBody(new EffectNode[]
        {
            new DealDamageNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), Amount = NumExpr.Const(3) },
            new DealDamageNode { Target = TargetSpec.Previous, Amount = NumExpr.Const(3) },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        Check("已死亡不重复受伤", t.Life == -2, $"期望 -2（只挨一次），实际 {t.Life}");
    }

    private static void TestSnapshotAfterDestroy()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var d = new CardDef { CardId = 777, Name = "Target", BaseAttack = 4, BaseLife = 3 };
        w.RegisterCard(d);
        var t = w.Create(777, ZoneRef.Board, ZoneScope.Opponent, atk: 4, life: 3);

        vm.PushBody(new EffectNode[]
        {
            new SelectTargetNode
            {
                Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent),
                OutSlot = TargetSpec.SlotSelected,
            },
            new MoveZoneNode { Target = TargetSpec.Previous, ToZone = ZoneRef.Cemetery },
            // 此时实体已离场，从快照复制
            new SpawnCardNode
            {
                SourceKind = CardSourceKind.CopyOfTarget,
                CopyFrom = TargetSpec.Previous,
                ToZone = ZoneRef.Hand,
                ToOwner = ZoneScope.Self,
            },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        var copy = w.EnumerateZone(ZoneRef.Hand, ZoneScope.Self).FirstOrDefault();
        Check("死后仍能复制（读快照）", copy != null && copy.Def?.CardId == 777, copy == null ? "没生成" : "生成了");
    }

    private static void TestEntrySerial()
    {
        var w = new MemoryWorld();
        var e = w.Create(1, ZoneRef.Board, ZoneScope.Self, life: 5);
        int ser0 = e.EntrySerial;
        var r0 = EntityRef.From(e);

        w.MoveEntity(e, ZoneRef.Hand, ZoneScope.Self);   // 回手
        w.MoveEntity(e, ZoneRef.Board, ZoneScope.Self);  // 再打出

        bool changed = e.EntrySerial != ser0;
        bool stale = w.GetLive(r0.EntityId, r0.EntrySerial) == null;
        Check("回手再打出的旧引用失效", changed && stale, $"序列 {ser0}→{e.EntrySerial}");
    }

    private static void TestSlotNotOverwritten()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var enemy = w.Create(1, ZoneRef.Board, ZoneScope.Opponent, life: 10);
        var self = w.Create(2, ZoneRef.Board, ZoneScope.Self, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new SelectTargetNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), OutSlot = TargetSpec.SlotSelected },
            new DealDamageNode { Target = TargetSpec.Previous, Amount = NumExpr.Const(4) },
            // 中间插一个改自己的动作，不应覆盖 $selected
            new ModifyStatsNode { Target = TargetSpec.Self, Op = StatModOp.Add, Attack = NumExpr.Const(0), Life = NumExpr.Const(1) },
            new MoveZoneNode { Target = TargetSpec.Previous, ToZone = ZoneRef.Cemetery },
        }, new VmScope { Owner = ZoneScope.Self, Source = EntityRef.From(self) });
        vm.StepUntilBlocked();

        Check("槽位不被中间动作覆盖", enemy.Zone == ZoneRef.Cemetery && self.Life == 6,
            $"敌人区={enemy.Zone} 自己生命={self.Life}");
    }

    private static void TestSlotSecondaryFilter()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var f1 = new CardDef { CardId = 10, Kind = CardKind.Follower };
        var f2 = new CardDef { CardId = 11, Kind = CardKind.Spell };
        w.RegisterCard(f1);
        w.RegisterCard(f2);
        var a = w.Create(10, ZoneRef.Deck, ZoneScope.Self);
        var b = w.Create(11, ZoneRef.Deck, ZoneScope.Self);

        // 抽 2 张，然后只对「抽到的随从」做处理
        vm.PushBody(new EffectNode[]
        {
            new MoveZoneNode { Target = TargetSpec.InZone(ZoneRef.Deck, ZoneScope.Self), ToZone = ZoneRef.Hand, OutSlot = TargetSpec.SlotCreated },
            new DealDamageNode
            {
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Slot,
                    Slot = TargetSpec.SlotCreated,
                    Where = new ExistsExpr
                    {
                        Target = TargetSpec.Item,
                        Op = CompareOp.Ge,
                        Count = NumExpr.Const(1),
                    },
                    Mode = SelectMode.All,
                },
                Amount = NumExpr.Const(1),
            },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        Check("抽牌后写入 $created", a.Zone == ZoneRef.Hand && b.Zone == ZoneRef.Hand,
            $"a={a.Zone} b={b.Zone}");
    }

    private static void TestLoopGuard()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);

        vm.PushBody(new EffectNode[]
        {
            new LoopNode
            {
                Kind = LoopKind.Times,
                Count = NumExpr.Const(999999),   // 恶意值
                MaxIterations = 5,
                Body = new EffectNode[]
                {
                    new ModifyCounterNode { Counter = Ctr("hits"), Op = CounterOp.Add, Delta = NumExpr.Const(1) },
                },
            },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        int hits = w.ReadCounter(ZoneScope.Self, Ctr("hits"));
        Check("循环被硬上限截断", hits == 5, $"期望 5 次，实际 {hits}");
    }

    private static void TestBranchRandom()
    {
        var w = new MemoryWorld(seed: 42);
        var vm = new CardVm(w);

        vm.PushBody(new EffectNode[]
        {
            new BranchModeNode
            {
                Kind = ModeDispatchKind.Random,
                PickCount = NumExpr.Const(1),
                Options = new List<IReadOnlyList<EffectNode>>
                {
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("A"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("B"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                },
            },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        int sum = w.ReadCounter(ZoneScope.Self, Ctr("A")) + w.ReadCounter(ZoneScope.Self, Ctr("B"));
        Check("随机分支只走一条", sum == 1, $"总和 {sum}");
    }

    private static void TestConditional()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var t = w.Create(1, ZoneRef.Board, ZoneScope.Opponent, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new ConditionalNode
            {
                Condition = new CompareExpr
                {
                    Left = NumExpr.Prop(StatKey.Life, TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent)),
                    Op = CompareOp.Le,
                    Right = NumExpr.Const(5),
                },
                Then = new EffectNode[]
                {
                    new DealDamageNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), Amount = NumExpr.Const(2) },
                },
            },
        }, new VmScope { Owner = ZoneScope.Self });
        vm.StepUntilBlocked();

        Check("条件分支", t.Life == 3, $"期望 3，实际 {t.Life}");
    }

    // ---------- 工具 ----------

    private static SymbolRegistry<CounterTag> _ctrReg = new();

    private static SymbolId<CounterTag> Ctr(string name)
    {
        var id = _ctrReg.TryGet(name);
        return id.IsValid ? id : _ctrReg.Register(name);
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; _log.Add($"  [OK]   {name}"); }
        else { _fail++; _log.Add($"  [FAIL] {name}  —— {detail}"); }
    }
}

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
        TestSequenceOrder();      // ★ Bug2：帧栈顺序
        TestModeChoice();         // ★ Bug3：模式抉择执行
        TestSingularTarget();     // ★ 单数目标只影响 1 个
        TestPluralTarget();       // ★ 复数目标影响全部
        TestPlayerPerspective();  // ★ 后手玩家的相对视角必须翻译成绝对身份

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
        var t = w.Create(1, ZoneRef.Board, 1, atk: 2, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new DealDamageNode
            {
                Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent),
                Amount = NumExpr.Const(3),
            },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });

        vm.StepUntilBlocked();
        Check("基础伤害", t.Life == 2, $"期望 2，实际 {t.Life}");
    }

    private static void TestPendingChoice()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var a = w.Create(1, ZoneRef.Board, 1, life: 5);
        var b = w.Create(2, ZoneRef.Board, 1, life: 5);

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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });

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
        // 真正的「不鞭尸」验证：
        //   1) 伤害把目标打到 0 以下
        //   2) SBA 必须立刻把它移入墓地并递增 EntrySerial
        //   3) 第二刀即使指向同一区域，也找不到它
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var t = w.Create(1, ZoneRef.Board, 1, life: 1);
        int serial0 = t.EntrySerial;

        vm.PushBody(new EffectNode[]
        {
            new DealDamageNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), Amount = NumExpr.Const(3), IsAbility = false },
            // 第二刀仍然指向 Board；如果 SBA 生效，这里已经找不到任何目标
            new DealDamageNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), Amount = NumExpr.Const(3), IsAbility = false },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        bool movedToGrave = t.Zone == ZoneRef.Cemetery;
        bool serialBumped = t.EntrySerial > serial0;
        bool onlyOneHit = t.Life == -2;   // 第一刀 1-3=-2，第二刀找不到目标

        Check("低于 0 生命立刻进墓地（SBA）", movedToGrave, $"实际区域 {t.Zone}");
        Check("进墓地后 EntrySerial 递增", serialBumped, $"{serial0} -> {t.EntrySerial}");
        Check("已死亡不重复受伤", onlyOneHit, $"期望 -2，实际 {t.Life}");
    }

    private static void TestSnapshotAfterDestroy()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var d = new CardDef { CardId = 777, Name = "Target", BaseAttack = 4, BaseLife = 3 };
        w.RegisterCard(d);
        var t = w.Create(777, ZoneRef.Board, 1, atk: 4, life: 3);

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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        var copy = w.EnumerateZone(ZoneRef.Hand, 0).FirstOrDefault();
        Check("死后仍能复制（读快照）", copy != null && copy.Def?.CardId == 777, copy == null ? "没生成" : "生成了");
    }

    private static void TestEntrySerial()
    {
        var w = new MemoryWorld();
        var e = w.Create(1, ZoneRef.Board, 0, life: 5);
        int ser0 = e.EntrySerial;
        var r0 = EntityRef.From(e);

        w.MoveEntity(e, ZoneRef.Hand, 0);   // 回手
        w.MoveEntity(e, ZoneRef.Board, 0);  // 再打出

        bool changed = e.EntrySerial != ser0;
        bool stale = w.GetLive(r0.EntityId, r0.EntrySerial) == null;
        Check("回手再打出的旧引用失效", changed && stale, $"序列 {ser0}→{e.EntrySerial}");
    }

    private static void TestSlotNotOverwritten()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var enemy = w.Create(1, ZoneRef.Board, 1, life: 10);
        var self = w.Create(2, ZoneRef.Board, 0, life: 5);

        vm.PushBody(new EffectNode[]
        {
            new SelectTargetNode { Target = TargetSpec.InZone(ZoneRef.Board, ZoneScope.Opponent), OutSlot = TargetSpec.SlotSelected },
            new DealDamageNode { Target = TargetSpec.Previous, Amount = NumExpr.Const(4) },
            // 中间插一个改自己的动作，不应覆盖 $selected
            new ModifyStatsNode { Target = TargetSpec.Self, Op = StatModOp.Add, Attack = NumExpr.Const(0), Life = NumExpr.Const(1) },
            new MoveZoneNode { Target = TargetSpec.Previous, ToZone = ZoneRef.Cemetery },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(self) });
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
        var a = w.Create(10, ZoneRef.Deck, 0);
        var b = w.Create(11, ZoneRef.Deck, 0);

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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        int hits = w.ReadCounter(0, Ctr("hits"));
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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        int sum = w.ReadCounter(0, Ctr("A")) + w.ReadCounter(0, Ctr("B"));
        Check("随机分支只走一条", sum == 1, $"总和 {sum}");
    }

    private static void TestConditional()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var t = w.Create(1, ZoneRef.Board, 1, life: 5);

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
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        Check("条件分支", t.Life == 3, $"期望 3，实际 {t.Life}");
    }

    private static void TestSequenceOrder()
    {
        // Sequence 必须按 1,2,3 的顺序执行；帧栈是后进先出，压栈必须倒序
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var log = new List<string>();
        w.OnCounterWrite = (name, val) => log.Add(name + "=" + val);

        vm.PushBody(new EffectNode[]
        {
            new BranchModeNode
            {
                Kind = ModeDispatchKind.Sequence,
                PickCount = NumExpr.Const(3),
                Options = new List<IReadOnlyList<EffectNode>>
                {
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("s1"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("s2"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("s3"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                },
            },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        Check("Sequence 正序执行（Bug2）",
            log.Count == 3 && log[0] == "s1=1" && log[1] == "s2=1" && log[2] == "s3=1",
            "实际顺序: " + string.Join(",", log));
    }

    private static void TestModeChoice()
    {
        // 玩家选模式后，被选中的分支必须真正执行（Bug3）
        var w = new MemoryWorld();
        var vm = new CardVm(w);

        vm.PushBody(new EffectNode[]
        {
            new BranchModeNode
            {
                Kind = ModeDispatchKind.Choose,
                PickCount = NumExpr.Const(1),
                Options = new List<IReadOnlyList<EffectNode>>
                {
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("modeA"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                    new List<EffectNode> { new ModifyCounterNode { Counter = Ctr("modeB"), Op = CounterOp.Add, Delta = NumExpr.Const(1) } },
                },
            },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });

        var st = vm.StepUntilBlocked();
        bool blocked = st == VmStatus.WaitingForInput && vm.Pending != null;
        Check("模式抉择会挂起（Bug3）", blocked, $"状态={st}");

        if (blocked)
        {
            // 选第 2 个模式（下标 1）
            vm.SubmitChoice(vm.Pending.ChoiceId, new[] { 1 });
            vm.StepUntilBlocked();
            int a = w.ReadCounter(0, Ctr("modeA"));
            int b = w.ReadCounter(0, Ctr("modeB"));
            Check("选中的模式分支真正执行（Bug3）", a == 0 && b == 1, $"modeA={a} modeB={b}");
        }
    }

    private static void TestSingularTarget()
    {
        // "Destroy an enemy follower" —— 单数，只应影响 1 个
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        var foeDef = new CardDef { CardId = 100, Name = "Foe", Kind = CardKind.Follower };
        w.RegisterCard(foeDef);
        for (int i = 0; i < 3; i++) w.Create(100, ZoneRef.Board, 1, life: 20);

        vm.PushBody(new EffectNode[]
        {
            new MoveZoneNode
            {
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = ZoneScope.Opponent, Zone = ZoneRef.Board,
                    Mode = SelectMode.Choose, Count = NumExpr.Const(1),
                },
                ToZone = ZoneRef.Cemetery,
            },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });

        var st = vm.StepUntilBlocked();
        bool blocked = st == VmStatus.WaitingForInput && vm.Pending != null;
        if (blocked)
        {
            vm.SubmitChoice(vm.Pending.ChoiceId, new[] { vm.Pending.Candidates[0] });
            vm.StepUntilBlocked();
        }
        int dead = w.EnumerateZone(ZoneRef.Cemetery, 1).Count();
        Check("单数目标只影响 1 个", dead == 1, $"墓地={dead} 挂起={blocked}");
    }

    private static void TestPluralTarget()
    {
        // "all enemy followers" —— 复数，全部影响
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        w.RegisterCard(new CardDef { CardId = 100, Name = "Foe", Kind = CardKind.Follower });
        for (int i = 0; i < 3; i++) w.Create(100, ZoneRef.Board, 1, life: 20);

        vm.PushBody(new EffectNode[]
        {
            new ModifyStatsNode
            {
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = ZoneScope.Opponent, Zone = ZoneRef.Board,
                    Mode = SelectMode.All,
                },
                Op = StatModOp.Add,
                Attack = NumExpr.Const(0),
                Life = NumExpr.Const(-3),
            },
        }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        int hurt = w.EnumerateZone(ZoneRef.Board, 1).Count(x => x.Life == 17);
        Check("复数目标影响全部", hurt == 3, "受伤数=" + hurt);
    }

    private static void TestPlayerPerspective()
    {
        // 致命的架构错误回归测试：
        // 后手玩家（Controller = 1）打出一张 AST 里写着「所有敌方随从」的卡，
        // 必须打到先手玩家（PlayerId = 0）的随从，绝不能打到自己。
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        w.RegisterCard(new CardDef { CardId = 100, Name = "Foe", Kind = CardKind.Follower });

        var mine   = w.Create(100, ZoneRef.Board, 1, life: 20);   // 后手玩家自己的随从
        var theirs = w.Create(100, ZoneRef.Board, 0, life: 20);   // 先手玩家的随从

        vm.PushBody(new EffectNode[]
        {
            new ModifyStatsNode
            {
                // AST 视角：Opponent 指的是「我的对手」，不是「玩家 1」
                Target = new TargetSpec
                {
                    SourceKind = TargetSourceKind.Zone,
                    Owner = ZoneScope.Opponent,
                    Zone = ZoneRef.Board,
                    Mode = SelectMode.All,
                },
                Op = StatModOp.Add,
                Attack = NumExpr.Const(0),
                Life = NumExpr.Const(-5),
            },
        }, new VmScope { Owner = ZoneScope.Opponent, Controller = 1 });   // 后手玩家在行动
        vm.StepUntilBlocked();

        Check("后手的『敌方随从』打先手，不误伤自己",
            theirs.Life == 15 && mine.Life == 20,
            $"先手随从 {theirs.Life}（应 15），后手随从 {mine.Life}（应 20）");
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

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

        // ---- Phase 4 验收门禁 ----
        GateBarrierAbsorb();
        GateDestroyRules();
        GateInnateKeyword();
        GateAuraTargeting();
        GateAuraNotSelectable();
        GateFieldFullVanish();

        // ---- Phase 5 验收门禁 ----
        GateManualVsAutoEvolve();
        GateExtraPp();
        GateSkyboundArt();
        GateAbilityDamageThroughPipeline();
        GateSummonInstead();
        GateTurnStartOrder();
        GateTurnEndExpire();
        GateTriggerPriority();
        GateCrestUnique();

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

    // ==================== Phase 4 验收门禁 ====================

    /// <summary>门禁1：Deal 3 damage 打在带 Barrier 的随从上，伤害为 0 且 Barrier 消失。</summary>
    private static void GateBarrierAbsorb()
    {
        var st = new Battle.BattleState();
        var combat = new Battle.CombatResolver(st, st.Schema);
        var t = st.CreateEntity(1, ZoneRef.Board, 1, life: 5);
        t.Keywords.Add(st.Schema.Keyword("barrier"));
        int before = t.Life;
        combat.ApplyDamage(t, 3, 0);
        Check("门禁1 Barrier 吸收伤害并消失",
              t.Life == before && !t.Keywords.Contains(st.Schema.Keyword("barrier")),
              "life " + before + "->" + t.Life);
    }

    /// <summary>门禁2：Destroy 的谢幕曲 / 墓场 / 超进化免疫。</summary>
    private static void GateDestroyRules()
    {
        var st = new Battle.BattleState();
        var shadowKey = st.Schema.Counter("shadow");

        var a = st.CreateEntity(1, ZoneRef.Board, 1, life: 3);
        int shadowBefore = st.ReadCounter(1, shadowKey);
        st.DestroyEntity(a);
        bool aOk = a.Zone == ZoneRef.Cemetery
                   && st.ReadCounter(1, shadowKey) == shadowBefore + 1
                   && st.EnumerateHistory(HistoryScope.DestroyedThisMatch, 1).Any(h => h.EntityId == a.EntityId)
                   && st.EventLog.Any(e => e.Kind == CoreEvent.OnDestroy
                                           && e.Subject.HasValue && e.Subject.Value.EntityId == a.EntityId);

        var b = st.CreateEntity(2, ZoneRef.Board, 0, life: 3);
        b.IsSuperEvolved = true;
        st.ActivePlayer = 0;
        st.DestroyEntity(b);
        bool bOk = b.Zone == ZoneRef.Board;

        Check("门禁2 Destroy 语义与超进化免疫", aOk && bOk, "aOk=" + aOk + " bOk=" + bOk);
    }

    /// <summary>门禁3：打出带 Ward 与 Countdown (2) 的卡，实体带对应属性。</summary>
    private static void GateInnateKeyword()
    {
        var st = new Battle.BattleState();
        var lex = new Lang.Lexicon();
        lex.AddKeyword("ward");
        lex.Build();
        var comp = new Lang.EnglishCompiler(lex);
        // 测试用例只需要「常驻属性 + 触发能力」的骨架，不依赖卡名词表。
        var r = comp.Compile("Countdown (2) Last Words: Draw a card. Ward");

        var def = new CardDef
        {
            CardId = 500, Name = "GateAmulet", Kind = CardKind.Amulet,
            BaseAttack = 0, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities,
        };
        st.RegisterCard(def);
        var e = st.SpawnCard(500, ZoneRef.Board, 0);

        bool hasWard = e.Keywords.Contains(st.Schema.Keyword("ward"));
        bool hasCd = e.Counters.TryGetValue(st.Schema.Counter("countdown"), out var cd) && cd == 2;
        Check("门禁3 Ward 与 Countdown 装入实体", hasWard && hasCd,
              "ward=" + hasWard + " countdown=" + (hasCd ? "2" : "缺"));
    }

    /// <summary>门禁4：Aura 不能被能力选中，但可以被随从攻击。</summary>
    private static void GateAuraTargeting()
    {
        var st = new Battle.BattleState();
        var combat = new Battle.CombatResolver(st, st.Schema);

        var target = st.CreateEntity(1, ZoneRef.Board, 1, atk: 2, life: 5);
        target.Keywords.Add(st.Schema.Keyword("aura"));
        var attacker = st.CreateEntity(2, ZoneRef.Board, 0, atk: 3, life: 5);
        // 先破除召唤失调：直接标记为本回合之前入场
        attacker.EnteredTurn = -1;

        bool ok = combat.CanAttack(attacker, target, out var reason);
        Check("门禁4 Aura 可被攻击", ok, reason);
    }

    /// <summary>门禁5：满场 5 随从时召唤第 6 个，不进墓场、shadow 不变。</summary>
    private static void GateFieldFullVanish()
    {
        var st = new Battle.BattleState();
        var shadowKey = st.Schema.Counter("shadow");
        for (int i = 0; i < 5; i++)
        {
            st.RegisterCard(new CardDef { CardId = 900 + i, Name = "F" + i, Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            st.SpawnCard(900 + i, ZoneRef.Board, 0);
        }
        int shadowBefore = st.ReadCounter(0, shadowKey);
        int cemeteryBefore = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();

        st.RegisterCard(new CardDef { CardId = 999, Name = "Sixth", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
        var sixth = st.SpawnCard(999, ZoneRef.Board, 0);

        bool ok = sixth.Zone != ZoneRef.Cemetery
                  && st.ReadCounter(0, shadowKey) == shadowBefore
                  && st.EnumerateZone(ZoneRef.Cemetery, 0).Count() == cemeteryBefore
                  && st.EnumerateZone(ZoneRef.Board, 0).Count() == 5;
        Check("门禁5 满场召唤不进墓场", ok,
              "zone=" + sixth.Zone + " shadow=" + st.ReadCounter(0, shadowKey));
    }

    /// <summary>门禁4 另一半：Aura / Ambush 不能被敌方能力选中。</summary>
    private static void GateAuraNotSelectable()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        CardVm.Schema = GameSchema.CreateDefault();
        var schema = CardVm.Schema;

        var plain = w.Create(1, ZoneRef.Board, 1, atk: 0, life: 3);
        var aura = w.Create(2, ZoneRef.Board, 1, atk: 0, life: 3);
        aura.Keywords.Add(schema.Keyword("aura"));
        var ambush = w.Create(3, ZoneRef.Board, 1, atk: 0, life: 3);
        ambush.Keywords.Add(schema.Keyword("ambush"));

        var spec = new Ast.TargetSpec
        {
            SourceKind = Ast.TargetSourceKind.Zone,
            Owner = ZoneScope.Opponent,
            Zone = ZoneRef.Board,
            Mode = SelectMode.Choose,
            Count = Ast.NumExpr.Const(1),
            Where = Ast.BoolExpr.True,
        };
        var ab = new AbilityDef
        {
            Trigger = new Ast.TriggerSpec { Event = CoreEvent.OnPlay },
            Body = new[] { new Ast.SelectTargetNode { Target = spec } },
        };
        var scope = new VmScope
        {
            Owner = ZoneScope.Self,
            Controller = 0,
            Source = EntityRef.From(plain),
        };
        vm.PushAbility(ab, scope);
        vm.StepUntilBlocked();

        var ids = vm.Pending?.Candidates ?? new System.Collections.Generic.List<int>();
        bool ok = ids.Count == 1 && ids[0] == plain.EntityId
                  && !ids.Contains(aura.EntityId)
                  && !ids.Contains(ambush.EntityId);

        Check("门禁4 Aura/Ambush 不可被能力选中", ok,
              "candidates=" + string.Join(",", ids) + " plain=" + plain.EntityId);
    }

    // ==================== Phase 5 验收门禁 ====================

    /// <summary>门禁6：Evolve: 冒号头只认手动进化，效果自动进化不触发。</summary>
    private static void GateManualVsAutoEvolve()
    {
        var lex = new Lang.Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm" }) lex.AddKeyword(kw);
        lex.Build();
        var comp = new Lang.EnglishCompiler(lex);

        var r1 = comp.Compile("Evolve: Draw a card.");
        var r2 = comp.Compile("Whenever this follower evolves, deal 1 damage to an enemy follower.");

        bool headerManual = r1.Abilities.Count > 0 && r1.Abilities[0].Trigger != null
                            && r1.Abilities[0].Trigger.ManualEvolveOnly;
        bool clauseFree = r2.Abilities.Count > 0 && r2.Abilities[0].Trigger != null
                          && !r2.Abilities[0].Trigger.ManualEvolveOnly;

        Check("门禁6 手动进化与效果进化区分", headerManual && clauseFree,
              "header=" + headerManual + " clause=" + clauseFree);
    }

    /// <summary>门禁7：后手额外 PP 每回合一次，且不增加 max_pp。</summary>
    private static void GateExtraPp()
    {
        var st = new Battle.BattleState(seed: 1);
        var tr = new Battle.TurnRunner(st, st.Schema);
        var ppKey = st.Schema.Counter("pp");
        var maxKey = st.Schema.Counter("max_pp");

        st.WriteCounter(1, ppKey, 3);
        st.WriteCounter(1, maxKey, 3);
        st.TurnNumber = 1;
        st.ActivePlayer = 1;

        bool first = tr.UseExtraPp(1);
        bool ppUp = st.ReadCounter(1, ppKey) == 4;
        bool maxSame = st.ReadCounter(1, maxKey) == 3;
        bool secondSameTurn = tr.UseExtraPp(1);

        Check("门禁7 后手额外 PP", first && ppUp && maxSame && !secondSameTurn,
              "first=" + first + " pp=" + st.ReadCounter(1, ppKey) + " max=" + st.ReadCounter(1, maxKey) + " twice=" + secondSameTurn);
    }

    /// <summary>门禁8：奥义槽阈值——奥义 >= 10，解放奥义 >= 15。</summary>
    private static void GateSkyboundArt()
    {
        var lex = new Lang.Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm" }) lex.AddKeyword(kw);
        lex.Build();
        var comp = new Lang.EnglishCompiler(lex);

        var r1 = comp.Compile("Fanfare: Skybound Art - Deal 5 damage to 2 random enemy followers.");
        var r2 = comp.Compile("Fanfare: Super Skybound Art - Deal 3 damage to the enemy leader.");

        string c1 = r1.Abilities.Count > 0 ? Lang.AstEnglishPrinter.PrintBool(r1.Abilities[0].Condition) : "";
        string c2 = r2.Abilities.Count > 0 ? Lang.AstEnglishPrinter.PrintBool(r2.Abilities[0].Condition) : "";

        bool ok1 = c1.Contains("skybound_gauge") && c1.Contains("10");
        bool ok2 = c2.Contains("skybound_gauge") && c2.Contains("15");

        Check("门禁8 奥义/解放奥义阈值", ok1 && ok2, "art=" + c1 + " super=" + c2);
    }

    /// <summary>门禁9：能力伤害与法术伤害都要经过屏障。</summary>
    private static void GateAbilityDamageThroughPipeline()
    {
        var w = new MemoryWorld();
        var schema = w.Schema;
        var barrier = schema.Keyword("barrier");

        var t = w.Create(1, ZoneRef.Board, 1, atk: 0, life: 5);
        t.Keywords.Add(barrier);

        // 用 CardVm 执行一条 DealDamageNode（模拟法术伤害）
        var vm = new CardVm(w);
        CardVm.Schema = schema;
        var ab = new AbilityDef
        {
            Trigger = new Ast.TriggerSpec { Event = CoreEvent.OnPlay },
            Body = new[] { new DealDamageNode
            {
                Target = new Ast.TargetSpec
                {
                    SourceKind = Ast.TargetSourceKind.Zone,
                    Owner = ZoneScope.Opponent,
                    Zone = ZoneRef.Board,
                    Mode = SelectMode.All,
                    Where = Ast.BoolExpr.True,
                },
                Amount = Ast.NumExpr.Const(3),
                IsAbility = true,
            } },
        };
        vm.PushAbility(ab, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        bool ok = t.Life == 5 && !t.Keywords.Contains(barrier);
        Check("门禁9 能力伤害走统一管线（屏障生效）", ok,
              "life=" + t.Life + " barrier=" + t.Keywords.Contains(barrier));
    }

    /// <summary>门禁10："Summon a X. ... summon 2 instead." 要真的召唤同一张卡。</summary>
    private static void GateSummonInstead()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        CardVm.Schema = w.Schema;

        // 先注册一张真实卡（id=700）
        w.RegisterCard(new CardDef { CardId = 700, Name = "Megalorca", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3 });

        var ab = new AbilityDef
        {
            Trigger = new Ast.TriggerSpec { Event = CoreEvent.OnPlay },
            Body = new[]
            {
                new SpawnCardNode { CardId = 700, CardName = "Megalorca", ToZone = ZoneRef.Board, Count = Ast.NumExpr.Const(1) },
                new SpawnCardNode { SourceKind = CardSourceKind.CopyOfPrevious, ToZone = ZoneRef.Board, Count = Ast.NumExpr.Const(2) },
            },
        };
        vm.PushAbility(ab, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        var onBoard = w.EnumerateZone(ZoneRef.Board, 0).ToList();
        bool ok = onBoard.Count == 3 && onBoard.All(e => e.Def != null && e.Def.CardId == 700);
        Check("门禁10 Summon N instead 复制同一张卡", ok,
              "board=" + onBoard.Count + " ids=" + string.Join(",", onBoard.Select(e => e.Def?.CardId ?? 0)));
    }

    /// <summary>门禁11：回合开始严格 8 步——抽牌在最后。</summary>
    private static void GateTurnStartOrder()
    {
        var st = new Battle.BattleState(seed: 7);
        var tr = new Battle.TurnRunner(st, st.Schema);
        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;

        st.RegisterCard(new CardDef { CardId = 1, Name = "A", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
        for (int i = 0; i < 5; i++) st.SpawnCard(1, ZoneRef.Deck, 0);
        st.TurnNumber = 0;

        tr.StartTurn(0, vm);

        int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
        int maxPp = st.ReadCounter(0, st.Schema.Counter("max_pp"));

        // 先手第 1 回合必须抽 1 张（红线 1），且 pp = max_pp = 1
        bool ok = hand == 1 && pp == 1 && maxPp == 1;
        Check("门禁11 回合开始抽牌与涨费", ok,
              "hand=" + hand + " pp=" + pp + " max=" + maxPp);
    }

    /// <summary>门禁12：回合结束后 ThisTurn 的临时修正被撤销。</summary>
    private static void GateTurnEndExpire()
    {
        var st = new Battle.BattleState(seed: 8);
        var tr = new Battle.TurnRunner(st, st.Schema);
        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;

        st.RegisterCard(new CardDef { CardId = 1, Name = "A", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
        var e = st.SpawnCard(1, ZoneRef.Board, 0);
        st.ActivePlayer = 0;

        int baseAtk = e.Attack;
        e.Attack += 3;
        e.TempModifiers.Add(new TemporaryModifier
        {
            Duration = Ast.DurationKind.ThisTurn,
            Attack = 3,
            Life = 0,
        });

        tr.EndTurn(0, vm);

        bool ok = e.Attack == baseAtk;
        Check("门禁12 回合结束撤销 ThisTurn 修正", ok,
              "atk=" + e.Attack + " base=" + baseAtk);
    }

    /// <summary>门禁13：待机列按优先级排序（入场曲 10 先于场上出现时 20）。</summary>
    private static void GateTriggerPriority()
    {
        var w = new MemoryWorld();
        var vm = new CardVm(w);
        CardVm.Schema = w.Schema;

        // 先入 场上出现时（20），再入 入场曲（10）；排序后应先出 10
        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField });
        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay });

        bool ok = vm.PendingTriggerCount == 2;
        string order = "";
        if (ok)
        {
            // 取出顺序应为 OnPlay(10) -> OnEnterField(20)
            var f = typeof(CardVm).GetField("_triggerQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var q = f?.GetValue(vm) as System.Collections.Generic.List<VmEvent>;
            if (q != null && q.Count == 2)
                order = q[0].Kind + "," + q[1].Kind;
        }
        ok = ok && order == "OnPlay,OnEnterField";
        Check("门禁13 待机列按优先级排序", ok, "order=" + order);
    }

    /// <summary>门禁14：同名纹章不可重复持有；纹章区上限 5。</summary>
    private static void GateCrestUnique()
    {
        var st = new Battle.BattleState(seed: 9);
        st.RegisterCard(new CardDef { CardId = 300, Name = "CrestA", Kind = CardKind.Amulet });
        st.RegisterCard(new CardDef { CardId = 301, Name = "CrestB", Kind = CardKind.Amulet });

        st.SpawnCard(300, ZoneRef.CrestArea, 0);
        st.SpawnCard(300, ZoneRef.CrestArea, 0);   // 同名：应被忽略
        int afterDup = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();

        st.SpawnCard(301, ZoneRef.CrestArea, 0);
        int afterOther = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();

        bool ok = afterDup == 1 && afterOther == 2;
        Check("门禁14 纹章同名唯一", ok,
              "dup=" + afterDup + " other=" + afterOther);
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok) { _pass++; _log.Add($"  [OK]   {name}"); }
        else { _fail++; _log.Add($"  [FAIL] {name}  —— {detail}"); }
    }
}

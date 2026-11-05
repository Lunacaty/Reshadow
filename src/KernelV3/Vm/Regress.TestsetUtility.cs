using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 测试集「效用级」实测
//
// 与 AST 打印串测试的区别：这里每张卡都真的打进 BattleState + TurnRunner +
// CardVm，断言它对世界状态造成的**真实改变**（区域、攻防、计数器、手牌数）。
//
// 为什么必须这样测：AST 好看不代表跑得通。
// 「Select another allied card」在 AST 里是 All 还是 Choose，
// 打印出来都像模像样，但一个是「给全场加守护」，另一个是「让玩家选一张」——
// 只有实际打出去看谁获得了关键词才分得出来。
// ============================================================================
public static partial class Regress
{
    public static string RunTestsetUtility()
    {
        int pass = 0, total = 0;
        var log = new StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        // ── 1. 抽牌类（Indomitable Fighter / Shoddy Plaything / Adventurers' Guild）──
        {
            var (ok, d) = UtilDrawCards();
            Chk("效用-1 入场曲抽牌真的进手", ok, d);
        }

        // ── 2. 单体伤害与目标选择（Bug Alert / Runeblade Conductor）──
        {
            var (ok, d) = UtilSelectiveDamage();
            Chk("效用-2 手选目标只打中选中的那个", ok, d);
        }

        // ── 3. another 排除自身（Godwood Staff）──
        {
            var (ok, d) = UtilAnotherExcludesSelf();
            Chk("效用-3 another 是单选且排除自身", ok, d);
        }

        // ── 4. 激奏与结晶（Shoddy Plaything / Prostrating Coward）──
        {
            var (ok, d) = UtilAltModes();
            Chk("效用-4 激奏按法术结算、结晶按护符进场", ok, d);
        }

        // ── 5. 吟唱到期与谢幕曲（Octrice / Prostrating Coward）──
        {
            var (ok, d) = UtilCountdownExpire();
            Chk("效用-5 吟唱归零当场到期并触发谢幕曲", ok, d);
        }

        // ── 6. 幽灵离场替代（Ghost）──
        {
            var (ok, d) = UtilGhostBanish();
            Chk("效用-6 幽灵战死进放逐区而非墓地", ok, d);
        }

        // ── 7. 融合材料校验（Sinciro / Striker Artifact）──
        {
            var (ok, d) = UtilFuseValidation();
            Chk("效用-7 融合材料必须满足声明条件", ok, d);
        }

        // ── 8. 启动护符校验（Godwood Staff / Detective's Lens）──
        {
            var (ok, d) = UtilEngageValidation();
            Chk("效用-8 白板护符不能启动、有 Engage 才能", ok, d);
        }

        // ── 9. 纹章监听（Aria）──
        {
            var (ok, d) = UtilCrestListener();
            Chk("效用-9 纹章监听 Pixie 进场并给疾驰", ok, d);
        }

        // ── 10. 信仰随选模式增长（Sham-Nacha）──
        {
            var (ok, d) = UtilFaithGrowth();
            Chk("效用-10 选模式让信仰 +1、资源不足不白嫖", ok, d);
        }

        // ── 11. 法术挂起时不得提前进墓地（Glittering Gold）──
        {
            var (ok, d) = UtilSpellWaitsForChoice();
            Chk("效用-11 法术挂起时仍在手牌，选完才进墓地", ok, d);
        }

        // ── 12. 回合末清理排在能力之后（Godwood Staff combo 门槛）──
        {
            var (ok, d) = UtilTurnEndOrder();
            Chk("效用-12 回合末清 Buff 排在回合末能力之后", ok, d);
        }

        log.AppendLine();
        log.AppendLine("=== 测试集效用 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    // ---------------------------------------------------------------- 夹具

    /// <summary>
    /// 建一个「编译期与运行时共用符号表」的夹具。
    ///
    /// 不能各自 new Lexicon / new BattleState —— 那样同一个兵种名会在两个
    /// GameSchema 实例里拿到不同的 Id，`HasTraitExpr` 在运行时永远匹配不上，
    /// 而编译零错误、AST 打印正常，属于最难发现的那类静默失效。
    /// </summary>
    private static CompileContext UtilCtx(int seed) => CompileContext.Create(seed)
        .Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
        .Traits("Pixie", "Artifact", "Loot", "Clue", "Golem", "Officer")
        // 卡名 → 卡号必须与 BattleState.BuiltinTokenDefs 一致。
        // 「Ghost」曾被错绑到 90001130（那是 Knight）—— 效果想加 Ghost，
        // 手里却出现一张白板骑士；81000020 才是测试集里的真 Ghost。
        // 同理 Striker / Gear / Shoddy 都是测试集真卡，不能另起 9000 号段。
        .Cards(("Fairy", 90001110), ("Bat", 90001120), ("Testset Ghost", 81000020),
               ("Kindled Blade", 90005150), ("Ominous Artifact \u03b1", 90005110),
               ("Ominous Artifact \u03b2", 90005120), ("Ominous Artifact \u03b3", 90005130),
               ("Testset Striker Artifact", 81000022), ("Remnant of Hollowness", 90005140),
               ("Testset Gear of Ambition", 81000038), ("Testset Shoddy Plaything", 81000005))
        .Build();

    private static (BattleState st, TurnRunner tr, CardVm vm) UtilState(CompileContext ctx)
    {
        var st = ctx.State;
        st.RegisterBuiltinTokens();
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0);
        l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1);
        l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 8;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(1, st.Schema.Counter("pp"), 20);
        var vm = new CardVm(st);
        vm.CurrentTurnOwner = 0;
        return (st, new TurnRunner(st, st.Schema), vm);
    }


    // ---------------------------------------------------------------- 用例

    private static (bool, string) UtilDrawCards()
    {
        var ctx = UtilCtx(1);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Fanfare: Draw 3 cards.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2001, Name = "Drawer", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        for (int i = 0; i < 6; i++)
        {
            st.RegisterCard(new CardDef { CardId = 2100 + i, Name = "Filler" + i, Kind = CardKind.Follower });
            st.SpawnCard(2100 + i, ZoneRef.Deck, 0);
        }
        var c = st.SpawnCard(2001, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int before = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm);
        vm.StepUntilBlocked();
        int after = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 打出 -1，抽 +3 → 净 +2
        return (after == before + 2, "手牌 " + before + "->" + after + "（期望 +2）");
    }

    private static (bool, string) UtilSelectiveDamage()
    {
        var ctx = UtilCtx(2);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Fanfare: Select an enemy follower on the field and deal it 2 damage.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2002, Name = "Shooter", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        st.RegisterCard(new CardDef { CardId = 2003, Name = "FoeA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 2004, Name = "FoeB", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        var a = st.SpawnCard(2003, ZoneRef.Board, 1);
        var b = st.SpawnCard(2004, ZoneRef.Board, 1);
        var c = st.SpawnCard(2002, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();

        tr.PlayCard(0, c, vm);
        bool blocked = vm.Pending != null;
        if (blocked) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { b.EntityId });
        vm.StepUntilBlocked();

        // 选中的 B 掉 2 血，没选的 A 满血
        bool ok = blocked && b.Life == 3 && a.Life == 5;
        return (ok, "挂起=" + blocked + " A=" + a.Life + "(期望5) B=" + b.Life + "(期望3)");
    }

    private static (bool, string) UtilAnotherExcludesSelf()
    {
        var ctx = UtilCtx(3);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Engage: Destroy this card. Select another allied card on the field and give it Ward.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2005, Name = "Staff", Kind = CardKind.Amulet,
            BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        st.RegisterCard(new CardDef { CardId = 2006, Name = "AllyX", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 2007, Name = "AllyY", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        var staff = st.SpawnCard(2005, ZoneRef.Board, 0);
        var x = st.SpawnCard(2006, ZoneRef.Board, 0);
        var y = st.SpawnCard(2007, ZoneRef.Board, 0);
        vm.DropPendingTriggers();

        tr.EngageAmulet(0, staff, vm);
        bool blocked = vm.Pending != null;
        var ward = st.Schema.Keyword("ward");
        if (blocked) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { x.EntityId });
        vm.StepUntilBlocked();

        int warded = (x.Keywords.Contains(ward) ? 1 : 0) + (y.Keywords.Contains(ward) ? 1 : 0);
        bool ok = blocked && warded == 1 && x.Keywords.Contains(ward);
        return (ok, "挂起=" + blocked + " 获得守护者=" + warded + "（期望恰好 1 个，且是选中的 X）");
    }

    private static (bool, string) UtilAltModes()
    {
        var ctx = UtilCtx(4);
        var comp = new EnglishCompiler(ctx.Lexicon);

        // 激奏：2 费按法术结算，召唤本体但不触发 Fanfare
        var accelAb = comp.Compile("Fanfare: Draw 3 cards.", null, "Shoddy");
        var alt = new (string, int, string)[] { ("Accelerate", 2, "Summon a Testset Shoddy Plaything.") };
        var accel = comp.Compile("Fanfare: Draw 3 cards.", alt, "Shoddy");

        var (st, tr, vm) = UtilState(ctx);
        st.WriteCounter(0, st.Schema.Counter("pp"), 2);
        // Shoddy Plaything 是测试集里的真卡（81000005），卡号必须统一 ——
        // 另起 9000 号段会让激奏召唤出来的那张「同名卡」其实是另一个实体，
        // 看着召唤成功，实际拿到的是一张没有 Fanfare/Ward 的白板。
        const int ShoddyId = 81000005;
        st.RegisterCard(new CardDef
        {
            CardId = ShoddyId, Name = "Shoddy Plaything", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 3, BaseCost = 6,
            Abilities = accel.Abilities, AccelerateDef = accel.AccelerateDef,
        });
        var c = st.SpawnCard(ShoddyId, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool played = tr.PlayCard(0, c, vm);
        vm.StepUntilBlocked();

        bool spellPath = played && c.Zone == ZoneRef.Cemetery && c.Def?.BaseCost == 6;
        bool noFanfare = st.EnumerateZone(ZoneRef.Hand, 0).Count() <= handBefore;
        bool summoned = st.EnumerateZone(ZoneRef.Board, 0)
            .Any(e => !ReferenceEquals(e, c) && e.Def?.CardId == ShoddyId);

        // 结晶：2 费按护符进场，带吟唱
        var cryAlt = new (string, int, string)[] { ("Crystallize", 2, "Countdown (3)") };
        var cry = comp.Compile("Fanfare: Restore 2 defense to your leader.", cryAlt, "Coward");
        var (st2, tr2, vm2) = UtilState(ctx);
        st2.WriteCounter(0, st2.Schema.Counter("pp"), 2);
        st2.RegisterCard(new CardDef
        {
            CardId = 2009, Name = "Coward", Kind = CardKind.Follower,
            BaseAttack = 4, BaseLife = 5, BaseCost = 5,
            Abilities = cry.Abilities, CrystallizeDef = cry.CrystallizeDef,
        });
        var c2 = st2.SpawnCard(2009, ZoneRef.Hand, 0);
        vm2.DropPendingTriggers();
        bool played2 = tr2.PlayCard(0, c2, vm2);
        vm2.StepUntilBlocked();
        int cd = c2.Counters.TryGetValue(st2.Schema.Counter("countdown"), out var cv) ? cv : -1;
        bool amuletPath = played2 && c2.Zone == ZoneRef.Board && c2.Def?.Kind == CardKind.Amulet && cd == 3;

        bool ok = spellPath && noFanfare && summoned && amuletPath;
        return (ok, "激奏=" + spellPath + "(墓场/本体费) 未触发Fanfare=" + noFanfare
                  + " 召唤本体=" + summoned + " 结晶=" + amuletPath + "(护符/吟唱" + cd + ")");
    }

    private static (bool, string) UtilCountdownExpire()
    {
        var ctx = UtilCtx(5);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Fanfare: Advance this amulet's count by 1.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2010, Name = "Ticker", Kind = CardKind.Amulet,
            BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        var c = st.SpawnCard(2010, ZoneRef.Hand, 0);
        c.Counters[st.Schema.Counter("countdown")] = 1;
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm);
        vm.StepUntilBlocked();
        // 吟唱 1 → 推进 1 → 0，当场到期进墓地
        bool ok = c.Zone == ZoneRef.Cemetery;
        return (ok, "吟唱归零后区域=" + c.Zone + "（期望 Cemetery）");
    }

    private static (bool, string) UtilGhostBanish()
    {
        var ctx = UtilCtx(6);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Storm\n<hr>When this card leaves the field, banish it.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2011, Name = "Ghost", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        var g = st.SpawnCard(2011, ZoneRef.Board, 0);
        vm.DropPendingTriggers();
        int shadowBefore = st.ReadCounter(0, st.Schema.Counter("shadow"));

        // 战死 = SBA 路径（生命归 0），不是能力破坏
        g.Life = 0;
        st.ProcessStateBasedActions();

        int shadowAfter = st.ReadCounter(0, st.Schema.Counter("shadow"));
        bool inBanished = st.EnumerateZone(ZoneRef.Banished, 0).Any(e => e.EntityId == g.EntityId);
        bool notInCem = !st.EnumerateZone(ZoneRef.Cemetery, 0).Any(e => e.EntityId == g.EntityId);
        bool noShadow = shadowAfter == shadowBefore;
        bool ok = inBanished && notInCem && noShadow;
        return (ok, "放逐=" + inBanished + " 不在墓地=" + notInCem
                  + " shadow " + shadowBefore + "->" + shadowAfter);
    }

    private static (bool, string) UtilFuseValidation()
    {
        var ctx = UtilCtx(7);
        var comp = new EnglishCompiler(ctx.Lexicon);

        var (st, tr, vm) = UtilState(ctx);
        var fuseAb = comp.Compile("When you Fuse to this card, draw a card.");

        // 主体：声明了 Fuse: Loot cards
        st.RegisterCard(new CardDef
        {
            CardId = 2020, Name = "LootBase", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 3,
            Abilities = fuseAb.Abilities, HasFuse = true,
            FuseMaterialFilter = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Self,
                Zone = ZoneRef.Hand, Mode = SelectMode.All,
                Where = new HasTraitExpr { Trait = st.Schema.Trait("Loot"), Of = TargetSpec.Item },
            },
        });
        st.RegisterCard(new CardDef { CardId = 2021, Name = "LootCard", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Traits = new[] { st.Schema.Trait("Loot") } });
        st.RegisterCard(new CardDef { CardId = 2022, Name = "PlainCard", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });

        var baseCard = st.SpawnCard(2020, ZoneRef.Hand, 0);
        var loot = st.SpawnCard(2021, ZoneRef.Hand, 0);
        var plain = st.SpawnCard(2022, ZoneRef.Hand, 0);

        bool lootOk = tr.CanFuseCards(0, baseCard, new[] { loot }, out _);
        bool plainRejected = !tr.CanFuseCards(0, baseCard, new[] { plain }, out _);
        bool selfRejected = !tr.CanFuseCards(0, baseCard, new[] { baseCard }, out _);

        // 没有 Fuse 声明的卡不能当主体
        st.RegisterCard(new CardDef { CardId = 2023, Name = "NoFuse", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        var noFuse = st.SpawnCard(2023, ZoneRef.Hand, 0);
        bool noFuseRejected = !tr.CanFuseCards(0, noFuse, new[] { loot }, out _);

        bool ok = lootOk && plainRejected && selfRejected && noFuseRejected;
        return (ok, "Loot可融合=" + lootOk + " 非Loot拒绝=" + plainRejected
                  + " 自身拒绝=" + selfRejected + " 无Fuse声明拒绝=" + noFuseRejected);
    }

    private static (bool, string) UtilEngageValidation()
    {
        var ctx = UtilCtx(8);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Engage: Draw a card.");
        var (st, tr, vm) = UtilState(ctx);

        st.RegisterCard(new CardDef
        {
            CardId = 2030, Name = "WithEngage", Kind = CardKind.Amulet,
            BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        st.RegisterCard(new CardDef { CardId = 2031, Name = "Blank", Kind = CardKind.Amulet, BaseCost = 1 });

        var withEngage = st.SpawnCard(2030, ZoneRef.Board, 0);
        var blank = st.SpawnCard(2031, ZoneRef.Board, 0);

        bool blankRejected = !tr.CanEngageAmulet(0, blank, out _);

        // 沉默后不能再启动
        withEngage.IsSilenced = true;
        bool silencedRejected = !tr.CanEngageAmulet(0, withEngage, out _);
        withEngage.IsSilenced = false;

        bool engageOk = tr.CanEngageAmulet(0, withEngage, out _);

        bool ok = blankRejected && silencedRejected && engageOk;
        return (ok, "白板拒绝=" + blankRejected + " 沉默拒绝=" + silencedRejected
                  + " 正常可启动=" + engageOk);
    }

    private static (bool, string) UtilCrestListener()
    {
        var ctx = UtilCtx(9);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var alt = new (string, int, string)[]
        {
            ("Crest", 0, "Whenever an allied Pixie follower enters the field, give it Storm."),
        };
        var r = comp.Compile("Fanfare: Gain Crest: Aria.", alt, "Aria");
        var (st, tr, vm) = UtilState(ctx);

        st.RegisterCard(new CardDef
        {
            CardId = 2040, Name = "Aria", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 3,
            Abilities = r.Abilities, CrestDef = r.CrestDef,
            InnateKeywords = r.InnateKeywords,
        });
        var aria = st.SpawnCard(2040, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, aria, vm);
        vm.StepUntilBlocked();

        bool hasCrest = st.EnumerateZone(ZoneRef.CrestArea, 0).Any(e => e.Def?.Kind == CardKind.Crest);

        // 召唤 Pixie 应被纹章捕捉并获得 storm
        var fairy = st.SpawnCard(90001110, ZoneRef.Board, 0);   // Fairy（Pixie 兵种）
        vm.StepUntilBlocked();
        bool gotStorm = fairy.Keywords.Contains(st.Schema.Keyword("storm"));

        // 非 Pixie 不应获得
        st.RegisterCard(new CardDef { CardId = 2041, Name = "Plain", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        var plain = st.SpawnCard(2041, ZoneRef.Board, 0);
        vm.StepUntilBlocked();
        bool plainNoStorm = !plain.Keywords.Contains(st.Schema.Keyword("storm"));

        bool ok = hasCrest && gotStorm && plainNoStorm;
        return (ok, "获得纹章=" + hasCrest + " Pixie得疾驰=" + gotStorm + " 非Pixie不得=" + plainNoStorm);
    }

    private static (bool, string) UtilFaithGrowth()
    {
        var ctx = UtilCtx(10);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var faithAlt = new (string, int, string)[]
        {
            ("Faith", 0, "This faith's value starts at 0.\n<hr>Whenever you select Modes, increase this faith's value by 1."),
        };
        var r = comp.Compile("Fanfare: Reduce your faith's value by 10 to give it \"Increase the number of Modes you can select by 1.\"", faithAlt, "Sham");
        var (st, tr, vm) = UtilState(ctx);

        st.RegisterCard(new CardDef
        {
            CardId = 2050, Name = "Sham", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1,
            Abilities = r.Abilities, FaithDef = r.FaithDef,
            InnateKeywords = r.InnateKeywords,
        });
        st.SpawnCard(2050, ZoneRef.Deck, 0);
        st.SetupFaith();

        var faith = st.FindFaith(0);
        bool hasFaith = faith != null;
        var fk = st.Schema.Counter("faith");

        // 0 信仰时打出：不该白嫖
        st.WriteCounter(0, fk, 0);
        var c1 = st.SpawnCard(2050, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int mc0 = st.ReadCounter(0, st.Schema.Counter("mode_count"));
        tr.PlayCard(0, c1, vm);
        vm.StepUntilBlocked();
        int mc1 = st.ReadCounter(0, st.Schema.Counter("mode_count"));

        // 15 信仰时打出：扣到 5，模式数 +1
        st.WriteCounter(0, fk, 15);
        var c2 = st.SpawnCard(2050, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c2, vm);
        vm.StepUntilBlocked();
        int mc2 = st.ReadCounter(0, st.Schema.Counter("mode_count"));
        int faithLeft = st.ReadCounter(0, fk);

        bool noFreeload = mc1 == mc0;
        bool paid = mc2 == mc1 + 1 && faithLeft == 5;
        bool ok = hasFaith && noFreeload && paid;
        return (ok, "信仰实体=" + hasFaith + " 0信仰不白嫖=" + noFreeload
                  + " 15信仰生效=" + paid + "(模式数" + mc1 + "->" + mc2 + ", 信仰余" + faithLeft + ")");
    }

    private static (bool, string) UtilSpellWaitsForChoice()
    {
        var ctx = UtilCtx(11);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Select a Mode to activate.\n 1. Draw a card.\n 2. Deal 2 damage to the enemy leader.\n 3. Restore 2 defense to your leader.");
        var (st, tr, vm) = UtilState(ctx);

        st.RegisterCard(new CardDef
        {
            CardId = 2060, Name = "Gold", Kind = CardKind.Spell,
            BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        for (int i = 0; i < 5; i++)
        {
            st.RegisterCard(new CardDef { CardId = 2600 + i, Name = "F" + i, Kind = CardKind.Follower });
            st.SpawnCard(2600 + i, ZoneRef.Deck, 0);
        }
        var c = st.SpawnCard(2060, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int shadowBefore = st.ReadCounter(0, st.Schema.Counter("shadow"));
        int cemBefore = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();

        tr.PlayCard(0, c, vm);
        bool blocked = vm.Pending != null;

        // 挂起这一刻：法术必须还在原处，墓地与 shadow 都不能动
        bool stillInHand = c.Zone == ZoneRef.Hand;
        bool noShadowYet = st.ReadCounter(0, st.Schema.Counter("shadow")) == shadowBefore;
        bool noCemYet = st.EnumerateZone(ZoneRef.Cemetery, 0).Count() == cemBefore;

        // 选模式 2（打敌方主战者 2 血）
        int foeBefore = st.Players[1].Leader.Life;
        if (blocked) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { 1 });
        vm.StepUntilBlocked();
        int foeAfter = st.Players[1].Leader.Life;
        bool modeResolved = foeAfter == foeBefore - 2;

        // 选完之后才进墓地、才 +shadow
        bool movedAfter = c.Zone == ZoneRef.Cemetery;
        bool shadowAfter = st.ReadCounter(0, st.Schema.Counter("shadow")) == shadowBefore + 1;

        bool ok = blocked && stillInHand && noShadowYet && noCemYet && modeResolved && movedAfter && shadowAfter;
        return (ok, "挂起=" + blocked + " 挂起时仍在手=" + stillInHand
                  + " 挂起时shadow未动=" + noShadowYet + " 挂起时墓地未动=" + noCemYet
                  + " 模式生效=" + modeResolved + " 选后进墓=" + movedAfter + " 选后shadow+1=" + shadowAfter);
    }

    private static (bool, string) UtilTurnEndOrder()
    {
        var ctx = UtilCtx(12);
        var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("At the end of your turn, Draw a card.");
        var (st, tr, vm) = UtilState(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 2070, Name = "Ender", Kind = CardKind.Amulet,
            BaseCost = 1, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        for (int i = 0; i < 5; i++)
        {
            st.RegisterCard(new CardDef { CardId = 2700 + i, Name = "G" + i, Kind = CardKind.Follower });
            st.SpawnCard(2700 + i, ZoneRef.Deck, 0);
        }
        var e = st.SpawnCard(2070, ZoneRef.Board, 0);
        // 挂一条限时加成，验证它在回合末能力之后才被撤
        e.Attack += 3;
        e.TempModifiers.Add(new TemporaryModifier { Duration = DurationKind.ThisTurn, Attack = 3, Life = 0 });

        vm.DropPendingTriggers();
        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.EndTurn(0, vm);
        vm.StepUntilBlocked();
        int handAfter = st.EnumerateZone(ZoneRef.Hand, 0).Count();

        bool drew = handAfter > handBefore;
        bool buffExpired = e.Attack == 0 || e.TempModifiers.Count == 0;
        bool ok = drew && buffExpired;
        return (ok, "回合末抽牌=" + drew + "(" + handBefore + "->" + handAfter + ")"
                  + " 限时加成已撤=" + buffExpired);
    }
}

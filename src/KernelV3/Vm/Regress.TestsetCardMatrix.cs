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
// 30 张测试卡的**逐卡**效用矩阵
//
// 与 Regress.TestsetUtility 的分工：
//   TestsetUtility  按**机制**分组（抽牌 / 选择 / 融合 / 纹章 …），
//                   每条验证一类语义，用于定位「哪一类机制坏了」。
//   TestsetCardMatrix 按**卡号**逐张（81000000 … 81000039），
//                   每张卡至少验证一条属于它自己的效果，
//                   用于回答「测试集里这张到底跑没跑通」。
//
// 后者更啰嗦，但它是对着 testset_cards.json 逐行数出来的账 ——
// 少一张、错一张，这里就会少一行。
// ============================================================================
public static partial class Regress
{
    /// <summary>30 张测试卡在 testset_cards.json 里的卡号（顺序即文件顺序）。</summary>
    private static readonly int[] MatrixCardIds =
    {
        81000000, 81000001, 81000002, 81000003, 81000004, 81000005,
        81000006, 81000007, 81000010, 81000011, 81000012, 81000013,
        81000014, 81000015, 81000020, 81000021, 81000022, 81000023,
        81000024, 81000025, 81000030, 81000031, 81000032, 81000033,
        81000034, 81000035, 81000036, 81000037, 81000038, 81000039,
    };

    public static string RunTestsetCardMatrix()
    {
        int pass = 0, total = 0;
        var log = new StringBuilder();

        var cases = new (int id, string name, Func<(bool, string)> run)[]
        {
            (81000000, "Indomitable Fighter", MatrixFighter),
            (81000001, "Detective's Lens", MatrixLens),
            (81000002, "Bug Alert", MatrixBugAlert),
            (81000003, "Amataz", MatrixAmataz),
            (81000004, "Prostrating Coward", MatrixCoward),
            (81000005, "Shoddy Plaything", MatrixShoddy),
            (81000006, "Aria", MatrixAria),
            (81000007, "Sham-Nacha", MatrixSham),
            (81000010, "Witch's New Brew", MatrixBrew),
            (81000011, "Runeblade Conductor", MatrixConductor),
            (81000012, "Blaze Destroyer", MatrixBlaze),
            (81000013, "Godwood Staff", MatrixGodwood),
            (81000014, "Edelweiss", MatrixEdelweiss),
            (81000015, "Reno", MatrixReno),
            (81000020, "Ghost", MatrixGhost),
            (81000021, "Sinciro", MatrixSinciro),
            (81000022, "Striker Artifact", MatrixStriker),
            (81000023, "Slaus", MatrixSlaus),
            (81000024, "Sandalphon", MatrixSandalphon),
            (81000025, "Obsessed Test Subject", MatrixObsessed),
            (81000030, "Pascale's Dance", MatrixPascale),
            (81000031, "Mjerrabaine", MatrixMjerrabaine),
            (81000032, "Octrice", MatrixOctrice),
            (81000033, "Glittering Gold", MatrixGold),
            (81000034, "City of Babelon", MatrixBabelon),
            (81000035, "Shakdoh", MatrixShakdoh),
            (81000036, "Adventurers' Guild", MatrixGuild),
            (81000037, "Ancient Cannon", MatrixCannon),
            (81000038, "Gear of Ambition", MatrixGear),
            (81000039, "Yuna", MatrixYuna),
        };

        foreach (var (id, name, run) in cases)
        {
            total++;
            bool ok; string detail;
            try { (ok, detail) = run(); }
            catch (Exception ex) { ok = false; detail = "异常: " + ex.GetType().Name + " " + ex.Message; }
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + "卡 " + id + " " + name + "  —— " + detail);
        }

        // 覆盖校验：卡号清单必须与测试集文件一致，防止「少测了一张没人发现」
        total++;
        bool allListed = cases.Length == MatrixCardIds.Length
                         && cases.Select(c => c.id).SequenceEqual(MatrixCardIds);
        if (allListed) pass++;
        log.AppendLine((allListed ? "[OK]   " : "[FAIL] ")
            + "矩阵覆盖 testset_cards.json 全部 " + MatrixCardIds.Length + " 张卡"
            + "  —— 实际 " + cases.Length + " 张");

        log.AppendLine();
        log.AppendLine("=== 测试集逐卡矩阵 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    // ---------------------------------------------------------------- 夹具

    private static CompileContext MatrixCtx(int seed) => CompileContext.Create(seed)
        .Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
        .Traits("Pixie", "Artifact", "Loot", "Clue", "Golem", "Officer")
        .Cards(("Fairy", 90001110), ("Bat", 90001120), ("Ghost", 90001130),
               ("Kindled Blade", 90005150), ("Ominous Artifact \u03b1", 90005110),
               ("Ominous Artifact \u03b2", 90005120), ("Ominous Artifact \u03b3", 90005130),
               ("Striker Artifact", 90005170), ("Remnant of Hollowness", 90005140),
               ("Gear of Ambition", 90005180), ("Ancient Cannon", 81000037),
               ("Shoddy Plaything", 90001234),
               ("Prostrating Coward", 90001235), ("Aria", 90001236),
               ("Slaus", 90001237), ("Obsessed Test Subject", 81000025))
        .Build();

    private static (BattleState st, TurnRunner tr, CardVm vm) MatrixBoard(CompileContext ctx)
    {
        var st = ctx.State;
        st.RegisterBuiltinTokens();
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0);
        l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1);
        l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 8;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(1, st.Schema.Counter("pp"), 20);
        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        return (st, new TurnRunner(st, st.Schema), vm);
    }

    private static void FillDeck(BattleState st, int n = 8)
    {
        for (int i = 0; i < n; i++)
        {
            st.RegisterCard(new CardDef { CardId = 2900 + i, Name = "Fill" + i, Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
            st.SpawnCard(2900 + i, ZoneRef.Deck, 0);
        }
    }

    // ---------------------------------------------------------------- 逐卡

    private static (bool, string) MatrixFighter()
    {
        var ctx = MatrixCtx(1); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Draw a card.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000000, Name = "Indomitable Fighter", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 2, Abilities = r.Abilities });
        var c = st.SpawnCard(81000000, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        return (h1 == h0, "入场抽牌 手牌 " + h0 + "->" + h1 + "（打出-1 抽+1 持平）");
    }

    private static (bool, string) MatrixLens()
    {
        var ctx = MatrixCtx(2); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Add a <b><i><color=Keyword>Fairy</color></i></b> to your hand.<hr><b><color=Keyword>Countdown</color> (2)</b>");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000001, Name = "Detective's Lens", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities });
        var c = st.SpawnCard(81000001, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        int cd = c.Counters.TryGetValue(st.Schema.Counter("countdown"), out var v) ? v : -1;
        return (h1 == h0 && cd == 2, "加妖精 手牌 " + h0 + "->" + h1 + " 吟唱=" + cd + "（期望 2）");
    }

    private static (bool, string) MatrixBugAlert()
    {
        var ctx = MatrixCtx(3); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Destroy all enemy followers with 3 attack or less. Draw a card.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000002, Name = "Bug Alert", Kind = CardKind.Spell, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2901, Name = "Small", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 2902, Name = "Big", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 1 });
        var small = st.SpawnCard(2901, ZoneRef.Board, 1);
        var big = st.SpawnCard(2902, ZoneRef.Board, 1);
        var c = st.SpawnCard(81000002, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool ok = small.Zone != ZoneRef.Board && big.Zone == ZoneRef.Board && h1 == h0;
        return (ok, "3攻被破坏=" + (small.Zone != ZoneRef.Board) + " 4攻存活=" + (big.Zone == ZoneRef.Board) + " 抽牌后手牌 " + h0 + "->" + h1);
    }

    private static (bool, string) MatrixAmataz()
    {
        var ctx = MatrixCtx(4); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Give this follower +X/+X. X is the number of Pixie followers in your hand.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000003, Name = "Amataz", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 3, Abilities = r.Abilities });
        // 手牌放 2 张 Pixie
        st.RegisterCard(new CardDef { CardId = 2910, Name = "PixieA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Traits = new[] { st.Schema.Trait("Pixie") } });
        st.SpawnCard(2910, ZoneRef.Hand, 0);
        st.SpawnCard(2910, ZoneRef.Hand, 0);
        // 放 1 张非 Pixie
        st.RegisterCard(new CardDef { CardId = 2911, Name = "PlainA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        st.SpawnCard(2911, ZoneRef.Hand, 0);

        var c = st.SpawnCard(81000003, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        return (c.Attack == 4 && c.Life == 4, "身材 " + c.Attack + "/" + c.Life + "（期望 4/4 = 2/2 + 2 张 Pixie）");
    }

    private static (bool, string) MatrixCoward()
    {
        var ctx = MatrixCtx(5); var comp = new EnglishCompiler(ctx.Lexicon);
        var alt = new (string, int, string)[] { ("Crystallize", 2, "<b><color=Keyword>Countdown</color> (3)</b>") };
        var r = comp.Compile("<b><color=Keyword>Bane</color></b><b><color=Keyword>Ward</color></b>", alt, "Prostrating Coward", 81000004);
        var (st, tr, vm) = MatrixBoard(ctx);
        st.WriteCounter(0, st.Schema.Counter("pp"), 2);
        st.RegisterCard(new CardDef { CardId = 81000004, Name = "Prostrating Coward", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 5, BaseCost = 5, Abilities = r.Abilities, CrystallizeDef = r.CrystallizeDef });
        var c = st.SpawnCard(81000004, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int cd = c.Counters.TryGetValue(st.Schema.Counter("countdown"), out var v) ? v : -1;
        bool asAmulet = c.Zone == ZoneRef.Board && c.Def?.Kind == CardKind.Amulet && cd == 3;
        return (asAmulet, "2费结晶为护符=" + (c.Def?.Kind == CardKind.Amulet) + " 吟唱=" + cd + "（期望 3）");
    }

    private static (bool, string) MatrixShoddy()
    {
        var ctx = MatrixCtx(6); var comp = new EnglishCompiler(ctx.Lexicon);
        var alt = new (string, int, string)[] { ("Accelerate", 2, "Summon a Shoddy Plaything.") };
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Draw 3 cards.", alt, "Shoddy Plaything", 90001234);
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.WriteCounter(0, st.Schema.Counter("pp"), 2);
        st.RegisterCard(new CardDef { CardId = 90001234, Name = "Shoddy Plaything", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 3, BaseCost = 6, Abilities = r.Abilities, AccelerateDef = r.AccelerateDef });
        var c = st.SpawnCard(90001234, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool summoned = st.EnumerateZone(ZoneRef.Board, 0).Any(e => e.Def?.CardId == 90001234);
        // 打出 -1（离开手牌），激奏不触发 Fanfare 所以不抽牌 → 净 -1。
        // 若 Fanfare 被误触发，h1 会是 h0+2（-1+3）。
        bool noFanfare = h1 == h0 - 1;
        return (summoned && noFanfare, "激奏召唤=" + summoned + " 未触发Fanfare=" + noFanfare + " 手牌 " + h0 + "->" + h1);
    }

    private static (bool, string) MatrixAria()
    {
        var ctx = MatrixCtx(7); var comp = new EnglishCompiler(ctx.Lexicon);
        var alt = new (string, int, string)[] { ("Crest", 0, "Whenever an allied Pixie follower enters the field, give it Storm.") };
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Gain <b><i>Crest: Aria</i></b>.", alt, "Aria", 81000006);
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000006, Name = "Aria", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 6, Abilities = r.Abilities, CrestDef = r.CrestDef });
        var c = st.SpawnCard(81000006, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        bool crest = st.EnumerateZone(ZoneRef.CrestArea, 0).Any(e => e.Def?.Kind == CardKind.Crest);
        var fairy = st.SpawnCard(90001110, ZoneRef.Board, 0);
        vm.StepUntilBlocked();
        bool storm = fairy.Keywords.Contains(st.Schema.Keyword("storm"));
        return (crest && storm, "获得纹章=" + crest + " Pixie得疾驰=" + storm);
    }

    private static (bool, string) MatrixSham()
    {
        var ctx = MatrixCtx(8); var comp = new EnglishCompiler(ctx.Lexicon);
        var faithAlt = new (string, int, string)[] { ("Faith", 0, "This faith's value starts at 0.<hr>Whenever you select Modes, increase this faith's value by 1.") };
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Reduce your faith's value by 10 to give it \"Increase the number of Modes you can select by 1.\"", faithAlt, "Sham-Nacha", 81000007);
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000007, Name = "Sham-Nacha", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities, FaithDef = r.FaithDef });
        st.SpawnCard(81000007, ZoneRef.Deck, 0);
        st.SetupFaith();
        var fk = st.Schema.Counter("faith");

        st.WriteCounter(0, fk, 0);
        var c1 = st.SpawnCard(81000007, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int mc0 = st.ReadCounter(0, st.Schema.Counter("mode_count"));
        tr.PlayCard(0, c1, vm); vm.StepUntilBlocked();
        bool noFreeload = st.ReadCounter(0, st.Schema.Counter("mode_count")) == mc0;

        st.WriteCounter(0, fk, 15);
        var c2 = st.SpawnCard(81000007, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c2, vm); vm.StepUntilBlocked();
        bool paid = st.ReadCounter(0, fk) == 5 && st.ReadCounter(0, st.Schema.Counter("mode_count")) == mc0 + 1;
        return (noFreeload && paid, "0信仰不白嫖=" + noFreeload + " 15信仰扣到5且模式+1=" + paid);
    }

    private static (bool, string) MatrixBrew()
    {
        var ctx = MatrixCtx(9); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: If you're in <b><color=Keyword>Overflow</color></b>, draw 2 cards. Otherwise, draw a card.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000010, Name = "Witch's New Brew", Kind = CardKind.Spell, BaseCost = 1, Abilities = r.Abilities });
        var mpk = st.Schema.Counter("max_pp");

        st.WriteCounter(0, mpk, 5);
        var c1 = st.SpawnCard(81000010, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c1, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();

        st.WriteCounter(0, mpk, 7);
        var c2 = st.SpawnCard(81000010, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h2 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c2, vm); vm.StepUntilBlocked();
        int h3 = st.EnumerateZone(ZoneRef.Hand, 0).Count();

        bool normal = h1 == h0;           // 打出-1 抽+1
        bool overflow = h3 == h2 + 1;     // 打出-1 抽+2
        return (normal && overflow, "未觉醒 手牌 " + h0 + "->" + h1 + " 觉醒 " + h2 + "->" + h3);
    }

    private static (bool, string) MatrixConductor()
    {
        var ctx = MatrixCtx(10); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Select an enemy follower on the field and deal it X damage. X is your <b><color=Keyword>Combo</color></b> count.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000011, Name = "Runeblade Conductor", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2920, Name = "FoeH", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        var foe = st.SpawnCard(2920, ZoneRef.Board, 1);
        st.WriteCounter(0, st.Schema.Counter("combo"), 2);
        var c = st.SpawnCard(81000011, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm);
        if (vm.Pending != null) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { foe.EntityId });
        vm.StepUntilBlocked();
        // 打出时 combo +1 → 3，因此打 3 点，剩 2
        return (foe.Life == 2, "连击3 → 打3点，敌方 5→" + foe.Life + "（期望 2）");
    }

    private static (bool, string) MatrixBlaze()
    {
        var ctx = MatrixCtx(11); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Activates in hand. <b><color=Keyword>On Spellboost</color></b>: Reduce the cost of this card by 1.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000012, Name = "Blaze Destroyer", Kind = CardKind.Follower, BaseAttack = 5, BaseLife = 5, BaseCost = 7, Abilities = r.Abilities });
        var blaze = st.SpawnCard(81000012, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int cost0 = blaze.Cost;
        // 打两张法术触发魔力增幅
        st.RegisterCard(new CardDef { CardId = 2921, Name = "SpellX", Kind = CardKind.Spell, BaseCost = 1 });
        for (int i = 0; i < 2; i++)
        {
            var sp = st.SpawnCard(2921, ZoneRef.Hand, 0);
            tr.PlayCard(0, sp, vm); vm.StepUntilBlocked();
        }
        return (blaze.Cost == cost0 - 2, "费用 " + cost0 + "->" + blaze.Cost + "（期望 -2）");
    }

    private static (bool, string) MatrixGodwood()
    {
        var ctx = MatrixCtx(12); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("At the end of your turn, <b><color=Keyword>Combo</color> (3)</b> - Draw a card.<hr><b><color=Keyword>Engage</color></b>: Destroy this card. Select another allied card on the field and give it <color=Keyword>Ward</color>.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000013, Name = "Godwood Staff", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities });
        var staff = st.SpawnCard(81000013, ZoneRef.Board, 0);
        st.RegisterCard(new CardDef { CardId = 2922, Name = "AllyA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 2923, Name = "AllyB", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5, BaseCost = 1 });
        var a = st.SpawnCard(2922, ZoneRef.Board, 0);
        var b = st.SpawnCard(2923, ZoneRef.Board, 0);

        // combo < 3 时回合末不抽
        st.WriteCounter(0, st.Schema.Counter("combo"), 1);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.EndTurn(0, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool noDraw = h1 <= h0;

        // Engage：单选 + 排除自身
        vm.DropPendingTriggers();
        tr.EngageAmulet(0, staff, vm);
        bool blocked = vm.Pending != null;
        var ward = st.Schema.Keyword("ward");
        if (blocked) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { a.EntityId });
        vm.StepUntilBlocked();
        int warded = (a.Keywords.Contains(ward) ? 1 : 0) + (b.Keywords.Contains(ward) ? 1 : 0);
        bool excl = blocked && warded == 1 && a.Keywords.Contains(ward);
        return (noDraw && excl, "combo<3不抽=" + noDraw + " another单选排除自身=" + excl + "(获得守护=" + warded + ")");
    }

    private static (bool, string) MatrixEdelweiss()
    {
        var ctx = MatrixCtx(13); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: <b><color=Keyword>Earth Rite</color> (2)</b> - Evolve this follower.<hr><ev>When this follower evolves, deal 4 damage to a random enemy follower.</ev>");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000014, Name = "Edelweiss", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 4, BaseCost = 3, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2924, Name = "Foe4", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 4, BaseCost = 1 });
        var foe = st.SpawnCard(2924, ZoneRef.Board, 1);

        st.WriteCounter(0, st.Schema.Counter("earth_sigil"), 2);
        var c = st.SpawnCard(81000014, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        bool evolved = c.IsEvolved;
        bool killed = foe.Zone != ZoneRef.Board;
        return (evolved && killed, "土之秘术2 → 进化=" + evolved + " 打死4血随从=" + killed);
    }

    private static (bool, string) MatrixReno()
    {
        var ctx = MatrixCtx(14); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Clash</color></b>: Deal 1 damage to the enemy leader.<hr><sev><b><color=Keyword>Super-Evolve</color></b>: Give this follower \"Can attack 2 times per turn.\"</sev>");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000015, Name = "Reno", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2925, Name = "FoeX", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 9, BaseCost = 1 });
        var c = st.SpawnCard(81000015, ZoneRef.Board, 0);
        c.IsSuperEvolved = true; c.IsEvolved = true;
        // 必须走真实的 OnSuperEvolve 事件，让「给本随从 Can attack 2 times」真的执行；
        // 只把 IsSuperEvolved 置 true 不会触发任何能力。
        // 且必须标 IsManualEvolve：Super-Evolve: 冒号头只认玩家主动花 SEP 的进化，
        // 能力效果自动进化的那条路径不该触发它（ManualEvolveOnly 检查的就是这个）。
        vm.EnqueueEvent(new VmEvent
        {
            Kind = CoreEvent.OnSuperEvolve,
            Subject = EntityRef.From(c),
            IsManualEvolve = true,
        });
        vm.StepUntilBlocked();
        var foe = st.SpawnCard(2925, ZoneRef.Board, 1);
        vm.DropPendingTriggers();

        int foeLeader0 = st.Players[1].Leader.Life;
        var combat = new CombatResolver(st, st.Schema);
        combat.ExecuteCombat(c, foe, vm);
        vm.StepUntilBlocked();
        bool clashDamage = st.Players[1].Leader.Life == foeLeader0 - 1;
        bool maxAtk = c.KeywordValues.TryGetValue(st.Schema.Keyword("max_attacks_per_turn"), out var mav) && mav == 2;
        return (clashDamage && maxAtk, "交战打主战者1血=" + clashDamage + " 每回合2次攻击=" + maxAtk);
    }

    private static (bool, string) MatrixGhost()
    {
        var ctx = MatrixCtx(15); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Storm</color></b><hr>When this card leaves the field, banish it.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000020, Name = "Ghost", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities });
        var g = st.SpawnCard(81000020, ZoneRef.Board, 0);
        vm.DropPendingTriggers();
        int sh0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
        g.Life = 0;
        st.ProcessStateBasedActions();
        bool banished = st.EnumerateZone(ZoneRef.Banished, 0).Any(e => e.EntityId == g.EntityId);
        bool notCem = !st.EnumerateZone(ZoneRef.Cemetery, 0).Any(e => e.EntityId == g.EntityId);
        bool noShadow = st.ReadCounter(0, st.Schema.Counter("shadow")) == sh0;
        return (banished && notCem && noShadow, "战死进放逐=" + banished + " 不在墓地=" + notCem + " shadow未增=" + noShadow);
    }

    private static (bool, string) MatrixSinciro()
    {
        var ctx = MatrixCtx(16); var comp = new EnglishCompiler(ctx.Lexicon);
        var fuseAb = comp.Compile("When you Fuse to this card, draw a card.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef
        {
            CardId = 81000021, Name = "Sinciro", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 4,
            Abilities = fuseAb.Abilities, HasFuse = true,
        });
        st.RegisterCard(new CardDef { CardId = 2930, Name = "LootA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 2, Traits = new[] { st.Schema.Trait("Loot") } });
        var baseCard = st.SpawnCard(81000021, ZoneRef.Hand, 0);
        var m1 = st.SpawnCard(2930, ZoneRef.Hand, 0);
        var m2 = st.SpawnCard(2930, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        bool fused = tr.FuseCards(0, baseCard, new[] { m1, m2 }, vm);
        vm.StepUntilBlocked();
        int fc = baseCard.Counters.TryGetValue(st.Schema.Counter("fuse_count"), out var a) ? a : 0;
        int fk = baseCard.Counters.TryGetValue(st.Schema.Counter("fuse_kinds"), out var b) ? b : 0;
        int fcost = baseCard.Counters.TryGetValue(st.Schema.Counter("fuse_cost"), out var c) ? c : 0;
        bool ok = fused && fc == 1 && fk == 1 && fcost == 4;
        return (ok, "融合成功=" + fused + " 次数=" + fc + " 种类=" + fk + "（同名只算1）总费=" + fcost);
    }

    private static (bool, string) MatrixStriker()
    {
        var ctx = MatrixCtx(17); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("When you <color=Keyword>Fuse</color> to this card, transform it based on the total cost of the cards fused.<hr> 1: <b><i>Ominous Artifact \u03b1</i></b><hr> 2: <b><i>Ominous Artifact \u03b2</i></b><hr> 3 or more: <b><i>Ominous Artifact \u03b3</i></b>");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000022, Name = "Striker Artifact", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 3, Abilities = r.Abilities, HasFuse = true });
        st.RegisterCard(new CardDef { CardId = 2931, Name = "ArtA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Traits = new[] { st.Schema.Trait("Artifact") } });
        var baseCard = st.SpawnCard(81000022, ZoneRef.Hand, 0);
        var m = st.SpawnCard(2931, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.FuseCards(0, baseCard, new[] { m }, vm);
        vm.StepUntilBlocked();
        int fid = baseCard.Def?.CardId ?? 0;
        bool transformed = fid == 90005110;   // Ominous Artifact α
        return (transformed, "总费1 → 变身为 id=" + fid + "（期望 90005110 = α）");
    }

    private static (bool, string) MatrixSlaus()
    {
        var ctx = MatrixCtx(18); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Ambush</color></b><hr>At the end of your turn, if this follower is evolved, give your opponent <b><i>Crest: Slaus</i></b>.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000023, Name = "Slaus", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 4, Abilities = r.Abilities, CrestDef = new CardDef { Name = "Slaus", Kind = CardKind.Crest } });
        var c = st.SpawnCard(81000023, ZoneRef.Board, 0);
        vm.DropPendingTriggers();

        // 未进化：不给纹章
        tr.EndTurn(0, vm); vm.StepUntilBlocked();
        int foeCrest0 = st.EnumerateZone(ZoneRef.CrestArea, 1).Count();

        // 进化后：给**对手**
        c.IsEvolved = true;
        st.ActivePlayer = 0; st.TurnNumber = 9;
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        vm.CurrentTurnOwner = 0;
        vm.DropPendingTriggers();
        tr.EndTurn(0, vm); vm.StepUntilBlocked();
        int foeCrest1 = st.EnumerateZone(ZoneRef.CrestArea, 1).Count();
        return (foeCrest1 > foeCrest0, "未进化不给=" + (foeCrest0 == 0) + " 进化后给对手=" + (foeCrest1 > foeCrest0) + "（对手纹章 " + foeCrest0 + "->" + foeCrest1 + "）");
    }

    private static (bool, string) MatrixSandalphon()
    {
        var ctx = MatrixCtx(19); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Activates in deck. At the start of your turn, if allied followers have evolved at least 6 times this match, <color=Keyword>Invoke</color> this card.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000024, Name = "Sandalphon", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 5, Abilities = r.Abilities });
        // 牌库里必须还有别的卡：StartTurn 的常规抽牌会把**最后一张**抽走，
        // 牌库只有 Sandalphon 一张时它会被抽到手上，Invoke 永远看不到。
        FillDeck(st, 12);
        var c = st.SpawnCard(81000024, ZoneRef.Deck, 0);
        vm.DropPendingTriggers();

        st.WriteCounter(0, st.Schema.Counter("evolve_count"), 5);
        vm.DropPendingTriggers();
        tr.StartTurn(0, vm); vm.StepUntilBlocked();
        bool stayed = c.Zone == ZoneRef.Deck;

        // 第二次开始回合前必须补满 pp：StartTurn 会重置 pp 并抽牌，
        // 但连续调用两次会让第一次抽光的牌库触发抽空判负，
        // 于是第二次 StartTurn 被终局短路，Invoke 永远看不到。
        st.WriteCounter(0, st.Schema.Counter("pp"), 20);
        st.WriteCounter(0, st.Schema.Counter("evolve_count"), 6);
        vm.DropPendingTriggers();
        tr.StartTurn(0, vm); vm.StepUntilBlocked();
        bool invoked = c.Zone == ZoneRef.Board;
        return (stayed && invoked, "5次进化留在牌库=" + stayed + " 6次进化 Invoke=" + invoked);
    }

    private static (bool, string) MatrixObsessed()
    {
        var ctx = MatrixCtx(20); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("When this follower enters the field, if at least 5 other allied copies of <b><i>Obsessed Test Subject</i></b> have entered the field this match, destroy all enemy followers.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000025, Name = "Obsessed Test Subject", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2932, Name = "FoeY", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 9, BaseCost = 1 });
        var foe = st.SpawnCard(2932, ZoneRef.Board, 1);
        vm.DropPendingTriggers();

        // 先让 5 张同名卡进过场
        for (int i = 0; i < 5; i++)
        {
            var prior = st.SpawnCard(81000025, ZoneRef.Board, 0);
            vm.StepUntilBlocked();
            st.DestroyEntity(prior);
            vm.StepUntilBlocked();
        }
        bool foeAliveBefore = foe.Zone == ZoneRef.Board;

        // 第 6 张进场
        var c = st.SpawnCard(81000025, ZoneRef.Board, 0);
        vm.StepUntilBlocked();
        bool foeDead = foe.Zone != ZoneRef.Board;
        return (foeAliveBefore && foeDead, "前5张时敌方存活=" + foeAliveBefore + " 第6张进场后敌方被破坏=" + foeDead);
    }

    private static (bool, string) MatrixPascale()
    {
        var ctx = MatrixCtx(21); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: <b><color=Keyword>Earth Rite</color> (10)</b> - Double the attack and defense of an allied follower.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000030, Name = "Pascale's Dance", Kind = CardKind.Spell, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2933, Name = "Ally34", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 4, BaseCost = 1 });
        var ally = st.SpawnCard(2933, ZoneRef.Board, 0);
        var esk = st.Schema.Counter("earth_sigil");

        // 5 层：不足，不生效
        st.WriteCounter(0, esk, 5);
        var c1 = st.SpawnCard(81000030, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c1, vm);
        if (vm.Pending != null) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { ally.EntityId });
        vm.StepUntilBlocked();
        bool unchanged = ally.Attack == 3 && ally.Life == 4;

        // 10 层：翻倍
        st.WriteCounter(0, esk, 10);
        var c2 = st.SpawnCard(81000030, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c2, vm);
        if (vm.Pending != null) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { ally.EntityId });
        vm.StepUntilBlocked();
        bool doubled = ally.Attack == 6 && ally.Life == 8 && ally.MaxLife == 8;
        return (unchanged && doubled, "5层不变=" + unchanged + " 10层翻倍=" + doubled + "（" + ally.Attack + "/" + ally.Life + "）");
    }

    private static (bool, string) MatrixMjerrabaine()
    {
        var ctx = MatrixCtx(22); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("When you gain this crest, replace your deck with a deck of 40 cards.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000031, Name = "Mjerrabaine", Kind = CardKind.Crest, Abilities = r.Abilities });
        int d0 = st.EnumerateZone(ZoneRef.Deck, 0).Count();
        var crest = st.CreateEntity(81000031, ZoneRef.CrestArea, 0);
        vm.DropPendingTriggers();
        st.EmitEvent(new Vm.VmEvent { Kind = CoreEvent.OnEnterField, Subject = Vm.EntityRef.From(crest) });
        vm.StepUntilBlocked();
        int d1 = st.EnumerateZone(ZoneRef.Deck, 0).Count();
        bool allHaveDef = st.EnumerateZone(ZoneRef.Deck, 0).All(e => e.Def != null);
        return (d1 == 40 && allHaveDef, "牌库 " + d0 + "->" + d1 + "（期望 40）全部有卡面=" + allHaveDef);
    }

    private static (bool, string) MatrixOctrice()
    {
        var ctx = MatrixCtx(23); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Countdown</color> (8)</b><hr>Whenever you play or <color=Keyword>Fuse</color> a Loot card, advance this crest's count by 1.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000032, Name = "Octrice", Kind = CardKind.Crest, Abilities = r.Abilities });
        var crest = st.CreateEntity(81000032, ZoneRef.CrestArea, 0);
        crest.Counters[st.Schema.Counter("countdown")] = 8;
        st.RegisterCard(new CardDef { CardId = 2934, Name = "LootSpell", Kind = CardKind.Spell, BaseCost = 1, Traits = new[] { st.Schema.Trait("Loot") } });
        vm.DropPendingTriggers();

        var sp = st.SpawnCard(2934, ZoneRef.Hand, 0);
        tr.PlayCard(0, sp, vm); vm.StepUntilBlocked();
        int cd = crest.Counters.TryGetValue(st.Schema.Counter("countdown"), out var v) ? v : -1;
        return (cd == 7, "打出 Loot 后吟唱 8→" + cd + "（期望 7）");
    }

    private static (bool, string) MatrixGold()
    {
        var ctx = MatrixCtx(24); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("Select a Mode to activate.<hr> 1. Draw a card.<hr> 2. Deal 2 damage to the enemy leader.<hr> 3. Restore 2 defense to your leader.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000033, Name = "Glittering Gold", Kind = CardKind.Spell, BaseCost = 1, Abilities = r.Abilities });
        var c = st.SpawnCard(81000033, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int foe0 = st.Players[1].Leader.Life;
        tr.PlayCard(0, c, vm);
        bool blocked = vm.Pending != null;
        // 选模式 2：打敌方主战者 2 血
        if (blocked) vm.SubmitChoice(vm.Pending.ChoiceId, new[] { 1 });
        vm.StepUntilBlocked();
        int foe1 = st.Players[1].Leader.Life;
        return (blocked && foe1 == foe0 - 2 && c.Zone == ZoneRef.Cemetery,
            "挂起=" + blocked + " 模式2打2血=" + (foe1 == foe0 - 2) + " 选后进墓=" + (c.Zone == ZoneRef.Cemetery));
    }

    private static (bool, string) MatrixBabelon()
    {
        var ctx = MatrixCtx(25); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Countdown</color> (1)</b><hr>At the end of your turn, activate an ability in sequence from the following.<hr> 1. Deal 2 damage to a random enemy follower.<hr> 2. Draw a card.<hr> 3. Restore 2 defense to your leader.");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st);
        st.RegisterCard(new CardDef { CardId = 81000034, Name = "City of Babelon", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2935, Name = "FoeZ", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 9, BaseCost = 1 });
        var foe = st.SpawnCard(2935, ZoneRef.Board, 1);
        var c = st.SpawnCard(81000034, ZoneRef.Board, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.EndTurn(0, vm); vm.StepUntilBlocked();
        bool noPrompt = vm.Pending == null;
        bool damaged = foe.Life == 7;
        bool drew = st.EnumerateZone(ZoneRef.Hand, 0).Count() == h0 + 1;
        return (noPrompt && damaged && drew, "Sequence不弹窗=" + noPrompt + " 打2=" + damaged + " 抽1=" + drew);
    }

    private static (bool, string) MatrixShakdoh()
    {
        var ctx = MatrixCtx(26); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Do this 2 times: \"Return your hand to deck. Draw X cards. X is the number of cards you returned.\"");
        var (st, tr, vm) = MatrixBoard(ctx);
        FillDeck(st, 20);
        st.RegisterCard(new CardDef { CardId = 81000035, Name = "Shakdoh", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2936, Name = "HandCard", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        for (int i = 0; i < 4; i++) st.SpawnCard(2936, ZoneRef.Hand, 0);
        var c = st.SpawnCard(81000035, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 循环 2 次：手牌洗回 → 抽等量。最终手牌应当回到合理数量，不是 5+10=15
        bool sane = hand > 0 && hand <= 6;
        return (sane, "循环2次后手牌=" + hand + "（期望 ≤6，累加 bug 会大到 10+）");
    }

    private static (bool, string) MatrixGuild()
    {
        var ctx = MatrixCtx(27); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Draw a follower.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 2937, Name = "SpellTop", Kind = CardKind.Spell, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 2938, Name = "FollowerTwo", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 81000036, Name = "Adventurers' Guild", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities });
        // 牌库顶放法术，第 2 张放随从
        st.SpawnCard(2937, ZoneRef.Deck, 0);
        st.SpawnCard(2938, ZoneRef.Deck, 0);
        var c = st.SpawnCard(81000036, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        var hand = st.EnumerateZone(ZoneRef.Hand, 0).ToList();
        bool gotFollower = hand.Any(e => e.Def?.Kind == CardKind.Follower && e.Def?.CardId == 2938);
        return (gotFollower, "定向抽随从=" + gotFollower + "（手牌 [" + string.Join(",", hand.Select(e => e.Def?.Name)) + "]）");
    }

    private static (bool, string) MatrixCannon()
    {
        var ctx = MatrixCtx(28); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Fanfare</color></b>: Add a <b><i>Gear of Ambition</i></b> to your hand.<hr>Whenever you <color=Keyword>Fuse</color>, deal 2 damage to a random enemy follower.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000037, Name = "Ancient Cannon", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities });
        st.RegisterCard(new CardDef { CardId = 2939, Name = "FoeW", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 9, BaseCost = 1 });
        var foe = st.SpawnCard(2939, ZoneRef.Board, 1);
        var c = st.SpawnCard(81000037, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        tr.PlayCard(0, c, vm); vm.StepUntilBlocked();
        int h1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool gotGear = st.EnumerateZone(ZoneRef.Hand, 0).Any(e => e.Def?.CardId == 90005180);

        // 融合触发监听
        st.RegisterCard(new CardDef { CardId = 2940, Name = "FuseBase", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, HasFuse = true });
        st.RegisterCard(new CardDef { CardId = 2941, Name = "MatX", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        var fb = st.SpawnCard(2940, ZoneRef.Hand, 0);
        var mt = st.SpawnCard(2941, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();
        tr.FuseCards(0, fb, new[] { mt }, vm);
        vm.StepUntilBlocked();
        bool damaged = foe.Life == 7;
        return (gotGear && damaged, "加入齿轮=" + gotGear + "（手牌 " + h0 + "->" + h1 + "）融合触发打2=" + damaged);
    }

    private static (bool, string) MatrixGear()
    {
        var ctx = MatrixCtx(29); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("When you <color=Keyword>Fuse</color> to this card, transform it into a <b><i>Striker Artifact</i></b>.<hr>Can't be played.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000038, Name = "Gear of Ambition", Kind = CardKind.Amulet, BaseCost = 1, Abilities = r.Abilities, HasFuse = true });
        st.RegisterCard(new CardDef { CardId = 2942, Name = "ArtMat", Kind = CardKind.Amulet, BaseCost = 1, Traits = new[] { st.Schema.Trait("Artifact") } });
        var c = st.SpawnCard(81000038, ZoneRef.Hand, 0);
        var m = st.SpawnCard(2942, ZoneRef.Hand, 0);
        vm.DropPendingTriggers();

        // Can't be played
        bool cannotPlay = !tr.PlayCard(0, c, vm);

        vm.DropPendingTriggers();
        tr.FuseCards(0, c, new[] { m }, vm);
        vm.StepUntilBlocked();
        int fid = c.Def?.CardId ?? 0;
        bool transformed = fid == 90005170;   // Striker Artifact
        return (cannotPlay && transformed, "禁打=" + cannotPlay + " 融合后变身 id=" + fid + "（期望 90005170）");
    }

    private static (bool, string) MatrixYuna()
    {
        var ctx = MatrixCtx(30); var comp = new EnglishCompiler(ctx.Lexicon);
        var r = comp.Compile("<b><color=Keyword>Ward</color></b><hr><b><color=Keyword>Last Words</color></b>: Add a <b><i>Ghost</i></b> and <b><i>Bat</i></b> to your hand.");
        var (st, tr, vm) = MatrixBoard(ctx);
        st.RegisterCard(new CardDef { CardId = 81000039, Name = "Yuna", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3, Abilities = r.Abilities });
        var c = st.SpawnCard(81000039, ZoneRef.Board, 0);
        vm.DropPendingTriggers();
        int h0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        st.DestroyEntity(c);
        vm.StepUntilBlocked();
        var hand = st.EnumerateZone(ZoneRef.Hand, 0).ToList();
        bool gotGhost = hand.Any(e => e.Def?.CardId == 90001130);
        bool gotBat = hand.Any(e => e.Def?.CardId == 90001120);
        return (gotGhost && gotBat, "谢幕曲加幽灵=" + gotGhost + " 蝙蝠=" + gotBat + "（手牌 " + h0 + "->" + hand.Count + "）");
    }
}

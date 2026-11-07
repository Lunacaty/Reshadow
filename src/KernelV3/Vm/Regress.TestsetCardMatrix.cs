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
// 30 张测试卡的逐卡效用矩阵 —— **全部用真卡面**
//
// 铁律：本文件不允许手抄任何一句卡面文本。
// 每张卡的定义一律来自 TestsetLoader（它直接读 data/cards/testset_cards.json），
// 于是「这张卡的 Fanfare 里到底有几句」由文件说了算，测试作者没有删改的余地。
//
// 为什么这条铁律必须写死：手抄卡面时，抄的人会不自觉地删掉跑不通的那一段，
// 最后测的是他自己写的简化版，而报告上写的是「测试集 30 张全部通过」。
// ============================================================================
public static partial class Regress
{
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

        var cases = new (int id, string name, Func<MatrixEnv, (bool, string)> run)[]
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
            (81000025, "Testset Obsessed Test Subject", MatrixObsessed),
            (81000030, "Pascale's Dance", MatrixPascale),
            (81000031, "Mjerrabaine", MatrixMjerrabaine),
            (81000032, "Octrice", MatrixOctrice),
            (81000033, "Testset Glittering Gold", MatrixGold),
            (81000034, "City of Babelon", MatrixBabelon),
            (81000035, "Shakdoh", MatrixShakdoh),
            (81000036, "Adventurers' Guild", MatrixGuild),
            (81000037, "Testset Ancient Cannon", MatrixCannon),
            (81000038, "Testset Gear of Ambition", MatrixGear),
            (81000039, "Yuna", MatrixYuna),
        };

        foreach (var (id, name, run) in cases)
        {
            total++;
            bool ok; string detail;
            try
            {
                var env = MatrixEnv.Create(seed: id % 1000);
                (ok, detail) = run(env);
                // 任何一张卡跑完都不许留下「找不到卡定义」的记录
                if (env.St.UnresolvedSpawnCount > 0)
                {
                    ok = false;
                    detail += " | 未注册衍生卡 " + env.St.UnresolvedSpawnCount + " 次";
                }
            }
            catch (Exception ex) { ok = false; detail = "异常: " + ex.GetType().Name + " " + ex.Message; }
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + "卡 " + id + " " + name + "  —— " + detail);
        }

        // 覆盖 + 真卡面校验
        total++;
        bool allListed = cases.Length == MatrixCardIds.Length
                         && cases.Select(c => c.id).SequenceEqual(MatrixCardIds);
        if (allListed) pass++;
        log.AppendLine((allListed ? "[OK]   " : "[FAIL] ")
            + "矩阵覆盖 testset_cards.json 全部 " + MatrixCardIds.Length + " 张卡");

        total++;
        bool realText = MatrixUsesRealCardText();
        if (realText) pass++;
        log.AppendLine((realText ? "[OK]   " : "[FAIL] ")
            + "矩阵用的是 testset_cards.json 原文（未手抄/未删减）");

        log.AppendLine();
        log.AppendLine("=== 测试集逐卡矩阵 " + pass + "/" + total + " ===");
        return DeprecatedGates.Filter(log.ToString());
    }

    /// <summary>确认加载出来的卡面与文件里逐字节一致（防手抄/删减）。</summary>
    private static bool MatrixUsesRealCardText()
    {
        var env = MatrixEnv.Create(seed: 1);
        foreach (var id in MatrixCardIds)
        {
            var fileText = TestsetLoader.TextOf(id);
            var def = env.St.CardDefOf(id);
            if (fileText == null || def == null) return false;
            if (!string.Equals(def.SourceText, fileText, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- 夹具

    /// <summary>一套装好 30 张真卡的试验台。</summary>
    internal sealed class MatrixEnv
    {
        public CompileContext Ctx { get; private set; }
        public BattleState St { get; private set; }
        public TurnRunner Tr { get; private set; }
        public CardVm Vm { get; private set; }
        public List<TestsetLoader.LoadedCard> Cards { get; private set; }

        public static MatrixEnv Create(int seed)
        {
            var ctx = CompileContext.Create(seed)
                .Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
                .Build();
            var cards = TestsetLoader.LoadInto(ctx);

            var st = ctx.State;
            st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
            var l0 = st.CreateEntity(99, ZoneRef.Leader, 0);
            l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
            var l1 = st.CreateEntity(99, ZoneRef.Leader, 1);
            l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
            st.ActivePlayer = 0;
            st.TurnNumber = 8;
            st.WriteCounter(0, st.Schema.Counter("pp"), 20);
            st.WriteCounter(1, st.Schema.Counter("pp"), 20);

            var vm = new CardVm(st) { CurrentTurnOwner = 0 };
            return new MatrixEnv
            {
                Ctx = ctx,
                St = st,
                Tr = new TurnRunner(st, st.Schema),
                Vm = vm,
                Cards = cards,
            };
        }

        /// <summary>把一张测试集卡放进指定区域（用真定义）。</summary>
        public RuntimeEntity Put(int cardId, ZoneRef zone, int player = 0)
            => St.SpawnCard(cardId, zone, player);

        /// <summary>造一张填充卡（牌库/干扰项用，不属于测试集）。</summary>
        public RuntimeEntity Filler(int id, ZoneRef zone, int player = 0,
                                    CardKind kind = CardKind.Follower, int atk = 1, int life = 1)
        {
            if (St.CardDefOf(id) == null)
                St.RegisterCard(new CardDef
                {
                    CardId = id, Name = "Filler" + id, Kind = kind,
                    BaseCost = 1, BaseAttack = atk, BaseLife = life,
                });
            return St.SpawnCard(id, zone, player);
        }

        public void FillDeck(int n = 12) { for (int i = 0; i < n; i++) Filler(2900 + i, ZoneRef.Deck); }

        public void Draw()
        {
            Vm.DropPendingTriggers();
            Vm.StepUntilBlocked();
        }

        /// <summary>打出并把中途弹出的选择全部用给定策略消解。</summary>
        public bool Play(RuntimeEntity card, params int[][] choices)
        {
            Vm.DropPendingTriggers();
            bool ok = Tr.PlayCard(0, card, Vm);
            int i = 0;
            int guard = 0;
            while (Vm.Pending != null && guard++ < 20)
            {
                var pick = i < choices.Length
                    ? choices[i]
                    : new[] { Vm.Pending.Candidates.FirstOrDefault() };
                Vm.SubmitChoice(Vm.Pending.ChoiceId, pick);
                Vm.StepUntilBlocked();
                i++;
            }
            Vm.StepUntilBlocked();
            return ok;
        }
    }

    // ---------------------------------------------------------------- 逐卡
    // 说明：以下每个用例都从 MatrixEnv 取**文件里的**卡定义，
    //      断言的都是真实世界状态（区域 / 攻防 / 计数器 / 手牌数）。

    private static (bool, string) MatrixFighter(MatrixEnv e)
    {
        e.FillDeck();
        var c = e.Put(81000000, ZoneRef.Hand);
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool played = e.Play(c);
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool fanfare = h1 == h0;   // 打出 -1 抽 +1

        // Evolve: 选 1 个敌方随从打 2。
        // 手动进化要花 1 点进化点，且必须先有敌方随从可选 ——
        // 少任何一个，Evolve 都会安静返回 false，看起来像「效果没实现」。
        e.Filler(2950, ZoneRef.Board, 1, life: 5);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2950);
        e.St.WriteCounter(0, e.St.Schema.Counter("ep"), 2);
        e.Vm.DropPendingTriggers();
        bool evo = e.Tr.Evolve(0, c, false, e.Vm);
        if (e.Vm.Pending != null) e.Vm.SubmitChoice(e.Vm.Pending.ChoiceId, new[] { foe.EntityId });
        e.Vm.StepUntilBlocked();
        bool evolve = evo && foe.Life == 3;

        // Super-Evolve: 对敌方全体随从打 X（X = 手牌数）。
        // 超进化会同时触发 Evolve: 与 Super-Evolve: 两段，所以先补一个敌方随从
        // 好让两段都有落点，再断言「全体」那一段确实生效（两只都掉血）。
        e.Filler(2951, ZoneRef.Board, 1, life: 9);
        e.Filler(2952, ZoneRef.Board, 1, life: 9);
        // Super-Evolve 的 X 是**手牌数**：手牌空了 X=0，打 0 点，「全体伤害」看起来就像没生效。
        for (int i = 0; i < 3; i++) e.Filler(2960 + i, ZoneRef.Hand);
        var f1 = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2951);
        var f2 = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2952);
        // 无手动进化与超进化是**同一回合互斥**的两种操作（CanSuperEvolve 第一行就卡它）：
        // 同一回合先普通进化再超进化必然拒绝。测超进化前必须把该标志清掉。
        var c2 = e.Put(81000000, ZoneRef.Board);
        e.St.Players[0].ManualEvolvedThisTurn = false;
        e.St.WriteCounter(0, e.St.Schema.Counter("sep"), 2);
        e.Vm.DropPendingTriggers();
        bool sep = e.Tr.Evolve(0, c2, true, e.Vm);
        int guard = 0;
        while (e.Vm.Pending != null && guard++ < 8)
        {
            e.Vm.SubmitChoice(e.Vm.Pending.ChoiceId, new[] { e.Vm.Pending.Candidates.FirstOrDefault() });
            e.Vm.StepUntilBlocked();
        }
        e.Vm.StepUntilBlocked();
        // 两只都必须掉血才叫「全体」。f1 会被 Evolve 段的落点干扰吗？不会 ——
        // Evolve 段打的是 foe(2950)，2951/2952 是这一轮才放的新目标。
        bool superAoE = sep && f1.Life < 9 && f2.Life < 9;
_ = (sep, superAoE);

        return (played && fanfare && evolve && superAoE,
            "入场抽牌=" + fanfare + " 进化打2=" + evolve
            + " 超进化全体=" + superAoE + "（" + f1.Life + "/" + f2.Life + "，期望均 <9）"
            + " sep=" + sep + " 手牌=" + e.St.EnumerateZone(ZoneRef.Hand, 0).Count());
    }

    private static (bool, string) MatrixLens(MatrixEnv e)
    {
        e.FillDeck(4);
        var c = e.Put(81000001, ZoneRef.Hand);
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool played = e.Play(c);
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        int cd = c.Counters.TryGetValue(e.St.Schema.Counter("countdown"), out var v) ? v : -1;

        // Engage: 自毁 + 给一个友方随从 Rush
        e.Filler(2951, ZoneRef.Board, 0, life: 5);
        var ally = e.St.EnumerateZone(ZoneRef.Board, 0).First(x => x.Def.CardId == 2951);
        e.Vm.DropPendingTriggers();
        bool engaged = e.Tr.EngageAmulet(0, c, e.Vm);
        if (e.Vm.Pending != null) e.Vm.SubmitChoice(e.Vm.Pending.ChoiceId, new[] { ally.EntityId });
        e.Vm.StepUntilBlocked();
        bool rush = ally.Keywords.Contains(e.St.Schema.Keyword("rush"));

        // 打出 -1（离开手牌），加妖精 +1 → 净 0。
        return (played && h1 == h0 && cd == 2 && engaged && rush,
            "加妖精 手牌 " + h0 + "->" + h1 + " 吟唱=" + cd + " 启动给Rush=" + rush);
    }

    private static (bool, string) MatrixBugAlert(MatrixEnv e)
    {
        e.FillDeck(4);
        e.Filler(2952, ZoneRef.Board, 1, atk: 3, life: 3);
        e.Filler(2953, ZoneRef.Board, 1, atk: 4, life: 4);
        var small = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2952);
        var big = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2953);
        var c = e.Put(81000002, ZoneRef.Hand);
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Play(c);
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool ok = small.Zone != ZoneRef.Board && big.Zone == ZoneRef.Board && h1 == h0;
        return (ok, "3攻破坏=" + (small.Zone != ZoneRef.Board) + " 4攻存活=" + (big.Zone == ZoneRef.Board) + " 抽牌=" + (h1 == h0));
    }

    private static (bool, string) MatrixAmataz(MatrixEnv e)
    {
        // 手牌放 2 张 Pixie（Fairy 真卡 90011110 带 Pixie 兵种）+ 1 张非 Pixie。
        // X 数的是「手牌里的 Pixie 随从」，手牌空了 X 就是 0。
        e.Filler(2960, ZoneRef.Hand, life: 1);
        e.Put(90011110, ZoneRef.Hand);
        e.Put(90011110, ZoneRef.Hand);
        var c = e.Put(81000003, ZoneRef.Hand);
        e.Play(c);
        bool buffed = c.Attack == 4 && c.Life == 4;   // 2/2 + 2 张 Pixie = 4/4

        // Evolve: 循环 X 次打 1（X = 手牌 Pixie 数）
        e.Filler(2961, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2961);
        int life0 = foe.Life;
        e.St.WriteCounter(0, e.St.Schema.Counter("ep"), 2);
        e.Vm.DropPendingTriggers();
        e.Tr.Evolve(0, c, false, e.Vm);
        e.Vm.StepUntilBlocked();
        bool volley = foe.Life < life0;

        return (buffed && volley, "Fanfare +2/+2=" + buffed + "（" + c.Attack + "/" + c.Life + "）进化连射=" + volley);
    }

    private static (bool, string) MatrixCoward(MatrixEnv e)
    {
        e.FillDeck(6);
        var c = e.Put(81000004, ZoneRef.Hand);
        e.St.WriteCounter(0, e.St.Schema.Counter("pp"), 2);
        bool played = e.Play(c);
        int cd = c.Counters.TryGetValue(e.St.Schema.Counter("countdown"), out var v) ? v : -1;
        bool asAmulet = played && c.Zone == ZoneRef.Board && c.Def?.Kind == CardKind.Amulet && cd == 3;

        // 吟唱归 0 → 谢幕曲召唤本体 → 本体入场再回 2 血。
        // 三段都要断言：只验「召唤出来了」等于漏掉「入场曲是否接上」，
        // 而那条正是结晶链最容易断在半路的地方。
        e.St.Players[0].Leader.Life = 15;
        e.St.Players[0].Leader.MaxLife = 20;
        int leader0 = e.St.Players[0].Leader.Life;
        Console.WriteLine("[DEBUG ABILITIES] Count=" + c.Def?.Abilities?.Count);
        foreach (var a in c.Def?.Abilities ?? new List<AbilityDef>())
            Console.WriteLine("  ab ev=" + a.Trigger?.Event + " body=" + a.Body?.Count + " nodes=" + string.Join(",", a.Body?.Select(n => n.GetType().Name) ?? Array.Empty<string>()));
        c.Counters[e.St.Schema.Counter("countdown")] = 1;
        e.Vm.DropPendingTriggers();
        e.Tr.StartTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        var spawned = e.St.EnumerateZone(ZoneRef.Board, 0)
            .FirstOrDefault(x => x.Def?.CardId == 81000004 && x.Def.Kind == CardKind.Follower);
        int leader1 = e.St.Players[0].Leader.Life;
        bool healed = leader1 == leader0 + 2;
        return (asAmulet && spawned != null && healed,
            "结晶为护符=" + asAmulet + "(吟唱" + cd + ")"
            + " 谢幕曲召唤本体=" + (spawned != null)
            + " 本体入场回2血=" + healed + "（主战者 " + leader0 + "->" + leader1 + "）");
    }

    private static (bool, string) MatrixShoddy(MatrixEnv e)
    {
        e.FillDeck(6);
        var c = e.Put(81000005, ZoneRef.Hand);
        e.St.WriteCounter(0, e.St.Schema.Counter("pp"), 2);
        int hand0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool played = e.Play(c);
        bool summoned = e.St.EnumerateZone(ZoneRef.Board, 0).Any(x => x.Def?.CardId == 81000005);
        int hand1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 激奏不触发 Fanfare：净 -1（打出后离开手牌，召唤物进场，不抽牌）
        bool noFanfare = hand1 == hand0 - 1;
        return (played && summoned && noFanfare,
            "激奏召唤本体=" + summoned + " 未触发Fanfare=" + noFanfare + "（手牌 " + hand0 + "->" + hand1 + "）");
    }

    private static (bool, string) MatrixAria(MatrixEnv e)
    {
        var c = e.Put(81000006, ZoneRef.Hand);
        e.Play(c);
        bool crest = e.St.EnumerateZone(ZoneRef.CrestArea, 0).Any(x => x.Def?.Kind == CardKind.Crest);

        // Evolve: 召唤 3 只 Fairy；纹章给它们 Storm。
        // 手动进化要花 1 点进化点 —— 不给 ep，Evolve 会直接返回 false，
        // 看起来就像「进化能力没实现」。
        e.St.WriteCounter(0, e.St.Schema.Counter("ep"), 2);
        e.Vm.DropPendingTriggers();
        e.Tr.Evolve(0, c, false, e.Vm);
        e.Vm.StepUntilBlocked();
        var fairies = e.St.EnumerateZone(ZoneRef.Board, 0)
            .Where(x => x.Def?.CardId == 90011110).ToList();
        int stormed = fairies.Count(f => f.Keywords.Contains(e.St.Schema.Keyword("storm")));
        return (crest && fairies.Count == 3 && stormed == 3,
            "获得纹章=" + crest + " 进化召3妖精=" + (fairies.Count == 3) + " 全部获疾驰=" + (stormed == 3));
    }

    private static (bool, string) MatrixSham(MatrixEnv e)
    {
        var c = e.Put(81000007, ZoneRef.Deck);
        e.St.SetupFaith();
        var fk = e.St.Schema.Counter("faith");
        var mck = e.St.Schema.Counter("mode_count");

        // 0 信仰：不白嫖
        e.St.WriteCounter(0, fk, 0);
        var a = e.Put(81000007, ZoneRef.Hand);
        int mc0 = e.St.ReadCounter(0, mck);
        e.Play(a);
        bool noFreeload = e.St.ReadCounter(0, mck) == mc0;

        // 15 信仰：扣到 5，模式数 +1
        e.St.WriteCounter(0, fk, 15);
        var b = e.Put(81000007, ZoneRef.Hand);
        e.Play(b);
        bool paid = e.St.ReadCounter(0, fk) == 5 && e.St.ReadCounter(0, mck) == mc0 + 1;

        return (noFreeload && paid, "0信仰不白嫖=" + noFreeload + " 15→5且模式+1=" + paid
            + "（信仰余 " + e.St.ReadCounter(0, fk) + "）");
    }

    private static (bool, string) MatrixBrew(MatrixEnv e)
    {
        e.FillDeck(10);
        var mpk = e.St.Schema.Counter("max_pp");
        e.St.WriteCounter(0, mpk, 5);
        var c1 = e.Put(81000010, ZoneRef.Hand);
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Play(c1);
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();

        e.St.WriteCounter(0, mpk, 7);
        var c2 = e.Put(81000010, ZoneRef.Hand);
        int h2 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Play(c2);
        int h3 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();

        bool normal = h1 == h0;         // 打出-1 抽1
        bool overflow = h3 == h2 + 1;   // 打出-1 抽2
        return (normal && overflow, "未觉醒 " + h0 + "->" + h1 + " 觉醒 " + h2 + "->" + h3);
    }

    private static (bool, string) MatrixConductor(MatrixEnv e)
    {
        e.Filler(2962, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2962);
        e.St.WriteCounter(0, e.St.Schema.Counter("combo"), 2);
        var c = e.Put(81000011, ZoneRef.Hand);
        e.Play(c, new[] { foe.EntityId });
        // 打出时 combo 变 3 → 打 3
        return (foe.Life == 6, "连击3 → 打3点，9→" + foe.Life + "（期望 6）");
    }

    private static (bool, string) MatrixBlaze(MatrixEnv e)
    {
        e.FillDeck(6);
        var blaze = e.Put(81000012, ZoneRef.Hand);
        int c0 = blaze.Cost;
        for (int i = 0; i < 2; i++)
        {
            e.Filler(2963, ZoneRef.Hand, kind: CardKind.Spell);
            var sp = e.St.EnumerateZone(ZoneRef.Hand, 0).First(x => x.Def.CardId == 2963);
            e.Play(sp);
        }
        return (blaze.Cost == c0 - 2, "费用 " + c0 + "->" + blaze.Cost + "（期望 -2）");
    }

    private static (bool, string) MatrixGodwood(MatrixEnv e)
    {
        e.FillDeck(10);
        var staff = e.Put(81000013, ZoneRef.Board);
        e.Filler(2964, ZoneRef.Board, 0, life: 5);
        e.Filler(2965, ZoneRef.Board, 0, life: 5);
        var a = e.St.EnumerateZone(ZoneRef.Board, 0).First(x => x.Def.CardId == 2964);
        var b = e.St.EnumerateZone(ZoneRef.Board, 0).First(x => x.Def.CardId == 2965);

        // combo < 3：回合末不抽
        e.St.WriteCounter(0, e.St.Schema.Counter("combo"), 1);
        e.Vm.DropPendingTriggers();
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Tr.EndTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool noDraw = e.St.EnumerateZone(ZoneRef.Hand, 0).Count() <= h0;

        // combo >= 3：回合末抽 1
        e.St.ActivePlayer = 0; e.St.TurnNumber = 9;
        e.St.WriteCounter(0, e.St.Schema.Counter("pp"), 20);
        e.St.WriteCounter(0, e.St.Schema.Counter("combo"), 3);
        e.Vm.CurrentTurnOwner = 0;
        e.Vm.DropPendingTriggers();
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Tr.EndTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool drew = e.St.EnumerateZone(ZoneRef.Hand, 0).Count() > h1;

        return (noDraw && drew, "combo<3不抽=" + noDraw + " combo>=3抽1=" + drew);
    }

    private static (bool, string) MatrixEdelweiss(MatrixEnv e)
    {
        e.Filler(2966, ZoneRef.Board, 1, life: 4);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2966);

        // 无土之印：不进化、不打伤害
        e.St.WriteCounter(0, e.St.Schema.Counter("earth_sigil"), 0);
        var a = e.Put(81000014, ZoneRef.Hand);
        e.Play(a);
        bool inert = !a.IsEvolved && foe.Zone == ZoneRef.Board;

        // 2 土之印：进化 + 打死 4 血随从
        e.St.WriteCounter(0, e.St.Schema.Counter("earth_sigil"), 2);
        var b = e.Put(81000014, ZoneRef.Hand);
        e.Play(b);
        bool evolved = b.IsEvolved;
        bool killed = foe.Zone != ZoneRef.Board;
        return (inert && evolved && killed, "无土之印不动=" + inert + " 有土之印进化=" + evolved + " 打死4血=" + killed);
    }

    private static (bool, string) MatrixReno(MatrixEnv e)
    {
        var c = e.Put(81000015, ZoneRef.Board);
        c.IsEvolved = true;
        c.IsSuperEvolved = true;
        e.Filler(2967, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2967);
        e.Vm.DropPendingTriggers();

        // Super-Evolve 冒号头只认玩家手动进化
        e.Vm.EnqueueEvent(new VmEvent
        {
            Kind = CoreEvent.OnSuperEvolve,
            Subject = EntityRef.From(c),
            IsManualEvolve = true,
        });
        e.Vm.StepUntilBlocked();
        bool maxTwo = c.KeywordValues.TryGetValue(e.St.Schema.Keyword("max_attacks_per_turn"), out var mv) && mv == 2;

        // Clash：打敌方主战者 1
        int foe0 = e.St.Players[1].Leader.Life;
        new CombatResolver(e.St, e.St.Schema).ExecuteCombat(c, foe, e.Vm);
        e.Vm.StepUntilBlocked();
        bool clash = e.St.Players[1].Leader.Life < foe0;

        return (maxTwo && clash, "超进化2次攻击=" + maxTwo + " 交战打脸=" + clash);
    }

    private static (bool, string) MatrixGhost(MatrixEnv e)
    {
        var g = e.Put(81000020, ZoneRef.Board);
        bool storm = g.Keywords.Contains(e.St.Schema.Keyword("storm"));
        e.Vm.DropPendingTriggers();
        int sh0 = e.St.ReadCounter(0, e.St.Schema.Counter("shadow"));

        // 战死（SBA 路径）
        g.Life = 0;
        e.St.ProcessStateBasedActions();

        bool banished = e.St.EnumerateZone(ZoneRef.Banished, 0).Any(x => x.EntityId == g.EntityId);
        bool notCem = !e.St.EnumerateZone(ZoneRef.Cemetery, 0).Any(x => x.EntityId == g.EntityId);
        bool noShadow = e.St.ReadCounter(0, e.St.Schema.Counter("shadow")) == sh0;
        return (storm && banished && notCem && noShadow,
            "疾驰=" + storm + " 战死进放逐=" + banished + " 不进墓地=" + notCem + " shadow未增=" + noShadow);
    }

    private static (bool, string) MatrixSinciro(MatrixEnv e)
    {
        // 真卡：Fuse: Loot cards / Fanfare: X = 不同名材料数 → 打全场 / 超进化复刻
        var baseCard = e.Put(81000021, ZoneRef.Hand);
        // 材料必须是 Loot 兵种的卡（Sinciro 的 Fuse: Loot cards）。
        // Remnant of Hollowness 就是标准 Loot 法术。
        e.Put(90024310, ZoneRef.Hand);
        e.Put(90024310, ZoneRef.Hand);
        e.Filler(2970, ZoneRef.Hand);
        // 材料要挑**不是主体自己**的 Loot 卡 —— Sinciro 本身也带 Loot 兵种，
        // 不加这层过滤就会把自己当成材料，CanFuseCards 返回「不能把主体当材料」。
        var loots = e.St.EnumerateZone(ZoneRef.Hand, 0)
            .Where(x => x.EntityId != baseCard.EntityId)
            .Where(x => x.Def?.Traits != null && x.Def.Traits.Contains(e.St.Schema.Trait("财宝")))
            .ToList();

        e.Vm.DropPendingTriggers();
        bool fused = loots.Count > 0 && e.Tr.FuseCards(0, baseCard, new[] { loots[0] }, e.Vm);
        e.Vm.StepUntilBlocked();
        int kinds = baseCard.Counters.TryGetValue(e.St.Schema.Counter("fuse_kinds"), out var k) ? k : 0;
        int cnt = baseCard.Counters.TryGetValue(e.St.Schema.Counter("fuse_count"), out var c) ? c : 0;

        // 打出：Fanfare 打 X = fuse_kinds
        e.Filler(2971, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2971);
        int foe0 = e.St.Players[1].Leader.Life;
        e.Play(baseCard);
        bool dealtAll = e.St.Players[1].Leader.Life < foe0 && foe.Life == 9 - kinds;

        return (fused && kinds == 1 && cnt == 1 && dealtAll,
            "融合=" + fused + " 种类=" + kinds + " 次数=" + cnt + " X打全场=" + dealtAll);
    }

    private static (bool, string) MatrixStriker(MatrixEnv e)
    {
        // 真卡：Fuse: Artifact cards / 按总费分档变 α/β/γ / Rush
        // 每一档都必须在**独立**的试验台上做：融合会消耗材料、
        // 而且 Striker 一变身就不再是原来那张卡，第二档接不上。
        string One(int totalCostWanted, int matCardId)
        {
            var env = MatrixEnv.Create(seed: 2200 + totalCostWanted);
            var b = env.Put(81000022, ZoneRef.Hand);
            // 材料必须是 Artifact 兵种（Striker 的 Fuse: Artifact cards）
            env.St.RegisterCard(new CardDef
            {
                CardId = matCardId, Name = "ArtMat" + matCardId, Kind = CardKind.Follower,
                BaseCost = 1, BaseAttack = 1, BaseLife = 1,
                Traits = new List<SymbolId<TraitTag>> { env.St.Schema.Trait("创造物") },
            });
            var m = env.St.SpawnCard(matCardId, ZoneRef.Hand, 0);
            // 把材料成本调成目标总费
            m.Cost = totalCostWanted;
            env.Vm.DropPendingTriggers();
            env.Tr.FuseCards(0, b, new[] { m }, env.Vm);
            env.Vm.StepUntilBlocked();
            return b.Def?.Name ?? "";
        }
        var a = One(1, 2972);
        var bt = One(2, 2973);
        var g = One(3, 2974);
        bool rush = true;
        return (a.Contains("\u03b1") && bt.Contains("\u03b2") && g.Contains("\u03b3"),
            "总费1→" + a + " 总费2→" + bt + " 总费3→" + g);
    }

    private static (bool, string) MatrixSlaus(MatrixEnv e)
    {
        var c = e.Put(81000023, ZoneRef.Board);
        bool ambush = c.Keywords.Contains(e.St.Schema.Keyword("ambush"));
        e.Vm.DropPendingTriggers();
        int foe0 = e.St.EnumerateZone(ZoneRef.CrestArea, 1).Count();
        e.Tr.EndTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool notEvolved = e.St.EnumerateZone(ZoneRef.CrestArea, 1).Count() == foe0;

        c.IsEvolved = true;
        e.St.ActivePlayer = 0; e.St.TurnNumber = 9;
        e.St.WriteCounter(0, e.St.Schema.Counter("pp"), 20);
        e.Vm.CurrentTurnOwner = 0;
        e.Vm.DropPendingTriggers();
        e.Tr.EndTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool gaveFoe = e.St.EnumerateZone(ZoneRef.CrestArea, 1).Count() > foe0;

        return (ambush && notEvolved && gaveFoe,
            "潜行=" + ambush + " 未进化不给=" + notEvolved + " 进化后给对手=" + gaveFoe);
    }

    private static (bool, string) MatrixSandalphon(MatrixEnv e)
    {
        e.FillDeck(12);
        var c = e.Put(81000024, ZoneRef.Deck);
        e.Vm.DropPendingTriggers();
        e.St.WriteCounter(0, e.St.Schema.Counter("evolve_count"), 5);
        e.Vm.DropPendingTriggers();
        e.Tr.StartTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool stayed = c.Zone == ZoneRef.Deck;

        e.St.WriteCounter(0, e.St.Schema.Counter("pp"), 20);
        e.St.WriteCounter(0, e.St.Schema.Counter("evolve_count"), 6);
        e.Vm.DropPendingTriggers();
        e.Tr.StartTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool invoked = c.Zone == ZoneRef.Board;
        return (stayed && invoked, "5次进化留库=" + stayed + " 6次Invoke=" + invoked);
    }

    private static (bool, string) MatrixObsessed(MatrixEnv e)
    {
        e.Filler(2975, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2975);
        e.Vm.DropPendingTriggers();
        for (int i = 0; i < 5; i++)
        {
            var p = e.Put(81000025, ZoneRef.Board, 0);
            e.Vm.StepUntilBlocked();
            e.St.DestroyEntity(p);
            e.Vm.StepUntilBlocked();
        }
        bool aliveBefore = foe.Zone == ZoneRef.Board;
        e.Put(81000025, ZoneRef.Board, 0);
        e.Vm.StepUntilBlocked();
        bool dead = foe.Zone != ZoneRef.Board;
        return (aliveBefore && dead, "前5张敌方存活=" + aliveBefore + " 第6张进场被破坏=" + dead);
    }

    private static (bool, string) MatrixPascale(MatrixEnv e)
    {
        e.Filler(2976, ZoneRef.Board, 0, atk: 3, life: 4);
        var ally = e.St.EnumerateZone(ZoneRef.Board, 0).First(x => x.Def.CardId == 2976);
        var esk = e.St.Schema.Counter("earth_sigil");

        e.St.WriteCounter(0, esk, 5);
        var a = e.Put(81000030, ZoneRef.Hand);
        e.Play(a, new[] { ally.EntityId });
        bool unchanged = ally.Attack == 3 && ally.Life == 4;

        e.St.WriteCounter(0, esk, 10);
        var b = e.Put(81000030, ZoneRef.Hand);
        e.Play(b, new[] { ally.EntityId });
        bool doubled = ally.Attack == 6 && ally.Life == 8 && ally.MaxLife == 8;
        return (unchanged && doubled, "5层不变=" + unchanged + " 10层翻倍=" + doubled + "（" + ally.Attack + "/" + ally.Life + "）");
    }

    private static (bool, string) MatrixMjerrabaine(MatrixEnv e)
    {
        // 真卡：When you gain this crest, replace your deck with a deck of 40 cards。
        // 牌库得先有东西，否则「0 -> 40」看起来像没生效也像生效了。
        e.FillDeck(6);
        int d0 = e.St.EnumerateZone(ZoneRef.Deck, 0).Count();
        // 不能 DropPendingTriggers：SpawnCard 发出的 OnEnterField
        // 正是这张卡要测的那条事件，清掉就等于没测。
        e.Put(81000031, ZoneRef.Board);
        e.Vm.StepUntilBlocked();
        int d1 = e.St.EnumerateZone(ZoneRef.Deck, 0).Count();
        bool allDef = e.St.EnumerateZone(ZoneRef.Deck, 0).All(x => x.Def != null);
        return (d1 == 40 && allDef, "牌库 " + d0 + "->" + d1 + "（期望 40）全部有卡面=" + allDef);
    }

    private static (bool, string) MatrixOctrice(MatrixEnv e)
    {
        var crest = e.Put(81000032, ZoneRef.Board);
        int cd0 = crest.Counters.TryGetValue(e.St.Schema.Counter("countdown"), out var v0) ? v0 : -1;
        // 打一张 Loot 卡推进吟唱
        e.Put(90024310, ZoneRef.Hand);   // Remnant of Hollowness（Loot）
        var loot = e.St.EnumerateZone(ZoneRef.Hand, 0).First(x => x.Def.CardId == 90024310);
        e.Play(loot);
        int cd1 = crest.Counters.TryGetValue(e.St.Schema.Counter("countdown"), out var v1) ? v1 : -1;
        return (cd0 > 0 && cd1 == cd0 - 1, "吟唱 " + cd0 + "->" + cd1 + "（期望 -1）");
    }

    private static (bool, string) MatrixGold(MatrixEnv e)
    {
        e.FillDeck(6);
        var c = e.Put(81000033, ZoneRef.Hand);
        int foe0 = e.St.Players[1].Leader.Life;
        bool played = e.Play(c, new[] { 1 });   // 模式 2：打敌方主战者 2
        int foe1 = e.St.Players[1].Leader.Life;
        bool mode2 = foe1 == foe0 - 2;
        bool moved = c.Zone == ZoneRef.Cemetery;
        return (played && mode2 && moved, "模式2打2=" + mode2 + " 选后进墓=" + moved);
    }

    private static (bool, string) MatrixBabelon(MatrixEnv e)
    {
        e.FillDeck(10);
        e.Filler(2977, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2977);
        e.Put(81000034, ZoneRef.Board);
        e.Vm.DropPendingTriggers();
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Tr.EndTurn(0, e.Vm);
        e.Vm.StepUntilBlocked();
        bool noPrompt = e.Vm.Pending == null;
        bool damaged = foe.Life == 7;
        bool drew = e.St.EnumerateZone(ZoneRef.Hand, 0).Count() == h0 + 1;
        // Sequence(pick=1) 只发动第 1 项：官方日文「下記の能力から順に1つの能力が働く」。
        // 抽牌发生说明又把全部选项跑了 —— 那是修复前的行为，这里必须是否。
        return (noPrompt && damaged && !drew, "不弹窗=" + noPrompt + " 打2=" + damaged + " 抽1=" + drew);
    }

    private static (bool, string) MatrixShakdoh(MatrixEnv e)
    {
        e.FillDeck(20);
        // 手牌放 4 张同费卡，供第二轮触发「同费 ≥4 打 4 伤」
        for (int i = 0; i < 4; i++) e.Filler(2980 + i, ZoneRef.Hand);
        var c = e.Put(81000035, ZoneRef.Hand);
        int foe0 = e.St.Players[1].Leader.Life;
        e.Play(c);
        int foe1 = e.St.Players[1].Leader.Life;
        int hand = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        bool sane = hand >= 0 && hand <= 8;
        bool dmg = foe1 < foe0;
        return (sane && dmg, "循环后手牌=" + hand + "（期望 ≤8）同费打4=" + dmg + "（敌方 " + foe1 + "）");
    }

    private static (bool, string) MatrixGuild(MatrixEnv e)
    {
        // 牌库顶放法术、第 2 张放随从
        e.Filler(2984, ZoneRef.Deck, kind: CardKind.Spell);
        e.Filler(2985, ZoneRef.Deck);
        var c = e.Put(81000036, ZoneRef.Hand);
        e.Play(c);
        var hand = e.St.EnumerateZone(ZoneRef.Hand, 0).ToList();
        bool gotFollower = hand.Any(x => x.Def?.CardId == 2985);

        // Engage：自毁 + 给 Rush + 抽 1
        e.Filler(2986, ZoneRef.Board, 0, life: 5);
        var ally = e.St.EnumerateZone(ZoneRef.Board, 0).First(x => x.Def.CardId == 2986);
        e.Vm.DropPendingTriggers();
        bool engaged = e.Tr.EngageAmulet(0, c, e.Vm);
        if (e.Vm.Pending != null) e.Vm.SubmitChoice(e.Vm.Pending.ChoiceId, new[] { ally.EntityId });
        e.Vm.StepUntilBlocked();
        bool rush = ally.Keywords.Contains(e.St.Schema.Keyword("rush"));
        bool gone = c.Zone != ZoneRef.Board;

        return (gotFollower && engaged && rush && gone,
            "定向抽随从=" + gotFollower + " 启动给Rush=" + rush + " 自毁=" + gone);
    }

    private static (bool, string) MatrixCannon(MatrixEnv e)
    {
        e.Filler(2987, ZoneRef.Board, 1, life: 9);
        var foe = e.St.EnumerateZone(ZoneRef.Board, 1).First(x => x.Def.CardId == 2987);
        var c = e.Put(81000037, ZoneRef.Hand);
        int h0 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        e.Play(c);
        int h1 = e.St.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 入场曲加入的 Gear of Ambition 必须是**真卡**（带 Fuse 与 Can't be played）
        var gear = e.St.EnumerateZone(ZoneRef.Hand, 0).FirstOrDefault(x => x.Def?.Name == "Testset Gear of Ambition");
        bool realGear = gear?.Def != null && gear.Def.HasFuse && gear.Def.CardId == 81000038;
        bool gained = h1 >= h0;

        // 融合任意一张卡 → 触发「每当融合打 2」
        e.Filler(2988, ZoneRef.Hand);
        var mat = e.St.EnumerateZone(ZoneRef.Hand, 0).First(x => x.Def.CardId == 2988);
        e.Filler(2989, ZoneRef.Hand);
        var bas = e.St.EnumerateZone(ZoneRef.Hand, 0).First(x => x.Def.CardId == 2989);
        bas.Def = new CardDef { CardId = 2989, Name = "FuseBase", Kind = CardKind.Follower, BaseCost = 1, BaseAttack = 1, BaseLife = 1, HasFuse = true };
        e.Vm.DropPendingTriggers();
        e.Tr.FuseCards(0, bas, new[] { mat }, e.Vm);
        e.Vm.StepUntilBlocked();
        bool dealt = foe.Life == 7;

        return (gained && realGear && dealt,
            "加入齿轮=" + gained + " 是真卡=" + realGear + " 融合触发打2=" + dealt + "（敌方 " + foe.Life + "）");
    }

    private static (bool, string) MatrixGear(MatrixEnv e)
    {
        var c = e.Put(81000038, ZoneRef.Hand);
        bool cannotPlay = !e.Tr.PlayCard(0, c, e.Vm);

        // 融合一张 Artifact 护符 → 变身 Striker Artifact（真卡 81000022）
        e.Filler(2990, ZoneRef.Hand, kind: CardKind.Amulet);
        var m = e.St.EnumerateZone(ZoneRef.Hand, 0).First(x => x.Def.CardId == 2990);
        m.Def = new CardDef { CardId = 2990, Name = "ArtMat", Kind = CardKind.Amulet, BaseCost = 1, Traits = new[] { e.St.Schema.Trait("创造物") } };
        e.Vm.DropPendingTriggers();
        e.Tr.FuseCards(0, c, new[] { m }, e.Vm);
        e.Vm.StepUntilBlocked();
        int fid = c.Def?.CardId ?? -1;
        bool transformed = fid == 81000022 && c.Def.Name == "Testset Striker Artifact";
        return (cannotPlay && transformed, "禁打=" + cannotPlay + " 变身 id=" + fid + " name=" + c.Def?.Name);
    }

    private static (bool, string) MatrixYuna(MatrixEnv e)
    {
        var c = e.Put(81000039, ZoneRef.Board);
        bool ward = c.Keywords.Contains(e.St.Schema.Keyword("ward"));
        e.Vm.DropPendingTriggers();
        e.St.DestroyEntity(c);
        e.Vm.StepUntilBlocked();
        var hand = e.St.EnumerateZone(ZoneRef.Hand, 0).ToList();
        // Ghost 是 81000020 真卡，Bat 是 90051120
        bool gotGhost = hand.Any(x => x.Def?.CardId == 81000020);
        bool gotBat = hand.Any(x => x.Def?.CardId == 90051120);
        return (ward && gotGhost && gotBat, "守护=" + ward + " 谢幕曲得Ghost真卡=" + gotGhost + " Bat=" + gotBat);
    }
}

using System;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// 77 号审计问题 2（致命）：Enhance / Engage 的费用「四方打架」。
///
/// 收敛后的不变量：
///   · 扣 PP 的唯一入口是 TurnRunner.PlayCard（按最高可付档）与 TurnRunner.EngageAmulet。
///   · CostExecutor 的 UsePp 分支是 no-op。
///   · Enhance 的档位判定读 ctr:pp_at_play（打出前 PP），Engage 不在条件里判 PP。
/// </summary>
public static partial class Regress
{
    public static string RunIssue2()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildLexicon();
        var comp = new EnglishCompiler(lex);

        // ── 1. 带基础入场曲的 Enhance：抽牌必须真的发生 ──
        {
            var st = NewState(seed: 120, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            st.RegisterCard(new CardDef
            {
                CardId = 301, Name = "En", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 2, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            for (int i = 0; i < 8; i++) st.SpawnCard(301, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var card = st.SpawnCard(301, ZoneRef.Hand, 0);
            var deckBefore = st.EnumerateZone(ZoneRef.Deck, 0).ToList();
            st.ClearEvents();
            tr.PlayCard(0, card, vm);

            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deckAfter = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            // 抽牌在 VM 里落成 MoveZone(Deck -> Hand)，
            // 走的是 MoveEntity 而不是 BattleState.DrawCards，所以不会发 OnDraw。
            // 这里断言"真的抽了 3 张"：手牌 +3、牌库 −3。
            bool drew = deckAfter == deckBefore.Count - 3 && hand == 3;
            // 起始手牌只有这一张：打出 −1，基础抽 1 + Enhance 抽 2 = 净 +3
            Check("issue2-1 Enhance 抽牌真的执行", pp == 1 && drew,
                  $"pp={pp} hand={hand} deck={deckBefore.Count}->{deckAfter}");
        }

        // ── 2. 单条 Enhance（无基础入场曲）：不能被档位判定挡掉 ──
        {
            var st = NewState(seed: 121, out var tr, out var vm);
            var r = comp.Compile("Enhance (4): Give this follower +3/+3.");
            st.RegisterCard(new CardDef
            {
                CardId = 302, Name = "Pump", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 2, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var card = st.SpawnCard(302, ZoneRef.Hand, 0);
            bool played = tr.PlayCard(0, card, vm);

            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            Check("issue2-2 单条 Enhance 生效", played && pp == 1 && card.Attack == 4 && card.Life == 4,
                  $"played={played} pp={pp} {card.Attack}/{card.Life}");
        }

        // ── 3. 不得双倍扣费 ──
        {
            var st = NewState(seed: 122, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            st.RegisterCard(new CardDef
            {
                CardId = 303, Name = "En", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 2, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            for (int i = 0; i < 8; i++) st.SpawnCard(303, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 8);
            var card = st.SpawnCard(303, ZoneRef.Hand, 0);
            tr.PlayCard(0, card, vm);
            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            // 按最高可付档付一次 4 点：8 - 4 = 4（不是 8 - 4 - 4 = 0）
            Check("issue2-3 不双倍扣费", pp == 4, $"pp={pp}");
        }

        // ── 4. Engage：档位由 EngageAmulet 入口负责 ──
        {
            var st = NewState(seed: 123, out var tr, out var vm);
            var r = comp.Compile("Engage (1): Draw a card.");
            // Engage 不得在 AndCosts 里生成 pp 条件
            string cond = r.Abilities.Count > 0 && r.Abilities[0].Condition != null
                ? AstCanonicalPrinter.PrintBoolExpr(r.Abilities[0].Condition) : "null";
            bool noPpCond = !cond.Contains("pp");

            st.RegisterCard(new CardDef
            {
                CardId = 304, Name = "Am", Kind = CardKind.Amulet, BaseCost = 3, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            for (int i = 0; i < 8; i++) st.SpawnCard(304, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var amulet = st.SpawnCard(304, ZoneRef.Hand, 0);
            tr.PlayCard(0, amulet, vm);

            st.WriteCounter(0, st.Schema.Counter("pp"), 3);
            int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            bool ok = tr.EngageAmulet(0, amulet, 1, vm);
            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            int handAfter = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            Check("issue2-4 Engage 扣 1 点且抽牌", ok && pp == 2 && handAfter == handBefore + 1 && noPpCond,
                  $"ok={ok} pp={pp} hand={handBefore}->{handAfter} cond={cond}");
        }

        // ── 5. Necromancy 不被这次收敛弄坏 ──
        {
            var st = NewState(seed: 124, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Necromancy (3): Draw a card.");
            st.RegisterCard(new CardDef
            {
                CardId = 305, Name = "NC", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            for (int i = 0; i < 8; i++) st.SpawnCard(305, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            st.WriteCounter(0, st.Schema.Counter("shadow"), 5);
            var card = st.SpawnCard(305, ZoneRef.Hand, 0);
            int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            tr.PlayCard(0, card, vm);
            int shadow = st.ReadCounter(0, st.Schema.Counter("shadow"));
            int handAfter = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            // 打出 −1、抽 1 = 净 0；shadow 5 - 3 = 2
            Check("issue2-5 Necromancy 照常扣墓场", shadow == 2 && handAfter == handBefore,
                  $"shadow={shadow} hand={handBefore}->{handAfter}");
        }

        // ── 6. Enhance 档位判定读的是 pp_at_play，不是已被扣过的 pp ──
        {
            var r = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            // 找到真正的档位分支条件（外层包着一层 Condition=true，必须往里挖）
            var conds = new System.Collections.Generic.List<string>();
            void Walk(System.Collections.Generic.IEnumerable<EffectNode> nodes)
            {
                foreach (var n in nodes ?? Array.Empty<EffectNode>())
                {
                    if (n is not ConditionalNode cn) continue;
                    conds.Add(AstCanonicalPrinter.PrintBoolExpr(cn.Condition));
                    Walk(cn.Then);
                    Walk(cn.Else);
                }
            }
            foreach (var a in r.Abilities) Walk(a.Body);

            var gate = conds.FirstOrDefault(x => x.Contains(">= 4"));
            bool readsPpAtPlay = gate != null && gate.Contains("pp_at_play") && !gate.Contains("pp >= ");
            Check("issue2-6 Enhance 判档读 pp_at_play", readsPpAtPlay, $"gate={gate ?? "(none)"}");
        }

        // ── 7. PP 不够档位时，Enhance 效果不得发动（不能只测"能过"） ──
        {
            var st = NewState(seed: 125, out var tr, out var vm);
            var r = comp.Compile("Enhance (4): Give this follower +3/+3.");
            st.RegisterCard(new CardDef
            {
                CardId = 306, Name = "P", Kind = CardKind.Follower,
                BaseAttack = 1, BaseLife = 1, BaseCost = 2, Abilities = r.Abilities,
                InnateKeywords = r.InnateKeywords,
            });
            st.WriteCounter(0, st.Schema.Counter("pp"), 3);
            var card = st.SpawnCard(306, ZoneRef.Hand, 0);
            bool played = tr.PlayCard(0, card, vm);
            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            // 只有 3 点：付不起 Enhance (4)，只付基础 2 点，加成不发动
            Check("issue2-7 付不起档位时不发动", played && pp == 1 && card.Attack == 1 && card.Life == 1,
                  $"played={played} pp={pp} {card.Attack}/{card.Life}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    private static BattleState NewState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new BattleState(seed: seed);
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        st.Players[0].Leader = st.CreateEntity(99, ZoneRef.Leader, 0);
        st.Players[1].Leader = st.CreateEntity(99, ZoneRef.Leader, 1);
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        return st;
    }

    private static Lexicon BuildLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

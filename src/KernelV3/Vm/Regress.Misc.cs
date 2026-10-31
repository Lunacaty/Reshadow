using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 杂项回归：爆牌事件 / 满场召唤的卡去哪了 / 返回牌组要洗牌
//
// 三处缺口都不在「效果写错了」，而在**共用回路**上，所以每条都先钉现状：
//
//   M1 爆牌仍发 OnDraw
//      文档 1.2：「手牌已有 9 张时，抽到的第 10 张牌直接爆牌送进墓场，
//      且墓场数 +1，但绝对不触发【谢幕曲】，不触发弃牌时 OnDiscard」。
//      76-Gemini 第五章动作 4 补一句「不触发 OnDraw」——
//      爆掉的那张没进手牌，它就不是「抽到的牌」。
//      BattleState / MemoryWorld 两条实现都必须在爆牌那次 continue。
//
//   M2 满场召唤让「从牌组出场」的卡永久消失
//      文档 1.2：满场时新召唤的随从「直接凭空消失」。
//      但 MoveEntity 先 Detach 再 PlaceInZone，满场判定落在 Detach 之后，
//      于是 PlaceInZone 只能把卡置 Zone=None —— 它已经从牌库被摘出来了。
//      6.2-8：Invoke 满场时**不发动**，即留在牌库。
//      判别点取「移动来源区域」：牌库 → 战场被拦下时原地不动，
//      其它来源（无来源 token / 手牌 / 墓地）仍然凭空消失。
//
//   M3 返回牌组不洗牌
//      文档第五章动作 6：「从手牌或战场移回牌组 Deck 并洗牌」，
//      「状态保留铁律（与回手相反）」。洗牌与状态保留必须同时成立。
// ============================================================================
public static partial class Regress
{
    public static string RunMisc()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildMiscLexicon();
        var comp = new EnglishCompiler(lex);

        // ── M1-1 爆牌：进墓地、shadow+1、不发 OnDraw ──
        {
            var st = MiscState(seed: 600);
            st.RegisterCard(MiscCard(901, "Ovr"));
            for (int i = 0; i < 9; i++) st.SpawnCard(901, ZoneRef.Hand, 0);
            var tenth = st.SpawnCard(901, ZoneRef.Deck, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            st.ClearEvents();

            st.DrawCards(0, 1);

            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int onDiscard = st.EventLog.Count(e => e.Kind == CoreEvent.OnDiscard);
            int onDestroy = st.EventLog.Count(e => e.Kind == CoreEvent.OnDestroy);
            bool buried = tenth.Zone == ZoneRef.Cemetery;
            bool shadowUp = st.ReadCounter(0, st.Schema.Counter("shadow")) == s0 + 1;
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();

            Chk("misc-1 爆牌进墓地 + shadow+1 且不发 OnDraw / OnDiscard / 谢幕曲",
                buried && shadowUp && hand == 9 && onDraw == 0 && onDiscard == 0 && onDestroy == 0,
                $"zone={tenth.Zone} shadow={shadowUp} hand={hand} onDraw={onDraw} " +
                $"onDiscard={onDiscard} onDestroy={onDestroy}");
        }

        // ── M1-2 正常抽牌不退化：每张都发 OnDraw ──
        {
            var st = MiscState(seed: 601);
            st.RegisterCard(MiscCard(901, "Ovr"));
            st.SpawnCard(901, ZoneRef.Deck, 0);
            st.SpawnCard(901, ZoneRef.Deck, 0);
            st.ClearEvents();

            st.DrawCards(0, 2);

            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            Chk("misc-2 正常抽牌仍逐张发 OnDraw", onDraw == 2 && hand == 2,
                $"onDraw={onDraw} hand={hand}");
        }

        // ── M1-3 手牌 8 张时抽 2：第 1 张进手牌并发 OnDraw，第 2 张爆掉不发 ──
        {
            var st = MiscState(seed: 602);
            st.RegisterCard(MiscCard(901, "Ovr"));
            for (int i = 0; i < 8; i++) st.SpawnCard(901, ZoneRef.Hand, 0);
            st.SpawnCard(901, ZoneRef.Deck, 0);
            st.SpawnCard(901, ZoneRef.Deck, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            st.ClearEvents();

            st.DrawCards(0, 2);

            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int cem = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();
            bool shadowUp = st.ReadCounter(0, st.Schema.Counter("shadow")) == s0 + 1;
            Chk("misc-3 抽 2 只爆 1 张：OnDraw 只有 1 条",
                onDraw == 1 && hand == 9 && cem == 1 && shadowUp,
                $"onDraw={onDraw} hand={hand} cemetery={cem} shadow+1={shadowUp}");
        }

        // ── M1-4 MemoryWorld 同语义（两条抽牌实现必须一致）──
        {
            var w = new MemoryWorld();
            w.RegisterCard(MiscCard(901, "Ovr"));
            for (int i = 0; i < 9; i++) w.Create(901, ZoneRef.Hand, 0);
            var tenth = w.Create(901, ZoneRef.Deck, 0);
            w.EventLog.Clear();

            w.DrawCards(0, 1);

            int onDrawBurned = w.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            bool buried = tenth.Zone == ZoneRef.Cemetery;
            bool shadowUp = w.ReadCounter(0, w.Schema.Counter("shadow")) == 1;

            var w2 = new MemoryWorld();
            w2.RegisterCard(MiscCard(901, "Ovr"));
            w2.Create(901, ZoneRef.Deck, 0);
            w2.Create(901, ZoneRef.Deck, 0);
            w2.EventLog.Clear();
            w2.DrawCards(0, 2);
            int onDrawNormal = w2.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);

            Chk("misc-4 MemoryWorld 爆牌不发 OnDraw、正常抽牌照发",
                buried && shadowUp && onDrawBurned == 0 && onDrawNormal == 2,
                $"zone={tenth.Zone} shadow={shadowUp} onDrawBurned={onDrawBurned} " +
                $"onDrawNormal={onDrawNormal}");
        }

        // ── M2-1 满场时「从牌组上场」的召唤被拦下，卡留在牌组 ──
        //
        // 这条走的是卡面能力的真实落点：Invoke / "move this card to the board"
        // 都编译成 MoveZone(Self → Board)，实际执行者是 CardVm.DoMoveZone →
        // IVmWorld.MoveEntity。之前它先 Detach 再撞满场，卡就 Zone=None 永久失踪。
        {
            var st = MiscState(seed: 610);
            st.RegisterCard(MiscCard(902, "Invoker"));
            for (int i = 0; i < 5; i++) st.SpawnCard(902, ZoneRef.Board, 0);
            var jumper = st.SpawnCard(902, ZoneRef.Deck, 0);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            int deckBefore = st.Players[0].Deck.Count;

            vm.PushBody(
                new EffectNode[] { new MoveZoneNode { Target = TargetSpec.Self, ToZone = ZoneRef.Board } },
                new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(jumper) });
            vm.StepUntilBlocked();

            bool inDeck = jumper.Zone == ZoneRef.Deck;
            bool boardFull = st.EnumerateZone(ZoneRef.Board, 0).Count() == 5;
            bool deckKept = st.Players[0].Deck.Count == deckBefore && st.Players[0].Deck.Contains(jumper);
            bool notInCemetery = !st.Players[0].Cemetery.Contains(jumper);

            Chk("misc-5 满场时从牌组上场的召唤留在牌组（不消失、不进墓地）",
                inDeck && boardFull && deckKept && notInCemetery,
                $"zone={jumper.Zone} board={st.EnumerateZone(ZoneRef.Board, 0).Count()} " +
                $"deck={deckBefore}->{st.Players[0].Deck.Count} cem={!notInCemetery}");
        }

        // ── M2-2 同样的路径在有空位时必须照常进场（拦截不得过宽）──
        {
            var st = MiscState(seed: 611);
            st.RegisterCard(MiscCard(902, "Invoker"));
            for (int i = 0; i < 4; i++) st.SpawnCard(902, ZoneRef.Board, 0);
            var jumper = st.SpawnCard(902, ZoneRef.Deck, 0);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;

            vm.PushBody(
                new EffectNode[] { new MoveZoneNode { Target = TargetSpec.Self, ToZone = ZoneRef.Board } },
                new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(jumper) });
            vm.StepUntilBlocked();

            bool onBoard = jumper.Zone == ZoneRef.Board;
            bool leftDeck = !st.Players[0].Deck.Contains(jumper);
            bool board5 = st.EnumerateZone(ZoneRef.Board, 0).Count() == 5;
            Chk("misc-6 满场不误伤：有空位时牌组召唤照常进场",
                onBoard && leftDeck && board5,
                $"zone={jumper.Zone} board={st.EnumerateZone(ZoneRef.Board, 0).Count()} " +
                $"stillInDeck={!leftDeck}");
        }

        // ── M2-3 无来源的凭空召唤在满场时仍然消失（不进墓地、shadow 不变）──
        {
            var st = MiscState(seed: 612);
            st.RegisterCard(MiscCard(903, "Token"));
            for (int i = 0; i < 5; i++) st.SpawnCard(903, ZoneRef.Board, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            int cem0 = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();

            var token = st.SpawnCard(903, ZoneRef.Board, 0);

            bool vanished = token.Zone == ZoneRef.None;
            bool noCemetery = st.EnumerateZone(ZoneRef.Cemetery, 0).Count() == cem0;
            bool shadowSame = st.ReadCounter(0, st.Schema.Counter("shadow")) == s0;
            bool board5 = st.EnumerateZone(ZoneRef.Board, 0).Count() == 5;
            bool notDestroyed = st.Players[0].DestroyedThisMatch.Count == 0;

            Chk("misc-7 满场的 token 召唤仍凭空消失（不进墓地 / shadow 不变 / 不进破坏池）",
                vanished && noCemetery && shadowSame && board5 && notDestroyed,
                $"zone={token.Zone} cem={cem0}->{st.EnumerateZone(ZoneRef.Cemetery, 0).Count()} " +
                $"shadowSame={shadowSame} board={st.EnumerateZone(ZoneRef.Board, 0).Count()} " +
                $"destroyed={st.Players[0].DestroyedThisMatch.Count}");
        }

        // ── M3-1 返回牌组后位置被打乱 ──
        //
        // 判据不写「末位不是它」：7 张洗牌后仍末位的概率是 1/7，单次会抖。
        // 这里用固定 seed 跑 40 局统计末位次数 —— 不洗牌时恒为 40，
        // 洗牌后期望约 5.7，阈值 20 留足抖动空间且仍咬得住缺陷。
        {
            const int trials = 40;
            int lastPos = 0, wrongCount = 0;
            for (int t = 0; t < trials; t++)
            {
                var st = MiscState(seed: 620 + t);
                st.RegisterCard(MiscCard(904, "Back"));
                for (int i = 0; i < 6; i++) st.SpawnCard(904, ZoneRef.Deck, 0);
                var back = st.SpawnCard(904, ZoneRef.Hand, 0);

                st.MoveEntity(back, ZoneRef.Deck, 0);

                var deck = st.Players[0].Deck;
                if (deck.Count != 7) wrongCount++;
                if (deck.Count > 0 && deck[deck.Count - 1] == back) lastPos++;
                if (back.Zone != ZoneRef.Deck) wrongCount++;
            }
            Chk("misc-8 返回牌组的卡被洗散（末位 != 固定）",
                lastPos <= 20 && wrongCount == 0,
                $"末位={lastPos}/{trials} 计数异常={wrongCount}");
        }

        // ── M3-2 返回牌组保留减费 / Buff / 魔力增幅（与回手相反）──
        {
            var st = MiscState(seed: 660);
            st.RegisterCard(MiscCard(905, "Cheap"));
            var card = st.SpawnCard(905, ZoneRef.Board, 0);
            card.Cost = 2;                        // 减费后的费用
            card.Attack = 9;                      // Buff 后的攻击力
            card.Life = 9;
            card.MaxLife = 9;
            var sb = st.Schema.Counter("spellboost");
            card.Counters[sb] = 3;                // 魔力增幅层数
            int serialBefore = card.EntrySerial;

            st.MoveEntity(card, ZoneRef.Deck, 0);

            int sbAfter = st.ReadEntityCounter(card, sb);
            bool kept = card.Cost == 2 && card.Attack == 9 && card.Life == 9 && sbAfter == 3;
            bool serialUp = card.EntrySerial == serialBefore + 1;
            bool inDeck = card.Zone == ZoneRef.Deck && st.Players[0].Deck.Contains(card);
            Chk("misc-9 返回牌组保留减费 / Buff / Spellboost（流水号仍 +1）",
                kept && serialUp && inDeck,
                $"cost={card.Cost} atk={card.Attack} life={card.Life} spellboost={sbAfter} " +
                $"serial={serialBefore}->{card.EntrySerial} inDeck={inDeck}");
        }

        // ── M3-3 MemoryWorld 返回牌组同样会洗 ──
        {
            const int trials = 40;
            int lastPos = 0;
            for (int t = 0; t < trials; t++)
            {
                var w = new MemoryWorld(seed: 700 + t);
                w.RegisterCard(MiscCard(904, "Back"));
                for (int i = 0; i < 6; i++) w.Create(904, ZoneRef.Deck, 0);
                var back = w.Create(904, ZoneRef.Hand, 0);

                w.MoveEntity(back, ZoneRef.Deck, 0);

                var deck = w.EnumerateZone(ZoneRef.Deck, 0).ToList();
                if (deck.Count == 7 && deck[deck.Count - 1] == back) lastPos++;
            }
            Chk("misc-10 MemoryWorld 返回牌组同样被洗散", lastPos <= 20,
                $"末位={lastPos}/{trials}");
        }

        log.AppendLine("OK " + pass + "/" + total);
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    private static CardDef MiscCard(int id, string name) => new()
    {
        CardId = id, Name = name, Kind = CardKind.Follower,
        BaseAttack = 1, BaseLife = 1, BaseCost = 1,
    };

    private static Battle.BattleState MiscState(int seed)
    {
        var st = new Battle.BattleState(seed: seed);
        st.RegisterCard(new CardDef { CardId = 950, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        st.Players[0].Leader = st.CreateEntity(950, ZoneRef.Leader, 0);
        st.Players[1].Leader = st.CreateEntity(950, ZoneRef.Leader, 1);
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        return st;
    }

    private static Lexicon BuildMiscLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

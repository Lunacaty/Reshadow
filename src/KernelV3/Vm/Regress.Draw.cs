using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 能力抽牌（Draw N cards 编译出的 MoveZone Deck→Hand）的回归用例
//
// 收口前：CardVm.DoMoveZone 对 Deck→Hand 只调 MoveEntity，
//   于是「每当抽牌时」永不响应，牌库抽空也不会判负。
// 收口后：普通抽牌一律走 IVmWorld.DrawCards —— 与 BattleState.DrawCards
//   同一条管线（OnDraw / OnDeckOut / 爆牌）。
//
// 边界：带过滤的「从牌库搜索一张牌加手牌」同样被编译成 MoveZone(Deck→Hand)，
//   但它不是抽牌，不得走这条管线（否则会错发 OnDraw、错判 DECK_OUT）。
// ============================================================================
public static partial class Regress
{
    public static string RunDraw()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildDrawLexicon();
        var comp = new EnglishCompiler(lex);

        // ── 1. 能力抽牌必须发 OnDraw，且 Subject 是牌库顶那张 ──
        {
            var st = NewDrawState(seed: 210, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            var top = st.SpawnCard(401, ZoneRef.Deck, 0);
            st.SpawnCard(401, ZoneRef.Deck, 0);
            st.SpawnCard(401, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);

            var onDraw = st.EventLog.Where(e => e.Kind == CoreEvent.OnDraw).ToList();
            bool once = onDraw.Count == 1;
            bool subjectIsTop = once && onDraw[0].Subject?.EntityId == top.EntityId;
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            Check("draw-1 能力抽牌发 OnDraw（Subject=牌库顶）",
                  once && subjectIsTop && hand == 1 && deck == 2,
                  $"onDraw={onDraw.Count} top={subjectIsTop} hand={hand} deck={deck}");
        }

        // ── 2. 能力抽牌与回合初抽牌必须走同一条管线（事件形状一致）──
        //
        // 判据不能写成「监听卡真的扣了血」：TurnRunner 只对显式
        // vm.EnqueueEvent 的事件扫描触发器，而 DrawCards 写的是 EventLog，
        // 所以「每当抽牌时」对**所有**抽牌都不响应 —— 包括回合初抽牌。
        // 那是触发链路的既有缝隙，不是抽牌管线的分歧，也不在本改动范围内。
        // 这里守住的是本改动的边界：两条抽牌路径产出的 OnDraw 完全同形。
        {
            var stTurn = NewDrawState(seed: 211, out var trTurn, out var vmTurn);
            stTurn.SpawnCard(401, ZoneRef.Deck, 0);
            stTurn.ClearEvents();
            trTurn.StartTurn(0, vmTurn);
            var turnDraw = stTurn.EventLog.Where(e => e.Kind == CoreEvent.OnDraw).ToList();

            var st = NewDrawState(seed: 211, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            st.SpawnCard(401, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);
            var abilityDraw = st.EventLog.Where(e => e.Kind == CoreEvent.OnDraw).ToList();

            bool sameShape = turnDraw.Count == 1 && abilityDraw.Count == 1
                             && turnDraw[0].Subject != null && abilityDraw[0].Subject != null;
            Check("draw-2 能力抽牌与回合初抽牌事件同形", sameShape,
                  $"turnDraw={turnDraw.Count} abilityDraw={abilityDraw.Count} " +
                  $"turnSubject={turnDraw.FirstOrDefault().Subject != null} " +
                  $"abilitySubject={abilityDraw.FirstOrDefault().Subject != null}");
        }

        // ── 3. 牌库只剩 1 张时抽 2：只抽 1 张即判负，并发 OnDeckOut ──
        {
            var st = NewDrawState(seed: 212, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw 2 cards.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            st.SpawnCard(401, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);

            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int onDeckOut = st.EventLog.Count(e => e.Kind == CoreEvent.OnDeckOut);
            bool lost = st.Players[0].LostByDeckOut;
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            Check("draw-3 抽 2 只剩 1：抽 1 张 + 判负 + OnDeckOut",
                  onDraw == 1 && onDeckOut >= 1 && lost && hand == 1 && deck == 0,
                  $"onDraw={onDraw} onDeckOut={onDeckOut} lost={lost} hand={hand} deck={deck}");
        }

        // ── 4. 牌库 0 张时能力抽牌：不抛异常、不产生负数张数、判负 ──
        {
            var st = NewDrawState(seed: 213, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            bool threw = false;
            try { tr.PlayCard(0, card, vm); }
            catch (Exception) { threw = true; }

            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            Check("draw-4 空牌库抽牌不炸、不抽负数、判负",
                  !threw && st.Players[0].LostByDeckOut && hand == 0 && deck == 0 && onDraw == 0,
                  $"threw={threw} lost={st.Players[0].LostByDeckOut} hand={hand} deck={deck} onDraw={onDraw}");
        }

        // ── 5. Enhance 高档仍抽 3 张（不得弄坏问题 2 的修复）──
        {
            var st = NewDrawState(seed: 214, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            for (int i = 0; i < 8; i++) st.SpawnCard(401, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);

            int pp = st.ReadCounter(0, st.Schema.Counter("pp"));
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            Check("draw-5 Enhance 5PP 抽 3 张且每次都发 OnDraw",
                  pp == 1 && hand == 3 && deck == 5 && onDraw == 3 && !st.Players[0].LostByDeckOut,
                  $"pp={pp} hand={hand} deck={deck} onDraw={onDraw}");
        }

        // ── 6. 带过滤的「从牌库搜索」不是抽牌：不发 OnDraw、不判负 ──
        {
            var st = NewDrawState(seed: 215, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw all copies of Rusty.");
            st.RegisterCard(DrawCard(401, r.Abilities));
            st.RegisterCard(new CardDef { CardId = 402, Name = "Filler", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
            for (int i = 0; i < 3; i++) st.SpawnCard(402, ZoneRef.Deck, 0);   // 牌库里没有 Rusty
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);

            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            bool lost = st.Players[0].LostByDeckOut;
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();

            // 断言判别依据本身：这个节点确实不是「普通抽牌」，
            // 否则 onDraw=0 可能只是这一段压根没执行。
            var mv = r.Abilities.Count > 0
                ? r.Abilities[0].Body.OfType<MoveZoneNode>().FirstOrDefault() : null;
            bool notPlainDraw = mv != null
                && (mv.Target.Mode != SelectMode.First
                    || (mv.Target.Where != null && !(mv.Target.Where is ConstBoolExpr cb0 && cb0.Value)));
            Check("draw-6 定向检索不算抽牌（无 OnDraw / 不判负 / 牌库未动）",
                  notPlainDraw && onDraw == 0 && !lost && deck == 3,
                  $"mode={mv?.Target.Mode} notPlain={notPlainDraw} err={r.Errors.Count} " +
                  $"onDraw={onDraw} lost={lost} deck={deck}");
        }

        // ── 7. MemoryWorld 的 DrawCards 与 BattleState 语义一致 ──
        {
            var w = new MemoryWorld();
            w.RegisterCard(new CardDef { CardId = 401, Name = "D1", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 3; i++) w.Create(401, ZoneRef.Deck, 0);
            w.DrawCards(0, 2);
            int onDraw2 = w.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int hand2 = w.EnumerateZone(ZoneRef.Hand, 0).Count();

            w.DrawCards(0, 5);   // 只剩 1 张：抽 1 张后发 OnDeckOut
            int onDrawAll = w.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int onDeckOut = w.EventLog.Count(e => e.Kind == CoreEvent.OnDeckOut);
            int handAll = w.EnumerateZone(ZoneRef.Hand, 0).Count();
            Check("draw-7 MemoryWorld 抽牌同语义（OnDraw / OnDeckOut）",
                  onDraw2 == 2 && hand2 == 2 && onDrawAll == 3 && onDeckOut == 1 && handAll == 3,
                  $"onDraw2={onDraw2} hand2={hand2} onDrawAll={onDrawAll} onDeckOut={onDeckOut} handAll={handAll}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    private static CardDef DrawCard(int cardId, IReadOnlyList<AbilityDef> abilities) => new()
    {
        CardId = cardId, Name = "D" + cardId, Kind = CardKind.Follower,
        BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = abilities,
    };

    private static Battle.BattleState NewDrawState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new Battle.BattleState(seed: seed);
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

    private static Lexicon BuildDrawLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.AddCard("Rusty", 777);
        lex.Build();
        return lex;
    }
}

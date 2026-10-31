using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 法术卡自身能力（Fanfare: / 裸 OnPlay）的回归用例
//
// 收口前：PlayCard 的法术分支先发 OnPlay 再进墓地，
//   于是结算那一刻这张法术还在手牌区；而它自己的 Fanfare: 编译成
//   ActiveZone=Board，MatchesTrigger 的区域判定直接 return false ——
//   法术一次效果都不跑（实测 20 → 20，随从版同句 20 → 18）。
//
// 边界：豁免只对「事件主体自身 · OnPlay」生效。
//   手牌 / 牌组里**其它**卡的监听语义一字不改：
//   手牌里印着 Fanfare 的卡仍按 Board 判定（不触发），
//   "Activates in hand" 的卡仍按 Hand 判定。
// ============================================================================
public static partial class Regress
{
    public static string RunSpell()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildSpellLexicon();
        var comp = new EnglishCompiler(lex);

        // ── 1. 法术【入场曲】必须结算 ──
        {
            var st = NewSpellState(seed: 310, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Deal 2 damage to the enemy leader.");
            st.RegisterCard(SpellCard(401, r.Abilities, CardKind.Spell));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            bool played = tr.PlayCard(0, card, vm);
            int enemy = st.Players[1].Leader.Life;
            Check("spell-1 法术 Fanfare 生效（敌方主战者 20 → 18）",
                  played && enemy == 18, $"played={played} enemyLeader={enemy}");
        }

        // ── 2. 无 Fanfare 前缀的裸效果句同样是 OnPlay，同样必须结算 ──
        {
            var st = NewSpellState(seed: 311, out var tr, out var vm);
            var r = comp.Compile("Deal 2 damage to the enemy leader.");
            bool isOnPlay = r.Abilities.Count > 0
                            && r.Abilities[0].Trigger?.Event == CoreEvent.OnPlay;
            st.RegisterCard(SpellCard(401, r.Abilities, CardKind.Spell));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            tr.PlayCard(0, card, vm);
            int enemy = st.Players[1].Leader.Life;
            Check("spell-2 无法术头的裸效果句也生效（编译为 OnPlay）",
                  isOnPlay && enemy == 18, $"onPlay={isOnPlay} enemyLeader={enemy}");
        }

        // ── 3. 法术抽牌走统一管线：手牌 +2 / 牌库 -2 / 两条 OnDraw ──
        {
            var st = NewSpellState(seed: 312, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Draw 2 cards.");
            st.RegisterCard(SpellCard(401, r.Abilities, CardKind.Spell));
            st.RegisterCard(SpellCard(500, Array.Empty<AbilityDef>(), CardKind.Follower));
            for (int i = 0; i < 8; i++) st.SpawnCard(500, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            int deck = st.EnumerateZone(ZoneRef.Deck, 0).Count();
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            Check("spell-3 法术抽 2：手牌 +2 / 牌库 -2 / OnDraw 2 条",
                  hand == 2 && deck == 6 && onDraw == 2,
                  $"hand={hand} deck={deck} onDraw={onDraw}");
        }

        // ── 4. 只执行一次：移区不得造成重复触发 ──
        // 手牌里同时放一张印着 Fanfare 的旁观卡和一张手牌监听卡：
        // 旁观卡若被豁免误伤会多打 3 点，法术若触发两次会多打 2 点。
        //
        // 期望 13 = 20 - 2（本卡入场曲）- 5×1（手牌监听卡响应 1 次）。
        //   若为 11   → 法术自身能力结算了两次（多扣 2）
        //   若为 10   → 旁观卡被区域豁免误伤（多扣 3）
        //   若为 8    → 两者同时发生
        // 手牌监听卡对**法术**打出响应 1 次是官方语义（"play a card" 包含法术）；
        // 本条早期版本把「对法术 0 响应」写进期望，那是触发链路的既有缝隙，
        // 已在本轮修复（见 Regress.Trigger.cs），此处守的是修复后的正确数值。
        {
            var st = NewSpellState(seed: 313, out var tr, out var vm);
            var spellR = comp.Compile("Fanfare: Deal 2 damage to the enemy leader.");
            var byFan = comp.Compile("Fanfare: Deal 3 damage to the enemy leader.");
            var byHand = comp.Compile(
                "Activates in hand. Whenever you play a card, deal 5 damage to the enemy leader.");
            st.RegisterCard(SpellCard(401, spellR.Abilities, CardKind.Spell));
            st.RegisterCard(SpellCard(402, byFan.Abilities, CardKind.Spell));
            st.RegisterCard(SpellCard(403, byHand.Abilities, CardKind.Spell));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var b1 = st.SpawnCard(402, ZoneRef.Hand, 0);
            var b2 = st.SpawnCard(403, ZoneRef.Hand, 0);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            tr.PlayCard(0, card, vm);
            int enemy = st.Players[1].Leader.Life;
            Check("spell-4 法术能力只结算一次（13 = 2 + 5×1；11=法术触发两次，10=旁观卡被误伤）",
                  enemy == 13 && b1.Zone == ZoneRef.Hand && b2.Zone == ZoneRef.Hand,
                  $"enemyLeader={enemy}（13 期望 / 11 法术结算两次 / 10 旁观卡误伤）" +
                  $" fanfareBystander={b1.Zone} handListener={b2.Zone}");
        }

        // ── 5. 手牌监听卡的响应次数不因「法术有了能力」而改变 ──
        // 这是豁免边界的判据：唯一变量是「被法术自身的 Fanfare 结算过」，
        // 手牌监听卡（Activates in hand）的触发次数必须前后一致。
        //
        // 口径：withAb = 2（法术 Fanfare）+ 5×1（监听 1 次）= 7
        //       withoutAb = 0 + 5×1 = 5
        //
        // 本条早期版本断言 hitsWith == 0（对法术 0 响应）—— 那是
        // 「SubjectFilter 只在 Board 里解析」这条触发链路缝隙造成的，
        // 缝隙已在本轮修复（T2，见 Regress.Trigger.cs）。
        // 修复后本条反而更强：它现在断言响应次数**恰好是 1**
        // （不是 0 = 又回去了，也不是 2 = 双重触发），
        // 同时仍守住「法术自身有没有入场曲不影响监听卡的响应次数」这个原始意图。
        {
            int PlaySpell(bool withFanfare, out int handListenerHits)
            {
                var st = NewSpellState(seed: 314, out var tr, out var vm);
                var byHand = comp.Compile(
                    "Activates in hand. Whenever you play a card, deal 5 damage to the enemy leader.");
                IReadOnlyList<AbilityDef> ab = withFanfare
                    ? comp.Compile("Fanfare: Deal 2 damage to the enemy leader.").Abilities
                    : Array.Empty<AbilityDef>();
                st.RegisterCard(SpellCard(401, ab, CardKind.Spell));
                st.RegisterCard(SpellCard(403, byHand.Abilities, CardKind.Spell));
                st.WriteCounter(0, st.Schema.Counter("pp"), 10);
                st.SpawnCard(403, ZoneRef.Hand, 0);
                var card = st.SpawnCard(401, ZoneRef.Hand, 0);
                tr.PlayCard(0, card, vm);
                // 每次响应打 5 点；扣掉本卡自己的 2 点入场曲后剩下的才是手牌监听卡的贡献
                int dealt = 20 - st.Players[1].Leader.Life;
                handListenerHits = (dealt - (withFanfare ? 2 : 0)) / 5;
                return dealt;
            }

            int withAb = PlaySpell(true, out int hitsWith);
            int withoutAb = PlaySpell(false, out int hitsWithout);
            Check("spell-5 手牌监听卡对法术打出响应恰好 1 次，且不因入场曲而改变",
                  withAb == 7 && withoutAb == 5 && hitsWith == hitsWithout && hitsWith == 1,
                  $"withFanfareDealt={withAb}（7 = 2+5×1）withoutFanfareDealt={withoutAb}（5 = 0+5×1）" +
                  $" hitsWith={hitsWith} hitsWithout={hitsWithout}（0=缝隙仍在，2=双重触发）");
        }

        // ── 6. 随从 / 护符的入场曲完全不退化 ──
        {
            int PlayKind(CardKind kind, out ZoneRef zone)
            {
                var st = NewSpellState(seed: 315, out var tr, out var vm);
                var r = comp.Compile("Fanfare: Deal 2 damage to the enemy leader.");
                st.RegisterCard(SpellCard(401, r.Abilities, kind));
                st.WriteCounter(0, st.Schema.Counter("pp"), 10);
                var card = st.SpawnCard(401, ZoneRef.Hand, 0);
                tr.PlayCard(0, card, vm);
                zone = card.Zone;
                return st.Players[1].Leader.Life;
            }

            int follower = PlayKind(CardKind.Follower, out var fz);
            int amulet = PlayKind(CardKind.Amulet, out var az);
            Check("spell-6 随从 / 护符的入场曲不退化",
                  follower == 18 && fz == ZoneRef.Board && amulet == 18 && az == ZoneRef.Board,
                  $"follower={follower}/{fz} amulet={amulet}/{az}");
        }

        // ── 7. 结算后仍进墓地、shadow +1、OnSpellboost 照常 ──
        {
            var st = NewSpellState(seed: 316, out var tr, out var vm);
            var spellR = comp.Compile("Fanfare: Deal 2 damage to the enemy leader.");
            var sbR = comp.Compile("Activates in hand. On Spellboost: Draw a card.");
            st.RegisterCard(SpellCard(401, spellR.Abilities, CardKind.Spell));
            st.RegisterCard(SpellCard(404, sbR.Abilities, CardKind.Follower));
            st.RegisterCard(SpellCard(500, Array.Empty<AbilityDef>(), CardKind.Follower));
            for (int i = 0; i < 5; i++) st.SpawnCard(500, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var sbCard = st.SpawnCard(404, ZoneRef.Hand, 0);
            var card = st.SpawnCard(401, ZoneRef.Hand, 0);
            st.ClearEvents();
            tr.PlayCard(0, card, vm);
            sbCard.Counters.TryGetValue(st.Schema.Counter("spellboost"), out var sbCount);
            int shadow = st.ReadCounter(0, st.Schema.Counter("shadow"));
            int enemy = st.Players[1].Leader.Life;
            Check("spell-7 结算后进墓地 / shadow +1 / 魔力增幅计数 +1",
                  card.Zone == ZoneRef.Cemetery && shadow == 1 && sbCount == 1 && enemy == 18,
                  $"zone={card.Zone} shadow={shadow} spellboost={sbCount} enemyLeader={enemy}");
        }

        // ── 8. 豁免不放行「入场曲以外的场面监听」：TurnStart 不该对手牌生效 ──
        {
            var st = NewSpellState(seed: 317, out var tr, out var vm);
            var turnR = comp.Compile("At the start of your turn, deal 2 damage to the enemy leader.");
            st.RegisterCard(SpellCard(406, turnR.Abilities, CardKind.Spell));
            var h1 = st.SpawnCard(406, ZoneRef.Hand, 0);
            var h2 = st.SpawnCard(406, ZoneRef.Hand, 1);
            st.ClearEvents();
            vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnStart });
            vm.StepUntilBlocked();
            int enemy = st.Players[1].Leader.Life;
            Check("spell-8 手牌里的回合开始能力仍不响应（豁免只覆盖 OnPlay）",
                  enemy == 20 && h1.Zone == ZoneRef.Hand && h2.Zone == ZoneRef.Hand,
                  $"enemyLeader={enemy} h1={h1.Zone} h2={h2.Zone}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    private static CardDef SpellCard(int cardId, IReadOnlyList<AbilityDef> abilities, CardKind kind) => new()
    {
        CardId = cardId, Name = "S" + cardId, Kind = kind,
        BaseAttack = 1,
        // 护符在场上没有生命值，但 SBA 会收掉 Life<=0 的场面实体；
        // 这里给 1 只是为了让「护符留在场上」这一条断言干净，不涉及引擎语义。
        BaseLife = 1,
        BaseCost = 1, Abilities = abilities,
    };

    private static Battle.BattleState NewSpellState(int seed, out TurnRunner tr, out CardVm vm)
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

    private static Lexicon BuildSpellLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

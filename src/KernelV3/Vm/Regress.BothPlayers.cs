using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// both · "both players <谓词>" 必须两侧同时成立
//
// 缺口形态（全卡池唯一一张受影响卡）：
//   10304120 Gilnelise, Voracity Manifest
//   "If both players have 10 max play points, add a Sweetness of Voracity to your hand."
//   旧编译：cond=(true and ctr:max_pp >= 10)  —— 只判自己
//   后果：只有自己满 10 费、对手没满时，卡也会塞手牌。
//
// 判据不能只看 AST 文字：条件恒真/恒假只有跑 VM 才看得出来，
// 所以每条断言都构造真实 BattleState，按「自己 / 对手」两种 max_pp 组合实跑。
// ============================================================================
public static partial class Regress
{
    public static string RunBothPlayers()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(BothLexicon());

        // ── 1. AST 形态：双方条件必须是 And(自己侧, 对手侧) ──
        {
            var r = comp.Compile("Fanfare: If both players have 10 max play points, draw a card.");
            string cond = r.Ok ? AstCanonicalPrinter.PrintBoolExpr(r.Abilities[0].Condition) : "<err>";
            bool twoSides = cond.Contains("@self") && cond.Contains("@opp");
            bool hasTen = cond.Contains(">= 10");
            Chk("both-1 both players 编译成 (自己 >= 10) and (对手 >= 10)",
                r.Ok && twoSides && hasTen, "ok=" + r.Ok + " cond=" + cond);
        }

        // ── 2. 实战三条：自己10/对手9 不触发；10/10 触发；9/10 不触发 ──
        {
            int Draw10_9 = Fires(10, 9, 8101);
            int Draw10_10 = Fires(10, 10, 8102);
            int Draw9_10 = Fires(9, 10, 8103);
            Chk("both-2 自己10/对手9 不触发，10/10 触发，9/10 不触发",
                Draw10_9 == 0 && Draw10_10 == 1 && Draw9_10 == 0,
                "10/9=" + Draw10_9 + " 10/10=" + Draw10_10 + " 9/10=" + Draw9_10 + "（抽牌张数，期望 0/1/0）");
        }

        // ── 3. 单侧写法不退化：自己10/对手0 仍触发 ──
        {
            int Fires1(int self, int opp, int seed) => Fires(self, opp, seed,
                "Fanfare: If you have 10 max play points, draw a card.");
            int a = Fires1(10, 0, 8104);
            int b = Fires1(9, 10, 8105);
            Chk("both-3 单侧 if you have 10 max play points 仍只判自己（10/0 触发，9/10 不触发）",
                a == 1 && b == 0, "10/0=" + a + " 9/10=" + b + "（期望 1/0）");
        }

        // ── 4. 其它 both players 句式：有两侧判据的必须是两侧 ──
        {
            string CondOf(string text)
            {
                var r = comp.Compile(text);
                return r.Ok ? AstCanonicalPrinter.PrintBoolExpr(r.Abilities[0].Condition) : "<err>";
            }
            string overflow = CondOf("Fanfare: If both players are in Overflow, draw a card.");
            string hand4 = CondOf("Fanfare: If both players have 4 or more cards in hand, draw a card.");
            string field3 = CondOf("Fanfare: If both players have at least 3 followers on the field, draw a card.");
            bool overflowOk = overflow.Contains("@self") && overflow.Contains("@opp") && overflow.Contains(">= 7");
            bool handOk = hand4.Contains("self:hand") && hand4.Contains("opponent:hand") && hand4.Contains(">= 4");
            bool fieldOk = field3.Contains("self:board") && field3.Contains("opponent:board") && field3.Contains(">= 3");
            Chk("both-4 overflow / hand / field 三种句式都展开成两侧",
                overflowOk && handOk && fieldOk,
                "overflow=" + overflow + " | hand=" + hand4 + " | field=" + field3);
        }

        // ── 5. 认不出来的 both players 句式必须显式报错，不许静默只判一侧 ──
        {
            var r = comp.Compile("Fanfare: If both players control a token, draw a card.");
            string errs = string.Join(" | ", r.Errors);
            bool rejected = !r.Ok && errs.Contains("both players");
            Chk("both-5 无法建模的 both players 句式显式报错（不静默错译）",
                rejected, "ok=" + r.Ok + " errs=" + errs);
        }

        // ── 6. 真实卡 10304120：整张卡编译出的条件带两侧，且实战按双方分流 ──
        {
            const string card =
                "Fanfare: Select another follower on the field and give it +2/-2. " +
                "If both players have 10 max play points, add a Sweetness of Voracity to your hand.";
            var r = comp.Compile(card);
            bool compiled = r.Ok;
            var condAbility = r.Abilities.FirstOrDefault(a => a.Trigger?.Event == CoreEvent.OnPlay
                                                              && a.Condition is not ConstBoolExpr cb0);
            string cond = condAbility != null ? AstCanonicalPrinter.PrintBoolExpr(condAbility.Condition) : "<none>";
            bool twoSides = cond.Contains("@self") && cond.Contains("@opp");
            Chk("both-6 真实卡 10304120 的条件是两侧合取",
                compiled && twoSides, "ok=" + compiled + " cond=" + cond);
        }

        // ── 7. 真实卡端到端：自己10/对手9 不塞手牌；10/10 塞 1 张 ──
        {
            int Added(int self, int opp, int seed) => RealCardAdded(self, opp, seed, comp);
            int low = Added(10, 9, 8106);
            int both = Added(10, 10, 8107);
            Chk("both-7 真实卡端到端：10/9 不进手牌，10/10 进 1 张",
                low == 0 && both == 1, "10/9=" + low + " 10/10=" + both + "（期望 0/1）");
        }

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 BothPlayers " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    // ---------- 公共设施 ----------

    /// <summary>
    /// 编译 "<条件> draw a card"，按自己 / 对手的 max_pp 组合跑一次 Fanfare，
    /// 返回实际抽到的牌数（1 = 条件成立）。
    /// </summary>
    private static int Fires(int selfMaxPp, int oppMaxPp, int seed, string text = null)
    {
        var comp = new EnglishCompiler(BothLexicon());
        var r = comp.Compile(text ?? "Fanfare: If both players have 10 max play points, draw a card.");
        if (!r.Ok) return -1;

        var st = BothState(seed, out var tr, out var vm);
        st.RegisterCard(new CardDef
        {
            CardId = 500, Name = "Probe", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 0, Abilities = r.Abilities,
        });
        st.RegisterCard(new CardDef
        {
            CardId = 501, Name = "Filler", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1,
        });
        // 牌组留 5 张，保证能抽到
        for (int i = 0; i < 5; i++) st.SpawnCard(501, ZoneRef.Deck, 0);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.WriteCounter(0, st.Schema.Counter("max_pp"), selfMaxPp);
        st.WriteCounter(1, st.Schema.Counter("max_pp"), oppMaxPp);

        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        var card = st.SpawnCard(500, ZoneRef.Hand, 0);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.ClearEvents();
        tr.PlayCard(0, card, vm);
        vm.StepUntilBlocked();
        return st.EnumerateZone(ZoneRef.Hand, 0).Count() - handBefore;
    }

    /// <summary>
    /// 真实卡 10304120 的端到端：条件成立时才把手里的 Sweetness of Voracity 塞进手牌。
    /// 第一个动作是 Select another follower —— 场上必须有一个可选目标，
    /// 否则整条能力在 Select 处就停住，后面的 If 根本轮不到求值。
    /// </summary>
    private static int RealCardAdded(int selfMaxPp, int oppMaxPp, int seed, EnglishCompiler comp)
    {
        const string cardText =
            "Fanfare: Select another follower on the field and give it +2/-2. " +
            "If both players have 10 max play points, add a Sweetness of Voracity to your hand.";
        var r = comp.Compile(cardText);
        if (!r.Ok) return -1;

        var st = BothState(seed, out var tr, out var vm);
        st.RegisterCard(new CardDef
        {
            CardId = 510, Name = "Gilnelise, Voracity Manifest", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 0, Abilities = r.Abilities,
        });
        st.RegisterCard(new CardDef
        {
            CardId = 511, Name = "Bystander", Kind = CardKind.Follower,
            BaseAttack = 2, BaseLife = 2, BaseCost = 1,
        });
        st.RegisterCard(new CardDef
        {
            CardId = 512, Name = "Sweetness of Voracity", Kind = CardKind.Spell,
            BaseAttack = 0, BaseLife = 0, BaseCost = 2,
        });
        st.SpawnCard(511, ZoneRef.Board, 0);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.WriteCounter(0, st.Schema.Counter("max_pp"), selfMaxPp);
        st.WriteCounter(1, st.Schema.Counter("max_pp"), oppMaxPp);

        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        var card = st.SpawnCard(510, ZoneRef.Hand, 0);
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.ClearEvents();
        tr.PlayCard(0, card, vm);
        vm.StepUntilBlocked();
        // Select 需要玩家选目标：把 $selected 绑到场上唯一的随从后再推进
        if (vm.Pending != null)
        {
            var tgt = st.EnumerateZone(ZoneRef.Board, 0).FirstOrDefault();
            if (tgt != null)
                vm.SubmitChoice(vm.Pending.ChoiceId, new List<int> { tgt.EntityId });
            vm.StepUntilBlocked();
        }
        return st.EnumerateZone(ZoneRef.Hand, 0).Count() - handBefore;
    }

    private static BattleState BothState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new BattleState(seed: seed);
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        vm.CurrentTurnOwner = 0;
        return st;
    }

    private static Lexicon BothLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        foreach (var tr in new[] { "departed", "clue", "token" }) lex.AddTrait(tr);
        foreach (var cl in new[] { "forestcraft", "neutral" }) lex.AddClan(cl);
        lex.AddCard("Sweetness of Voracity", 10304121);
        lex.Build();
        return lex;
    }
}

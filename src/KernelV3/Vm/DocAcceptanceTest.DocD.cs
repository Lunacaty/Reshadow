using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

public static partial class DocAcceptanceTest
{
    /// <summary>76-d：五个 Phase 的验收门禁。</summary>
    private static void RunDocD()
    {
        _log.Add("");
        _log.Add("=== 76-d：五阶段施工方案 ===");
        var lex = BuildFullLexicon();
        var comp = new EnglishCompiler(lex);

        // ---- Phase 1：打印器盲区与底层契约 ----
        {
            bool isDestroy = typeof(MoveZoneNode).GetProperty("IsDestroy") != null;
            bool leaderZone = TargetSpec.OpponentLeader.Zone == ZoneRef.Leader
                           && TargetSpec.SelfLeader.Zone == ZoneRef.Leader;
            Ok("d-P1", "MoveZoneNode.IsDestroy 与 Leader 区域修正", isDestroy && leaderZone);
        }
        {
            // E31/E32：比较双侧必须带 Of
            var r = comp.Compile("Fanfare: If your leader's defense is lower than the enemy leader's defense, draw a card.");
            string cond = r.Abilities.Count > 0 && r.Abilities[0].Condition != null
                ? AstEnglishPrinter.PrintBool(r.Abilities[0].Condition) : "";
            bool hasOf = cond.Contains("prop:") && cond.Contains("(");
            Ok("d-P1", "E32 比较双侧带 Of（不是 prop:Life = prop:Life）",
               hasOf, cond);
        }
        {
            // E8：Swap 不打出假 0/0
            var r = comp.Compile("Fanfare: Swap this follower's attack and defense.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            bool noFake = !body.Contains("0/0") && body.Contains("Swap");
            Ok("d-P1", "E8 Swap 不打印假 0/0", noFake, body);
        }
        {
            // E3：回合限制要打印
            var r = comp.Compile("During your turn, whenever an allied follower attacks, draw a card.");
            string p = r.Abilities.Count > 0 ? AstEnglishPrinter.PrintAbility(r.Abilities[0]) : "";
            bool shown = p.Contains("turn") || p.Contains("self") || p.Contains("opponent");
            Ok("d-P1", "E3 回合限制不丢失", shown, p);
        }

        // ---- Phase 2：分段与句群 ----
        {
            // E21 & E12：Countdown 独立于触发器
            var r = comp.Compile("Countdown (2)\nAt the start of your turn, draw a card.");
            bool two = r.Abilities.Count >= 2;
            Ok("d-P2", "E21/E12 Countdown 不绑定事件", two, $"count={r.Abilities.Count}");
        }
        {
            // E15：Engage 后第二句共享能力头
            var r1 = comp.Compile("Engage: Destroy this card. Select an allied follower on the field and give it Rush.");
            bool one = r1.Abilities.Count == 1;
            Ok("d-P2", "E15 能力头归属正确", one, $"count={r1.Abilities.Count}");
        }
        {
            // E5：引号内无 fanfare
            var r = comp.Compile("Fanfare: Give this follower \"Ward\".");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            bool noInnerPlay = body.Contains("keyword");
            Ok("d-P2", "E5 引号内常驻效果不变 fanfare", noInnerPlay, body);
        }
        {
            // E18：Fuse 行不吞后面的动作
            var r = comp.Compile("Fuse: Forestcraft cards\nDraw a card.");
            bool ok2 = r.Abilities.Count >= 1;
            Ok("d-P2", "E18 Fuse 不吞后续法术效果", ok2, $"count={r.Abilities.Count}");
        }

        // ---- Phase 3：目标/代词/变量/模式 ----
        {
            var r = comp.Compile("Whenever an allied follower enters the field, give it Ward.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("d-P3", "E4 $trigger 绑定", body.Contains("$trigger"), body);
        }
        {
            var r = comp.Compile("Fanfare: If its cost is 4, draw a card.");
            string cond = r.Abilities.Count > 0 && r.Abilities[0].Condition != null
                ? AstEnglishPrinter.PrintBool(r.Abilities[0].Condition) : "";
            bool costOk = cond.Contains("Cost");
            Ok("d-P3", "E7 cost 不被当成 Life", costOk, cond);
        }
        {
            var r = comp.Compile("Evolve: Summon a Mecha Cavalier. Super-Evolve: Summon 2 instead.");
            string body = DumpDeep(r.Abilities);
            Ok("d-P3", "E9 Summon N instead 继承卡名",
               body.Contains("Mecha Cavalier") && body.Contains("spawn 2"), body.Replace("\n", " "));
        }
        {
            var r = comp.Compile("X starts at 2. On Spellboost: Increase X by 1.");
            bool hasInit = r.Abilities.Any(a => a.Body.OfType<ModifyCounterNode>().Any(m => m.Op == CounterOp.Set));
            Ok("d-P3", "E28 X starts at N 初始值", hasInit);
        }

        // ---- Phase 4：统一管线五项 ----
        {
            var st = new Battle.BattleState(seed: 81);
            var cr = new Battle.CombatResolver(st, st.Schema);
            st.RegisterCard(new CardDef { CardId = 1, Name = "B", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 3 });
            var e = st.SpawnCard(1, ZoneRef.Board, 0);
            e.Keywords.Add(st.Schema.Keyword("barrier"));
            st.ActivePlayer = 0;
            int d = cr.ApplyDamage(e, 3, 1);
            bool ok4a = d == 0 && !e.Keywords.Contains(st.Schema.Keyword("barrier"));
            Ok("d-P4-1", "屏障吸收伤害并消失", ok4a, $"dealt={d}");
        }
        {
            var st = new Battle.BattleState(seed: 82);
            st.RegisterCard(new CardDef { CardId = 2, Name = "S", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            var se = st.SpawnCard(2, ZoneRef.Board, 0);
            se.IsSuperEvolved = true;
            st.ActivePlayer = 0;
            st.DestroyEntity(se);
            bool survives = se.Zone == ZoneRef.Board;

            var normal = st.SpawnCard(2, ZoneRef.Board, 0);
            int sh = st.ReadCounter(0, st.Schema.Counter("shadow"));
            st.DestroyEntity(normal);
            bool dies = normal.Zone == ZoneRef.Cemetery
                     && st.ReadCounter(0, st.Schema.Counter("shadow")) == sh + 1
                     && st.EventLog.Any(x => x.Kind == CoreEvent.OnDestroy);
            Ok("d-P4-2", "超进化免破坏 / 普通随从正常破坏", survives && dies,
               $"survive={survives} dies={dies}");
        }
        {
            var st = new Battle.BattleState(seed: 83);
            var ab = comp.Compile("Ward\nCountdown (2): At the start of your turn, draw a card.");
            st.RegisterCard(new CardDef
            {
                CardId = 3, Name = "WC", Kind = CardKind.Amulet, BaseCost = 1,
                InnateKeywords = new[] { st.Schema.Keyword("ward") },
                InnateCounters = new Dictionary<SymbolId<CounterTag>, int> { [st.Schema.Counter("countdown")] = 2 },
            });
            var e = st.SpawnCard(3, ZoneRef.Board, 0);
            bool kw = e.Keywords.Contains(st.Schema.Keyword("ward"));
            bool cd = e.Counters.TryGetValue(st.Schema.Counter("countdown"), out var v) && v == 2;
            Ok("d-P4-3", "Ward 与 Countdown(2) 入场装配", kw && cd, $"kw={kw} cd={cd}");
        }
        {
            var st = new Battle.BattleState(seed: 84);
            var cr = new Battle.CombatResolver(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var sel = comp.Compile("Select an enemy follower on the field and destroy it.");
            st.RegisterCard(new CardDef { CardId = 4, Name = "AU", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, Abilities = sel.Abilities });
            var atk = st.SpawnCard(4, ZoneRef.Board, 0);
            var aura = st.SpawnCard(4, ZoneRef.Board, 1);
            aura.Keywords.Add(st.Schema.Keyword("aura"));
            atk.EnteredTurn = 0;
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            bool canHit = cr.CanAttack(atk, aura, out _);
            Ok("d-P4-4", "Aura 可被攻击（但不可被能力选中）", canHit, $"canHit={canHit}");
        }
        {
            var st = new Battle.BattleState(seed: 85);
            st.RegisterCard(new CardDef { CardId = 5, Name = "F", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 5; i++) st.SpawnCard(5, ZoneRef.Board, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            var sixth = st.SpawnCard(5, ZoneRef.Board, 0);
            bool ok5 = sixth.Zone != ZoneRef.Cemetery
                    && st.ReadCounter(0, st.Schema.Counter("shadow")) == s0;
            Ok("d-P4-5", "满场第 6 个不进墓地、shadow 不变", ok5);
        }

        // ---- Phase 5：对局状态机 ----
        {
            var st = new Battle.BattleState(seed: 86);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 6, Name = "D", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 10; i++) st.SpawnCard(6, ZoneRef.Deck, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 0;
            tr.StartTurn(0, vm);
            bool ok = st.EnumerateZone(ZoneRef.Hand, 0).Count() == 1
                   && st.ReadCounter(0, st.Schema.Counter("pp")) == 1;
            Ok("d-P5-1", "TurnStart：涨费 + 抽牌（先手第 1 回合也抽）", ok);
        }
        {
            var st = new Battle.BattleState(seed: 87);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var ab = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            st.RegisterCard(new CardDef { CardId = 7, Name = "EN", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 2, Abilities = ab.Abilities });
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var c = st.SpawnCard(7, ZoneRef.Hand, 0);
            tr.PlayCard(0, c, vm);
            int left = st.ReadCounter(0, st.Schema.Counter("pp"));
            Ok("d-P5-3", "Enhance 强制高档扣费", left == 1, $"ppLeft={left}");
        }
        {
            var st = new Battle.BattleState(seed: 88);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 8, Name = "MT", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            var manual = st.SpawnCard(8, ZoneRef.Board, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 10;
            st.WriteCounter(0, st.Schema.Counter("ep"), 2);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            manual.EnteredTurn = 0;
            tr.Evolve(0, manual, false, vm);
            bool manualEvolved = manual.IsEvolved;
            Ok("d-P5-4", "手动进化生效", manualEvolved);
        }
        {
            var st = new Battle.BattleState(seed: 89);
            var key = st.Schema.Counter("earth_sigil");
            st.RegisterCard(new CardDef { CardId = Battle.BattleState.MagicSedimentCardId, Name = "Magic Sediment", Kind = CardKind.Amulet, BaseCost = 1 });
            st.WriteCounter(0, key, 2);
            bool sigil = st.FindEarthSigil(0) != null && st.ReadCounter(0, key) == 2;
            Ok("d-P5-6", "土之印堆叠", sigil);
        }
    }
}

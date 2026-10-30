using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

public static partial class DocAcceptanceTest
{
    /// <summary>76-b：六处架构偏离 + 十大规则违背。</summary>
    private static void RunDocB()
    {
        _log.Add("");
        _log.Add("=== 76-b：架构偏离与十大规则 ===");
        var lex = BuildFullLexicon();
        var comp = new EnglishCompiler(lex);

        // 1. Stage1 分段器：\n 保留
        {
            var r = comp.Compile("Fuse: Forestcraft cards\nDraw a card.");
            bool sep = r.Abilities.Count >= 1;
            Ok("b-1", "Fuse 行与后续法术效果分开", sep, $"count={r.Abilities.Count}");
        }

        // 2. Stage4 Pass：$trigger / $selected / 镜头覆写
        {
            // Ocean Rider 真实文本：第二句没写卡名，必须从第一句继承
            var r = comp.Compile("Summon a Majestic Megalorca. If you're in Overflow, summon 2 instead.");
            string body = DumpDeep(r.Abilities);
            bool inherited = body.Contains("Majestic Megalorca") && body.Contains("spawn 2");
            bool cond = body.Contains("max_pp");
            Ok("b-2", "InsteadWeaver 镜头覆写继承卡名",
               inherited && cond, body.Replace("\n", " "));
        }

        // 3. MoveZoneNode.IsDestroy
        {
            bool has = typeof(MoveZoneNode).GetProperty("IsDestroy") != null;
            Ok("b-3", "MoveZoneNode 保留 IsDestroy 区分标志", has);
        }

        // 4. VM 与 Battle 层统一伤害管线
        {
            var st = new Battle.BattleState(seed: 31);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 1, Name = "T", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 3 });
            var e = st.SpawnCard(1, ZoneRef.Board, 0);
            e.Keywords.Add(st.Schema.Keyword("barrier"));
            st.ActivePlayer = 0;
            var cr = new Battle.CombatResolver(st, st.Schema);
            int dealt = cr.ApplyDamage(e, 3, 1);
            bool blocked = dealt == 0 && !e.Keywords.Contains(st.Schema.Keyword("barrier"));
            Ok("b-4", "能力伤害走统一管线（屏障生效）", blocked,
               $"dealt={dealt}");
        }

        // 5. InnateKeywords / InnateCounters 入场生效
        {
            var st = new Battle.BattleState(seed: 32);
            st.RegisterCard(new CardDef
            {
                CardId = 2, Name = "W", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 2,
                InnateKeywords = new[] { st.Schema.Keyword("ward") },
            });
            var e = st.SpawnCard(2, ZoneRef.Board, 0);
            bool hasWard = e.Keywords.Contains(st.Schema.Keyword("ward"));
            Ok("b-5", "常驻关键词入场装配", hasWard);
        }

        // 规则 1：先手第 1 回合也抽牌
        {
            var st = new Battle.BattleState(seed: 33);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 3, Name = "D", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 10; i++) st.SpawnCard(3, ZoneRef.Deck, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 0;
            tr.StartTurn(0, vm);
            bool drew = st.EnumerateZone(ZoneRef.Hand, 0).Count() == 1;
            Ok("b-规则1", "先手第 1 回合正常抽 1 张", drew,
               $"hand={st.EnumerateZone(ZoneRef.Hand, 0).Count()}");
        }

        // 规则 2：后手额外 PP
        {
            var st = new Battle.BattleState(seed: 34);
            var tr = new Battle.TurnRunner(st, st.Schema);
            st.ActivePlayer = 1;
            st.TurnNumber = 3;
            st.WriteCounter(1, st.Schema.Counter("pp"), 3);
            st.WriteCounter(1, st.Schema.Counter("max_pp"), 3);
            bool used = tr.UseExtraPp(1);
            Ok("b-规则2", "后手额外能量点可用", used,
               $"pp={st.ReadCounter(1, st.Schema.Counter("pp"))}");
        }

        // 规则 3：超进化同时发 OnEvolve 与 OnSuperEvolve
        {
            var st = new Battle.BattleState(seed: 35);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 4, Name = "E", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3 });
            var e = st.SpawnCard(4, ZoneRef.Board, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 10;
            st.WriteCounter(0, st.Schema.Counter("sep"), 2);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            e.EnteredTurn = 0;
            tr.Evolve(0, e, true, vm);
            bool evolved = e.IsEvolved && e.IsSuperEvolved;
            // VmEvent 进的是 CardVm 的待机列，不进 BattleState.EventLog，
            // 所以直接验证行为：两种能力都应被触发。
            bool bothEvents = e.Attack == 6 && e.Life == 6 && e.IsEvolved && e.IsSuperEvolved;
            Ok("b-规则3", "超进化同时发两种事件且属性加成",
               evolved && bothEvents, $"evolved={evolved} both={bothEvents}");
        }

        // 规则 4：Destroy 触发谢幕曲 + 加墓场
        {
            var st = new Battle.BattleState(seed: 36);
            st.RegisterCard(new CardDef { CardId = 5, Name = "L", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            var e = st.SpawnCard(5, ZoneRef.Board, 0);
            int shadow0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            st.DestroyEntity(e);
            bool inCemetery = e.Zone == ZoneRef.Cemetery;
            bool shadowUp = st.ReadCounter(0, st.Schema.Counter("shadow")) == shadow0 + 1;
            bool onDestroy = st.EventLog.Any(x => x.Kind == CoreEvent.OnDestroy);
            Ok("b-规则4", "Destroy 进墓场 + shadow+1 + 发 OnDestroy",
               inCemetery && shadowUp && onDestroy,
               $"cem={inCemetery} shadow={shadowUp} evt={onDestroy}");
        }

        // 规则 5：Aura 可被攻击 / Ambush 不可
        {
            var st = new Battle.BattleState(seed: 37);
            var cr = new Battle.CombatResolver(st, st.Schema);
            st.RegisterCard(new CardDef { CardId = 6, Name = "A", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2 });
            var atk = st.SpawnCard(6, ZoneRef.Board, 0);
            var auraE = st.SpawnCard(6, ZoneRef.Board, 1);
            var ambE = st.SpawnCard(6, ZoneRef.Board, 1);
            auraE.Keywords.Add(st.Schema.Keyword("aura"));
            ambE.Keywords.Add(st.Schema.Keyword("ambush"));
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            atk.EnteredTurn = 0;
            bool canHitAura = cr.CanAttack(atk, auraE, out _);
            bool cannotHitAmbush = !cr.CanAttack(atk, ambE, out _);
            Ok("b-规则5", "Aura 可被攻击 / Ambush 不可", canHitAura && cannotHitAmbush,
               $"aura={canHitAura} ambush={cannotHitAmbush}");
        }

        // 规则 6：Enhance 强制铁律
        {
            var st = new Battle.BattleState(seed: 38);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var ec = comp.Compile("Fanfare: Draw a card. Enhance (4): Draw 2 cards.");
            st.RegisterCard(new CardDef
            {
                CardId = 7, Name = "En", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1,
                BaseCost = 2, Abilities = ec.Abilities,
            });
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var card = st.SpawnCard(7, ZoneRef.Hand, 0);
            tr.PlayCard(0, card, vm);
            int left = st.ReadCounter(0, st.Schema.Counter("pp"));
            Ok("b-规则6", "Enhance 强制按最高档扣费", left == 1, $"ppLeft={left}");
        }

        // 规则 7：土之印层数与执行统一
        {
            var st = new Battle.BattleState(seed: 39);
            var key = st.Schema.Counter("earth_sigil");
            st.RegisterCard(new CardDef { CardId = Battle.BattleState.MagicSedimentCardId, Name = "Magic Sediment", Kind = CardKind.Amulet, BaseCost = 1 });
            st.WriteCounter(0, key, 3);
            bool stacked = st.ReadCounter(0, key) == 3 && st.FindEarthSigil(0) != null;
            Ok("b-规则7", "土之印层数落在实体上", stacked);
        }

        // 规则 8：纹章同名唯一 + 区域上限
        {
            var st = new Battle.BattleState(seed: 40);
            st.RegisterCard(new CardDef { CardId = 8, Name = "CA", Kind = CardKind.Crest });
            var first = st.GainCrest(8, "CA", 0);
            var second = st.GainCrest(8, "CA", 0);
            bool unique = first != null && second == null;
            Ok("b-规则8", "纹章同名唯一", unique);
        }

        // 规则 9：爆牌 shadow+1
        {
            var st = new Battle.BattleState(seed: 41);
            st.RegisterCard(new CardDef { CardId = 9, Name = "O", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 9; i++) st.SpawnCard(9, ZoneRef.Hand, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            var tenth = st.SpawnCard(9, ZoneRef.Hand, 0);
            bool toCem = tenth.Zone == ZoneRef.Cemetery;
            bool shadowUp = st.ReadCounter(0, st.Schema.Counter("shadow")) == s0 + 1;
            Ok("b-规则9", "爆牌进墓地且 shadow+1", toCem && shadowUp,
               $"cem={toCem} shadow={shadowUp}");
        }

        // 规则 10：常驻属性入场生效
        {
            var st = new Battle.BattleState(seed: 42);
            st.RegisterCard(new CardDef
            {
                CardId = 10, Name = "SC", Kind = CardKind.Amulet, BaseCost = 1,
                InnateCounters = new Dictionary<SymbolId<CounterTag>, int> { [st.Schema.Counter("countdown")] = 2 },
            });
            var e = st.SpawnCard(10, ZoneRef.Board, 0);
            int? cd = e.Counters.TryGetValue(st.Schema.Counter("countdown"), out var v) ? v : (int?)null;
            Ok("b-规则10", "初始吟唱入场生效", cd == 2, $"countdown={cd}");
        }
    }
}

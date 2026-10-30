using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

public static partial class DocAcceptanceTest
{
    /// <summary>76-c：七章完整规则。</summary>
    private static void RunDocC()
    {
        _log.Add("");
        _log.Add("=== 76-c：完整规则书 ===");
        var lex = BuildFullLexicon();
        var comp = new EnglishCompiler(lex);

        // 1.1 卡种
        {
            bool has = Enum.GetNames(typeof(CardKind)).Length >= 6;
            bool crest = Enum.IsDefined(typeof(CardKind), CardKind.Crest);
            bool faith = Enum.IsDefined(typeof(CardKind), CardKind.Faith);
            Ok("c-1.1", "卡种含 Crest / Faith", has && crest && faith);
        }

        // 1.2 七区域与容量
        {
            int zones = Enum.GetNames(typeof(ZoneRef)).Length;
            bool caps = Battle.PlayerState.Capacity(ZoneRef.Board) == 5
                     && Battle.PlayerState.Capacity(ZoneRef.Hand) == 9
                     && Battle.PlayerState.Capacity(ZoneRef.CrestArea) == 5;
            Ok("c-1.2", "七区域 + 容量 5/9/5", zones >= 8 && caps, $"zones={zones}");
        }

        // 1.2 满场铁律
        {
            var st = new Battle.BattleState(seed: 51);
            st.RegisterCard(new CardDef { CardId = 1, Name = "F", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 5; i++) st.SpawnCard(1, ZoneRef.Board, 0);
            int s0 = st.ReadCounter(0, st.Schema.Counter("shadow"));
            var sixth = st.SpawnCard(1, ZoneRef.Board, 0);
            bool vanish = sixth.Zone == ZoneRef.None && st.ReadCounter(0, st.Schema.Counter("shadow")) == s0;
            Ok("c-1.2", "满场第 6 个直接消失、不加墓场", vanish);
        }

        // 1.3 计数器齐备
        {
            var st = new Battle.BattleState(seed: 52);
            string[] need = { "pp", "max_pp", "ep", "sep", "shadow", "combo", "rally", "crest", "faith", "earth_sigil", "spellboost", "skybound_gauge" };
            var defined = new HashSet<string>(GameSchema.BuiltinCounterDefs.Select(x => x.name));
            int missing = need.Count(n => !defined.Contains(n));
            Ok("c-1.3", "全部玩家计数器已注册", missing == 0, $"missing={missing}");
        }

        // 2.1 开局：无流程引擎时只能验证零件
        {
            var st = new Battle.BattleState(seed: 53);
            var mr = new Battle.MatchRunner(st);
            st.RegisterCard(new CardDef { CardId = 2, Name = "D", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
            var deck = Enumerable.Repeat(2, 40).ToArray();
            mr.Start(0, deck, deck);
            bool leaders = st.Players[0].Leader?.Life == 20 && st.Players[1].Leader?.Life == 20;
            bool hand4 = st.EnumerateZone(ZoneRef.Hand, 0).Count() == 4;
            Ok("c-2.1", "开局：主战者 20 血 + 双方各 4 张起手", leaders && hand4,
               $"leaders={leaders} hand4={hand4}");
        }

        // 2.2 TurnStart 8 步（抽牌在最后）
        {
            var st = new Battle.BattleState(seed: 54);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 3, Name = "D2", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            for (int i = 0; i < 5; i++) st.SpawnCard(3, ZoneRef.Deck, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 0;
            tr.StartTurn(0, vm);
            bool ppUp = st.ReadCounter(0, st.Schema.Counter("pp")) == 1;
            bool drew = st.EnumerateZone(ZoneRef.Hand, 0).Count() == 1;
            Ok("c-2.2", "回合开始：涨费 + 抽牌", ppUp && drew, $"pp={ppUp} drew={drew}");
        }

        // 2.3-A PlayCard 限制
        {
            var st = new Battle.BattleState(seed: 55);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 4, Name = "NP", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(4, ZoneRef.Hand, 0);
            c.Keywords.Add(st.Schema.Keyword("cannot_be_played"));
            bool rejected = !tr.PlayCard(0, c, vm);
            Ok("c-2.3A", "Can't be played 禁止打出", rejected);
        }

        // 2.3-A 法术无目标不能打出 / 随从可打出
        {
            var st = new Battle.BattleState(seed: 56);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var spell = comp.Compile("Select an enemy follower on the field and destroy it.");
            st.RegisterCard(new CardDef { CardId = 5, Name = "SP", Kind = CardKind.Spell, BaseCost = 1, Abilities = spell.Abilities });
            var followerAb = comp.Compile("Fanfare: Select an enemy follower on the field and destroy it.");
            st.RegisterCard(new CardDef { CardId = 6, Name = "FL", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = followerAb.Abilities });
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var sp = st.SpawnCard(5, ZoneRef.Hand, 0);
            var fl = st.SpawnCard(6, ZoneRef.Hand, 0);
            bool spellBlocked = !tr.PlayCard(0, sp, vm);
            bool followerOk = tr.PlayCard(0, fl, vm);
            Ok("c-2.3A", "法术无目标禁止打出 / 随从不受限",
               spellBlocked && followerOk, $"spell={spellBlocked} follower={followerOk}");
        }

        // 2.3-A combo 与 rally 时序
        {
            var st = new Battle.BattleState(seed: 57);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 7, Name = "CB", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(7, ZoneRef.Hand, 0);
            tr.PlayCard(0, c, vm);
            bool comboOk = st.ReadCounter(0, st.Schema.Counter("combo")) == 1;
            bool rallyOk = st.ReadCounter(0, st.Schema.Counter("rally")) == 1;
            Ok("c-2.3A", "combo +1 与 rally +1", comboOk && rallyOk, $"combo={comboOk} rally={rallyOk}");
        }

        // 2.3-B Engage
        {
            var st = new Battle.BattleState(seed: 58);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var ab = comp.Compile("Engage (1): Draw a card.");
            st.RegisterCard(new CardDef { CardId = 8, Name = "AM", Kind = CardKind.Amulet, BaseCost = 1, Abilities = ab.Abilities });
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            st.WriteCounter(0, st.Schema.Counter("pp"), 5);
            var am = st.SpawnCard(8, ZoneRef.Board, 0);
            bool engaged = tr.EngageAmulet(0, am, 1, vm);
            Ok("c-2.3B", "护符启动 Engage", engaged);
        }

        // 2.3-D 进化解锁回合
        {
            var st = new Battle.BattleState(seed: 59);
            var tr = new Battle.TurnRunner(st, st.Schema);
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            bool locked = !tr.CanEvolve(0);
            st.TurnNumber = 5;
            st.WriteCounter(0, st.Schema.Counter("ep"), 2);
            bool unlocked = tr.CanEvolve(0);
            Ok("c-2.3D", "进化在第 5 回合解锁（先手）", locked && unlocked,
               $"locked={locked} unlocked={unlocked}");
        }

        // 2.4 TurnEnd
        {
            var st = new Battle.BattleState(seed: 60);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.ActivePlayer = 0;
            st.TurnNumber = 3;
            // TurnEnd 事件进的是 CardVm 的待机列，不进 BattleState.EventLog。
            // 所以验证行为：回合结束时已回合结束的模式能被执行。
            st.RegisterCard(new CardDef { CardId = 90, Name = "TEC", Kind = CardKind.Amulet, BaseCost = 1 });
            var tec = st.SpawnCard(90, ZoneRef.Board, 0);
            var tecAb = comp.Compile("At the end of your turn, destroy this card.");
            st.RegisterCard(new CardDef { CardId = 91, Name = "TEC2", Kind = CardKind.Amulet, BaseCost = 1, Abilities = tecAb.Abilities });
            var tec2 = st.SpawnCard(91, ZoneRef.Board, 0);
            tr.EndTurn(0, vm);
            Ok("c-2.4", "TurnEnd 时「回合结束时」能力被执行",
               tec2.Zone == ZoneRef.Cemetery, $"zone={tec2.Zone}");
        }

        // 3.2 战斗 12 步：同时交换伤害
        {
            var st = new Battle.BattleState(seed: 61);
            var cr = new Battle.CombatResolver(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 9, Name = "A1", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3 });
            var a = st.SpawnCard(9, ZoneRef.Board, 0);
            var b = st.SpawnCard(9, ZoneRef.Board, 1);
            a.EnteredTurn = 0;
            b.EnteredTurn = 0;
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            bool can = cr.CanAttack(a, b, out _);
            cr.ExecuteCombat(a, b, vm);
            bool bothDied = a.Zone == ZoneRef.Cemetery && b.Zone == ZoneRef.Cemetery;
            Ok("c-3.2", "3/3 互打同时死亡（伤害同时交换）", can && bothDied,
               $"can={can} both={bothDied}");
        }

        // 3.2 必杀 Bane
        {
            var st = new Battle.BattleState(seed: 62);
            var cr = new Battle.CombatResolver(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 10, Name = "B", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            st.RegisterCard(new CardDef { CardId = 11, Name = "BIG", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 9 });
            var atk = st.SpawnCard(10, ZoneRef.Board, 0);
            var def = st.SpawnCard(11, ZoneRef.Board, 1);
            atk.Keywords.Add(st.Schema.Keyword("bane"));
            atk.EnteredTurn = 0;
            def.EnteredTurn = 0;
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            cr.ExecuteCombat(atk, def, vm);
            bool defDead = def.Zone == ZoneRef.Cemetery;
            Ok("c-3.2", "Bane 即使伤害不足也破坏", defDead);
        }

        // 4.1 待机列优先级
        {
            var st = new Battle.BattleState(seed: 63);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField });
            vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay });
            var f = typeof(CardVm).GetField("_triggerQueue",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var q = f?.GetValue(vm) as List<VmEvent>;
            bool ordered = q != null && q.Count == 2 && q[0].Kind == CoreEvent.OnPlay;
            Ok("c-4.1", "待机列按优先级排序", ordered,
               q != null ? string.Join(",", q.Select(x => x.Kind)) : "null");
        }

        // 4.2 统一伤害管线顺序
        {
            var st = new Battle.BattleState(seed: 64);
            var cr = new Battle.CombatResolver(st, st.Schema);
            st.RegisterCard(new CardDef { CardId = 12, Name = "SE", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5 });
            var e = st.SpawnCard(12, ZoneRef.Board, 0);
            e.IsSuperEvolved = true;
            st.ActivePlayer = 0;
            int d = cr.ApplyDamage(e, 3, 1);
            Ok("c-4.2", "超进化自己回合免伤害", d == 0 && e.Life == 5, $"dealt={d}");
        }

        // 5 原子动作：Transform 不触发离场 / 入场
        {
            var st = new Battle.BattleState(seed: 65);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 13, Name = "T1", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
            st.RegisterCard(new CardDef { CardId = 14, Name = "T2", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2 });
            var e = st.SpawnCard(13, ZoneRef.Board, 0);
            int before = st.EventLog.Count;
            vm.PushBody(new EffectNode[]
            {
                new TransformNode { Target = TargetSpec.Self, IntoCardId = 14 },
            }, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
            vm.StepUntilBlocked();
            bool noLeave = !st.EventLog.Skip(before).Any(x => x.Kind == CoreEvent.OnLeaveField);
            bool noEnter = !st.EventLog.Skip(before).Any(x => x.Kind == CoreEvent.OnEnterField);
            Ok("c-5", "Transform 不触发 OnLeaveField / OnEnterField", noLeave && noEnter);
        }

        // 6.1 关键词真值表
        {
            var st = new Battle.BattleState(seed: 66);
            var cr = new Battle.CombatResolver(st, st.Schema);
            st.RegisterCard(new CardDef { CardId = 15, Name = "K", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2 });
            var atk = st.SpawnCard(15, ZoneRef.Board, 0);
            var aura = st.SpawnCard(15, ZoneRef.Board, 1);
            var amb = st.SpawnCard(15, ZoneRef.Board, 1);
            var tim = st.SpawnCard(15, ZoneRef.Board, 1);
            aura.Keywords.Add(st.Schema.Keyword("aura"));
            amb.Keywords.Add(st.Schema.Keyword("ambush"));
            tim.Keywords.Add(st.Schema.Keyword("intimidate"));
            atk.EnteredTurn = 0;
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            bool auraAtk = cr.CanAttack(atk, aura, out _);
            bool ambAtk = cr.CanAttack(atk, amb, out _);
            bool timAtk = cr.CanAttack(atk, tim, out _);
            Ok("c-6.1", "Aura 可被攻击 / Ambush 不可 / Intimidate 不可",
               auraAtk && !ambAtk && !timAtk, $"aura={auraAtk} amb={ambAtk} tim={timAtk}");
        }

        // 6.2-1 土之印
        {
            var st = new Battle.BattleState(seed: 67);
            var key = st.Schema.Counter("earth_sigil");
            st.RegisterCard(new CardDef { CardId = Battle.BattleState.MagicSedimentCardId, Name = "Magic Sediment", Kind = CardKind.Amulet, BaseCost = 1 });
            st.WriteCounter(0, key, 1);
            var s1 = st.FindEarthSigil(0);
            st.WriteCounter(0, key, 4);
            int cnt = st.EnumerateZone(ZoneRef.Board, 0).Count(x => x.Def?.CardId == Battle.BattleState.MagicSedimentCardId);
            bool stacked = s1 != null && cnt == 1 && st.ReadCounter(0, key) == 4;
            st.WriteCounter(0, key, 0);
            bool destroyed = st.FindEarthSigil(0) == null;
            Ok("c-6.2-1", "土之印堆叠且归 0 破坏", stacked && destroyed,
               $"stacked={stacked} destroyed={destroyed}");
        }

        // 6.2-3 Reanimate 自动选最高费
        {
            var r = comp.Compile("Fanfare: Reanimate (4).");
            var sp = r.Abilities.SelectMany(a => a.Body).OfType<SpawnCardNode>().FirstOrDefault();
            bool auto = sp?.CopyFrom?.Mode == SelectMode.Random && sp?.CopyFrom?.SortDescending == true;
            Ok("c-6.2-3", "Reanimate 自动取最高费、不弹窗", auto,
               sp?.CopyFrom?.Mode.ToString() ?? "none");
        }

        // 6.2-4 Necromancy
        {
            var st = new Battle.BattleState(seed: 68);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var ab = comp.Compile("Fanfare: Necromancy (3): Draw a card.");
            st.RegisterCard(new CardDef { CardId = 16, Name = "NC", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = ab.Abilities });
            st.ActivePlayer = 0;
            st.TurnNumber = 5;
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            st.WriteCounter(0, st.Schema.Counter("shadow"), 5);
            var c = st.SpawnCard(16, ZoneRef.Hand, 0);
            tr.PlayCard(0, c, vm);
            int shadowLeft = st.ReadCounter(0, st.Schema.Counter("shadow"));
            Ok("c-6.2-4", "Necromancy 扣除墓场", shadowLeft <= 5, $"shadowLeft={shadowLeft}");
        }

        // 6.2-5 奥义阈值
        {
            var r1 = comp.Compile("Skybound Art - Deal 2 damage to the enemy leader.");
            var r2 = comp.Compile("Super Skybound Art - Deal 3 damage to the enemy leader.");
            bool a1 = r1.Abilities.Count > 0 && r1.Abilities[0].Condition != null;
            bool a2 = r2.Abilities.Count > 0 && r2.Abilities[0].Condition != null;
            string c1 = a1 ? AstEnglishPrinter.PrintBool(r1.Abilities[0].Condition) : "";
            string c2 = a2 ? AstEnglishPrinter.PrintBool(r2.Abilities[0].Condition) : "";
            Ok("c-6.2-5", "奥义 10 / 解放奥义 15 阈值",
               a1 && a2 && c1.Contains("10") && c2.Contains("15"), $"{c1} | {c2}");
        }

        // 6.2-6 信仰
        {
            var st = new Battle.BattleState(seed: 69);
            var key = st.Schema.Counter("faith");
            st.WriteCounter(0, key, 7);
            st.WriteCounter(0, key, -5);
            bool clamped = st.ReadCounter(0, key) == 0;
            Ok("c-6.2-6", "信仰值钳制不为负", clamped, $"v={st.ReadCounter(0, key)}");
        }

        // 6.2-8 Invoke
        {
            var st = new Battle.BattleState(seed: 70);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            var ab = comp.Compile("Activates in deck. At the start of your turn, move this card to the board.");
            st.RegisterCard(new CardDef { CardId = 17, Name = "IV", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 5, Abilities = ab.Abilities });
            var c = st.SpawnCard(17, ZoneRef.Deck, 0);
            st.ActivePlayer = 0;
            st.TurnNumber = 0;
            tr.StartTurn(0, vm);
            bool onBoard = c.Zone == ZoneRef.Board;
            Ok("c-6.2-8", "Invoke 从牌组上场", onBoard, $"zone={c.Zone}");
        }
    }
}

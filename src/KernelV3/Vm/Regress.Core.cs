using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 核心回路回归：召唤失调 / 状态结算（SBA）/ 触发代词 / 属性减半
//
// 本轮钉住的四处缺口都在**每次原子动作之后**都会经过的回路上，
// 所以每一条都必须先钉现状再改：
//
//   1 召唤失调：EnteredTurn 只有 PlayCard 会写，能力召唤出来的实体恒为 0，
//     而 TurnNumber 已经 5+ —— CanAttack 认为它「不是本回合入场的」，
//     召唤出的随从当回合就能攻击主战者。
//   2 护符被 SBA 秒杀：ProcessStateBasedActions 把场上所有 Life<=0 的实体
//     当阵亡，而护符没有生命值概念（Life 恒 0），拍下来第一个 SBA 就进墓地。
//   3 触发主体代词：$self / $trigger 的主体过滤器是**槽位**目标，
//     判定作用域没带 Source / Trigger 锚点，池子恒空 ——
//     「本卡受到伤害时」这类监听器一次都不触发。
//   4 Halve 目标：生命减半必须对真实目标取 MaxLife，奇数向上取整。
// ============================================================================
public static partial class Regress
{
    public static string RunCore()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        SummonSickness(Chk);
        AmuletSurvivesStateBasedActions(Chk);
        TriggerSubjectSlots(Chk);
        HalveDefenseTargets(Chk);

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Core " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    // ---------- 1 召唤失调 ----------

    private static void SummonSickness(Action<string, bool, string> chk)
    {
        var st = CoreState(2100, out _, out _);
        st.RegisterCard(CoreCard(100, "Plain", CardKind.Follower, 2, 2));
        st.RegisterCard(CoreCard(101, "Stormer", CardKind.Follower, 2, 2));
        st.RegisterCard(CoreCard(102, "Rusher", CardKind.Follower, 2, 2));
        var cr = new CombatResolver(st, st.Schema);

        // Summon 路径（不是 PlayCard）：SpawnCard 造出来的实体走 PlaceInZone 盖章。
        var plain = st.SpawnCard(100, ZoneRef.Board, 0);
        var enemyFollower = st.SpawnCard(100, ZoneRef.Board, 1);
        chk("core-1a 能力召唤的随从带上 EnteredTurn",
            plain.EnteredTurn == st.TurnNumber,
            "TurnNumber=" + st.TurnNumber + " EnteredTurn=" + plain.EnteredTurn);

        bool toLeader = cr.CanAttack(plain, st.Players[1].Leader, out var rLeader);
        chk("core-1b 召唤当回合不能攻击主战者", !toLeader, "reason=" + rLeader);

        bool toFollower = cr.CanAttack(plain, enemyFollower, out var rFollower);
        chk("core-1c 召唤当回合也不能攻击随从", !toFollower, "reason=" + rFollower);

        // 下一回合：TurnNumber 前进一轮后，同一张随从应当恢复攻击权。
        st.TurnNumber++;
        bool nextRound = cr.CanAttack(plain, st.Players[1].Leader, out var rNext);
        chk("core-1d 下一回合可以攻击主战者", nextRound, "reason=" + rNext);

        // 反向：上一回合入场的随从不能被误判成召唤失调。
        var storm = st.SpawnCard(101, ZoneRef.Board, 0);
        storm.Keywords.Add(st.Schema.Keyword("storm"));
        bool stormLeader = cr.CanAttack(storm, st.Players[1].Leader, out var rStorm);
        chk("core-1e 疾走随从召唤当回合可以攻击主战者", stormLeader, "reason=" + rStorm);

        var rush = st.SpawnCard(102, ZoneRef.Board, 0);
        rush.Keywords.Add(st.Schema.Keyword("rush"));
        bool rushFollower = cr.CanAttack(rush, enemyFollower, out var rRushF);
        bool rushLeader = cr.CanAttack(rush, st.Players[1].Leader, out var rRushL);
        chk("core-1f 突进随从当回合能打随从、不能打主战者",
            rushFollower && !rushLeader,
            "打随从=" + rushFollower + " 打主战者=" + rushLeader + " reason=" + rRushL);
    }

    // ---------- 2 护符不会被 SBA 秒杀 ----------

    private static void AmuletSurvivesStateBasedActions(Action<string, bool, string> chk)
    {
        {
            var st = CoreState(2200, out _, out _);
            st.RegisterCard(CoreCard(200, "Plain Amulet", CardKind.Amulet, 0, 0));
            var amulet = st.SpawnCard(200, ZoneRef.Board, 0);
            int destroyed = st.ProcessStateBasedActions();
            chk("core-2a 普通护符不会被状态结算破坏",
                amulet.Zone == ZoneRef.Board && destroyed == 0,
                "zone=" + amulet.Zone + " destroyed=" + destroyed);
        }

        {
            // 土之印层数归 0 仍然破坏（既有规则不得退化）。
            var st = CoreState(2201, out _, out _);
            CoreRegisterSigil(st);
            st.WriteCounter(0, st.Schema.Counter("earth_sigil"), 2);
            var sigil = st.FindEarthSigil(0);
            bool alive = sigil != null && sigil.Zone == ZoneRef.Board;
            st.WriteCounter(0, st.Schema.Counter("earth_sigil"), 0);
            chk("core-2b 土之印层数归 0 仍然破坏",
                alive && sigil.Zone == ZoneRef.Cemetery,
                "归零前在场=" + alive + " 归零后 zone=" + (sigil?.Zone.ToString() ?? "null"));
        }

        {
            // 土之印 / Clue 仍然免能力破坏（R-6.2）。
            var st = CoreState(2202, out _, out _);
            CoreRegisterSigil(st);
            st.WriteCounter(0, st.Schema.Counter("earth_sigil"), 2);
            var sigil = st.FindEarthSigil(0);
            st.DestroyEntity(sigil);
            chk("core-2c 土之印仍然免能力破坏",
                sigil != null && sigil.Zone == ZoneRef.Board,
                "zone=" + (sigil?.Zone.ToString() ?? "null"));
        }

        {
            // Countdown 归 0 仍然破坏。
            var st = CoreState(2203, out var tr, out var vm);
            var cdKey = st.Schema.Counter("countdown");
            st.RegisterCard(new CardDef
            {
                CardId = 201, Name = "Countdown Amulet", Kind = CardKind.Amulet, BaseCost = 1,
                Abilities = new[]
                {
                    new AbilityDef
                    {
                        Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                        Condition = BoolExpr.True,
                        Costs = new[] { new CostSpec
                        {
                            Kind = CostKind.Countdown, Amount = NumExpr.Const(1), Label = "countdown",
                        } },
                        Body = Array.Empty<EffectNode>(),
                    },
                },
            });
            var amulet = st.SpawnCard(201, ZoneRef.Board, 0);
            bool hadCountdown = amulet.Counters.TryGetValue(cdKey, out var cd) && cd == 1;
            tr.StartTurn(0, vm);
            chk("core-2d 吟唱归 0 仍然破坏",
                hadCountdown && amulet.Zone == ZoneRef.Cemetery,
                "初始吟唱=" + (hadCountdown ? "1" : "缺") + " zone=" + amulet.Zone);
        }

        {
            // 超进化随从的生命归零仍要破坏：SBA 是「已经死了」的收尾，
            // 不受 DestroyEntity 的超进化免破坏拦截。
            var st = CoreState(2204, out _, out _);
            st.RegisterCard(CoreCard(202, "Evolved", CardKind.Follower, 3, 3));
            var follower = st.SpawnCard(202, ZoneRef.Board, 0);
            follower.IsSuperEvolved = true;
            follower.Life = 0;
            st.ProcessStateBasedActions();
            chk("core-2e 超进化随从生命归零仍被状态结算破坏",
                follower.Zone == ZoneRef.Cemetery,
                "zone=" + follower.Zone);
        }
    }

    // ---------- 3 触发主体代词 ----------

    private static void TriggerSubjectSlots(Action<string, bool, string> chk)
    {
        var comp = new EnglishCompiler(CoreLexicon());

        {
            // "give it +1/+1" 的 it 必须指向刚进场的那一张，不是监听者自己。
            var st = CoreState(2300, out var tr, out var vm);
            var r = comp.Compile("Whenever an allied follower enters the field, give it +1/+1.");
            st.RegisterCard(CoreCard(300, "Watcher", CardKind.Follower, 1, 1, r.Abilities));
            st.RegisterCard(CoreCard(301, "Small", CardKind.Follower, 1, 1));
            var watcher = st.SpawnCard(300, ZoneRef.Board, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var played = st.SpawnCard(301, ZoneRef.Hand, 0);
            st.ClearEvents();
            // 布置阶段造的 watcher 入场也会发 OnEnterField；
            // 那不是本用例要测的事件，先丢掉待机触发再开始。
            vm.DropPendingTriggers();
            tr.PlayCard(0, played, vm);
            chk("core-3a 进场触发的 it 指向事件主体",
                played.Attack == 2 && played.Life == 2 && watcher.Attack == 1,
                "进场=" + played.Attack + "/" + played.Life + " 监听者=" + watcher.Attack + "/" + watcher.Life);
        }

        {
            // $self：只有受到伤害的那一张自己涨，旁观者不动。
            var st = CoreState(2301, out _, out var vm);
            var r = comp.Compile("Whenever this follower takes damage, give this follower +1/+0.");
            st.RegisterCard(CoreCard(310, "Watched", CardKind.Follower, 2, 5, r.Abilities));
            st.RegisterCard(CoreCard(311, "Bystander", CardKind.Follower, 2, 5));
            var watched = st.SpawnCard(310, ZoneRef.Board, 0);
            var bystander = st.SpawnCard(311, ZoneRef.Board, 0);
            st.ClearEvents();
            st.ApplyDamage(watched, 2, 1);
            vm.StepUntilBlocked();
            chk("core-3b 受伤触发只加受伤害的那张自己",
                watched.Attack == 3 && bystander.Attack == 2,
                "受伤者=" + watched.Attack + "/" + watched.Life + " 旁观者=" + bystander.Attack + "/" + bystander.Life);
        }

        {
            // $trigger 不能被泛化成「所有场景都用事件主体」：
            // "Whenever this follower attacks" 里的 this follower 必须仍是本卡。
            var st = CoreState(2302, out _, out var vm);
            var r = comp.Compile("Whenever this follower attacks, give this follower +1/+0.");
            st.RegisterCard(CoreCard(320, "Attacker", CardKind.Follower, 2, 5, r.Abilities));
            st.RegisterCard(CoreCard(321, "Other", CardKind.Follower, 2, 5));
            var attacker = st.SpawnCard(320, ZoneRef.Board, 0);
            var other = st.SpawnCard(321, ZoneRef.Board, 0);
            var defender = st.SpawnCard(321, ZoneRef.Board, 1);
            attacker.EnteredTurn = 0;
            other.EnteredTurn = 0;
            defender.EnteredTurn = 0;
            st.ClearEvents();
            new CombatResolver(st, st.Schema).ExecuteCombat(attacker, defender, vm);
            chk("core-3c 攻击时触发的 this follower 仍指本卡",
                attacker.Attack == 3 && other.Attack == 2,
                "攻击者=" + attacker.Attack + " 旁观者=" + other.Attack);
        }
    }

    // ---------- 4 Halve 的目标 ----------

    private static void HalveDefenseTargets(Action<string, bool, string> chk)
    {
        var comp = new EnglishCompiler(CoreLexicon());

        {
            var st = CoreState(2400, out _, out var vm);
            var r = comp.Compile("Fanfare: Halve your leader's defense.");
            st.RegisterCard(CoreCard(400, "Halver", CardKind.Follower, 1, 1, r.Abilities));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var me = st.SpawnCard(400, ZoneRef.Hand, 0);
            new TurnRunner(st, st.Schema).PlayCard(0, me, vm);
            chk("core-4a Halve your leader's defense（21→11）",
                st.Players[0].Leader.Life == 11 && st.Players[1].Leader.Life == 21,
                "我方=" + st.Players[0].Leader.Life + " 敌方=" + st.Players[1].Leader.Life);
        }

        {
            var st = CoreState(2401, out _, out var vm);
            var r = comp.Compile("Fanfare: Halve an enemy follower's defense.");
            st.RegisterCard(CoreCard(401, "Enemy", CardKind.Follower, 5, 5));
            st.RegisterCard(CoreCard(402, "Halver", CardKind.Follower, 1, 1, r.Abilities));
            var enemy = st.SpawnCard(401, ZoneRef.Board, 1);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var me = st.SpawnCard(402, ZoneRef.Hand, 0);
            new TurnRunner(st, st.Schema).PlayCard(0, me, vm);
            // 5 → 3：奇数向上取整。
            chk("core-4b Halve an enemy follower's defense（5→3，向上取整）",
                enemy.Life == 3 && enemy.Zone == ZoneRef.Board,
                "life=" + enemy.Life + " zone=" + enemy.Zone);
        }

        {
            // 主战者目标同样生效，且不误伤对方。
            var st = CoreState(2402, out _, out var vm);
            var r = comp.Compile("Fanfare: Halve the enemy leader's defense.");
            st.RegisterCard(CoreCard(403, "Halver", CardKind.Follower, 1, 1, r.Abilities));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var me = st.SpawnCard(403, ZoneRef.Hand, 0);
            new TurnRunner(st, st.Schema).PlayCard(0, me, vm);
            chk("core-4c Halve the enemy leader's defense（21→11）",
                st.Players[1].Leader.Life == 11 && st.Players[0].Leader.Life == 21,
                "敌方=" + st.Players[1].Leader.Life + " 我方=" + st.Players[0].Leader.Life);
        }
    }

    // ---------- 公共设施 ----------

    private static BattleState CoreState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new BattleState(seed: seed);
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 21 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0);
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1);
        l0.Life = 21; l0.MaxLife = 21;
        l1.Life = 21; l1.MaxLife = 21;
        st.Players[0].Leader = l0;
        st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        vm.CurrentTurnOwner = 0;
        return st;
    }

    private static CardDef CoreCard(int id, string name, CardKind kind, int atk, int life,
                                    IReadOnlyList<AbilityDef> abilities = null)
        => new CardDef
        {
            CardId = id, Name = name, Kind = kind,
            BaseAttack = atk, BaseLife = life, BaseCost = 1,
            // CreateEntity 会遍历 Def.Abilities，null 会直接炸；无能力的卡给空数组。
            Abilities = abilities ?? Array.Empty<AbilityDef>(),
        };

    private static void CoreRegisterSigil(BattleState st)
        => st.RegisterCard(new CardDef
        {
            CardId = BattleState.MagicSedimentCardId, Name = "Magic Sediment",
            Kind = CardKind.Amulet, BaseCost = 1,
        });

    private static Lexicon CoreLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

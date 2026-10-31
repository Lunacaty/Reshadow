using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 77 号审计 · 问题 3 / 问题 4 的回归用例
//
// 问题 3：CardVm.FlushNextTrigger 用只有 Controller 的空作用域求值 ab.Condition，
//         依赖 Source / Trigger 的条件（<ev> 的 HasState(Evolved, Self)、
//         手牌实体上的 skybound_gauge 奥义计数器）在判定阶段全部失明。
// 问题 4：VmEval.ResolveRef 的 Where 过滤用 GetLive 逐条丢弃历史池候选
//         （DestroyEntity 已把 EntrySerial 顶掉，永远取不到活体），
//         且 Sort 之后接 Random 会把顺序整个洗掉。
// ============================================================================

public static partial class Regress
{
    private static int _pass, _fail;
    private static readonly List<string> _log = new();

    private static void Ok(string id, string title, bool cond, string detail = "")
    {
        if (cond) _pass++; else _fail++;
        _log.Add((cond ? "  [OK]   " : "  [FAIL] ") + "[" + id + "] " + title
                 + (string.IsNullOrEmpty(detail) ? "" : " —— " + detail));
    }

    private static Lexicon MkLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }

    private static Battle.BattleState NewState(int seed)
    {
        var st = new Battle.BattleState(seed: seed);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        return st;
    }

    public static string RunIssue34()
    {
        _pass = _fail = 0;
        _log.Clear();

        var comp = new EnglishCompiler(MkLexicon());

        ReanimateTakesHighestCost(comp);
        RandomHighestAttackOnlyPicksMax(comp);
        SkyboundCounterVisibleInCondition(comp);
        EvolvedSelfConditionUsesTriggerScope();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine();
        sb.AppendLine("=== 回归 Issue34 " + _pass + "/" + (_pass + _fail) + " ===");
        foreach (var l in _log) sb.AppendLine(l);
        return sb.ToString();
    }

    /// <summary>
    /// Fanfare: Reanimate (4)：墓地里有 2 费与 4 费随从时，
    /// 上场的必须是 4 费那个，而不是施放这张卡自己的实体。
    /// </summary>
    private static void ReanimateTakesHighestCost(EnglishCompiler comp)
    {
        var st = NewState(3401);
        var tr = new Battle.TurnRunner(st, st.Schema);
        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 1, Name = "TwoDrop", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 2 });
        st.RegisterCard(new CardDef { CardId = 2, Name = "FourDrop", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 4 });
        var ab = comp.Compile("Fanfare: Reanimate (4).");
        st.RegisterCard(new CardDef { CardId = 3, Name = "Reanimator", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = ab.Abilities });

        var two = st.SpawnCard(1, ZoneRef.Board, 0);
        var four = st.SpawnCard(2, ZoneRef.Board, 0);
        st.DestroyEntity(two);
        st.DestroyEntity(four);

        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(3, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);

        var board = st.EnumerateZone(ZoneRef.Board, 0).ToList();
        bool reanimated = board.Any(b => b.Def != null && b.Def.CardId == 2);
        bool noSelf = board.Count(b => b.Def != null && b.Def.CardId == 1) == 0;
        Ok("i34-1", "Reanimate(4) 从墓地取最高费随从", reanimated && noSelf,
           "board=[" + string.Join(",", board.Select(b => (b.Def?.Name ?? "?") + "/c" + b.Cost)) + "]");
    }

    /// <summary>
    /// "destroy a random enemy follower with the highest attack"：
    /// 极值随机只能落在并列最高攻的那个子集里，攻击力 2 的 B 一次都不该被选。
    /// </summary>
    private static void RandomHighestAttackOnlyPicksMax(EnglishCompiler comp)
    {
        var node = comp.Compile("Fanfare: Destroy a random enemy follower with the highest attack.")
                       .Abilities[0].Body;

        int killedA = 0, killedB = 0, neither = 0;
        for (int i = 0; i < 40; i++)
        {
            var st = NewState(5000 + i);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 11, Name = "A", Kind = CardKind.Follower, BaseAttack = 5, BaseLife = 5 });
            st.RegisterCard(new CardDef { CardId = 12, Name = "B", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2 });
            var a = st.SpawnCard(11, ZoneRef.Board, 1);
            var b = st.SpawnCard(12, ZoneRef.Board, 1);

            vm.PushBody(node, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
            vm.StepUntilBlocked();

            bool aDead = a.Zone != ZoneRef.Board;
            bool bDead = b.Zone != ZoneRef.Board;
            if (aDead && !bDead) killedA++;
            else if (bDead && !aDead) killedB++;
            else neither++;
        }

        Ok("i34-2", "极值随机只落在最高攻并列子集", killedA == 40 && killedB == 0 && neither == 0,
           "40 次重开：A(攻5) 被破坏 " + killedA + " 次，B(攻2) " + killedB + " 次，异常 " + neither);
    }

    /// <summary>
    /// 奥义：手牌实体自带 skybound_gauge 计数器。
    /// 条件求值阶段必须能读到实体计数器，否则恒假、效果永不发动。
    /// </summary>
    private static void SkyboundCounterVisibleInCondition(EnglishCompiler comp)
    {
        var ab = comp.Compile("Fanfare: Skybound Art - Deal 2 damage to the enemy leader.");
        int dmgAt0 = -1, dmgAt10 = -1;

        foreach (int gauge in new[] { 0, 10 })
        {
            var st = NewState(7000 + gauge);
            var tr = new Battle.TurnRunner(st, st.Schema);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            st.RegisterCard(new CardDef { CardId = 21, Name = "SBA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = ab.Abilities });
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(21, ZoneRef.Hand, 0);
            c.Counters[st.Schema.Counter("skybound_gauge")] = gauge;
            tr.PlayCard(0, c, vm);

            int dmg = 20 - st.Players[1].Leader.Life;
            if (gauge == 0) dmgAt0 = dmg; else dmgAt10 = dmg;
        }

        Ok("i34-3", "奥义计数器在条件判定阶段可读", dmgAt0 == 0 && dmgAt10 == 2,
           "gauge=0 伤害=" + dmgAt0 + "，gauge=10 伤害=" + dmgAt10);
    }

    /// <summary>
    /// 条件里的 Self 必须落到事件源实体：未进化时条件为假，进化后为真。
    /// </summary>
    private static void EvolvedSelfConditionUsesTriggerScope()
    {
        var dmg = new EnglishCompiler(MkLexicon()).Compile("Deal 3 damage to the enemy leader.").Abilities[0].Body;
        var st = NewState(8100);
        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 31, Name = "EvoCond", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
        var f = st.SpawnCard(31, ZoneRef.Board, 0);

        var gate = new AbilityDef
        {
            Trigger = new TriggerSpec { Event = CoreEvent.OnEnterField },
            Condition = new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self },
            Body = dmg,
        };
        st.GrantAbility(f, gate);

        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(f) });
        vm.StepUntilBlocked();
        int beforeEvolve = 20 - st.Players[1].Leader.Life;

        f.IsEvolved = true;
        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(f) });
        vm.StepUntilBlocked();
        int afterEvolve = 20 - st.Players[1].Leader.Life;

        Ok("i34-4", "条件里的 Self 绑定到事件源实体", beforeEvolve == 0 && afterEvolve == 3,
           "未进化伤害=" + beforeEvolve + "，进化后累计=" + afterEvolve);
    }
}

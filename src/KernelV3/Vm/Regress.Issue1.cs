using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// 77 号审计问题 1 的回归用例（致命级）：
/// InsteadWeaver.Merge 曾经把 cur 的 Trigger 与 cur 的 Condition 搬到能力顶层，
/// 于是「同一时机替代」的外层条件变成了替代条件本身 ——
/// "Fanfare: Deal 2. If you're in Overflow, deal 4 instead." 在未觉醒时
/// 整条能力被 FlushNextTrigger 跳过，Else 分支（打 2）成为死代码；
/// 「跨时机替代」则被合成的一条能力吞掉，另一个事件永远不触发。
/// </summary>
public static partial class Regress
{
    public static string RunIssue1()
    {
        var log = new List<string>();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) { pass++; log.Add("  [OK]   " + name); }
            else { fail++; log.Add("  [FAIL] " + name + (detail.Length > 0 ? "  —— " + detail : "")); }
        }

        var comp = new EnglishCompiler(BuildLex());

        // ---- 用例 1：同时机替代，外层 Trigger / Condition 必须保持不变 ----
        {
            var r = comp.Compile(
                "Fanfare: Deal 2 damage to the enemy leader. If you're in Overflow, deal 4 damage instead.");

            bool oneAbility = r.Abilities.Count == 1;
            var ab = oneAbility ? r.Abilities[0] : null;

            bool onPlay = ab?.Trigger?.Event == CoreEvent.OnPlay;
            bool outerCondClean = ab != null && IsTriviallyTrue(ab.Condition);

            var branch = ab?.Body.Count == 1 ? ab.Body[0] as ConditionalNode : null;
            var elseDmg = branch?.Else.OfType<DealDamageNode>().FirstOrDefault();
            var thenDmg = branch?.Then.OfType<DealDamageNode>().FirstOrDefault();
            bool bodyShape = branch != null
                             && elseDmg != null && IsConst(elseDmg.Amount, 2)
                             && thenDmg != null && IsConst(thenDmg.Amount, 4);
            // Else 必须真的打到敌方主战者（替代句自己没写目标，靠透镜继承）
            bool elseHitsLeader = elseDmg?.Target != null
                                  && elseDmg.Target.Owner == ZoneScope.Opponent
                                  && elseDmg.Target.Zone == ZoneRef.Leader;
            bool thenHitsLeader = thenDmg?.Target != null
                                  && thenDmg.Target.Owner == ZoneScope.Opponent
                                  && thenDmg.Target.Zone == ZoneRef.Leader;

            Check("1 同时机替代只产出 1 条能力", oneAbility,
                  "count=" + r.Abilities.Count + (r.Errors.Count > 0 ? " err=" + string.Join(";", r.Errors) : ""));
            Check("1 外层 Trigger 保持 OnPlay", onPlay, "event=" + ab?.Trigger?.Event);
            Check("1 外层 Condition 不含 max_pp（为 true / null）", outerCondClean,
                  "cond=" + (ab == null ? "null" : AstEnglishPrinter.PrintBool(ab.Condition)));
            Check("1 Body 是 1 个 ConditionalNode（Then 打 4 / Else 打 2）", bodyShape,
                  branch == null ? "no ConditionalNode" : "then=" + PrintAmount(thenDmg?.Amount) + " else=" + PrintAmount(elseDmg?.Amount));
            Check("1 Else 分支确实打敌方主战者 / Then 亦然", elseHitsLeader && thenHitsLeader,
                  "else=" + PrintTarget(elseDmg?.Target) + " then=" + PrintTarget(thenDmg?.Target));
        }

        // ---- 用例 2：实战门禁 —— 未觉醒打 2，觉醒打 4（不是 2+4=6） ----
        {
            var abilities = comp.Compile(
                "Fanfare: Deal 2 damage to the enemy leader. If you're in Overflow, deal 4 damage instead.").Abilities;

            int low = PlayAndReadEnemyLeader(abilities, maxPp: 3, seed: 1101);
            int high = PlayAndReadEnemyLeader(abilities, maxPp: 7, seed: 1102);

            Check("2 max_pp=3 打出后敌方主战者 20 -> 18", low == 18, "实际 " + low);
            Check("2 max_pp=7 打出后敌方主战者 20 -> 16", high == 16, "实际 " + high);
        }

        // ---- 用例 3：跨时机 A（OnPlay -> OnSuperEvolve）必须拆成两条 ----
        {
            var r = comp.Compile(
                "Fanfare: Restore 2 defense to your leader. Super-Evolve: Restore 4 defense instead.");

            bool two = r.Abilities.Count == 2;
            var evs = r.Abilities.Select(a => a.Trigger?.Event).ToList();
            bool split = two && evs[0] == CoreEvent.OnPlay && evs[1] == CoreEvent.OnSuperEvolve;

            var playAb = two ? r.Abilities[0] : null;
            var sevAb = two ? r.Abilities[1] : null;
            var playRestore = playAb?.Body.OfType<RestoreLifeNode>().FirstOrDefault();
            var sevRestore = sevAb?.Body.OfType<RestoreLifeNode>().FirstOrDefault();

            bool amounts = playRestore != null && IsConst(playRestore.Amount, 2)
                           && sevRestore != null && IsConst(sevRestore.Amount, 4);
            bool targetsOk = playRestore?.Target != null
                             && playRestore.Target.Owner == ZoneScope.Self
                             && playRestore.Target.Zone == ZoneRef.Leader
                             && sevRestore?.Target != null
                             && sevRestore.Target.Owner == ZoneScope.Self
                             && sevRestore.Target.Zone == ZoneRef.Leader;

            Check("3 跨时机 A 产出 2 条能力", two,
                  "count=" + r.Abilities.Count + " err=" + string.Join(";", r.Errors));
            Check("3 Trigger 分别是 OnPlay / OnSuperEvolve", split,
                  "events=" + string.Join(",", evs));
            Check("3 两条各自保留 2 / 4 点回复", amounts,
                  "play=" + PrintAmount(playRestore?.Amount) + " sev=" + PrintAmount(sevRestore?.Amount));
            Check("3 两条都回复我方主战者（透镜补全目标）", targetsOk,
                  "play=" + PrintTarget(playRestore?.Target) + " sev=" + PrintTarget(sevRestore?.Target));
        }

        // ---- 用例 4：跨时机 B（OnEvolve -> OnSuperEvolve）基础句必须让位 ----
        {
            var r = comp.Compile("Evolve: Summon a X. Super-Evolve: Summon 2 X instead.");

            bool two = r.Abilities.Count == 2;
            var evs = r.Abilities.Select(a => a.Trigger?.Event).ToList();
            bool split = two && evs[0] == CoreEvent.OnEvolve && evs[1] == CoreEvent.OnSuperEvolve;

            var evoAb = two ? r.Abilities[0] : null;
            var sevAb = two ? r.Abilities[1] : null;
            bool guard = evoAb != null && HasSuperEvolvedNegation(evoAb.Condition);
            bool guardInBody = evoAb != null && evoAb.Body.OfType<ConditionalNode>()
                                               .Any(c => HasSuperEvolvedNegation(c.Condition));

            var evoSpawn = evoAb?.Body.OfType<SpawnCardNode>().FirstOrDefault()
                           ?? evoAb?.Body.OfType<ConditionalNode>().SelectMany(c => c.Then)
                                              .OfType<SpawnCardNode>().FirstOrDefault();
            var sevSpawn = sevAb?.Body.OfType<SpawnCardNode>().FirstOrDefault();
            bool counts = evoSpawn != null && IsConst(evoSpawn.Count, 1)
                          && sevSpawn != null && IsConst(sevSpawn.Count, 2);
            bool card = evoSpawn?.CardId == SevTestCardId && sevSpawn?.CardId == SevTestCardId;

            Check("4 跨时机 B 产出 2 条能力", two,
                  "count=" + r.Abilities.Count + " err=" + string.Join(";", r.Errors));
            Check("4 Trigger 分别是 OnEvolve / OnSuperEvolve", split, "events=" + string.Join(",", evs));
            Check("4 OnEvolve 那条的条件含 SuperEvolved 的否定", guard,
                  "cond=" + (evoAb == null ? "null" : AstEnglishPrinter.PrintBool(evoAb.Condition)));
            Check("4 OnEvolve 那条 Body 内也有一道同样的门", guardInBody,
                  "body=" + (evoAb == null ? "null" : string.Join("|", evoAb.Body.Select(x => x.GetType().Name))));
            Check("4 两条分别召唤 1 张 / 2 张同一张卡", counts && card,
                  "evo=" + PrintAmount(evoSpawn?.Count) + "/" + evoSpawn?.CardId
                  + " sev=" + PrintAmount(sevSpawn?.Count) + "/" + sevSpawn?.CardId);
        }

        // ---- 用例 4 实战门禁：普通进化召唤 1，超进化召唤 2（不是 1+2=3） ----
        {
            var abilities = comp.Compile("Evolve: Summon a X. Super-Evolve: Summon 2 X instead.").Abilities;

            int normal = EvolveAndCountSummons(abilities, super: false, seed: 1201);
            int super = EvolveAndCountSummons(abilities, super: true, seed: 1202);

            Check("4 普通进化只召唤 1 张", normal == 1, "实际 " + normal);
            Check("4 超进化只召唤 2 张（不是 1+2=3）", super == 2, "实际 " + super);
        }

        log.Insert(0, fail == 0 ? "OK " + pass + "/" + (pass + fail) : "FAILED " + fail + " of " + (pass + fail));
        return string.Join("\n", log);
    }

    // ---------- 辅助 ----------

    private const int SevTestCardId = 700;

    private static Lexicon BuildLex()
    {
        var lex = new Lexicon();
        lex.AddCard("X", SevTestCardId);
        foreach (var kw in new[] { "ward", "rush", "storm" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }

    private static bool IsTriviallyTrue(BoolExpr e)
        => e == null || ReferenceEquals(e, BoolExpr.True) || (e is ConstBoolExpr c && c.Value);

    private static bool IsConst(NumExpr e, int v)
        => e != null && e.Op == NumOp.Const && e.Value == v;

    private static string PrintAmount(NumExpr e) => e == null ? "null" : AstEnglishPrinter.PrintNum(e);

    private static string PrintTarget(TargetSpec t)
        => t == null ? "null" : t.SourceKind + ":" + t.Owner + "/" + t.Zone;

    /// <summary>整棵条件树里是否出现「SuperEvolved 的否定」。</summary>
    private static bool HasSuperEvolvedNegation(BoolExpr e)
    {
        if (e == null) return false;
        switch (e)
        {
            case NotExpr n:
                return n.Inner is HasStateExpr hs && hs.State == UnitState.SuperEvolved
                       || HasSuperEvolvedNegation(n.Inner);
            case AndExpr a:
                return a.Items.Any(HasSuperEvolvedNegation);
            case OrExpr o:
                return o.Items.Any(HasSuperEvolvedNegation);
            default:
                return false;
        }
    }

    /// <summary>造一局只有双方主战者的对局环境。</summary>
    private static BattleState NewState(int seed, int maxPp)
    {
        var st = new BattleState(seed: seed);
        st.RegisterCard(new CardDef { CardId = 99, Name = "Leader", Kind = CardKind.Leader, BaseLife = 20 });
        st.RegisterCard(new CardDef { CardId = 98, Name = "FoeLeader", Kind = CardKind.Leader, BaseLife = 20 });

        var self = st.CreateEntity(99, ZoneRef.Leader, 0);
        self.Life = 20; self.MaxLife = 20; st.Players[0].Leader = self;
        var foe = st.CreateEntity(98, ZoneRef.Leader, 1);
        foe.Life = 20; foe.MaxLife = 20; st.Players[1].Leader = foe;

        st.ActivePlayer = 0;
        st.TurnNumber = 8;
        st.WriteCounter(0, st.Schema.Counter("pp"), maxPp);
        st.WriteCounter(0, st.Schema.Counter("max_pp"), maxPp);
        st.WriteCounter(0, st.Schema.Counter("ep"), 1);
        st.WriteCounter(0, st.Schema.Counter("sep"), 1);
        return st;
    }

    /// <summary>打出一张带给定能力的随从，返回敌方主战者剩余生命。</summary>
    private static int PlayAndReadEnemyLeader(IReadOnlyList<AbilityDef> abilities, int maxPp, int seed)
    {
        var st = NewState(seed, maxPp);
        st.RegisterCard(new CardDef
        {
            CardId = 5, Name = "Nova", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 3, BaseCost = 1,
            Abilities = abilities.ToArray(),
        });

        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        var tr = new TurnRunner(st, st.Schema);
        var card = st.SpawnCard(5, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);
        return st.Players[1].Leader.Life;
    }

    /// <summary>进化一张带给定能力的随从，返回场上召唤出的测试卡数量。</summary>
    private static int EvolveAndCountSummons(IReadOnlyList<AbilityDef> abilities, bool super, int seed)
    {
        var st = NewState(seed, maxPp: 5);
        st.RegisterCard(new CardDef
        {
            CardId = SevTestCardId, Name = "X", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1,
        });
        st.RegisterCard(new CardDef
        {
            CardId = 6, Name = "EvoBody", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 3, BaseCost = 1,
            Abilities = abilities.ToArray(),
        });

        var vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        var tr = new TurnRunner(st, st.Schema);
        var e = st.SpawnCard(6, ZoneRef.Board, 0);
        tr.Evolve(0, e, super, vm);

        return st.EnumerateZone(ZoneRef.Board, 0)
                 .Count(x => x.Def != null && x.Def.CardId == SevTestCardId);
    }
}

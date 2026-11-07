using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 77 号审计 · 问题 5（战斗与对局层规则违背）的回归用例
//
//   5-1 效果自动进化不加属性、也不置 IsEvolved
//   5-2 屏障先删关键词再判「是否被屏障抵消」，Bane 穿透屏障
//   5-3 守护过滤漏掉威慑，Ward + Intimidate 同时在场时玩家一手都打不出
//   5-4 分伤用整除，7 点分给 2 个随从只掉 6 点，分给 8 个变成全 0
//   5-5 回手只换区不重置，进化加成被白带进手牌
//   5-6 融合材料进墓场，等于给亡者召还喂肥料
//   5-7 费用减半向下截断，1 费变 0 费
// ============================================================================
public static partial class Regress
{
    public static string RunCombat()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(CombatLexicon());

        AutoEvolveAddsStats(comp, Chk);
        AutoEvolveBumpsSkyboundGauge(comp, Chk);
        BaneDoesNotPierceBarrier(Chk);
        WardPlusIntimidateDoesNotDeadlock(Chk);
        SplitDamageUsesRemaining(comp, Chk);
        ReturnToHandResetsToPrinted(comp, Chk);
        FuseMaterialsLeaveTheGame(Chk);
        HalveCostRoundsUp(comp, Chk);

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Combat " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    // ---------- 5-1 自动进化 ----------

    private static void AutoEvolveAddsStats(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var r = comp.Compile("Fanfare: Evolve this follower.");
        var st = NewCombatState(9100, out var tr, out var vm);
        st.RegisterCard(new CardDef
        {
            CardId = 501, Name = "TwoTwo", Kind = CardKind.Follower,
            BaseAttack = 2, BaseLife = 2, BaseCost = 2, Abilities = r.Abilities,
            InnateKeywords = r.InnateKeywords,
        });
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(501, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);

        chk("5-1a 自动进化 +2/+2 且置 IsEvolved",
            card.Attack == 4 && card.Life == 4 && card.MaxLife == 4 && card.IsEvolved,
            "卡片=" + card.Attack + "/" + card.Life + " max=" + card.MaxLife + " evolved=" + card.IsEvolved);

        // 重复执行「进化这个随从」不得再叠一次
        var scope = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(card) };
        vm.PushBody(r.Abilities[0].Body, scope);
        vm.StepUntilBlocked();
        chk("5-1b 重复自动进化不再加属性",
            card.Attack == 4 && card.Life == 4,
            "二次后=" + card.Attack + "/" + card.Life);

        // 超进化 +3/+3
        var st2 = NewCombatState(9101, out _, out var vm2);
        st2.RegisterCard(new CardDef { CardId = 502, Name = "T2", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 2 });
        var sup = st2.SpawnCard(502, ZoneRef.Board, 0);
        var seScope = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(sup) };
        vm2.PushBody(new EffectNode[]
        {
            new EvolveNode { Target = TargetSpec.Self, IsSuperEvolve = true },
        }, seScope);
        vm2.StepUntilBlocked();
        chk("5-1c 自动超进化 +3/+3 且两种标志都置位",
            sup.Attack == 5 && sup.Life == 5 && sup.MaxLife == 5 && sup.IsEvolved && sup.IsSuperEvolved,
            "超进化后=" + sup.Attack + "/" + sup.Life + " evo=" + sup.IsEvolved + " sev=" + sup.IsSuperEvolved);
    }

    /// <summary>自动进化必须与手动进化共用同一套奥义槽逻辑。</summary>
    private static void AutoEvolveBumpsSkyboundGauge(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var sb = comp.Compile("Fanfare: Skybound Art - Deal 2 damage to the enemy leader.");
        var evo = comp.Compile("Fanfare: Evolve this follower.");

        var st = NewCombatState(9102, out _, out var vm);
        st.RegisterCard(new CardDef
        {
            CardId = 511, Name = "EvoBody", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = evo.Abilities,
        });
        st.RegisterCard(new CardDef
        {
            CardId = 512, Name = "SBA", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1, Abilities = sb.Abilities,
        });

        var gaugeKey = st.Schema.Counter("skybound_gauge");
        var holder = st.SpawnCard(512, ZoneRef.Hand, 0);
        int before = holder.Counters.TryGetValue(gaugeKey, out var g0) ? g0 : 0;

        var body = st.SpawnCard(511, ZoneRef.Board, 0);
        vm.PushBody(evo.Abilities[0].Body,
                    new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(body) });
        vm.StepUntilBlocked();

        int after = holder.Counters.TryGetValue(gaugeKey, out var g1) ? g1 : 0;
        chk("5-1d 自动进化也给手牌奥义槽 +1", after == before + 1, "gauge " + before + " -> " + after);
    }

    // ---------- 5-2 Bane vs Barrier ----------

    private static void BaneDoesNotPierceBarrier(Action<string, bool, string> chk)
    {
        var st = NewCombatState(9200, out _, out var vm);
        st.RegisterCard(new CardDef { CardId = 601, Name = "Bane", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3 });
        st.RegisterCard(new CardDef { CardId = 602, Name = "Barrier", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 5 });

        var atk = st.SpawnCard(601, ZoneRef.Board, 0);
        atk.EnteredTurn = 0;
        atk.Keywords.Add(st.Schema.Keyword("bane"));
        var def = st.SpawnCard(602, ZoneRef.Board, 1);
        def.EnteredTurn = 0;
        def.Keywords.Add(st.Schema.Keyword("barrier"));

        var cr = new CombatResolver(st, st.Schema);
        cr.ExecuteCombat(atk, def, vm);

        bool alive = def.Zone == ZoneRef.Board;
        bool barrierGone = !def.Keywords.Contains(st.Schema.Keyword("barrier"));
        bool unHurt = def.Life == 5;
        chk("5-2 屏障吸收后 Bane 不破坏防守方", alive && barrierGone && unHurt,
            "zone=" + def.Zone + " barrier消费=" + barrierGone + " life=" + def.Life);

        // 反向：没有屏障时 Bane 必须生效，否则上一条是「让 Bane 全废」换来的
        var st2 = NewCombatState(9201, out _, out var vm2);
        st2.RegisterCard(new CardDef { CardId = 611, Name = "Bane2", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5 });
        st2.RegisterCard(new CardDef { CardId = 612, Name = "NoBarrier", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5 });
        var a2 = st2.SpawnCard(611, ZoneRef.Board, 0);
        a2.EnteredTurn = 0;
        a2.Keywords.Add(st2.Schema.Keyword("bane"));
        var d2 = st2.SpawnCard(612, ZoneRef.Board, 1);
        d2.EnteredTurn = 0;
        new CombatResolver(st2, st2.Schema).ExecuteCombat(a2, d2, vm2);
        chk("5-2b 无屏障时 Bane 仍然一击必杀", d2.Zone != ZoneRef.Board,
            "zone=" + d2.Zone);
    }

    // ---------- 5-3 Ward + Intimidate ----------

    private static void WardPlusIntimidateDoesNotDeadlock(Action<string, bool, string> chk)
    {
        // ① 同时拥有守护 + 威慑：守护失效，攻击方至少能打其它随从或主战者
        {
            var st = NewCombatState(9300, out _, out _);
            var (atk, wi, plain) = BuildWardCase(st, ward: true, intimidate: true);
            var cr = new CombatResolver(st, st.Schema);

            bool hitWi = cr.CanAttack(atk, wi, out var rWi);
            bool hitPlain = cr.CanAttack(atk, plain, out var rPlain);
            bool hitLeader = cr.CanAttack(atk, st.Players[1].Leader, out var rLeader);

            chk("5-3a 守护 + 威慑不再死锁", hitPlain && hitLeader && !hitWi,
                "wi=" + hitWi + "(" + (rWi ?? "-") + ") plain=" + hitPlain + "(" + (rPlain ?? "-")
                + ") leader=" + hitLeader + "(" + (rLeader ?? "-") + ")");
        }

        // ② 有守护无威慑：守护必须照常生效（不得反向放宽）
        {
            var st = NewCombatState(9301, out _, out _);
            var (atk, wardOnly, plain) = BuildWardCase(st, ward: true, intimidate: false);
            var cr = new CombatResolver(st, st.Schema);

            bool hitWard = cr.CanAttack(atk, wardOnly, out _);
            bool hitPlain = cr.CanAttack(atk, plain, out _);
            bool hitLeader = cr.CanAttack(atk, st.Players[1].Leader, out _);

            chk("5-3b 有守护无威慑时守护仍然强制", hitWard && !hitPlain && !hitLeader,
                "ward=" + hitWard + " plain=" + hitPlain + " leader=" + hitLeader);
        }

        // ③ 完全没有守护：不得凭空造出一个「只能打守护」的约束
        {
            var st = NewCombatState(9302, out _, out _);
            var (atk, wardOnly, plain) = BuildWardCase(st, ward: false, intimidate: false);
            var cr = new CombatResolver(st, st.Schema);

            bool hitPlain = cr.CanAttack(atk, plain, out _);
            bool hitLeader = cr.CanAttack(atk, st.Players[1].Leader, out _);

            chk("5-3c 无守护时不被强制只能打守护", hitPlain && hitLeader,
                "plain=" + hitPlain + " leader=" + hitLeader + " wardOnly在场=" + (wardOnly != null));
        }
    }

    private static (RuntimeEntity atk, RuntimeEntity wardish, RuntimeEntity plain) BuildWardCase(
        BattleState st, bool ward, bool intimidate)
    {
        st.RegisterCard(new CardDef { CardId = 701, Name = "Atk", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3 });
        st.RegisterCard(new CardDef { CardId = 702, Name = "Wardish", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });
        st.RegisterCard(new CardDef { CardId = 703, Name = "Plain", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1 });

        var atk = st.SpawnCard(701, ZoneRef.Board, 0);
        atk.EnteredTurn = 0;
        RuntimeEntity wardish = null;
        if (ward)
        {
            wardish = st.SpawnCard(702, ZoneRef.Board, 1);
            wardish.Keywords.Add(st.Schema.Keyword("ward"));
            if (intimidate) wardish.Keywords.Add(st.Schema.Keyword("intimidate"));
        }
        var plain = st.SpawnCard(703, ZoneRef.Board, 1);
        return (atk, wardish, plain);
    }

    // ---------- 5-4 分伤 ----------

    private static void SplitDamageUsesRemaining(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var r = comp.Compile("Deal 7 damage split between all enemy followers.");
        var body = r.Abilities[0].Body;

        // 7 点分给 2 个 5 血随从：必须掉满 7 点（旧实现整除 → 各 3 点 = 6 点）
        {
            var st = NewCombatState(9400, out _, out var vm);
            st.RegisterCard(new CardDef { CardId = 801, Name = "F5", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5 });
            var a = SpawnOnBoardUncapped(st, 801, 1);
            var b = SpawnOnBoardUncapped(st, 801, 1);

            vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
            vm.StepUntilBlocked();

            int dealt = (5 - a.Life) + (5 - b.Life);
            chk("5-4a 7 点分给 2 个 5 血随从，共掉 7 点", dealt == 7,
                "剩余=" + a.Life + "/" + b.Life + " 共掉=" + dealt);
        }

        // 7 点分给 8 个 5 血随从：仍要掉满 7 点（旧实现每份 0 点）
        {
            var st = NewCombatState(9401, out _, out var vm);
            st.RegisterCard(new CardDef { CardId = 802, Name = "F5b", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 5 });
            var list = new List<RuntimeEntity>();
            for (int i = 0; i < 8; i++) list.Add(SpawnOnBoardUncapped(st, 802, 1));
            int boardCount = st.EnumerateZone(ZoneRef.Board, 1).Count();

            vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
            vm.StepUntilBlocked();

            int dealt = list.Sum(x => 5 - x.Life);
            chk("5-4b 7 点分给 8 个 5 血随从，共掉 7 点", dealt == 7 && boardCount == 8,
                "目标数=" + boardCount + " 共掉=" + dealt);
        }
    }

    /// <summary>
    /// 直接放进战场列表：战场容量（5）不是本用例的被测对象，
    /// 这里要的是「目标数量大于伤害总量」这一种分配形态。
    /// </summary>
    private static RuntimeEntity SpawnOnBoardUncapped(BattleState st, int cardId, int playerId)
    {
        var e = st.SpawnCard(cardId, ZoneRef.Hand, playerId);
        st.Players[playerId].Hand.Remove(e);
        e.Zone = ZoneRef.Board;
        st.Players[playerId].Board.Add(e);
        return e;
    }

    // ---------- 5-5 回手重置 ----------

    private static void ReturnToHandResetsToPrinted(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var r = comp.Compile("Return an enemy follower to its owner's hand.");
        var st = NewCombatState(9500, out _, out var vm);
        st.RegisterCard(new CardDef
        {
            CardId = 1001, Name = "Bounce", Kind = CardKind.Follower,
            BaseAttack = 2, BaseLife = 2, BaseCost = 3,
        });

        var t = st.SpawnCard(1001, ZoneRef.Board, 1);
        t.Attack = 5; t.Life = 1; t.MaxLife = 4; t.Cost = 7;
        t.IsEvolved = true;
        t.TempModifiers.Add(new TemporaryModifier { Duration = DurationKind.ThisTurn, Attack = 3, Life = -1 });

        vm.PushBody(r.Abilities[0].Body, new VmScope { Owner = ZoneScope.Self, Controller = 0 });
        vm.StepUntilBlocked();

        bool ok = t.Zone == ZoneRef.Hand
                  && t.Attack == 2 && t.Life == 2 && t.MaxLife == 2
                  && t.Cost == 3
                  && !t.IsEvolved && !t.IsSuperEvolved && !t.IsSilenced
                  && t.TempModifiers.Count == 0;
        chk("5-5 回手重置为卡面印刷值", ok,
            "zone=" + t.Zone + " " + t.Attack + "/" + t.Life + " max=" + t.MaxLife
            + " cost=" + t.Cost + " evo=" + t.IsEvolved + " temp=" + t.TempModifiers.Count);
    }

    // ---------- 5-6 融合材料离场 ----------

    private static void FuseMaterialsLeaveTheGame(Action<string, bool, string> chk)
    {
        var st = NewCombatState(9600, out var tr, out var vm);

        // 融合目标自己带一条「Whenever fused」的监听，用来证明 OnFuse 照发
        var onFuse = new AbilityDef
        {
            Trigger = new TriggerSpec { Event = CoreEvent.OnFuse, ActiveZone = ActiveZoneScope.Hand },
            Body = new EffectNode[]
            {
                new DealDamageNode
                {
                    Target = TargetSpec.OpponentLeader,
                    Amount = NumExpr.Const(1),
                    IsAbility = true,
                },
            },
        };

        st.RegisterCard(new CardDef
        {
            CardId = 1101, Name = "Base", Kind = CardKind.Follower,
            BaseAttack = 1, BaseLife = 1, BaseCost = 1,
            Abilities = new[] { onFuse },
        });
        st.RegisterCard(new CardDef { CardId = 1102, Name = "MatA", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 2 });
        st.RegisterCard(new CardDef { CardId = 1103, Name = "MatB", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 3 });

        var baseCard = st.SpawnCard(1101, ZoneRef.Hand, 0);
        var mA = st.SpawnCard(1102, ZoneRef.Hand, 0);
        var mB = st.SpawnCard(1103, ZoneRef.Hand, 0);

        int cemBefore = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();
        int shadowBefore = st.ReadCounter(0, st.Schema.Counter("shadow"));
        st.ClearEvents();

        bool fused = tr.FuseCards(0, baseCard, new[] { mA, mB }, vm);

        int cemAfter = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();
        int shadowAfter = st.ReadCounter(0, st.Schema.Counter("shadow"));
        bool notInCemetery = !st.EnumerateZone(ZoneRef.Cemetery, -1).Any(x => x == mA || x == mB);
        // 材料现在停在**融合素材区**（实体保留），不再被销毁成 None。
        // 仍然要求：已离开手牌、未进墓场、未被放逐。
        bool outOfPlay = mA.Zone == ZoneRef.FuseMaterial && mB.Zone == ZoneRef.FuseMaterial
                         && !st.EnumerateZone(ZoneRef.Hand, 0).Any(x => x == mA || x == mB);
        bool noDiscard = !st.EventLog.Any(x => x.Kind == CoreEvent.OnDiscard);
        bool noLeave = !st.EventLog.Any(x => x.Kind == CoreEvent.OnLeaveField);
        // 三个融合计数器是**实体级**的（卡面写 Fused to **this card**），
        // 不再记在玩家身上。
        int fc = baseCard.Counters.TryGetValue(st.Schema.Counter("fuse_count"), out var fcv) ? fcv : 0;
        int fcost = baseCard.Counters.TryGetValue(st.Schema.Counter("fuse_cost"), out var fcostv) ? fcostv : 0;
        // 181 号文档 3：《76-c》2.3-C 规定 fuse_count += **材料张数**。
        // 本次一次喂入 mA + mB 两张材料，因此 fuse_count 必须是 2（不是 1）。
        // 旧断言把「融合次数」与「材料张数」混为一谈，正是这条文档要修的缺陷。
        bool counts = fc == 2 && fcost == 5;
        bool onFuseFired = st.Players[1].Leader.Life == 19;

        chk("5-6a 融合材料离场但不进墓场",
            fused && outOfPlay && notInCemetery && cemAfter == cemBefore,
            "fusion=" + fused + " zone=" + mA.Zone + "/" + mB.Zone + " 墓场 " + cemBefore + " -> " + cemAfter);
        chk("5-6b 融合不加 shadow、不发 OnDiscard / OnLeaveField",
            shadowAfter == shadowBefore && noDiscard && noLeave,
            "shadow " + shadowBefore + " -> " + shadowAfter + " discard=" + !noDiscard + " leave=" + !noLeave);
        chk("5-6c fuse_count / fuse_cost 正确累计，OnFuse 照发",
            counts && onFuseFired,
            "count=" + fc + " cost=" + fcost
            + " 敌方主战者=" + st.Players[1].Leader.Life);
    }

    // ---------- 5-7 费用减半 ----------

    private static void HalveCostRoundsUp(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var r = comp.Compile("Halve the cost of this card.");
        var body = r.Abilities[0].Body;

        var st = NewCombatState(9700, out _, out var vm);
        st.RegisterCard(new CardDef { CardId = 1201, Name = "C3", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 3 });
        st.RegisterCard(new CardDef { CardId = 1202, Name = "C1", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });

        var c3 = st.SpawnCard(1201, ZoneRef.Board, 0);
        var c1 = st.SpawnCard(1202, ZoneRef.Board, 0);

        vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(c3) });
        vm.StepUntilBlocked();
        vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(c1) });
        vm.StepUntilBlocked();

        chk("5-7 费用减半向上取整（3→2，1→1）", c3.Cost == 2 && c1.Cost == 1,
            "3费减半=" + c3.Cost + " 1费减半=" + c1.Cost);
    }

    // ---------- 公共设施 ----------

    private static BattleState NewCombatState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new BattleState(seed: seed);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        vm.CurrentTurnOwner = 0;
        return st;
    }

    private static Lexicon CombatLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

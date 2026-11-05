using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 77 号文档「问题 6」的回归用例 —— 编译器装死点
//
// 这批缺陷的共同形态：能编译、不报错、AST 看起来也像回事，
// 但表达式在 VmEval 里恒真/恒假，或指错了区域，运行期完全不是卡面的意思。
// 因此每条断言都必须**跑 VM**，只比对 AST 文字抓不到这类问题。
// ============================================================================
public static partial class Regress
{
    public static string RunLang()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(BuildLangLexicon());

        // ── 1. "attacks a follower" 必须能编译，且条件可求值 ──
        // 旧实现：attacks 之后剩下的 "a follower" 无人消费 → 整句报「无法推进」。
        {
            var r = comp.Compile("Whenever this follower attacks a follower, draw a card.");
            bool compiled = r.Ok && r.Abilities.Count == 1
                            && r.Abilities[0].Trigger?.Event == CoreEvent.OnAttack;
            bool evals = AttackConditionEvaluates(comp, out bool onFollower, out bool onLeader);

            // 同一条件换成区域主语（"an allied follower …"）必须真的跑通触发链：
            // 「this follower」主语本身卡在 CardVm 的 $self 过滤器上（见报告），
            // 与本条缺陷无关，不能拿它当判据。
            bool firesOnFollower = AttackTriggers(comp, attackLeader: false);
            bool skipsOnLeader = !AttackTriggers(comp, attackLeader: true);

            Check("lang-1 attacks a follower 可编译，条件可求值且按攻击目标分流",
                  compiled && evals && onFollower && !onLeader && firesOnFollower && skipsOnLeader,
                  $"ok={r.Ok} trig={r.Abilities.FirstOrDefault()?.Trigger?.Event} eval={onFollower}/{onLeader} fires={firesOnFollower}/{skipsOnLeader}");
        }

        // ── 2. "takes damage but isn't destroyed" = 受伤后仍存活 ──
        // 旧实现塞 UnitState.None（VmEval 恒 false），取反后恒真 = 任何伤害都触发。
        {
            var r = comp.Compile("Whenever this follower takes damage but isn't destroyed, draw a card.");
            bool compiled = r.Ok && r.Abilities.Count == 1;
            bool evals = DamageConditionEvaluates(comp, out bool aliveTrue, out bool deadFalse);

            // 「活着的随从挨 1 点触发 / 被打死的不触发」走真实触发链。
            bool aliveFires = DamageTriggers(comp, lethal: false);
            bool deadSkips = !DamageTriggers(comp, lethal: true);

            Check("lang-2 takes damage but isn't destroyed 只在存活时触发",
                  compiled && evals && aliveTrue && !deadFalse && aliveFires && deadSkips,
                  $"ok={r.Ok} eval={aliveTrue}/{deadFalse} fires={aliveFires}/{deadSkips}");
        }

        // ── 3. "X is the number of cards you returned" 必须是回手计数，不是场上张数 ──
        {
            var r = comp.Compile(
                "Fanfare: Deal X damage to the enemy leader. X is the number of cards you returned to your hand this turn.");
            bool compiled = r.Ok && r.Abilities.Count == 1;

            // 场上放 3 张随从：若 X 仍被编成「场上张数」，这 3 张会一起计入。
            int dealt = XDamageOnReturnedCards(comp, out int onBoard);
            Check("lang-3 X = 本回合回到手牌的卡数（不含场上张数）",
                  compiled && dealt == 0 && onBoard == 3,
                  $"ok={r.Ok} dealt={dealt} onBoard={onBoard}");
        }

        // ── 4. "cards with the same cost in your hand" 必须对手牌求 MaxGroupSize ──
        {
            var r = comp.Compile(
                "Fanfare: If you have at least 4 cards with the same cost in your hand, deal 4 damage to the enemy leader.");
            var cmp = r.Abilities.SelectMany(a => a.Body).Count()
                      + (r.Abilities.FirstOrDefault()?.Condition == null ? 0 : 1);
            bool usesMaxGroup = AstEnglishPrinter.PrintBool(r.Abilities.FirstOrDefault()?.Condition)
                                    .Contains("maxgroup(");

            // 手牌 4 张同费 → 触发；手牌 3 张同费 → 不触发。
            int dmg4 = SameCostHandDamage(comp, handOfSameCost: 4);
            int dmg3 = SameCostHandDamage(comp, handOfSameCost: 3);
            Check("lang-4 同费手牌条件用 MaxGroupSize 对手牌求值",
                  r.Ok && usesMaxGroup && dmg4 == 4 && dmg3 == 0,
                  $"ok={r.Ok} maxgroup={usesMaxGroup} dmg4={dmg4} dmg3={dmg3}");
        }

        // ── 5. 融合变身表按 fuse_count 分档，只召唤一个 ──
        {
            // 用 77 号文档的原始句子，而不是精简版：
            // 精简版会绕开「表必须挂回它前面的那句」这条路径。
            var r = comp.Compile("Fanfare: If you've Fused, Summon a Fused follower. 1: Alpha. 2: Beta. 3 or more: Gamma.");
            bool noSeqBranch = !r.Abilities.SelectMany(a => a.Body).OfType<BranchModeNode>().Any();
            bool hasChain = r.Abilities.SelectMany(a => a.Body).OfType<ConditionalNode>().Any();

            // fuse_count = 2 → 只召出 Beta，Alpha/Gamma 都不出现。
            var spawned = FuseTierSpawn(comp, fuseCount: 2);
            bool onlyBeta = spawned.Count == 1 && spawned[0] == "Beta";

            var three = FuseTierSpawn(comp, fuseCount: 3);
            bool onlyGamma = three.Count == 1 && three[0] == "Gamma";

            Check("lang-5 融合变身表按 fuse_count 选一档，不依次召唤",
                  r.Ok && noSeqBranch && hasChain && onlyBeta && onlyGamma,
                  $"ok={r.Ok} noSeq={noSeqBranch} chain={hasChain} f2=[{string.Join(",", spawned)}] f3=[{string.Join(",", three)}]");
        }

        // ── 6. "When you've Fused both to this card," 必须带 fuse_count >= 2 ──
        {
            var r = comp.Compile("When you've Fused both to this card, deal 4 damage to the enemy leader.");
            // 分档判据现在是实体级 fuse_kinds（融合种类数）。
            bool hasCond = AstEnglishPrinter.PrintBool(r.Abilities.FirstOrDefault()?.Condition)
                              .Contains("fuse_kinds");

            // 条件必须真正可求值：融合 1 次为假，2 次为真。
            bool evals = FuseBothConditionEvaluates(comp, out bool once, out bool twice);
            Check("lang-6 Fused both 生成可求值的 fuse_count >= 2 条件",
                  r.Ok && hasCond && evals && !once && twice,
                  $"ok={r.Ok} cond={AstEnglishPrinter.PrintBool(r.Abilities.FirstOrDefault()?.Condition)} once={once} twice={twice}");
        }

        // ── 7. "Halve the enemy leader's defense" 对真实目标求值且向上取整 ──
        {
            var r = comp.Compile("Fanfare: Halve the enemy leader's defense.");
            int enemyLife = HalveEnemyLeader(comp, out int selfLife);
            Check("lang-7 Halve 敌方主战者生命（奇数向上取整 21→11），不动自己",
                  r.Ok && enemyLife == 11 && selfLife == 20,
                  $"ok={r.Ok} enemyLife={enemyLife} selfLife={selfLife}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    // ---------- 探针：每条都真跑一次 VM ----------

    /// <summary>
    /// 直接用 VmEval 求值 "attacks a follower" 的条件。
    /// 返回 true 表示条件不是常量：攻击随从为真、攻击主战者为假。
    /// </summary>
    private static bool AttackConditionEvaluates(EnglishCompiler comp, out bool onFollower, out bool onLeader)
    {
        var r = comp.Compile("Whenever this follower attacks a follower, draw a card.");
        var st = NewLangState(seed: 500, out var _);
        st.RegisterCard(LangCard(410, r.Abilities));
        var self = st.SpawnCard(410, ZoneRef.Board, 0);
        var foe = st.SpawnCard(410, ZoneRef.Board, 1);
        var eval = new VmEval(st);

        bool Eval(bool leaderTarget)
        {
            // CombatResolver 在发 OnAttack 之前就在**攻击者**身上写定该字段，
            // 而 OnAttack 的 Subject 也是攻击者 —— 条件读的是 $trigger，
            // 所以这里必须把 Trigger 绑到攻击者，绑到防御方就永远读到 false。
            self.CurrentAttackHitLeader = leaderTarget;
            var sc = new VmScope
            {
                Owner = ZoneScope.Self,
                Controller = 0,
                Source = EntityRef.From(self),
                Trigger = EntityRef.From(self),
                Event = new VmEvent
                {
                    Kind = CoreEvent.OnAttack,
                    Subject = EntityRef.From(self),
                    Object = EntityRef.From(leaderTarget ? st.Players[1].Leader : foe),
                },
            };
            return eval.EvalBool(r.Abilities[0].Condition, sc);
        }

        onFollower = Eval(false);
        onLeader = Eval(true);
        return onFollower && !onLeader;
    }

    /// <summary>用 VmEval 求值 "takes damage but isn't destroyed" 的条件。</summary>
    private static bool DamageConditionEvaluates(EnglishCompiler comp, out bool aliveTrue, out bool deadFalse)
    {
        var r = comp.Compile("Whenever this follower takes damage but isn't destroyed, draw a card.");
        var st = NewLangState(seed: 502, out var _);
        st.RegisterCard(LangCard(420, r.Abilities));
        var victim = st.SpawnCard(420, ZoneRef.Board, 0);
        var eval = new VmEval(st);

        bool Eval()
        {
            var sc = new VmScope
            {
                Owner = ZoneScope.Self,
                Controller = 0,
                Source = EntityRef.From(victim),
                Trigger = EntityRef.From(victim),
                Event = new VmEvent { Kind = CoreEvent.OnDamaged, Subject = EntityRef.From(victim), Amount = 1 },
            };
            return eval.EvalBool(r.Abilities[0].Condition, sc);
        }

        victim.Life = 4; aliveTrue = Eval();     // 受伤后仍存活
        victim.Life = 0; deadFalse = Eval();     // 被打死
        return aliveTrue && !deadFalse;
    }

    /// <summary>
    /// 攻击触发探针：真实走一遍 ExecuteCombat + 触发链，返回是否抽到牌。
    ///
    /// 能力挂在**旁观卡**上而不是攻击者身上 —— 攻击者自己也是
    /// "an allied follower"，把它当监听者的主体会让两种目标都命中。
    /// </summary>
    private static bool AttackTriggers(EnglishCompiler comp, bool attackLeader)
    {
        var st = NewLangState(seed: 501, out var vm);
        var r = comp.Compile("Whenever an allied follower attacks a follower, draw a card.");
        st.RegisterCard(LangCard(410, r.Abilities));          // 旁观监听者
        st.RegisterCard(LangCard(500, Array.Empty<AbilityDef>()));

        st.SpawnCard(410, ZoneRef.Board, 0);                  // 监听者留在场上
        for (int i = 0; i < 4; i++) st.SpawnCard(500, ZoneRef.Deck, 0);

        var atk = st.SpawnCard(500, ZoneRef.Board, 0);
        atk.EnteredTurn = 0;
        RuntimeEntity target = attackLeader
            ? st.Players[1].Leader
            : st.SpawnCard(500, ZoneRef.Board, 1);
        if (target != st.Players[1].Leader) target.EnteredTurn = 0;

        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        new CombatResolver(st, st.Schema).ExecuteCombat(atk, target, vm);
        vm.StepUntilBlocked();
        return st.EnumerateZone(ZoneRef.Hand, 0).Count() > handBefore;
    }

    /// <summary>受伤触发探针：lethal=true 时一击毙命，应判为「被破坏」。</summary>
    private static bool DamageTriggers(EnglishCompiler comp, bool lethal)
    {
        var st = NewLangState(seed: 512, out var vm);
        var r = comp.Compile("Whenever an allied follower takes damage but isn't destroyed, draw a card.");
        st.RegisterCard(LangCard(420, r.Abilities, baseLife: lethal ? 1 : 5));   // 受伤者兼监听者
        st.RegisterCard(LangCard(500, Array.Empty<AbilityDef>()));
        for (int i = 0; i < 4; i++) st.SpawnCard(500, ZoneRef.Deck, 0);

        var victim = st.SpawnCard(420, ZoneRef.Board, 0);
        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 必须把 vm 传进伤害管线，否则 OnDamaged 只落 EventLog、不进触发队列
        new Battle.CombatResolver(st, st.Schema).ApplyDamage(victim, lethal ? 3 : 1, 1, vm);
        vm.StepUntilBlocked();
        return st.EnumerateZone(ZoneRef.Hand, 0).Count() > handBefore;
    }

    /// <summary>
    /// X 取「回到手牌的卡数」。场上摆 3 张随从作为反证：
    /// 若 X 仍被编成「场上卡数」，敌方主战者会挨 3 点。
    /// </summary>
    private static int XDamageOnReturnedCards(EnglishCompiler comp, out int onBoard)
    {
        var st = NewLangState(seed: 503, out var tr, out var vm);
        var r = comp.Compile(
            "Fanfare: Deal X damage to the enemy leader. X is the number of cards you returned to your hand this turn.");
        st.RegisterCard(LangCard(430, r.Abilities));
        st.RegisterCard(LangCard(500, Array.Empty<AbilityDef>()));
        for (int i = 0; i < 3; i++) st.SpawnCard(500, ZoneRef.Board, 0);
        onBoard = st.EnumerateZone(ZoneRef.Board, 0).Count();

        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(430, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);
        return 20 - st.Players[1].Leader.Life;
    }

    /// <summary>手牌里摆 N 张同费卡，返回敌方主战者吃到的伤害。</summary>
    private static int SameCostHandDamage(EnglishCompiler comp, int handOfSameCost)
    {
        var st = NewLangState(seed: 504, out var tr, out var vm);
        var r = comp.Compile(
            "Fanfare: If you have at least 4 cards with the same cost in your hand, deal 4 damage to the enemy leader.");
        st.RegisterCard(LangCard(440, r.Abilities, cost: 5));
        // 三张不同费别的填充，确保「手牌张数 ≥ 4」不会被别的口径满足
        st.RegisterCard(LangCard(501, Array.Empty<AbilityDef>(), cost: 2));
        st.RegisterCard(LangCard(502, Array.Empty<AbilityDef>(), cost: 3));
        st.RegisterCard(LangCard(503, Array.Empty<AbilityDef>(), cost: 7));

        for (int i = 0; i < handOfSameCost; i++) st.SpawnCard(501, ZoneRef.Hand, 0);
        st.SpawnCard(502, ZoneRef.Hand, 0);
        st.SpawnCard(503, ZoneRef.Hand, 0);

        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(440, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);
        vm.StepUntilBlocked();
        return 20 - st.Players[1].Leader.Life;
    }

    /// <summary>把 fuse_count 设为指定值后打出变身卡，返回实际进场的卡名。</summary>
    private static List<string> FuseTierSpawn(EnglishCompiler comp, int fuseCount)
    {
        var st = NewLangState(seed: 505, out var tr, out var vm);
        var r = comp.Compile("Fanfare: If you've Fused, Summon a Fused follower. 1: Alpha. 2: Beta. 3 or more: Gamma.");
        st.RegisterCard(LangCard(450, r.Abilities));
        RegisterTierCards(st);

        var before = new HashSet<int>(st.EnumerateZone(ZoneRef.Board, 0).Select(e => e.EntityId));
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(450, ZoneRef.Hand, 0);
        // 这句是「Summon a Fused follower」形态，分档判据是**融合次数**
        // （实体级 fuse_count）。只有「based on the total cost of the cards
        // fused」那种写法才用 fuse_cost。
        card.Counters[st.Schema.Counter("fuse_count")] = fuseCount;
        tr.PlayCard(0, card, vm);

        // 变身卡自身也上了场，排除它只留召唤出来的那一档
        return st.EnumerateZone(ZoneRef.Board, 0)
                 .Where(e => !before.Contains(e.EntityId) && e.EntityId != card.EntityId)
                 .Select(e => e.Def?.Name ?? "")
                 .ToList();
    }

    /// <summary>
    /// 直接用 VmEval 求值 "Fused both" 的条件：
    /// 融合 1 次必须为假，2 次必须为真。
    ///
    /// 不走完整触发链是因为该能力的 SubjectFilter 是「本卡」，
    /// 而 CardVm.MatchesTrigger 的主体判定不绑定 $self（既有引擎缝隙，见报告），
    /// 与本条缺陷（条件根本没生成）无关。
    /// </summary>
    private static bool FuseBothConditionEvaluates(EnglishCompiler comp, out bool once, out bool twice)
    {
        var r = comp.Compile("When you've Fused both to this card, deal 4 damage to the enemy leader.");
        var st = NewLangState(seed: 506, out var _);
        st.RegisterCard(LangCard(460, r.Abilities));
        var card = st.SpawnCard(460, ZoneRef.Hand, 0);
        var eval = new VmEval(st);
        // both 判据是**实体级**的 fuse_kinds（融合种类数），不是融合次数。
        var ctr = st.Schema.Counter("fuse_kinds");

        bool Eval()
        {
            var sc = new VmScope
            {
                Owner = ZoneScope.Self,
                Controller = 0,
                Source = EntityRef.From(card),
                Trigger = EntityRef.From(card),
                Event = new VmEvent { Kind = CoreEvent.OnFuse, Subject = EntityRef.From(card) },
            };
            return eval.EvalBool(r.Abilities[0].Condition, sc);
        }

        card.Counters[ctr] = 1; once = Eval();
        card.Counters[ctr] = 2; twice = Eval();
        return !once && twice;
    }

    /// <summary>打出「生命上限减半」，返回双方主战者结算后的生命值。</summary>
    private static int HalveEnemyLeader(EnglishCompiler comp, out int selfLife)
    {
        var st = NewLangState(seed: 507, out var tr, out var vm);
        var r = comp.Compile("Fanfare: Halve the enemy leader's defense.");
        st.RegisterCard(LangCard(470, r.Abilities));

        // 敌方 21 上限：奇数用于验证向上取整（(21+1)/2 = 11），20 会被向下取整成 10
        st.Players[1].Leader.MaxLife = 21;
        st.Players[1].Leader.Life = 21;

        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(470, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);
        vm.StepUntilBlocked();

        selfLife = st.Players[0].Leader.Life;
        return st.Players[1].Leader.Life;
    }

    // ---------- 夹具 ----------

    private static CardDef LangCard(int cardId, IReadOnlyList<AbilityDef> abilities,
                                    int baseLife = 3, int cost = 1) => new()
    {
        CardId = cardId, Name = "L" + cardId, Kind = CardKind.Follower,
        BaseAttack = 1, BaseLife = baseLife, BaseCost = cost, Abilities = abilities,
    };

    private static Battle.BattleState NewLangState(int seed, out CardVm vm)
        => NewLangState(seed, out _, out vm);

    private static Battle.BattleState NewLangState(int seed, out TurnRunner tr, out CardVm vm)
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

    /// <summary>变身表的目标卡必须进战斗卡池，否则 SpawnCard 只造出空实体。</summary>
    private static void RegisterTierCards(Battle.BattleState st)
    {
        st.RegisterCard(new CardDef { CardId = 9001, Name = "Alpha", Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1 });
        st.RegisterCard(new CardDef { CardId = 9002, Name = "Beta", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 2 });
        st.RegisterCard(new CardDef { CardId = 9003, Name = "Gamma", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3 });
    }

    private static Lexicon BuildLangLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.AddCard("Alpha", 9001);
        lex.AddCard("Beta", 9002);
        lex.AddCard("Gamma", 9003);
        lex.Build();
        return lex;
    }
}

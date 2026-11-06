using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 完整对局模拟 + 全局不变量门禁
//
// 单卡效用测试只能证明「这张卡自己跑得通」。真正会翻车的是**交互**：
// 两个玩家的回合交替、手牌/牌库/墓地/放逐区的进出、融合与纹章的
// 跨回合状态、以及终局判定。这些只有让 Bot 自己打完整局才暴露得出来。
//
// 每执行一个动作后立刻检查七条不变量 —— 违反任何一条都说明
// 「某处绕过了权威管线」，而不是「这一局恰好打得难看」。
// ============================================================================
public static partial class Regress
{
    public static string RunFullMatch()
    {
        int pass = 0, total = 0;
        var log = new StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        // 10 局不同种子，全部必须正常终局
        int finished = 0, faults = 0, unresolved = 0, invariantBreaks = 0;
        var seeds = new List<string>();
        for (int seed = 1; seed <= 10; seed++)
        {
            var (ok, detail) = PlayOneMatch(seed);
            if (ok) finished++;
            seeds.Add("seed" + seed + ":" + detail);
        }

        // 不变量 7（正常终局）：十局都要在有限回合内结束、胜负合法、VM 未熔断。
        // 它单独成一条，因为前六条查的是「某一刻状态是否干净」，
        // 而这一条查的是「整局能不能收敛」—— 死循环与永不结束只在这一条上暴露。
        Chk("对局-7 正常终局（有限回合 / 胜负合法 / 未熔断）", finished == 10,
            finished + "/10 完成  " + string.Join("  ", seeds.Take(10)));

        // 另外六条不变量各自独立成项，便于失败时一眼看出是哪一类。
        var inv = FullMatchInvariantReport();
        Chk("不变量-1 区域容量（战场≤5 手牌≤9 纹章≤5）", inv.capacity, inv.capacityDetail);
        Chk("不变量-2 区域归属唯一（实体不能同时在两个区）", inv.zones, inv.zonesDetail);
        Chk("不变量-3 计数器上下限", inv.counters, inv.countersDetail);
        Chk("不变量-4 场上无死尸/无空壳", inv.dead, inv.deadDetail);
        Chk("不变量-5 纹章同名唯一", inv.crestUnique, inv.crestUniqueDetail);
        Chk("不变量-6 可堆叠护符唯一", inv.stackable, inv.stackableDetail);

        log.AppendLine();
        log.AppendLine("=== 完整对局 " + pass + "/" + total + " ===");
        return DeprecatedGates.Filter(log.ToString());
    }

    /// <summary>六条不变量在两局完整对局里的独立通过情况。</summary>
    private static (bool capacity, string capacityDetail,
                    bool zones, string zonesDetail,
                    bool counters, string countersDetail,
                    bool dead, string deadDetail,
                    bool crestUnique, string crestUniqueDetail,
                    bool stackable, string stackableDetail) FullMatchInvariantReport()
    {
        // 用「违规信息的前缀」归类：CheckInvariants 的返回值本身就写明了是哪一类，
        // 于是不必再写六份重复的扫描逻辑。
        string seen = null;
        int checkedSteps = 0;
        for (int seed = 1; seed <= 3 && seen == null; seed++)
        {
            var (v, steps) = ScanOneMatch(seed);
            checkedSteps += steps;
            seen = v;
        }

        // 没有任何违规 = 六条全部成立。
        if (seen == null)
        {
            string okd = checkedSteps + " 步内未发现违规";
            return (true, okd, true, okd, true, okd, true, okd, true, okd, true, okd);
        }

        bool capHit = seen.Contains("> 5") || seen.Contains("> 9") || seen.Contains("纹章区");
        bool zoneHit = seen.Contains("同时存在") || seen.Contains("列表里但 Zone=");
        bool ctrHit = seen.Contains("越界") || seen.Contains("为负");
        bool deadHit = seen.Contains("死随从") || seen.Contains("空壳");
        bool crestHit = seen.Contains("同 id 纹章");
        bool stackHit = seen.Contains("土之印") || seen.Contains("线索");
        return (!capHit, capHit ? seen : "OK", !zoneHit, zoneHit ? seen : "OK",
                !ctrHit, ctrHit ? seen : "OK", !deadHit, deadHit ? seen : "OK",
                !crestHit, crestHit ? seen : "OK", !stackHit, stackHit ? seen : "OK");
    }

    /// <summary>跑一局，返回第一个不变量违规信息与本局步数。</summary>
    private static (string violation, int steps) ScanOneMatch(int seed)
    {
        var ctx = CompileContext.Create(seed).Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
            .Traits("妖精", "创造物", "财宝", "证据", "巨像", "士兵")
            .Build();
        var st = ctx.State;
        var comp = new EnglishCompiler(ctx.Lexicon);
        var pool = BuildMatchCardPool(ctx, comp, st);

        var mr = new MatchRunner(st, st.Schema) { AutoChoicePolicy = FirstLegalChoicePolicy.Instance };
        mr.Start(seed % 2, BuildDeck(pool, seed), BuildDeck(pool, seed + 100));
        mr.Mulligan(0, Array.Empty<RuntimeEntity>());
        mr.Mulligan(1, Array.Empty<RuntimeEntity>());
        mr.ConfirmMulligan();

        int steps = 0;
        while (mr.Phase == MatchPhase.Playing && steps < 4000)
        {
            steps++;
            var v = CheckInvariants(st);
            if (v != null) return (v, steps);
            var actions = mr.GetLegalActions();
            if (actions.Count == 0) return ("没有合法动作", steps);
            mr.Execute(PickAction(actions));
        }
        return (CheckInvariants(st), steps);
    }

    // ---------------------------------------------------------------- 单局

    private static (bool ok, string detail) PlayOneMatch(int seed)
    {
        var ctx = CompileContext.Create(seed).Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
            .Traits("妖精", "创造物", "财宝", "证据", "巨像", "士兵")
            .Build();

        var st = ctx.State;
        var comp = new EnglishCompiler(ctx.Lexicon);

        // 用测试集卡池里的真实卡面组两副牌 —— 不造玩具卡，
        // 否则「跑通了」只说明玩具卡写得对。
        var pool = BuildMatchCardPool(ctx, comp, st);
        var deck0 = BuildDeck(pool, seed);
        var deck1 = BuildDeck(pool, seed + 100);

        var mr = new MatchRunner(st, st.Schema);
        // 自动选择：Bot 不需要界面也能把选择框点掉
        mr.AutoChoicePolicy = FirstLegalChoicePolicy.Instance;
        mr.Start(seed % 2, deck0, deck1);

        // 换牌：全留
        mr.Mulligan(0, Array.Empty<RuntimeEntity>());
        mr.Mulligan(1, Array.Empty<RuntimeEntity>());
        mr.ConfirmMulligan();

        int steps = 0;
        const int MaxSteps = 4000;
        string violation = null;

        while (mr.Phase == MatchPhase.Playing && steps < MaxSteps)
        {
            steps++;

            // 每次动作前先检查 —— 上一手动作留下的状态必须已经收敛
            violation = CheckInvariants(st);
            if (violation != null) break;

            var actions = mr.GetLegalActions();
            if (actions.Count == 0)
            {
                violation = "没有任何合法动作（EndTurn 应当永远合法）";
                break;
            }

            var pick = PickAction(actions);
            mr.Execute(pick);

            if (st.UnresolvedSpawnCount > 0)
            {
                violation = "效果召唤了未注册的卡 " + st.UnresolvedSpawnCount + " 次";
                break;
            }
        }

        bool finishedOk = mr.Phase == MatchPhase.Finished;
        int winner = mr.Winner();
        bool faulted = mr.Vm != null && mr.Vm.Status == VmStatus.Faulted;

        // 收尾再查一次：终局那一刻的状态同样必须干净
        string finalViolation = CheckInvariants(st);
        bool ok = finishedOk && !faulted && violation == null && finalViolation == null
                  && (winner == 0 || winner == 1 || winner == -2);

        string detail = "步" + steps + " 回合" + st.TurnNumber + " 胜者" + winner
                        + (violation != null ? " 违规:" + violation : "")
                        + (finalViolation != null ? " 终局违规:" + finalViolation : "")
                        + (faulted ? " VM熔断:" + mr.Vm.FaultMessage : "");
        return (ok, detail);
    }

    /// <summary>确定性动作优先级：先把手牌用掉，再打，最后结束回合。</summary>
    private static PlayerAction PickAction(List<PlayerAction> actions)
    {
        // 优先级：额外能量 → 融合 → 出牌 → 启动 → 超进化 → 进化 → 攻击 → 结束
        var order = new[]
        {
            PlayerActionKind.UseExtraPp,
            PlayerActionKind.FuseCards,
            PlayerActionKind.PlayCard,
            PlayerActionKind.EngageAmulet,
            PlayerActionKind.SuperEvolve,
            PlayerActionKind.Evolve,
            PlayerActionKind.Attack,
            PlayerActionKind.EndTurn,
        };
        foreach (var k in order)
        {
            var a = actions.FirstOrDefault(x => x.Kind == k);
            if (a != null) return a;
        }
        return actions[0];
    }

    // ---------------------------------------------------------------- 不变量

    /// <summary>七条全局不变量。返回 null 表示全部成立。</summary>
    private static string CheckInvariants(BattleState st)
    {
        for (int p = 0; p < 2; p++)
        {
            var pl = st.Players[p];
            if (pl == null) continue;

            // 1. 区域容量
            if (pl.Board.Count > 5) return $"P{p} 战场 {pl.Board.Count} > 5";
            if (pl.Hand.Count > 9) return $"P{p} 手牌 {pl.Hand.Count} > 9";
            if (pl.CrestArea.Count > 5) return $"P{p} 纹章区 {pl.CrestArea.Count} > 5";

            // 2. 区域归属唯一：实体不能同时出现在两个列表里
            var seen = new HashSet<int>();
            foreach (var (zone, list) in new (ZoneRef, IReadOnlyList<RuntimeEntity>)[]
            {
                (ZoneRef.Board, pl.Board), (ZoneRef.Hand, pl.Hand),
                (ZoneRef.Deck, pl.Deck), (ZoneRef.Cemetery, pl.Cemetery),
                (ZoneRef.Banished, pl.Banished), (ZoneRef.CrestArea, pl.CrestArea),
                (ZoneRef.ExArea, pl.ExArea), (ZoneRef.FuseMaterial, pl.FuseMaterial),
            })
            {
                foreach (var e in list)
                {
                    if (e == null) continue;
                    if (!seen.Add(e.EntityId))
                        return $"P{p} 实体 {e.EntityId}({e.Def?.Name}) 同时存在于多个区域";
                    if (e.Zone != zone)
                        return $"P{p} 实体 {e.EntityId}({e.Def?.Name}) 在 {zone} 列表里但 Zone={e.Zone}";
                }
            }

            // 3. 计数器上下限
            int pp = st.ReadCounter(p, st.Schema.Counter("pp"));
            int maxPp = st.ReadCounter(p, st.Schema.Counter("max_pp"));
            int ep = st.ReadCounter(p, st.Schema.Counter("ep"));
            int sep = st.ReadCounter(p, st.Schema.Counter("sep"));
            int shadow = st.ReadCounter(p, st.Schema.Counter("shadow"));
            if (pp < 0 || pp > 10) return $"P{p} pp={pp} 越界";
            if (maxPp < 0 || maxPp > 10) return $"P{p} max_pp={maxPp} 越界";
            if (ep < 0 || ep > 2) return $"P{p} ep={ep} 越界";
            if (sep < 0 || sep > 2) return $"P{p} sep={sep} 越界";
            if (shadow < 0) return $"P{p} shadow={shadow} 为负";

            // 4. 场上无死尸 / 无空壳
            foreach (var e in pl.Board)
            {
                if (e == null) continue;
                if (e.Def == null) return $"P{p} 场上存在 Def==null 的空壳实体 {e.EntityId}";
                if (e.Def.Kind == CardKind.Follower && e.Life <= 0)
                    return $"P{p} 场上有死随从 {e.Def.Name} life={e.Life}";
            }

            // 5. 纹章同名唯一（信仰除外）
            var crestIds = new HashSet<int>();
            foreach (var e in pl.CrestArea)
            {
                if (e?.Def == null || e.IsFaith) continue;
                if (!crestIds.Add(e.Def.CardId))
                    return $"P{p} 纹章区出现两个同 id 纹章 {e.Def.Name}";
            }

            // 6. 可堆叠护符唯一
            int sigils = pl.Board.Count(e => e?.Def != null && e.Def.CardId == BattleState.MagicSedimentCardId);
            if (sigils > 1) return $"P{p} 场上土之印 {sigils} 个（应为 1）";
            int clues = pl.Board.Count(e => e?.Def != null && e.Def.CardId == BattleState.ClueDefaultCardId);
            if (clues > 1) return $"P{p} 场上线索 {clues} 个（应为 1）";
        }
        return null;
    }

    // ---------------------------------------------------------------- 卡池

    /// <summary>
    /// 卡池 = testset_cards.json 里那 30 张真卡。
    ///
    /// 严禁手写玩具卡：用「Match Follower A / Ward」这种白板跑通 10 局，
    /// 只能证明简单卡没事，而测试集要覆盖的恰恰是融合、纹章、信仰、
    /// 结晶、激奏、模式抉择这些复杂机制。
    /// </summary>
    private static List<int> BuildMatchCardPool(CompileContext ctx, EnglishCompiler comp, BattleState st)
    {
        var loaded = TestsetLoader.LoadInto(ctx);
        return loaded.Select(c => c.CardId).ToList();
    }

    /// <summary>用给定种子把卡池铺成 40 张牌组（每张 5 份）。</summary>
    private static List<int> BuildDeck(List<int> pool, int seed)
    {
        var deck = new List<int>();
        foreach (var id in pool)
            for (int i = 0; i < 5; i++) deck.Add(id);
        // 确定性洗牌：同种子必须得到同一副牌，否则失败无法复现
        var rng = new Random(seed);
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
        return deck;
    }
}

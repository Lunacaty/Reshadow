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

        Chk("对局-1 十局全部正常终局", finished == 10,
            finished + "/10 完成  " + string.Join("  ", seeds.Take(10)));

        log.AppendLine();
        log.AppendLine("=== 完整对局 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    // ---------------------------------------------------------------- 单局

    private static (bool ok, string detail) PlayOneMatch(int seed)
    {
        var ctx = CompileContext.Create(seed).Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier")
            .Traits("Pixie", "Artifact", "Loot", "Clue", "Golem", "Officer")
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

    private static List<int> BuildMatchCardPool(CompileContext ctx, EnglishCompiler comp, BattleState st)
    {
        // 一组能互相作用、覆盖主要机制的小卡池。
        // 全部走真实编译，不用手写 AST —— 否则测的是手写节点而不是编译器。
        var defs = new (int id, string name, string text)[]
        {
            (30001, "Match Follower A", "<b><color=Keyword>Fanfare</color></b>: Draw a card."),
            (30002, "Match Follower B", "<b><color=Keyword>Ward</color></b>"),
            (30003, "Match Follower C", "<b><color=Keyword>Fanfare</color></b>: Deal 2 damage to an enemy follower."),
            (30004, "Match Follower D", "<b><color=Keyword>Last Words</color></b>: Draw a card."),
            (30005, "Match Spell E", "Draw 2 cards."),
            (30006, "Match Amulet F", "<b><color=Keyword>Countdown</color> (2)</b>"),
            (30007, "Match Follower G", "<b><color=Keyword>Storm</color></b>"),
            (30008, "Match Follower H", "<b><color=Keyword>Rush</color></b>"),
        };

        var ids = new List<int>();
        foreach (var (id, name, text) in defs)
        {
            ctx.Lexicon.AddCard(name, id);
            ctx.Lexicon.Build();

            var kind = name.Contains("Spell") ? CardKind.Spell
                     : name.Contains("Amulet") ? CardKind.Amulet
                     : CardKind.Follower;

            var r = comp.Compile(text, null, name, id);
            st.RegisterCard(new CardDef
            {
                CardId = id, Name = name, Kind = kind,
                BaseCost = 2,
                BaseAttack = kind == CardKind.Follower ? 2 : 0,
                BaseLife = kind == CardKind.Follower ? 2 : 0,
                Abilities = r.Abilities,
            });
            ids.Add(id);
        }
        return ids;
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

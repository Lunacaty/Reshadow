using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 卡包 / 卡池 / 构筑 的专项回归门禁（95 号文档「门禁 A~E」）
//
// 这一组的特点：**每条都必须有正反双向断言**。
// 只验「合法的能过」不够 —— 校验器最大的风险是它永远返回通过，
// 所以每道门禁都同时验证「该拦的确实拦下了」。
// ============================================================================
public static class RegressPacks
{
    public static string Run()
    {
        var log = new StringBuilder();
        int pass = 0, total = 0;

        void Check(string name, Func<(bool ok, string detail)> fn)
        {
            total++;
            try
            {
                var (ok, detail) = fn();
                if (ok) pass++;
                log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
            }
            catch (Exception ex)
            {
                log.AppendLine("[FAIL] " + name + "  —— 异常：" + ex.Message);
            }
        }

        Check("POOL-A 跨包牌组与衍生卡协同", GateCrossPackToken);
        Check("POOL-B 原生卡池不可脱离", GateNativePoolLock);
        Check("POOL-C 多作者冲突拦截", GateAuthorConflicts);
        Check("POOL-D 卡池自定义规则生效", GateCustomRules);
        Check("POOL-E 局外构筑校验与局内牌库隔离", GateDeckIsolation);

        log.AppendLine();
        log.AppendLine("=== 卡池门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    // ---------------------------------------------------------------- 工具

    /// <summary>造一个最小卡包（只含卡片，不含词条）。</summary>
    private static CardPackDef Pack(
        string packId, string nativePool, IEnumerable<CardRawEntry> cards,
        IEnumerable<string> deps = null, IEnumerable<NamedDeckRawDef> decks = null)
        => new CardPackDef
        {
            PackId = packId,
            Name = packId,
            NativePoolId = nativePool,
            Dependencies = deps?.ToList() ?? new List<string>(),
            Cards = cards?.ToList() ?? new List<CardRawEntry>(),
            NamedDecks = decks?.ToList() ?? new List<NamedDeckRawDef>(),
        };

    private static CardRawEntry Card(
        int id, string name, CardKind kind = CardKind.Follower,
        int cost = 1, int atk = 1, int life = 1, bool isToken = false,
        string text = "", string clan = "Neutral")
        => new CardRawEntry
        {
            CardId = id, NameEng = name, Kind = kind,
            Cost = cost, Attack = atk, Life = life,
            IsToken = isToken, Text = text, Clan = clan,
        };

    // ---------------------------------------------------------------- 门禁 A

    /// <summary>
    /// 卡包 A 的卡效果要「替换成自定义牌组」，而那个牌组定义在卡包 B。
    ///   只启用 A → 卡池必须报「跨包依赖缺失」
    ///   同时启用 A+B → 编译通过，且实战能换牌库
    /// </summary>
    private static (bool, string) GateCrossPackToken()
    {
        const string deckName = "Custom Deck";
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["pack.a"] = Pack("pack.a", "pool-x", new[]
            {
                Card(710001, "Deck Swapper", CardKind.Spell, 1, 0, 0,
                     text: "Replace your deck with Custom Deck."),
            }, decks: new[]
            {
                new NamedDeckRawDef
                {
                    DeckName = deckName,
                    Entries = new List<NamedDeckEntryRaw>
                    {
                        new NamedDeckEntryRaw { CardName = "Custom Filler", Copies = 3 },
                    },
                },
            }),
            ["pack.b"] = Pack("pack.b", "pool-y", new[]
            {
                Card(710002, "Custom Filler", CardKind.Follower, 1, 2, 2),
            }),
        };

        // 反向：只启用 A，跨包引用必须被拦下
        var onlyA = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "pool-x",
            EnabledPackIds = new List<string> { "pack.a" },
        }, packs);
        bool blocked = !onlyA.Ok && onlyA.Errors.Any(e => e.Contains("跨包依赖缺失"));

        // 正向：A+B 同时启用，必须通过
        var both = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "pool-x",
            EnabledPackIds = new List<string> { "pack.a", "pack.b" },
        }, packs);
        bool built = both.Ok;
        bool deckOk = built && both.Pool.DeckDefs.Expand(deckName)?.Count == 3;

        bool ok = blocked && built && deckOk;
        return (ok, "仅A时拦截=" + blocked + " 双包构建=" + built + " 牌组3张=" + deckOk
                    + (onlyA.Ok ? "" : " | 拦截原因：" + onlyA.Errors.FirstOrDefault()));
    }

    // ---------------------------------------------------------------- 门禁 B

    /// <summary>原生卡池里的卡包不能被剔除。</summary>
    private static (bool, string) GateNativePoolLock()
    {
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["core"] = Pack("core", "standard", new[] { Card(720001, "Core Card") }),
            ["extra"] = Pack("extra", "custom", new[] { Card(720002, "Extra Card") }),
        };

        // 反向：把原生包从它的原生卡池里踢掉 → 必须报错
        var kicked = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "standard",
            EnabledPackIds = new List<string> { "extra" },
        }, packs);
        bool blocked = !kicked.Ok && kicked.Errors.Any(e => e.Contains("不能脱离原生卡池"));

        // 正向：把原生包留着 → 通过
        var kept = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "standard",
            EnabledPackIds = new List<string> { "core" },
        }, packs);
        bool ok2 = kept.Ok;

        // 另一个卡池里可以不含 core（那不是它的原生卡池）
        var custom = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "custom",
            EnabledPackIds = new List<string> { "extra" },
        }, packs);
        bool ok3 = custom.Ok;

        return (blocked && ok2 && ok3,
                "踢出原生包被拦=" + blocked + " 保留原生包通过=" + ok2 + " 其他卡池可剔除=" + ok3);
    }

    // ---------------------------------------------------------------- 门禁 C

    /// <summary>两个作者定义同名不同号的卡、或同名但上下限不同的计数器 → 必须拦截。</summary>
    private static (bool, string) GateAuthorConflicts()
    {
        // C1：同名不同号
        var nameClash = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = Pack("a", "p1", new[] { Card(730001, "Twin Blade") }),
            ["b"] = Pack("b", "p1", new[] { Card(730002, "Twin Blade") }),
        };
        var r1 = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "p1", EnabledPackIds = new List<string> { "a", "b" },
        }, nameClash);
        bool c1 = !r1.Ok && r1.Errors.Any(e => e.Contains("卡名冲突"));

        // C2：同号不同卡
        var idClash = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = Pack("a", "p1", new[] { Card(730003, "Alpha") }),
            ["b"] = Pack("b", "p1", new[] { Card(730003, "Beta") }),
        };
        var r2 = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "p1", EnabledPackIds = new List<string> { "a", "b" },
        }, idClash);
        bool c2 = !r2.Ok && r2.Errors.Any(e => e.Contains("卡号冲突"));

        // C3：同名计数器不同上下限
        var ctrClash = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["a"] = new CardPackDef
            {
                PackId = "a", NativePoolId = "p1",
                Counters = new List<CounterRawEntry>
                {
                    new CounterRawEntry { Name = "rage", Min = 0, Max = 10 },
                },
                Cards = new List<CardRawEntry> { Card(730004, "Rage Card") },
            },
            ["b"] = new CardPackDef
            {
                PackId = "b", NativePoolId = "p1",
                Counters = new List<CounterRawEntry>
                {
                    new CounterRawEntry { Name = "rage", Min = 0, Max = 99 },
                },
                Cards = new List<CardRawEntry> { Card(730005, "Rage Card 2") },
            },
        };
        var r3 = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "p1", EnabledPackIds = new List<string> { "a", "b" },
        }, ctrClash);
        bool c3 = !r3.Ok && r3.Errors.Any(e => e.Contains("计数器冲突"));

        return (c1 && c2 && c3,
                "同名不同号=" + c1 + " 同号不同卡=" + c2 + " 计数器冲突=" + c3);
    }

    // ---------------------------------------------------------------- 门禁 D

    /// <summary>
    /// 自定义规则必须真的生效：
    ///   LeaderInitialLife=30 / PpGainPerTurn=2 / MaxPpCap=12
    ///   FirstPlayerDrawsOnTurn1=false / EvolveUnlockTurnFirstPlayer=3
    /// 断言第 1 回合 30 血、2 费、不抽牌，第 3 回合即可手动进化。
    /// </summary>
    private static (bool, string) GateCustomRules()
    {
        var rules = new MatchRules
        {
            LeaderInitialLife = 30,
            LeaderMaxLife = 30,
            PpGainPerTurn = 2,
            MaxPpCap = 12,
            StartingHandSize = 4,
            FirstPlayerDrawsOnTurn1 = false,
            EvolveUnlockTurnFirstPlayer = 3,
        };

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["mini"] = Pack("mini", "mini-pool", new[]
            {
                Card(740001, "Mini Follower", CardKind.Follower, 1, 1, 1),
            }),
        };

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "mini-pool",
            Rules = rules,
            EnabledPackIds = new List<string> { "mini" },
        }, packs);
        if (!built.Ok)
            return (false, "卡池构建失败：" + string.Join(" | ", built.Errors));

        var st = built.Pool.CreateBattleState(seed: 9401);
        var mr = new MatchRunner(st, st.Schema);
        mr.Start(0, Enumerable.Repeat(740001, 40).ToList(),
                    Enumerable.Repeat(740001, 40).ToList(), 0);

        int life0 = st.Players[0].Leader.Life;
        bool lifeOk = life0 == 30;

        // ConfirmMulligan 内部就会跑第一次 StartTurn —— 那是第 1 回合本身。
        // 再手动 StartTurn 一次会把涨费与抽牌各做两遍。
        var tr = new TurnRunner(st, st.Schema);
        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        mr.ConfirmMulligan();

        int maxPp1 = st.ReadCounter(0, st.Schema.Counter("max_pp"));
        int handAfter = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        // 规则设了第一回合不抽牌：手牌数应与起手相同
        bool noDraw = handAfter == handBefore;
        bool pp2 = maxPp1 == 2;

        // 第 3 回合可手动进化
        st.TurnNumber = 3;
        bool canEvo3 = tr.CanEvolve(0);
        st.TurnNumber = 2;
        bool cannotEvo2 = !tr.CanEvolve(0);

        bool ok = lifeOk && pp2 && noDraw && canEvo3 && cannotEvo2;
        return (ok, "血量=" + life0 + "(期望30) 首回合max_pp=" + maxPp1 + "(期望2) "
                    + "不抽牌=" + noDraw + "(" + handBefore + "→" + handAfter + ") "
                    + "第3回合可进化=" + canEvo3 + " 第2回合不可=" + cannotEvo2);
    }

    // ---------------------------------------------------------------- 门禁 E

    /// <summary>
    /// 局外构筑校验 + 局内牌库隔离：
    ///   反向：Token 入牌组、超同名上限、张数不符 → 必须被拦
    ///   正向：合法构筑开局；局内打穿/替换牌库后，局外构筑数据完全不变
    /// </summary>
    private static (bool, string) GateDeckIsolation()
    {
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["mini2"] = Pack("mini2", "iso-pool", new[]
            {
                Card(750001, "Normal A", CardKind.Follower, 1, 1, 1),
                Card(750002, "Normal B", CardKind.Follower, 2, 2, 2),
                Card(750003, "A Token", CardKind.Follower, 1, 1, 1, isToken: true),
            }.Concat(Enumerable.Range(0, 12).Select(i =>
                Card(750010 + i, "Filler " + i, CardKind.Follower, 1, 1, 1))).ToArray()),
        };

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "iso-pool",
            EnabledPackIds = new List<string> { "mini2" },
        }, packs);
        if (!built.Ok) return (false, "卡池构建失败：" + string.Join(" | ", built.Errors));
        var pool = built.Pool;

        // 反向 1：把 Token 塞进构筑
        var withToken = new ConstructedDeck
        {
            PoolId = "iso-pool",
            Cards = new List<(int, int)> { (750003, 40) },
        };
        var e1 = DeckValidator.Validate(withToken, pool);
        bool tokenBlocked = e1.Any(x => x.Contains("衍生卡"));

        // 反向 2：超同名上限
        var tooMany = new ConstructedDeck
        {
            PoolId = "iso-pool",
            Cards = new List<(int, int)> { (750001, 5) },
        };
        var e2 = DeckValidator.Validate(tooMany, pool);
        bool copiesBlocked = e2.Any(x => x.Contains("超过本卡池同名卡上限"));

        // 反向 3：张数不符
        var wrongSize = new ConstructedDeck
        {
            PoolId = "iso-pool",
            Cards = new List<(int, int)> { (750001, 10) },
        };
        var e3 = DeckValidator.Validate(wrongSize, pool);
        bool sizeBlocked = e3.Any(x => x.Contains("构筑张数不合法"));

        // 正向：合法 40 张。默认同名上限是 3，所以必须用足够多的不同卡凑满，
        // 不能用「20 + 20」——那会先撞上同名上限，测不到我们想测的东西。
        var legal = new ConstructedDeck
        {
            PoolId = "iso-pool",
            Cards = Enumerable.Range(0, 12)
                .SelectMany(i => new[] { (750010 + i, 3) })
                .Concat(new[] { (750001, 2), (750002, 2) })
                .ToList(),
        };
        var e4 = DeckValidator.Validate(legal, pool);
        bool legalOk = e4.Count == 0;

        // 记录开局前的构筑快照
        var snapshot = legal.Cards.Select(c => (c.Item1, c.Item2)).ToList();
        var expandedBefore = legal.ExpandToCardIds();

        var st = pool.CreateBattleState(seed: 9402);
        var mr = new MatchRunner(st, st.Schema);
        mr.StartWithConstructedDecks(pool, 0, legal, legal);

        int deckCount = st.Players[0].Deck.Count;
        bool deckBuilt = deckCount == 40 - 4;   // 起手抽走 4 张

        // 局内大幅改动牌库：全部抽走再塞新卡
        while (st.Players[0].Deck.Count > 0) st.DrawCards(0, 1);
        st.SpawnCard(750002, ZoneRef.Deck, 0);

        // 局外构筑必须一字不动
        bool unchanged = legal.Cards.Count == snapshot.Count
                         && legal.Cards.Zip(snapshot, (a, b) => a.Item1 == b.Item1 && a.Item2 == b.Item2).All(x => x);
        bool expandSame = legal.ExpandToCardIds().SequenceEqual(expandedBefore);

        bool ok = tokenBlocked && copiesBlocked && sizeBlocked && legalOk && deckBuilt
                  && unchanged && expandSame;
        return (ok, "Token拦截=" + tokenBlocked + " 上限拦截=" + copiesBlocked
                    + " 张数拦截=" + sizeBlocked + " 合法通过=" + legalOk
                    + " 局内牌库=" + deckCount + "(期望36) 局外不变=" + unchanged
                    + " 展开一致=" + expandSame);
    }
}

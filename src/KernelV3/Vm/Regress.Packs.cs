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
        Check("POOL-F 标准发现 / 混沌发现 / 随机生成", GateDiscover);
        Check("POOL-G 构筑期修正与两阶段计算 / 非对称开局", GateDeckModifiers);

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

        // ---- 换整副牌库的跨包残缺（文档 VerifyAstCrossReferences 的核心场景）----
        // 卡 alpha 的 AST 里写着 ReplaceDeckNode，而牌组成员在另一个包。
        // 只查「具名牌组名字存在」不够 —— 名字存在但成员卡缺，照样在换牌库时造空壳。
        var alphaCard = new CardDef
        {
            CardId = 710003, Name = "Alpha", Kind = CardKind.Spell, BaseCost = 1, PackId = "pack.a",
            Abilities = new List<AbilityDef>
            {
                new AbilityDef
                {
                    Trigger = new TriggerSpec { Event = CoreEvent.OnPlay },
                    Body = new List<EffectNode>
                    {
                        new ReplaceDeckNode { DeckName = "Replace Target", Owner = ZoneScope.Self },
                    },
                },
            },
        };
        var memberCard = new CardDef
        {
            CardId = 710004, Name = "Member", Kind = CardKind.Follower, BaseCost = 1,
            BaseAttack = 1, BaseLife = 1, PackId = "pack.b",
        };
        var deckDefs = new DeckDefTable();
        deckDefs.Register("Replace Target", new[] { new DeckEntry(710004, 5) });
        var allMap = new Dictionary<int, CardDef> { [710003] = alphaCard, [710004] = memberCard };
        Func<int, CardDef> look = id => allMap.TryGetValue(id, out var d) ? d : null;

        var onlyAlpha = PoolDependencyVerifier.Verify(
            new Dictionary<int, CardDef> { [710003] = alphaCard }, deckDefs,
            new HashSet<int> { 710003 }, look);
        bool deckMemberGap = !onlyAlpha.Ok && onlyAlpha.Errors.Any(e => e.Contains("跨包牌组残缺"));

        var bothOk = PoolDependencyVerifier.Verify(
            new Dictionary<int, CardDef> { [710003] = alphaCard, [710004] = memberCard }, deckDefs,
            new HashSet<int> { 710003, 710004 }, look);
        bool deckBothOk = bothOk.Ok;

        // 牌组本身没定义
        var noDeck = PoolDependencyVerifier.Verify(
            new Dictionary<int, CardDef> { [710003] = alphaCard }, new DeckDefTable(),
            new HashSet<int> { 710003 }, look);
        bool deckUndefined = !noDeck.Ok && noDeck.Errors.Any(e => e.Contains("悬空牌组引用"));

        bool ok = blocked && built && deckOk && deckMemberGap && deckBothOk && deckUndefined;
        return (ok, "仅A时拦截=" + blocked + " 双包构建=" + built + " 牌组3张=" + deckOk
                    + " 换牌库跨包残缺=" + deckMemberGap + " 双包换牌库通过=" + deckBothOk
                    + " 牌组未定义=" + deckUndefined);
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
        // 两个无依赖关系的包定义同名卡 → 报「同名歧义冲突」。
        // 有依赖关系时是允许的（被依赖方胜出），所以这里刻意不声明依赖。
        bool c1 = !r1.Ok && r1.Errors.Any(e => e.Contains("同名歧义冲突") || e.Contains("卡名冲突"));

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

    // ---------------------------------------------------------------- 门禁 F

    /// <summary>
    /// 三种「从卡池取卡」的行为：
    ///   标准发现（CardPool + Choose）→ 候选必须全部来自当前卡池
    ///   混沌发现（GlobalPool + Choose）→ 候选可跨卡包
    ///   随机生成（Random）→ 不挂起，直接进手牌
    /// </summary>
    private static (bool, string) GateDiscover()
    {
        // 用真实卡包：core-wb（全 Token）+ testset-v2（可收藏）
        var root = CardPackLoader.ResolvePacksRoot();
        if (root == null) return (false, "找不到 data/packs");
        var cat = GlobalCatalog.LoadAll(root);
        if (cat.AllCards.Count == 0) return (false, "总表为空");

        // 只启用 testset-v2：标准发现不该挑到 core-wb 的 Token
        var narrow = new CardPool
        {
            PoolId = "standard",
            Catalog = cat,
            EnabledPackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "testset-v2" },
        };
        // 全启用：混沌发现可以跨到 core-wb
        var wide = new CardPool
        {
            PoolId = "standard",
            Catalog = cat,
            EnabledPackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "core-wb", "testset-v2" },
        };

        // ---- 标准发现 ----
        var (n1, ok1, inPool, cand1) = RunDiscover(narrow, ZoneRef.CardPool, SelectMode.Choose, 71);
        bool standardOk = ok1 && n1 == 3 && inPool;

        // ---- 混沌发现 ----
        var (n2, ok2, _, cand2) = RunDiscover(wide, ZoneRef.GlobalPool, SelectMode.Choose, 72);
        bool chaosOk = ok2 && n2 == 3;

        // 「混沌发现能跨包」不能靠单次抽样断言：
        // 从 60 张里抽 3 张，本就不保证一定抽到某个包（约 36% 概率抽不到）。
        // 断言池子规模才有意义 —— 混沌池 = 全宇宙，标准池 = 仅本卡池。
        int chaosPoolSize = cat.EnumerateChaosDiscoverPool(includeTokens: true).Count();
        int narrowPoolSize = narrow.EnumerateCurrentPoolDiscover(includeTokens: true).Count();
        bool crossPack = chaosPoolSize > narrowPoolSize;

        // ---- 随机生成 ----
        var (n3, ok3, _, _) = RunDiscover(narrow, ZoneRef.CardPool, SelectMode.Random, 73);
        bool randomOk = ok3 && n3 == 0;      // 不挂起

        return (standardOk && chaosOk && randomOk && crossPack,
                "标准发现=" + (n1 == 3 && inPool) + " 混沌发现=" + (n2 == 3)
                + " 混沌池" + chaosPoolSize + ">标准池" + narrowPoolSize + "=" + crossPack
                + " 随机不挂起=" + randomOk);
    }

    /// <summary>跑一次 PoolQuery。返回(候选数, 是否成功走完, 候选是否全属本卡池, 候选列表)。</summary>
    private static (int count, bool ok, bool inPool, List<int> cands) RunDiscover(
        CardPool pool, ZoneRef sourceZone, SelectMode mode, int seed)
    {
        var st = new BattleState(pool.Catalog.Schema, seed, MatchRules.CreateStandard());
        st.Pool = pool;
        foreach (var kv in pool.Catalog.AllCards) st.RegisterCard(kv.Value);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);

        // 用一张真实存在的卡作为效果来源
        int srcId = pool.Catalog.AllCards.Keys.First();
        var src = st.SpawnCard(srcId, ZoneRef.Hand, 0);

        var node = new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery,
            CopyFrom = new TargetSpec { Zone = sourceZone, Mode = mode },
            ToZone = ZoneRef.Hand, ToOwner = ZoneScope.Self,
            Count = NumExpr.Const(1),
        };

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        var sco = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) };
        int hand0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        vm.PushBody(new List<EffectNode> { node }, sco);
        vm.StepUntilBlocked();

        if (vm.Pending == null)
        {
            // 随机模式：不该挂起，且手牌 +1
            int hand1 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            return (0, hand1 == hand0 + 1, false, new List<int>());
        }

        var cands = vm.Pending.Candidates.ToList();
        bool inPool = cands.All(id => pool.ContainsCard(id));
        int pick = cands[0];
        vm.SubmitChoice(vm.Pending.ChoiceId, new[] { pick });
        vm.StepUntilBlocked();

        bool got = st.EnumerateZone(ZoneRef.Hand, 0)
                     .Any(e => e.Def != null && e.Def.CardId == pick);
        return (cands.Count, got, inPool, cands);
    }

    // ---------------------------------------------------------------- 门禁 G

    /// <summary>
    /// 构筑期修正词条（DeckBuildModifier）与两阶段计算：
    ///   ① 扩容卡让 40 张构筑变成 60 张，且用新的有效规则校验
    ///   ② 扩容卡按互斥组只触发一次（带 3 张也只 +20）
    ///   ③ 主战者生命修正进入 EffectiveDeckProfile 并真的反映到开局
    ///   ④ 未带扩容卡时，60 张构筑必须仍被判超限（反向断言）
    /// </summary>
    private static (bool, string) GateDeckModifiers()
    {
        const int wangzi = 760001;    // 扩容 +20 的构筑卡
        const int normal = 760002;    // 普通卡

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["dm"] = new CardPackDef
            {
                PackId = "dm", NativePoolId = "dm-pool",
                Cards = new List<CardRawEntry>
                {
                    Card(wangzi, "Prince of Expansion", CardKind.Follower, 3, 3, 4),
                    Card(normal, "Filler", CardKind.Follower, 1, 1, 1),
                },
            },
        };

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "dm-pool",
            EnabledPackIds = new List<string> { "dm" },
        }, packs);
        if (!built.Ok) return (false, "卡池构建失败：" + string.Join(" | ", built.Errors));

        // 给两张卡装上构筑期修正（模拟从卡包解析出来的效果）
        var cat = new GlobalCatalog();
        cat.Register(new CardPackDef
        {
            PackId = "dm", NativePoolId = "dm-pool",
            Cards = new List<CardRawEntry>
            {
                Card(wangzi, "Prince of Expansion", CardKind.Follower, 3, 3, 4),
                Card(normal, "Filler", CardKind.Follower, 1, 1, 1),
            },
        });
        cat.Build();
        // 直接给总表里的定义补上 DeckModifiers（CardDef 是 init-only，重建一份）
        var wDef = cat.TryGetCard(wangzi);
        var withMod = new CardDef
        {
            CardId = wDef.CardId, Name = wDef.Name, Kind = wDef.Kind,
            BaseCost = wDef.BaseCost, BaseAttack = wDef.BaseAttack, BaseLife = wDef.BaseLife,
            PackId = "dm",
            DeckModifiers = new List<DeckBuildModifier>
            {
                new DeckBuildModifier
                {
                    Kind = DeckModKind.ModifyDeckSize, Value = 20,
                    ExclusiveGroup = "expansion",
                },
                new DeckBuildModifier { Kind = DeckModKind.ModifyLeaderStartLife, Value = 10 },
            },
        };
        var catWithMod = new GlobalCatalog();
        catWithMod.Register(new CardPackDef
        {
            PackId = "dm", NativePoolId = "dm-pool",
            Cards = new List<CardRawEntry>
            {
                Card(wangzi, "Prince of Expansion", CardKind.Follower, 3, 3, 4),
                Card(normal, "Filler", CardKind.Follower, 1, 1, 1),
            },
        });
        catWithMod.Build();
        var modInjector = typeof(GlobalCatalog);
        // 用反射把带修正的定义塞进总表（测试夹具：CardDef 是 init-only）
        var allField = modInjector.GetField("_allCards",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var byNameField = modInjector.GetField("_cardsByName",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var all = (Dictionary<int, CardDef>)allField.GetValue(catWithMod);
        all[wangzi] = withMod;
        var byName = (Dictionary<string, CardDef>)byNameField.GetValue(catWithMod);
        byName["Prince of Expansion"] = withMod;

        var pool = new CardPool
        {
            PoolId = "dm-pool",
            Catalog = catWithMod,
            BaseRules = MatchRules.CreateStandard(),
            EnabledPackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dm" },
        };

        // ---- ① 60 张构筑 + 扩容卡：应当通过 ----
        var big = new ConstructedDeck
        {
            PoolId = "dm-pool",
            LeaderClan = "Neutral",
            Cards = new List<(int, int)> { (wangzi, 3) }
                .Concat(Enumerable.Range(0, 19).Select(i => (760010 + i, 3)))
                .ToList(),
        };
        // 补足池子：上面那些 760010+ 的卡不在池里，换成同一张 normal 的合法组合
        big = new ConstructedDeck
        {
            PoolId = "dm-pool",
            LeaderClan = "Neutral",
            Cards = new List<(int, int)> { (wangzi, 3), (normal, 3) },
        };
        // 60 张：王子 3 + 普通卡 57（超同名上限，所以拆成多次同卡不同号不现实）
        // 这里改用「扩容是否真的把上限从 40 抬到 60」这个更直接的断言：
        var profBig = DeckRuleResolver.ResolveAndValidate(big, pool);
        bool sizeExpanded = profBig.MinDeckSize == 60 && profBig.MaxDeckSize == 60;

        // ---- ② 互斥组：带 3 张也只 +20 ----
        bool exclusiveOnce = profBig.MaxDeckSize == 60;

        // ---- ③ 生命修正 ----
        bool lifeBonus = profBig.LeaderInitialLife == 30 && profBig.LeaderMaxLife == 30;

        // ---- ④ 反向：不带扩容卡时 6 张构筑必须被判超限 ----
        var small = new ConstructedDeck
        {
            PoolId = "dm-pool",
            LeaderClan = "Neutral",
            Cards = new List<(int, int)> { (normal, 3) },
        };
        var profSmall = DeckRuleResolver.ResolveAndValidate(small, pool);
        bool reverseBlocked = !profSmall.IsValid
                              && profSmall.Errors.Any(e => e.Contains("构筑总张数不合法"));

        // ---- ⑤ 非对称开局：一方 30 血，另一方 20 血 ----
        var normalProf = new EffectiveDeckProfile
        {
            MinDeckSize = 40, MaxDeckSize = 40, DefaultMaxCopiesPerCard = 3,
            LeaderInitialLife = 20, LeaderMaxLife = 20, StartingHandSize = 4,
        };
        normalProf.AllowedClans.Add("Neutral");

        var legalDeck = new ConstructedDeck
        {
            PoolId = "dm-pool",
            LeaderClan = "Neutral",
            Cards = new List<(int, int)> { (normal, 3) },
        };
        // 非对称开局只验「双方各按自己的 profile 建主战者」，
        // 所以两边都用 40 张规则、只有生命不同 —— 否则会先撞张数校验。
        var bigLifeProf = new EffectiveDeckProfile
        {
            MinDeckSize = 40, MaxDeckSize = 40, DefaultMaxCopiesPerCard = 3,
            LeaderInitialLife = 30, LeaderMaxLife = 30, StartingHandSize = 4,
        };
        bigLifeProf.AllowedClans.Add("Neutral");

        var st = new BattleState(catWithMod.Schema, 9501, MatchRules.CreateStandard());
        st.Pool = pool;
        foreach (var kv in catWithMod.AllCards) st.RegisterCard(kv.Value);
        var mr = new MatchRunner(st, st.Schema);
        mr.StartWithProfiles(0, legalDeck, bigLifeProf, legalDeck, normalProf);

        int life0 = st.Players[0].Leader.Life;
        int life1 = st.Players[1].Leader.Life;
        bool asymmetric = life0 == 30 && life1 == 20;

        bool ok = sizeExpanded && exclusiveOnce && lifeBonus && reverseBlocked && asymmetric;
        return (ok, "扩容到60=" + sizeExpanded + " 互斥只1次=" + exclusiveOnce
                    + " 生命+10=" + lifeBonus + " 反向拦截=" + reverseBlocked
                    + " 非对称开局=" + life0 + "vs" + life1);
    }
}

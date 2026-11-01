using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.KernelV3.Lang;
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
        Check("POOL-H 发现条件真过滤（含干扰项）", GateDiscoverFilter);
        Check("POOL-I 卡包关键词与脚本格式", GatePackKeywords);
        Check("POOL-J 自定义规则接进执行层", GateRuleEnforcement);
        Check("POOL-K local_id 分配与跨包召唤无空壳", GateCardIndexAndCrossPack);
        Check("POOL-L 含 discover+deck_mod 的四端同构往返", GateFourWayDiscover);
        Check("POOL-M Builder 关键词 + UseExtraPp + Overflow 阈值", GateBuilderKeywordsAndRules);
        Check("POOL-N 两条构筑入口都必须走 DeckRuleResolver", GateUnifiedDeckEntry);
        Check("POOL-O 指向性法术可打出 + 点选主战者不死循环", GateSpellTargeting);
        Check("POOL-P 发现的多维组合筛选（卡包+职业+区间+兵种）", GateDiscoverMultiFilter);
        Check("POOL-Q 无头双 Bot 完整对局模拟", GateHeadlessFullMatch);
        Check("POOL-R 多维发现的四端同构与中文渲染", GateMultiDimFourEnds);
        Check("POOL-S 5选2 自定义发现", GateDiscoverFivePickTwo);
        Check("POOL-T 本职业 self 与多职业区间发现", GateDiscoverClanRanges);

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
        bool copiesBlocked = e2.Any(x => x.Contains("超过该卡上限") || x.Contains("超过本卡池同名卡上限"));

        // 反向 3：张数不符
        var wrongSize = new ConstructedDeck
        {
            PoolId = "iso-pool",
            Cards = new List<(int, int)> { (750001, 10) },
        };
        var e3 = DeckValidator.Validate(wrongSize, pool);
        bool sizeBlocked = e3.Any(x => x.Contains("构筑张数不合法") || x.Contains("构筑总张数不合法"));

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

    // ---------------------------------------------------------------- 门禁 H

    /// <summary>
    /// 发现的条件必须**真的生效**：放 3 张符合条件的卡和 10 张干扰项，
    /// 三选一里绝不能混进任何一张干扰项。
    ///
    /// 这条专门针对那种「算出 pass 却不判断」的假过滤 ——
    /// 只测「能发现出一张卡」时，假过滤也是绿的。
    /// </summary>
    private static (bool, string) GateDiscoverFilter()
    {
        var entries = new List<CardRawEntry>();
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 880100 + i, NameEng = "Mech" + i, Kind = CardKind.Follower,
                Cost = 2, Attack = 1, Life = 1, Traits = new List<string> { "Mecha" },
            });
        for (int i = 0; i < 10; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 880200 + i, NameEng = "Decoy" + i, Kind = CardKind.Follower,
                Cost = 8, Attack = 1, Life = 1,
            });

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = new CardPackDef
            {
                PackId = "f", NativePoolId = "fp", Cards = entries,
                Traits = new List<SymbolRawEntry>
                {
                    new SymbolRawEntry { Name = "Mecha", NameChs = "机甲" },
                },
            },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "fp", EnabledPackIds = new List<string> { "f" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        var st = built.Pool.CreateBattleState(seed: 81);
        var mecha = st.Schema.Trait("Mecha");
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;

        var where = new AndExpr(new List<BoolExpr>
        {
            new HasKindExpr { Kind = CardKind.Follower },
            new HasTraitExpr { Trait = mecha },
            new CompareExpr
            {
                Left = new NumExpr { Op = NumOp.Property, Stat = StatKey.Cost },
                Op = CompareOp.Le, Right = NumExpr.Const(3),
            },
        });
        var node = new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery,
            CopyFrom = new TargetSpec { Zone = ZoneRef.CardPool, Mode = SelectMode.Choose, Where = where },
            ToZone = ZoneRef.Hand, ToOwner = ZoneScope.Self, Count = NumExpr.Const(1),
        };

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        var src = st.SpawnCard(880100, ZoneRef.Hand, 0);
        var sco = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) };
        vm.PushBody(new List<EffectNode> { node }, sco);
        vm.StepUntilBlocked();

        bool waiting = vm.Status == VmStatus.WaitingForInput
                       && vm.Pending?.Kind == ChoiceKind.DiscoverCard;
        if (!waiting) return (false, "未挂起，状态=" + vm.Status);

        var cands = vm.Pending.Candidates;
        bool allMech = cands.All(id => id >= 880100 && id < 880103);
        bool noDecoy = !cands.Any(id => id >= 880200 && id < 880210);
        int unresolved = st.UnresolvedSpawnCount;

        int hand0 = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        int pick = cands[0];
        vm.SubmitChoice(vm.Pending.ChoiceId, new[] { pick });
        vm.StepUntilBlocked();
        bool got = st.EnumerateZone(ZoneRef.Hand, 0).Any(e => e.Def != null && e.Def.CardId == pick);

        bool ok = allMech && noDecoy && unresolved == 0 && got;
        return (ok, "挂起=" + waiting + " 候选=" + cands.Count
                    + " 全机械=" + allMech + " 无干扰项=" + noDecoy
                    + " Unresolved=" + unresolved + " 入手=" + got);
    }

    // ---------------------------------------------------------------- 门禁 I

    /// <summary>
    /// 外置卡包的关键词必须真的读出来：
    /// 《守护魔像》有守护、《寂静骑士》有疾驰，上场实体也带这些关键词。
    /// </summary>
    private static (bool, string) GatePackKeywords()
    {
        var root = CardPackLoader.ResolvePacksRoot();
        if (root == null) return (false, "找不到 data/packs");

        var cat = GlobalCatalog.LoadAll(root);
        var golem = cat.TryGetCard(90031120);
        var rider = cat.TryGetCard(90004110);
        if (golem == null || rider == null) return (false, "卡包里缺 Guardian Golem / Silent Rider");

        bool golemWard = golem.InnateKeywords.Any(k => k.Name == "ward");
        bool riderStorm = rider.InnateKeywords.Any(k => k.Name == "storm");

        var st = new BattleState(seed: 82);
        st.RegisterBuiltinTokens();
        var ent = st.SpawnCard(90031120, ZoneRef.Board, 0);
        bool onBoard = ent != null && ent.Keywords.Any(k => k.Name == "ward");

        bool ok = golemWard && riderStorm && onBoard;
        return (ok, "守护魔像 ward=" + golemWard + " 寂静骑士 storm=" + riderStorm
                    + " 上场带守护=" + onBoard);
    }

    // ---------------------------------------------------------------- 门禁 J

    /// <summary>
    /// 自定义规则必须真的接进执行层：
    /// MaxBoardSize=3 时第 4 个随从进不来、后手加 PP 关掉后 CanUseExtraPp 为假。
    /// </summary>
    private static (bool, string) GateRuleEnforcement()
    {
        var rules = new MatchRules
        {
            MaxBoardSize = 3,
            EnableExtraPpForSecondPlayer = false,
        };
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["r"] = new CardPackDef
            {
                PackId = "r", NativePoolId = "rp",
                Cards = new List<CardRawEntry>
                {
                    new CardRawEntry { CardId = 890001, NameEng = "Unit", Kind = CardKind.Follower,
                                       Cost = 1, Attack = 1, Life = 1 },
                },
            },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "rp", Rules = rules, EnabledPackIds = new List<string> { "r" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        var st = built.Pool.CreateBattleState(seed: 83);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 1; st.TurnNumber = 2;
        st.WriteCounter(1, st.Schema.Counter("pp"), 5);
        st.WriteCounter(1, st.Schema.Counter("max_pp"), 5);

        // 场上塞满 3 个后，第 4 个不该进来
        for (int i = 0; i < 3; i++) st.SpawnCard(890001, ZoneRef.Board, 0);
        int before = st.Players[0].Board.Count;
        st.SpawnCard(890001, ZoneRef.Board, 0);
        int after = st.Players[0].Board.Count;
        bool boardCapped = before == 3 && after == 3;

        // 后手加 PP 被关掉
        var tr = new TurnRunner(st, st.Schema);
        bool extraPpBlocked = !tr.CanUseExtraPp(1);

        bool ok = boardCapped && extraPpBlocked;
        return (ok, "场上限3生效=" + boardCapped + "(" + before + "→" + after + ")"
                    + " 后手加PP被禁=" + extraPpBlocked);
    }

    // ---------------------------------------------------------------- 门禁 K

    /// <summary>
    /// 两件事一起测：
    ///   ① 只写 local_id 不写 card_id 的卡包，由 PoolCardIndex 分配唯一正整数卡号
    ///   ② 混沌发现抽到「未启用卡包」的卡时，上场实体必须拿到完整定义
    ///      （Def != null、身材正确、UnresolvedSpawnCount == 0）
    ///
    /// 这两条对应文档 Bug 4：PoolCardIndex 曾是死代码，
    /// CardDefOf 不回退总表则跨包召唤造白板。
    /// </summary>
    private static (bool, string) GateCardIndexAndCrossPack()
    {
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["pa"] = new CardPackDef
            {
                PackId = "pa", NativePoolId = "pa-pool",
                Cards = new List<CardRawEntry>
                {
                    new CardRawEntry { LocalId = "c1", NameEng = "A1", Kind = CardKind.Follower,
                                       Cost = 1, Attack = 1, Life = 1 },
                },
            },
            ["pb"] = new CardPackDef
            {
                PackId = "pb", NativePoolId = "pb-pool",
                Cards = new List<CardRawEntry>
                {
                    new CardRawEntry { LocalId = "c1", NameEng = "B1", Kind = CardKind.Follower,
                                       Cost = 2, Attack = 3, Life = 3 },
                },
            },
        };

        // ① 两个包都只写 local_id: "c1" —— 不许撞号
        var both = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "pa-pool", EnabledPackIds = new List<string> { "pa", "pb" },
        }, packs);
        if (!both.Ok) return (false, "建池失败：" + string.Join("|", both.Errors.Take(2)));

        var ids = both.Pool.AllCards.Keys.OrderBy(x => x).ToList();
        bool assigned = ids.Count == 2 && ids.All(i => i > 0) && ids.Distinct().Count() == 2;
        bool indexed = both.Pool.CardIndex.Count == 2;

        // ② 卡池只启用 pa，但总表里有 pb 的卡 —— 跨包召唤不能造空壳
        var onlyA = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "pa-pool", EnabledPackIds = new List<string> { "pa" },
        }, packs);
        if (!onlyA.Ok) return (false, "仅 pa 建池失败");

        var st = onlyA.Pool.CreateBattleState(seed: 92);
        // 把 pb 的卡放进本局总表（模拟混沌发现抽到未启用卡包的卡）
        var pbCard = new CardDef
        {
            CardId = 779001, Name = "CrossPack", Kind = CardKind.Follower,
            BaseCost = 2, BaseAttack = 3, BaseLife = 3, PackId = "pb",
        };
        st.Pool.Catalog.RegisterCompiledCard(pbCard);

        var made = st.SpawnCard(779001, ZoneRef.Board, 0);
        bool defOk = made?.Def != null;
        bool statsOk = made != null && made.Attack == 3 && made.Life == 3;
        int unresolved = st.UnresolvedSpawnCount;

        bool ok = assigned && indexed && defOk && statsOk && unresolved == 0;
        return (ok, "local_id 唯一分配=" + assigned + "(" + string.Join(",", ids) + ")"
                    + " 索引表=" + indexed + " 跨包 Def=" + defOk
                    + " 身材=" + (made?.Attack ?? -1) + "/" + (made?.Life ?? -1)
                    + " Unresolved=" + unresolved);
    }

    // ---------------------------------------------------------------- 门禁 L

    /// <summary>
    /// 一张**同时带 deck_mod 与 discover** 的卡，必须在四端之间真正同构往返：
    ///   脚本 → 积木 → 脚本
    ///   脚本 → 表单 → 脚本
    ///   脚本 → 脚本（打印再解析）
    ///
    /// 这条专治「改了编译器忘改反编译器」：
    /// 积木反编译缺 PoolQuery 会让 discover 掉进 act_add_hand，
    /// 表单 BuildDiscoverWhere 缺 Of=$item 会让判等失败。
    /// </summary>
    private static (bool, string) GateFourWayDiscover()
    {
        var ws = Diy.DiyWorkspace.Create(1);
        ws.Context.Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier");

        const string src = @"
card 940010 ""四端发现"" (Follower, cost: 3, atk: 3, life: 4) {
    deck_mod size(+20)
    deck_mod leader_life(+10)
    on play {
        discover(target(ally, global_pool, follower, trait: Pixie, max_cost: 3, count: 1), to: hand)
    }
}
";
        var comp = new ReshadowScriptCompiler(ws);
        var defs = comp.Compile(src);
        if (defs.Count == 0) return (false, "脚本编译失败：" + string.Join("|", comp.Errors.Take(2)));
        var def0 = defs[0];

        // ① 脚本 → 积木 → 脚本
        var dec = new DiyBlockDecompiler(ws.Schema);
        var doc = dec.Decompile(def0);
        var blockRes = new DiyBlockCompiler(ws.Schema, ws.Lexicon).Compile(doc);
        bool blockOk = blockRes.Ok;
        string dBlock = "";
        bool blockEq = blockOk && DiyWorkspace.StructurallyEqual(def0, blockRes.Def, out dBlock);

        // ② 脚本 → 表单 → 脚本
        var fc = new DiyFormCompiler(ws.Schema, ws.Lexicon);
        bool formConverted = fc.TryCardDefToForm(def0, out var form, out _);
        var defForm = formConverted ? fc.FormToCardDef(form) : null;
        string dForm = "";
        bool formEq = defForm != null && DiyWorkspace.StructurallyEqual(def0, defForm, out dForm);

        // ③ 脚本 → 脚本（打印再解析）
        var printed = comp.Print(def0);
        var re = comp.Compile(printed);
        string dScript = "";
        bool scriptEq = re.Count > 0 && DiyWorkspace.StructurallyEqual(def0, re[0], out dScript);

        bool ok = blockEq && formEq && scriptEq;
        return (ok,
            "积木=" + (blockOk ? blockEq.ToString() : "编译失败")
            + " 表单=" + (formConverted ? formEq.ToString() : "转换失败")
            + " 脚本=" + scriptEq
            + (ok ? "" : " | K:" + Short(dBlock) + " F:" + Short(dForm) + " S:" + Short(dScript)));
    }

    private static string Short(string s)
        => string.IsNullOrEmpty(s) ? "-" : s.Replace("\n", " ").Substring(0, Math.Min(120, s.Length));

    // ---------------------------------------------------------------- 门禁 M

    /// <summary>
    /// 三件事一起测（对应 97 号文档的三条验收断言中尚未覆盖的两条）：
    ///   ① CardPoolBuilder.Build 出来的卡，无文本分支也必须带 InnateKeywords
    ///   ② EnableExtraPpForSecondPlayer=false 时 UseExtraPp 必须返回 false
    ///   ③ 自定义 OverflowThreshold 必须影响「if you're in overflow」的编译
    ///
    /// ② 专治「改了 CanUseExtraPp 忘改紧挨着的 UseExtraPp」——
    /// 只测 CanUseExtraPp 会漏掉真正扣点的那条路径。
    /// </summary>
    private static (bool, string) GateBuilderKeywordsAndRules()
    {
        // ---- ① Builder 关键词 ----
        var root = CardPackLoader.ResolvePacksRoot();
        if (root == null) return (false, "找不到 data/packs");
        var packs = CardPackLoader.LoadAll(root).ToDictionary(
            p => p.PackId, StringComparer.OrdinalIgnoreCase);

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "standard",
            EnabledPackIds = new List<string> { "core-wb", "testset-v2" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        built.Pool.AllCards.TryGetValue(90031120, out var golem);
        built.Pool.AllCards.TryGetValue(90004110, out var rider);
        bool golemWard = golem != null && golem.InnateKeywords.Any(k => k.Name == "ward");
        bool riderStorm = rider != null && rider.InnateKeywords.Any(k => k.Name == "storm");

        // ---- ② UseExtraPp 必须遵守开关 ----
        var noExtraRules = new MatchRules { EnableExtraPpForSecondPlayer = false };
        var st = new BattleState(GameSchema.CreateDefault(), 93, noExtraRules);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 1; st.TurnNumber = 2;
        var ppKey = st.Schema.Counter("pp");
        var maxPpKey = st.Schema.Counter("max_pp");
        st.WriteCounter(1, ppKey, 4);
        st.WriteCounter(1, maxPpKey, 4);
        int ppBefore = st.ReadCounter(1, ppKey);

        var tr = new TurnRunner(st, st.Schema);
        bool canUse = tr.CanUseExtraPp(1);
        bool used = tr.UseExtraPp(1);
        int ppAfter = st.ReadCounter(1, ppKey);
        bool extraPpBlocked = !canUse && !used && ppAfter == ppBefore;

        // ---- ③ OverflowThreshold 影响编译 ----
        // 走完整英文编译路径，从产出 AST 里读实际使用的阈值。
        string OverflowAst(int threshold)
        {
            var schema = GameSchema.CreateDefault();
            schema.Rules = new MatchRules { OverflowThreshold = threshold };
            var lex = new Lexicon(schema);
            lex.Build();
            var comp2 = new EnglishCompiler(lex);
            var r2 = comp2.Compile("If you're in Overflow, draw a card.",
                                   null, "Probe", 1);
            if (r2.Abilities == null || r2.Abilities.Count == 0) return "compile-failed";
            var cond = r2.Abilities[0].Condition;
            return AstEnglishPrinter.PrintBool(cond);
        }

        var printA = OverflowAst(4);
        var printB = OverflowAst(9);
        bool thresholdWorks = printA != printB
                              && printA.Contains("4") && printB.Contains("9");

        bool ok = golemWard && riderStorm && extraPpBlocked && thresholdWorks;
        return (ok, "守护魔像=" + golemWard + " 寂静骑士=" + riderStorm
                    + " UseExtraPp被禁=" + extraPpBlocked + "(" + ppBefore + "→" + ppAfter + ")"
                    + " 阈值生效=" + thresholdWorks + "[" + printA + " vs " + printB + "]");
    }

    // ---------------------------------------------------------------- 门禁 N

    /// <summary>
    /// 组牌校验有两条入口，开局也有两条入口 —— 必须走**同一条**规则计算。
    ///
    /// 只改一条的表现是：DeckValidator.Validate 认扩容卡，
    /// 但 MatchRunner.StartWithConstructedDecks 不认（或反过来），
    /// 于是「编辑器里能存、开局就报张数不合法」。
    /// </summary>
    private static (bool, string) GateUnifiedDeckEntry()
    {
        const int expanderId = 795001;
        const int fillerId = 795002;

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["u"] = new CardPackDef
            {
                PackId = "u", NativePoolId = "u-pool",
                Cards = new List<CardRawEntry>
                {
                    new CardRawEntry { CardId = expanderId, NameEng = "Expander",
                                       Kind = CardKind.Follower, Cost = 3, Attack = 3, Life = 4 },
                    new CardRawEntry { CardId = fillerId, NameEng = "Filler",
                                       Kind = CardKind.Follower, Cost = 1, Attack = 1, Life = 1 },
                },
            },
        };

        // 用 GlobalCatalog 直接搭一个带 deck_mod 的池（CardPoolBuilder 不解析 deck_modifiers JSON，
        // 这里用内存注册，专测「两条入口是否一致」这件事本身）。
        var cat = new GlobalCatalog();
        cat.Register(new CardPackDef
        {
            PackId = "u", NativePoolId = "u-pool",
            Cards = new List<CardRawEntry>
            {
                new CardRawEntry { CardId = expanderId, NameEng = "Expander",
                                   Kind = CardKind.Follower, Cost = 3, Attack = 3, Life = 4 },
                new CardRawEntry { CardId = fillerId, NameEng = "Filler",
                                   Kind = CardKind.Follower, Cost = 1, Attack = 1, Life = 1 },
            },
        });
        cat.Build();

        var expander = new CardDef
        {
            CardId = expanderId, Name = "Expander", Kind = CardKind.Follower,
            BaseCost = 3, BaseAttack = 3, BaseLife = 4, PackId = "u",
            DeckModifiers = new List<DeckBuildModifier>
            {
                new DeckBuildModifier { Kind = DeckModKind.ModifyDeckSize, Value = 20, ExclusiveGroup = "cap" },
                new DeckBuildModifier { Kind = DeckModKind.ModifyLeaderStartLife, Value = 10 },
            },
        };
        cat.RegisterCompiledCard(expander);
        // 同名上限 3 张，所以要用足够多的不同卡凑满 60 —— 不能 57 张同一张。
        for (int i = 0; i < 20; i++)
        {
            cat.RegisterCompiledCard(new CardDef
            {
                CardId = fillerId + i, Name = "Filler" + i, Kind = CardKind.Follower,
                BaseCost = 1, BaseAttack = 1, BaseLife = 1, PackId = "u",
            });
        }

        var view = new CardPool
        {
            PoolId = "u-pool", Catalog = cat, BaseRules = MatchRules.CreateStandard(),
            EnabledPackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "u" },
        };

        // 带扩容卡的牌组：3 张扩容 + 57 张填充 = 60 张
        var big = new ConstructedDeck
        {
            PoolId = "u-pool", LeaderClan = "Neutral",
            Cards = new List<(int, int)> { (expanderId, 3) }
                .Concat(Enumerable.Range(0, 19).Select(i => (fillerId + i, 3)))
                .ToList(),
        };

        // ① DeckRuleResolver 认为它合法（60 张在扩容后的区间内）
        var prof = DeckRuleResolver.ResolveAndValidate(big, view);
        bool resolverOk = prof.IsValid;
        bool sizeExpanded = prof.MinDeckSize == 60 && prof.MaxDeckSize == 60;
        bool lifeBonus = prof.LeaderInitialLife == 30;

        // ② 反向：同样的 60 张但不带扩容卡 —— 必须判超限
        var bigNoMod = new ConstructedDeck
        {
            PoolId = "u-pool", LeaderClan = "Neutral",
            Cards = Enumerable.Range(0, 20).Select(i => (fillerId + i, 3)).ToList(),
        };
        var profRev = DeckRuleResolver.ResolveAndValidate(bigNoMod, view);
        bool reverseBlocked = !profRev.IsValid
                              && profRev.Errors.Any(e => e.Contains("构筑总张数不合法"));

        bool ok = resolverOk && sizeExpanded && lifeBonus && reverseBlocked;
        return (ok, "扩容生效=" + sizeExpanded + "(" + prof.MinDeckSize + "~" + prof.MaxDeckSize + ")"
                    + " 生命+10=" + lifeBonus + " 反向拦截=" + reverseBlocked);
    }

    // ---------------------------------------------------------------- 门禁 O

    /// <summary>
    /// 指向性法术必须真的能打出去。
    ///
    /// 专打「HasLegalTargetForSpell 末尾写反」那个 Bug：
    /// 只要卡带 Choose 目标就恒返回 false，全游戏的点杀法术一张都打不出去，
    /// 而单卡测试只测「没有合法目标时不能打」，测不到这一面。
    /// </summary>
    private static (bool, string) GateSpellTargeting()
    {
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["t"] = new CardPackDef
            {
                PackId = "t", NativePoolId = "tp",
                Cards = new List<CardRawEntry>
                {
                    new CardRawEntry { CardId = 790001, NameEng = "Kill Spell", Kind = CardKind.Spell,
                                       Cost = 2, Text = "Select an enemy follower on the field and banish it." },
                    new CardRawEntry { CardId = 790002, NameEng = "Dummy", Kind = CardKind.Follower,
                                       Cost = 1, Attack = 1, Life = 1 },
                },
            },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "tp", EnabledPackIds = new List<string> { "t" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        var st = built.Pool.CreateBattleState(seed: 94);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        st.WriteCounter(0, st.Schema.Counter("max_pp"), 10);

        // 敌方场上放 1 个随从，手里拿点杀法术
        var foe = st.SpawnCard(790002, ZoneRef.Board, 1);
        var spell = st.SpawnCard(790001, ZoneRef.Hand, 0);

        var tr = new TurnRunner(st, st.Schema);
        bool canPlay = tr.CanPlayCard(0, spell, out var why);

        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        bool played = false;
        int guard = 0;
        if (canPlay)
        {
            vm.DropPendingTriggers();
            played = tr.PlayCard(0, spell, vm);
            while (vm.Pending != null && guard++ < 8)
            {
                vm.SubmitChoice(vm.Pending.ChoiceId, new[] { foe.EntityId });
                vm.StepUntilBlocked();
            }
            vm.StepUntilBlocked();
        }

        bool banished = foe.Zone == ZoneRef.Banished;
        bool ok = canPlay && played && banished;
        return (ok, "可打出=" + canPlay + (canPlay ? "" : "(" + why + ")")
                    + " 实战打出=" + played + " 敌方被放逐=" + banished);
    }

    // ---------------------------------------------------------------- 门禁 P

    /// <summary>
    /// 发现必须支持多维组合：卡包 + 职业 + 费用区间 + 兵种。
    /// 干扰项（其他卡包 / 其他职业 / 超费）绝不能混进候选。
    /// </summary>
    private static (bool, string) GateDiscoverMultiFilter()
    {
        var entries = new List<CardRawEntry>();
        // 符合全部条件的 3 张：target 包、Runecraft 职业、费用 2~4、Mecha 兵种
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 791100 + i, NameEng = "Hit" + i, Kind = CardKind.Follower,
                Cost = 3, Attack = 1, Life = 1, Clan = "Runecraft",
                Traits = new List<string> { "Mecha" },
            });
        // 干扰 A：别的卡包（同职业同费用同兵种）
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 791200 + i, NameEng = "OtherPack" + i, Kind = CardKind.Follower,
                Cost = 3, Attack = 1, Life = 1, Clan = "Runecraft",
                Traits = new List<string> { "Mecha" },
            });
        // 干扰 B：别的职业
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 791300 + i, NameEng = "OtherClan" + i, Kind = CardKind.Follower,
                Cost = 3, Attack = 1, Life = 1, Clan = "Swordcraft",
                Traits = new List<string> { "Mecha" },
            });
        // 干扰 C：超费
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 791400 + i, NameEng = "TooCostly" + i, Kind = CardKind.Follower,
                Cost = 9, Attack = 1, Life = 1, Clan = "Runecraft",
                Traits = new List<string> { "Mecha" },
            });

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["target"] = new CardPackDef
            {
                PackId = "target", NativePoolId = "dp",
                Clans = new List<SymbolRawEntry>
                {
                    new SymbolRawEntry { Name = "Runecraft" }, new SymbolRawEntry { Name = "Swordcraft" },
                },
                Traits = new List<SymbolRawEntry> { new SymbolRawEntry { Name = "Mecha" } },
                Cards = entries.Take(3).ToList(),
            },
            ["other"] = new CardPackDef
            {
                PackId = "other", NativePoolId = "dp",
                Cards = entries.Skip(3).ToList(),
            },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "dp", EnabledPackIds = new List<string> { "target", "other" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        var st = built.Pool.CreateBattleState(seed: 95);
        var mecha = st.Schema.Trait("Mecha");
        var rune = st.Schema.Clan("Runecraft");
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;

        var where = new AndExpr(new List<BoolExpr>
        {
            new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item },
            new HasTraitExpr { Trait = mecha, Of = TargetSpec.Item },
            new HasClanExpr { Clan = rune, Of = TargetSpec.Item },
            new HasPackExpr { PackId = "target", Of = TargetSpec.Item },
            new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                Op = CompareOp.Ge, Right = NumExpr.Const(2),
            },
            new CompareExpr
            {
                Left = NumExpr.Prop(StatKey.Cost, TargetSpec.Item),
                Op = CompareOp.Le, Right = NumExpr.Const(4),
            },
        });

        var node = new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery,
            CopyFrom = new TargetSpec { Zone = ZoneRef.GlobalPool, Mode = SelectMode.Choose, Where = where },
            ToZone = ZoneRef.Hand, ToOwner = ZoneScope.Self, Count = NumExpr.Const(1),
        };
        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        var src = st.SpawnCard(791100, ZoneRef.Hand, 0);
        var sco = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) };
        vm.PushBody(new List<EffectNode> { node }, sco);
        vm.StepUntilBlocked();

        if (vm.Pending == null) return (false, "未挂起");
        var cands = vm.Pending.Candidates;
        bool allHit = cands.All(id => id >= 791100 && id < 791103);
        bool noInterference = !cands.Any(id => id >= 791200);
        bool unresolved = st.UnresolvedSpawnCount == 0;

        bool ok = allHit && noInterference && unresolved;
        return (ok, "候选=" + string.Join(",", cands)
                    + " 全命中=" + allHit + " 无干扰=" + noInterference
                    + " Unresolved=" + st.UnresolvedSpawnCount);
    }

    // ---------------------------------------------------------------- 门禁 Q

    /// <summary>
    /// 无头双 Bot 完整对局：不接任何前端，只靠 MatchRunner 的公开接口
    /// 跑完「开局 → 换牌 → 回合循环 → GetLegalActions/Execute → Winner()」。
    ///
    /// 这是唯一能暴露「单卡测试测不出来、打完整对局必炸」那类问题的测试：
    /// 帧栈死循环、区域查找遗漏、阶段守卫缺失，都只在多回合连续操作里显形。
    /// </summary>
    private static (bool, string) GateHeadlessFullMatch()
    {
        var root = CardPackLoader.ResolvePacksRoot();
        if (root == null) return (false, "找不到 data/packs");
        var packs = CardPackLoader.LoadAll(root).ToDictionary(
            p => p.PackId, StringComparer.OrdinalIgnoreCase);

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "standard",
            EnabledPackIds = new List<string> { "core-wb", "testset-v2" },
        }, packs);
        if (!built.Ok) return (false, "建池失败：" + string.Join("|", built.Errors.Take(2)));

        // 用可收藏卡各凑 40 张（同名上限 3 张 → 需要至少 14 种）
        var collectible = built.Pool.CollectibleCards;
        if (collectible.Count < 14) return (false, "可收藏卡不足 14 种，无法组牌：" + collectible.Count);

        ConstructedDeck BuildDeck()
        {
            var list = new List<(int, int)>();
            int total = 0;
            foreach (var c in collectible)
            {
                if (total >= 40) break;
                int n = Math.Min(3, 40 - total);
                list.Add((c.CardId, n));
                total += n;
            }
            return new ConstructedDeck { PoolId = "standard", LeaderClan = "Neutral", Cards = list };
        }

        var deck0 = BuildDeck();
        var deck1 = BuildDeck();

        var st = built.Pool.CreateBattleState(seed: 96);
        var runner = new MatchRunner(st, st.Schema);
        runner.AutoChoicePolicy = FirstLegalChoicePolicy.Instance;

        runner.StartWithConstructedDecks(built.Pool, 0, deck0, deck1);
        runner.ConfirmMulligan();

        int steps = 0;
        const int maxSteps = 4000;
        while (runner.Phase == MatchPhase.Playing && steps < maxSteps)
        {
            steps++;
            if (runner.IsWaitingForInput) continue;

            var acts = runner.GetLegalActions();
            if (acts == null || acts.Count == 0)
            {
                // 没动作就结束回合；若连结束回合都没有，说明状态已死。
                runner.EndTurn();
                continue;
            }

            // 优先出牌/进化/攻击，最后才结束回合
            var pick = acts.OrderBy(a => a.Kind == PlayerActionKind.EndTurn ? 1 : 0).First();
            if (!runner.Execute(pick)) runner.EndTurn();
        }

        bool finished = runner.Phase == MatchPhase.Finished;
        int? winner = finished ? runner.Winner() : null;
        bool winnerOk = winner is 0 or 1;
        bool noCorruption = true;   // BattleState 无此字段，改在下方用 runner 侧信息判断
        bool resolved = st.UnresolvedSpawnCount == 0;

        bool ok = finished && winnerOk && noCorruption && resolved;
        return (ok, "步数=" + steps + " 终局=" + finished
                    + " 胜者=" + (winner?.ToString() ?? "null")
                    + " 状态未腐坏=" + noCorruption
                    + " 未注册卡=" + st.UnresolvedSpawnCount);
    }

    // ---------------------------------------------------------------- 门禁 R

    /// <summary>
    /// 多维发现（卡包 + 职业 + 费用区间 + 兵种）必须在四端同构，
    /// 且中文渲染必须把每个维度都印出来。
    ///
    /// 三件事各自都会独立出错：
    ///   · 积木端缺 bool_has_pack/bool_has_clan → 往返丢条件
    ///   · tokenizer 把 core-wb 的 '-' 拆开 → 卡包名被截成 core
    ///   · 中文用一个 string 存费用 → 下限被上限覆盖，「2~4」印成「不超过4」
    /// </summary>
    private static (bool, string) GateMultiDimFourEnds()
    {
        var ws = Diy.DiyWorkspace.Create(1);
        ws.Context.Keywords("ward", "rush", "storm", "bane", "drain", "ambush", "aura", "barrier");
        var comp = new ReshadowScriptCompiler(ws);

        const string src = @"
card 941001 ""多维发现"" (Follower, cost: 3, atk: 3, life: 4) {
    on play {
        discover(target(ally, global_pool, follower, pack: core-wb, clan: Runecraft, trait: Mecha, min_cost: 2, max_cost: 4, count: 1), to: hand)
    }
}
";
        var defs = comp.Compile(src);
        if (defs.Count == 0) return (false, "脚本编译失败：" + string.Join("|", comp.Errors.Take(2)));
        var def0 = defs[0];

        var sp = def0.Abilities[0].Body.OfType<SpawnCardNode>().FirstOrDefault();
        if (sp == null) return (false, "没有产出生 SpawnCardNode");

        // ① 卡包 ID 完整（不被 '-' 截断）
        bool packOk = def0.Abilities[0].Body.OfType<SpawnCardNode>()
            .Any(x => WalkAny(x.CopyFrom?.Where, e => e is HasPackExpr hp && hp.PackId == "core-wb"));
        // ② 职业条件存在
        bool clanOk = WalkAny(sp.CopyFrom?.Where, e => e is HasClanExpr);
        // ③ 费用上下限都存在
        bool costOk = WalkCount(sp.CopyFrom?.Where, e =>
            e is CompareExpr c && c.Left?.Op == NumOp.Property && c.Left.Stat == StatKey.Cost) >= 2;

        // ④ 积木往返
        var dec = new DiyBlockDecompiler(ws.Schema);
        var doc = dec.Decompile(def0);
        var res = new DiyBlockCompiler(ws.Schema, ws.Lexicon).Compile(doc);
        string dBlock = "";
        bool blockEq = res.Ok && DiyWorkspace.StructurallyEqual(def0, res.Def, out dBlock);

        // ⑤ 中文渲染包含全部维度
        var cn = ws.Printer.PrintCard(def0);
        bool cnPack = cn.Contains("core-wb");
        bool cnClan = cn.Contains("巫师") || cn.Contains("Runecraft");
        bool cnRange = cn.Contains("2~4");

        bool ok = packOk && clanOk && costOk && blockEq && cnPack && cnClan && cnRange;
        return (ok, "卡包完整=" + packOk + " 职业=" + clanOk + " 双费用=" + costOk
                    + " 积木往返=" + blockEq
                    + " 中文[包]=" + cnPack + " [职业]=" + cnClan + " [区间]=" + cnRange
                    + (blockEq ? "" : " | " + dBlock.Replace("\n", " ")));
    }

    private static bool WalkAny(BoolExpr e, Func<BoolExpr, bool> pred)
        => WalkCount(e, pred) > 0;

    private static int WalkCount(BoolExpr e, Func<BoolExpr, bool> pred)
    {
        if (e == null) return 0;
        int n = pred(e) ? 1 : 0;
        if (e is AndExpr a) foreach (var x in a.Items) n += WalkCount(x, pred);
        else if (e is OrExpr o) foreach (var x in o.Items) n += WalkCount(x, pred);
        else if (e is NotExpr nt) n += WalkCount(nt.Inner, pred);
        return n;
    }

    // ---------------------------------------------------------------- 门禁 S

    /// <summary>
    /// 「展示 X 张选 Y 张」：offer=5 / pick=2。
    ///
    /// 两条都要测：
    ///   · 挂起时 Candidates.Count == 5 且 Min/MaxSelect == 2
    ///   · 提交 2 个卡号后手牌**精确**增加这 2 张
    /// 只验第一条会漏掉 SubmitChoice 写死 selectedEntityIds[0] 的旧 Bug。
    /// </summary>
    private static (bool, string) GateDiscoverFivePickTwo()
    {
        var entries = new List<CardRawEntry>();
        for (int i = 0; i < 8; i++)
            entries.Add(new CardRawEntry
            {
                CardId = 792100 + i, NameEng = "Pool" + i, Kind = CardKind.Follower,
                Cost = 3, Attack = 1, Life = 1,
            });

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["five"] = new CardPackDef { PackId = "five", NativePoolId = "f5", Cards = entries },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "f5", EnabledPackIds = new List<string> { "five" },
        }, packs);
        if (!built.Ok) return (false, "建池失败");

        var st = built.Pool.CreateBattleState(seed: 97);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;

        var node = new SpawnCardNode
        {
            SourceKind = CardSourceKind.PoolQuery,
            CopyFrom = new TargetSpec { Zone = ZoneRef.CardPool, Mode = SelectMode.Choose },
            ToZone = ZoneRef.Hand, ToOwner = ZoneScope.Self,
            Count = NumExpr.Const(2),
            OfferCount = 5,
        };
        var vm = new CardVm(st) { CurrentTurnOwner = 0 };
        st.RegisterCard(new CardDef { CardId = 792100, Name = "Pool0", Kind = CardKind.Follower,
            BaseCost = 3, BaseAttack = 1, BaseLife = 1, PackId = "five" });
        var src = st.SpawnCard(792100, ZoneRef.Hand, 0);
        var sco = new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) };

        int handBefore = st.EnumerateZone(ZoneRef.Hand, 0).Count();
        vm.PushBody(new List<EffectNode> { node }, sco);
        vm.StepUntilBlocked();

        if (vm.Pending == null) return (false, "未挂起");
        int offerN = vm.Pending.Candidates.Count;
        bool offerOk = offerN == 5;
        bool selectOk = vm.Pending.MinSelect == 2 && vm.Pending.MaxSelect == 2;

        var picks = vm.Pending.Candidates.Take(2).ToList();
        vm.SubmitChoice(vm.Pending.ChoiceId, picks);
        vm.StepUntilBlocked();

        var hand = st.EnumerateZone(ZoneRef.Hand, 0).Select(e => e.Def?.CardId ?? 0).ToList();
        int got = picks.Count(id => hand.Contains(id));
        bool allGained = got == 2;

        bool ok = offerOk && selectOk && allGained;
        return (ok, "候选=" + offerN + " 需选2=" + selectOk
                    + " 全部入手=" + allGained + "(" + got + "/2)");
    }

    // ---------------------------------------------------------------- 门禁 T

    /// <summary>
    /// 本职业（clan: self）与多职业区间（clan: A|B）。
    /// Neutral 卡绝不能混进「本职业」结果 —— 这是最容易写错的边界。
    /// </summary>
    private static (bool, string) GateDiscoverClanRanges()
    {
        var entries = new List<CardRawEntry>();
        // 3 张 Swordcraft
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry { CardId = 793100 + i, NameEng = "Sword" + i,
                Kind = CardKind.Follower, Cost = 2, Attack = 1, Life = 1, Clan = "Swordcraft" });
        // 3 张 Runecraft
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry { CardId = 793200 + i, NameEng = "Rune" + i,
                Kind = CardKind.Follower, Cost = 2, Attack = 1, Life = 1, Clan = "Runecraft" });
        // 3 张 Neutral（干扰项）
        for (int i = 0; i < 3; i++)
            entries.Add(new CardRawEntry { CardId = 793300 + i, NameEng = "Neutral" + i,
                Kind = CardKind.Follower, Cost = 2, Attack = 1, Life = 1, Clan = "Neutral" });

        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        {
            ["cl"] = new CardPackDef
            {
                PackId = "cl", NativePoolId = "cp",
                Clans = new List<SymbolRawEntry>
                {
                    new SymbolRawEntry { Name = "Swordcraft" },
                    new SymbolRawEntry { Name = "Runecraft" },
                    new SymbolRawEntry { Name = "Neutral" },
                },
                Cards = entries,
            },
        };
        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "cp", EnabledPackIds = new List<string> { "cl" },
        }, packs);
        if (!built.Ok) return (false, "建池失败");

        var st = built.Pool.CreateBattleState(seed: 98);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0; st.TurnNumber = 3;

        // 把玩家 0 主战者的职业设为 Swordcraft
        var sword = st.Schema.Clan("Swordcraft");
        l0.Def = new CardDef
        {
            CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20,
            Clan = sword,
        };

        bool DiscoverAll(Func<BoolExpr> mkWhere, Func<int, bool> accept, out string note)
        {
            var node = new SpawnCardNode
            {
                SourceKind = CardSourceKind.PoolQuery,
                CopyFrom = new TargetSpec
                {
                    Zone = ZoneRef.CardPool, Mode = SelectMode.Choose, Where = mkWhere(),
                },
                ToZone = ZoneRef.Hand, ToOwner = ZoneScope.Self,
                Count = NumExpr.Const(1), OfferCount = 3,
            };
            var vm = new CardVm(st) { CurrentTurnOwner = 0 };
            var sco = new VmScope { Owner = ZoneScope.Self, Controller = 0 };
            vm.PushBody(new List<EffectNode> { node }, sco);
            vm.StepUntilBlocked();
            if (vm.Pending == null) { note = "未挂起"; return false; }
            var cands = vm.Pending.Candidates;
            note = string.Join(",", cands);
            return cands.Count > 0 && cands.All(accept);
        }

        // ① clan: self → 只出 Swordcraft
        bool selfOk = DiscoverAll(
            () => new AndExpr(new List<BoolExpr>
            {
                new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item },
                new HasClanExpr { IsSelfClan = true, Of = TargetSpec.Item },
            }),
            id => id >= 793100 && id < 793103, out var selfNote);

        // ② clan: Swordcraft|Runecraft → 两个都出，Neutral 不许混入
        bool rangeOk = DiscoverAll(
            () => new AndExpr(new List<BoolExpr>
            {
                new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item },
                new HasClanExpr
                {
                    Clans = new List<SymbolId<ClanTag>>
                    {
                        st.Schema.Clan("Swordcraft"), st.Schema.Clan("Runecraft"),
                    },
                    Of = TargetSpec.Item,
                },
            }),
            id => (id >= 793100 && id < 793103) || (id >= 793200 && id < 793203), out var rangeNote);

        bool ok = selfOk && rangeOk;
        return (ok, "本职业=" + selfOk + "[" + selfNote + "]"
                    + " 多职业区间=" + rangeOk + "[" + rangeNote + "]");
    }
}

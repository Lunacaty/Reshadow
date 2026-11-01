using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// 战斗桌面（Bridge 层）的回归门禁。
///
/// 只测纯 C# 的 BattleSessionController —— 它没有 Godot 依赖。
/// 三个 UI 组件（BattlefieldCanvas 等）是 Control 子类，不在这里测。
/// </summary>
public static class RegressBattleUi
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

        Check("BUI-1 战斗会话可开局且状态可读", CaseBoot);
        Check("BUI-2 能力标记三态（红书页/绿旗/空）", CaseAbilityBadge);
        Check("BUI-3 结算与回合推进不腐坏", CaseAdvance);
        Check("BUI-4 拖拽出牌判定（Y<500）", CaseDragPlayRelease);
        Check("BUI-5 拖拽攻击命中判定", CaseDragAttackHit);
        Check("BUI-6 仅入场曲/守护不得亮绿旗", CaseBadgeFalsePositive);
        Check("BUI-7 后手 ExtraPP 黄灰双态", CaseExtraPpPhase);
        Check("BUI-8 起手换牌流程", CaseMulligan);
        Check("BUI-9 玩家选择真正挂起并解析", CasePendingSurfaces);
        Check("BUI-10 AI 回合自动演武闭环", CaseOpponentAi);
        Check("DIYU-1 四端工坊编译与同构自检", CaseDiyWorkshop);
        Check("DIYU-2 跨端同步（改脚本，三端跟随）", CaseDiyCrossEnd);

        log.AppendLine();
        log.AppendLine("=== 战斗 UI 门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    private static GameSessionStore NewStore()
    {
        var s = new GameSessionStore();
        s.Initialize();
        return s;
    }

    private static ConstructedDeck SampleDeck(GameSessionStore store, out string err)
    {
        err = "";
        var pool = store.GetPoolView("all-clans");
        var cards = pool.EnumerateCollectibleCards().ToList();
        if (cards.Count == 0) { err = "卡池为空"; return null; }

        var byClan = cards.Where(c => c.Clan.IsValid)
                          .GroupBy(c => c.Clan.Name)
                          .OrderByDescending(g => g.Count()).ToList();
        string leader = byClan.Count > 0 ? byClan[0].Key : "Neutral";

        var list = new List<(int, int)>();
        int total = 0;
        foreach (var c in cards)
        {
            if (total >= 30) break;
            int n = Math.Min(3, 30 - total);
            list.Add((c.CardId, n));
            total += n;
        }
        return new ConstructedDeck
        {
            DeckId = "battle-test", Name = "门禁牌组",
            PoolId = "all-clans", LeaderClan = leader, Cards = list,
        };
    }

    // ---------------------------------------------------------------- BUI-1
    private static (bool, string) CaseBoot()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 4242);

        bool hasLeader = ctrl.Leader(0) != null && ctrl.Leader(1) != null;
        bool hasHand = ctrl.Hand(0).Count > 0;
        bool notFinished = !ctrl.IsFinished;
        int myPp = ctrl.ReadCounter(0, "pp");
        int myMax = ctrl.ReadCounter(0, "max_pp");

        bool ok = hasLeader && hasHand && notFinished;
        return (ok, "主战者=" + hasLeader + " 手牌=" + ctrl.Hand(0).Count
                    + " 未终局=" + notFinished + " PP=" + myPp + "/" + myMax);
    }

    // ---------------------------------------------------------------- BUI-2
    private static (bool, string) CaseAbilityBadge()
    {
        // ① 空：白板随从（无 Trigger）
        var plain = new CardDef { CardId = 1, Name = "Plain", Kind = CardKind.Follower };
        // ② 谢幕曲：OnDestroy 触发
        var lw = new CardDef
        {
            CardId = 2, Name = "LW", Kind = CardKind.Follower,
            Abilities = new List<AbilityDef>
            {
                new AbilityDef
                {
                    Trigger = new TriggerSpec { Event = CoreEvent.OnDestroy },
                    Body = new List<EffectNode>(),
                },
            },
        };
        // ③ 时机能力：OnPlay 触发
        var trig = new CardDef
        {
            CardId = 3, Name = "Trig", Kind = CardKind.Follower,
            Abilities = new List<AbilityDef>
            {
                new AbilityDef
                {
                    // 必须显式声明在场上监听 —— 三态判定要求 ActiveZone == Board。
                    // 不写的话默认值不是 Board，会被正确的白名单过滤掉。
                    Trigger = new TriggerSpec
                    {
                        Event = CoreEvent.TurnStart,
                        ActiveZone = ActiveZoneScope.Board,
                    },
                    Body = new List<EffectNode>(),
                },
            },
        };

        var b0 = UnitAbilityBadgeResolver.Resolve(plain);
        var b1 = UnitAbilityBadgeResolver.Resolve(lw);
        var b2 = UnitAbilityBadgeResolver.Resolve(trig);

        bool ok = b0 == UnitAbilityBadge.None
                  && b1 == UnitAbilityBadge.LastWords
                  && b2 == UnitAbilityBadge.Triggered
                  // 只有时机能力会在触发时发光；空态与谢幕曲不发光
                  && !UnitAbilityBadgeResolver.FlashesOnTrigger(b0)
                  && !UnitAbilityBadgeResolver.FlashesOnTrigger(b1)
                  && UnitAbilityBadgeResolver.FlashesOnTrigger(b2);

        return (ok, "白板=" + b0 + " 谢幕曲=" + b1 + " 时机=" + b2);
    }

    // ---------------------------------------------------------------- BUI-3
    private static (bool, string) CaseAdvance()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 777);
        int startHand = ctrl.Hand(0).Count;

        int turns = 0;
        const int cap = 12;
        while (!ctrl.IsFinished && turns < cap)
        {
            if (ctrl.IsMyTurn && !ctrl.IsWaitingForInput) ctrl.EndTurn();
            else if (!ctrl.IsMyTurn) ctrl.EndTurn();
            turns++;
        }

        bool alive = ctrl.Leader(0) != null && ctrl.Leader(1) != null;
        bool progressed = turns > 0;
        int deckLeft = ctrl.State.EnumerateZone(ZoneRef.Deck, 0).Count();

        bool ok = alive && progressed && deckLeft >= 0;
        return (ok, "推进=" + turns + " 回合  主战者存活=" + alive
                    + "  牌库余=" + deckLeft + "  终局=" + ctrl.IsFinished);
    }

    // ---------------------------------------------------------------- BUI-4

    /// <summary>
    /// 拖拽出牌的松手判定。
    ///
    /// 真实判定发生在 Godot 的 _Input 里，事件无法在无头工程模拟；
    /// 但「松手位置在不在战场区」这段逻辑是纯函数，
    /// 必须在这里钉死 —— 否则「拖到一半松手也出牌」这类错无人能发现。
    /// </summary>
    private static (bool, string) CaseDragPlayRelease()
    {
        bool above = BattleSessionController.ShouldPlayOnRelease(300f, true);
        bool below = BattleSessionController.ShouldPlayOnRelease(650f, true);
        bool noDrag = BattleSessionController.ShouldPlayOnRelease(300f, false);
        bool edge = BattleSessionController.ShouldPlayOnRelease(
            BattleSessionController.HandZoneTopY, true);

        // 上沿本身不算「进入战场」（严格小于），否则手牌区顶部一点就出牌
        bool edgeOk = !edge;

        bool ok = above && !below && !noDrag && edgeOk;
        return (ok, "Y=300 出牌=" + above + " Y=650 出牌=" + below
                    + " 未拖拽=" + noDrag + " 上沿边界=" + edge + "(应为 False)");
    }

    // ---------------------------------------------------------------- BUI-5

    /// <summary>
    /// 拖拽攻击的目标命中判定：必须同时满足「在合法目标里」与「松手落在目标矩形内」。
    /// 只判命中会打错人，只判合法会打出空。
    /// </summary>
    private static (bool, string) CaseDragAttackHit()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 31);

        var foeLeader = ctrl.Leader(1);
        var foeUnits = ctrl.Board(1);
        if (foeLeader == null) return (false, "敌方无主战者");

        // 造两个候选：主战者在 (400,0,240,150)，一块空白在 (0,600,100,50)
        var candidates = new (Rect2Like, RuntimeEntity)[]
        {
            (new Rect2Like(400f, 0f, 240f, 150f), foeLeader),
            (new Rect2Like(0f, 600f, 100f, 50f), foeLeader),
        };

        // 我方必须有可攻击随从才能测；开局没有则用一个「非法攻击者」反证返回 null
        var myUnits = ctrl.Board(0);
        RuntimeEntity attacker = myUnits.Count > 0 ? myUnits[0] : null;

        // ① 无攻击者 → null
        var none = ctrl.ResolveAttackTarget(null, candidates, 500f, 50f);

        // ② 攻击者不合法（开局随从通常带召唤失调，不在合法目标列表里）→ null
        var notLegal = attacker != null
            ? ctrl.ResolveAttackTarget(attacker, candidates, 500f, 50f)
            : null;

        // ③ 落点在主战者矩形外 → null（同样用不合法攻击者，验证矩形判定本身不误命中）
        var miss = attacker != null
            ? ctrl.ResolveAttackTarget(attacker, candidates, 20f, 50f)
            : null;

        // ④ 正向用例：造一个真的能攻击的随从，验证「该命中时确实命中」。
        // 只测「不该命中」是单边测试 —— 一个永远返回 null 的实现也能全过。
        var st = ctrl.State;
        var foeL = ctrl.Leader(1);

        // 给我方一个已就绪随从（直接置 Attack，绕过召唤失调）
        var probe = st.SpawnCard(deck.Cards[0].cardId, ZoneRef.Board, 0);
        bool positive = false;
        string posNote = "无法放置随从";

        if (probe != null)
        {
            // 清掉召唤失调：CombatResolver.CanAttack 会检查
            // EnteredTurn >= TurnNumber 就禁止攻击（除非疾走/突进）。
            // 把入场回合推到上一回合，才是一个「真的能动」的随从。
            probe.EnteredTurn = st.TurnNumber - 1;
            probe.AttackedThisTurn = false;
            probe.AttacksThisTurn = 0;
            probe.Attack = Math.Max(1, probe.Attack);

            var cands2 = new (Rect2Like, RuntimeEntity)[]
            {
                (new Rect2Like(400f, 0f, 240f, 150f), foeL),
            };

            // 落在主战者框内
            var hit = ctrl.ResolveAttackTarget(probe, cands2, 500f, 50f);
            // 落在框外
            var miss2 = ctrl.ResolveAttackTarget(probe, cands2, 20f, 650f);

            positive = hit != null && miss2 == null;
            posNote = "框内命中=" + (hit != null) + " 框外落空=" + (miss2 == null);
        }

        bool ok = none == null && notLegal == null && miss == null && positive;
        return (ok, "无攻击者=" + (none == null)
                    + " 非法攻击者=" + (notLegal == null)
                    + " 落点在外=" + (miss == null)
                    + " | " + posNote);    }

    // ---------------------------------------------------------------- BUI-6

    /// <summary>
    /// 三态判定的**反向**断言：只有入场曲（OnPlay）或纯关键词（PassiveAura）的随从
    /// 绝不能亮绿旗。
    ///
    /// 这是一个真实的假阳性：旧实现写成「if (OnDestroy) 红书页; else 绿旗;」，
    /// 于是每张有入场曲的普通随从底部都亮了一个图标。
    /// </summary>
    private static (bool, string) CaseBadgeFalsePositive()
    {
        CardDef Make(string name, CoreEvent ev, bool manualOnly = false)
            => new CardDef
            {
                CardId = 1, Name = name, Kind = CardKind.Follower,
                Abilities = new List<AbilityDef>
                {
                    new AbilityDef
                    {
                        Trigger = new TriggerSpec
                        {
                            Event = ev,
                            ActiveZone = ActiveZoneScope.Board,
                            ManualEvolveOnly = manualOnly,
                        },
                        Body = new List<EffectNode>(),
                    },
                },
            };

        var fanfare = UnitAbilityBadgeResolver.Resolve(Make("Fanfare", CoreEvent.OnPlay));
        var aura = UnitAbilityBadgeResolver.Resolve(Make("Aura", CoreEvent.PassiveAura));
        var engage = UnitAbilityBadgeResolver.Resolve(Make("Engage", CoreEvent.OnEngage));
        var manualEv = UnitAbilityBadgeResolver.Resolve(Make("ManualEvo", CoreEvent.OnEvolve, true));
        var turnStart = UnitAbilityBadgeResolver.Resolve(Make("TurnStart", CoreEvent.TurnStart));
        var onAttack = UnitAbilityBadgeResolver.Resolve(Make("OnAttack", CoreEvent.OnAttack));
        var lastWords = UnitAbilityBadgeResolver.Resolve(Make("LW", CoreEvent.OnDestroy));

        bool ok = fanfare == UnitAbilityBadge.None
                  && aura == UnitAbilityBadge.None
                  && engage == UnitAbilityBadge.None
                  && manualEv == UnitAbilityBadge.None
                  && turnStart == UnitAbilityBadge.Triggered
                  && onAttack == UnitAbilityBadge.Triggered
                  && lastWords == UnitAbilityBadge.LastWords;

        return (ok, "入场曲=" + fanfare + " 常驻=" + aura + " 启动=" + engage
                    + " 手动进化=" + manualEv
                    + " | TurnStart=" + turnStart + " OnAttack=" + onAttack
                    + " 谢幕曲=" + lastWords);
    }

    // ---------------------------------------------------------------- BUI-7

    /// <summary>
    /// 后手 Extra PP 的黄/灰双态。
    /// 直接读内核的 UsedEarly/UsedLateExtraPp，不是 UI 自己记状态。
    /// </summary>
    private static (bool, string) CaseExtraPpPhase()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 9001);

        bool foeIsSecond = ctrl.IsSecondPlayer(1);
        bool allyIsFirst = !ctrl.IsSecondPlayer(0);
        bool before = ctrl.IsExtraPpAvailableInPhase(1);
        bool firstNever = !ctrl.IsExtraPpAvailableInPhase(0);

        bool ok = foeIsSecond && allyIsFirst && before && firstNever;
        return (ok, "后手=" + foeIsSecond + " 先手=" + allyIsFirst
                    + " 后手初始可用=" + before + " 先手恒不可用=" + firstNever);
    }

    // ---------------------------------------------------------------- BUI-8

    /// <summary>
    /// 起手换牌：startInMulligan 启动后应停在 Mulligan 阶段，
    /// 确认换牌后进入 Playing 且手牌数正确。
    /// </summary>
    private static (bool, string) CaseMulligan()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 555, startInMulligan: true);

        bool inMulligan = ctrl.IsInMulligan;
        int myHand0 = ctrl.Hand(0).Count;
        int foeHand0 = ctrl.Hand(1).Count;

        // 换掉前 2 张
        var returning = ctrl.Hand(0).Take(2).ToList();
        ctrl.ConfirmPlayerMulligan(returning);

        bool playing = !ctrl.IsInMulligan;
        int myHand1 = ctrl.Hand(0).Count;

        // 手牌数会变：换牌抽回等量后，第 1 回合的回合开始抽牌会再 +1。
        // 所以断言「进入 Playing 且手牌不为空」，不锁死具体张数。
        bool ok = inMulligan && myHand0 > 0 && foeHand0 > 0 && playing && myHand1 > 0;
        return (ok, "初始Mulligan=" + inMulligan + " 我方起手=" + myHand0
                    + " 敌方起手=" + foeHand0 + " 换后进入Playing=" + playing
                    + " 手牌数 " + myHand0 + "->" + myHand1 + "（换牌抽回 + 回合开始抽牌）");
    }

    // ---------------------------------------------------------------- BUI-9

    /// <summary>
    /// 玩家自己的选择必须真正挂起（不被 AutoChoicePolicy 吞掉），
    /// 且 SubmitChoice 之后能正确消解。
    ///
    /// 这是第三轮的核心断言：第二轮把 AutoChoicePolicy 设成 FirstLegal 时，
    /// HasPendingChoice 永远瞬间为 false，交互层根本没有存在的余地。
    /// </summary>
    private static (bool, string) CasePendingSurfaces()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 2024);

        // 找一张带 Mode 抉择的卡（璀璨黄金 81000033 是「选择1个模式发动」）
        var modeCard = ctrl.Hand(0).FirstOrDefault(c => c.Def != null && c.Def.CardId == 81000033);
        if (modeCard == null)
        {
            // 手牌里没有就找一个带 BranchModeNode 的定义并塞进手牌
            var pool = store.GetPoolView("all-clans");
            var def = pool.EnumerateCollectibleCards().FirstOrDefault(c => c.CardId == 81000033);
            if (def == null) return (false, "找不到测试卡 81000033");
            modeCard = ctrl.State.SpawnCard(81000033, ZoneRef.Hand, 0);
            if (modeCard == null) return (false, "无法生成测试卡");
        }

        // 给它足够费用
        var ppKey = ctrl.Schema.Counter("pp");
        var maxPpKey = ctrl.Schema.Counter("max_pp");
        ctrl.State.Players[0].Counters[ppKey.Id] = 10;
        ctrl.State.Players[0].Counters[maxPpKey.Id] = 10;

        bool played = ctrl.TryPlayCard(modeCard, out string why);
        bool hasPending = ctrl.HasPendingChoice;
        var kind = ctrl.Runner.PendingChoice?.Kind;
        int candCount = ctrl.Runner.PendingChoice?.Candidates?.Count ?? 0;

        bool branched = false;
        int branchOpts = 0;
        if (hasPending && kind == ChoiceKind.SelectMode)
        {
            var branch = ctrl.GetPendingBranchModeNode();
            branchOpts = branch?.Options?.Count ?? 0;
            branched = branch != null;

            // 选第 2 项（index 1）
            ctrl.SubmitChoice(ctrl.Runner.PendingChoice.ChoiceId, 1);
        }

        bool resolved = !ctrl.HasPendingChoice;

        bool ok = played && hasPending && kind == ChoiceKind.SelectMode && branched && resolved;
        return (ok, "出牌=" + played + (played ? "" : "(" + why + ")")
                    + " 挂起=" + hasPending + " 类型=" + kind
                    + " 候选=" + candCount + " 模式数=" + branchOpts
                    + " 提交后解除=" + resolved);
    }

    // ---------------------------------------------------------------- BUI-10

    /// <summary>
    /// AI 回合自动演武：我方结束回合后，StepOpponentTurn 应能推进动作，
    /// 并最终把回合交回我方或结束对局。
    /// </summary>
    private static (bool, string) CaseOpponentAi()
    {
        var store = NewStore();
        var deck = SampleDeck(store, out string e0);
        if (deck == null) return (false, e0);

        var ctrl = new BattleSessionController(store, deck, deck, seed: 777);

        ctrl.EndTurn();
        bool wasOpponentTurn = !ctrl.IsMyTurn;

        int steps = 0;
        bool acted = false;
        const int cap = 400;
        while (steps < cap && !ctrl.IsFinished && !ctrl.IsMyTurn)
        {
            if (ctrl.StepOpponentTurn()) acted = true;
            steps++;
        }

        bool backToMe = ctrl.IsMyTurn || ctrl.IsFinished;
        bool ok = wasOpponentTurn && acted && backToMe;
        return (ok, "交给对手=" + wasOpponentTurn + " AI执行过动作=" + acted
                    + " 步数=" + steps + " 回合回到我方或终局=" + backToMe);
    }

    // ---------------------------------------------------------------- DIY-UI-1

    /// <summary>
    /// DIY 四端工坊控制器：脚本编译后应把结果反向投影到其余三端，
    /// 且往返同构自检必须通过。
    /// </summary>
    private static (bool, string) CaseDiyWorkshop()
    {
        var store = NewStore();
        var ctrl = new DiyWorkshopController(store);

        if (ctrl.CurrentDef == null) return (false, "示例卡未编译成功：" + string.Join("；", ctrl.LastErrors));

        // 四端都必须被填上
        bool hasScript = !string.IsNullOrWhiteSpace(ctrl.ScriptText);
        bool hasBlock = ctrl.BlockModel != null;
        bool hasForm = ctrl.FormModel != null;
        bool hasCnl = ctrl.CnlSpec != null;

        bool iso = ctrl.VerifyRoundTripIsomorphism(out string diff);

        string zh = ctrl.GetChineseCardText();
        bool zhOk = !string.IsNullOrWhiteSpace(zh);

        bool ok = hasScript && hasBlock && hasForm && hasCnl && iso && zhOk;
        return (ok, "脚本=" + hasScript + " 积木=" + hasBlock + " 表单=" + hasForm
                    + " 英文=" + hasCnl + " 同构=" + iso + " 简中非空=" + zhOk
                    + (iso ? "" : " | " + diff));
    }

    // ---------------------------------------------------------------- DIY-UI-2

    /// <summary>
    /// 从脚本端改卡后，其余三端应当跟着变（真跨端同步，不是各存一份）。
    /// </summary>
    private static (bool, string) CaseDiyCrossEnd()
    {
        var store = NewStore();
        var ctrl = new DiyWorkshopController(store);

        string before = ctrl.GetCanonicalAstText();
        int beforeCost = ctrl.CurrentDef.BaseCost;

        string edited = string.Join("\n",
            "card 950002 \"改过的卡\" (Follower, cost: 7, atk: 5, life: 6, clan: Runecraft) {",
            "    keywords: [storm]",
            "    on play {",
            "        draw(2)",
            "    }",
            "}");

        bool compiled = ctrl.CompileFromScript(edited);
        if (!compiled) return (false, "改卡编译失败：" + string.Join("；", ctrl.LastErrors));

        string after = ctrl.GetCanonicalAstText();
        int afterCost = ctrl.CurrentDef.BaseCost;

        bool costChanged = afterCost == 7 && beforeCost != afterCost;
        bool astChanged = after != before;
        bool nameZh = ctrl.CurrentDef.NameChs == "改过的卡";
        bool blockSynced = ctrl.BlockModel != null;

        bool ok = costChanged && astChanged && nameZh && blockSynced;
        return (ok, "费用 " + beforeCost + "->" + afterCost + " AST变了=" + astChanged
                    + " 中文名=\"" + ctrl.CurrentDef.NameChs + "\" 积木已同步=" + blockSynced);
    }
}

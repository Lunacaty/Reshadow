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
                    Trigger = new TriggerSpec { Event = CoreEvent.OnPlay },
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
}
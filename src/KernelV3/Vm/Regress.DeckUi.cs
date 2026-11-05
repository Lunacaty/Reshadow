using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;

namespace ShadowverseFan.KernelV3.Vm;

/// <summary>
/// UI 层（Bridge 部分）的回归门禁。
///
/// 只测**纯 C# 的 ViewModel 与持久化** —— 它们没有 Godot 依赖，
/// 能在这个无头工程里直接跑。Godot 控件（Control 子类）不在这里测。
/// </summary>
public static class RegressDeckUi
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

        Check("UIC-1 存档往返不丢字段", CasePersistRoundTrip);
        Check("UIC-2 动态构筑规则联动", CaseDynamicRules);
        Check("UIC-3 多维筛选与费用曲线", CaseFilterAndCurve);

        log.AppendLine();
        log.AppendLine("=== UI 门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }

    private static GameSessionStore NewStore()
    {
        var s = new GameSessionStore();
        s.Initialize();
        return s;
    }

    // ---------------------------------------------------------------- UIC-1
    private static (bool, string) CasePersistRoundTrip()
    {
        var store = NewStore();
        var deck = store.CreateDeck("standard", "皇家", "往返测试");
        var pool = store.GetPoolView("standard");
        var first = pool.EnumerateCollectibleCards().FirstOrDefault();
        if (first == null) return (false, "卡池无卡");

        deck.Cards.Add((first.CardId, 2));
        deck.ChosenExtraClans.Add("巫师");
        store.SaveDeck(deck);

        var store2 = NewStore();
        var back = store2.SavedDecks.FirstOrDefault(d => d.DeckId == deck.DeckId);
        if (back == null) return (false, "重读后找不到该牌组");

        bool ok = back.Name == deck.Name
                  && back.PoolId == deck.PoolId
                  && back.LeaderClan == deck.LeaderClan
                  && back.Cards.Count == deck.Cards.Count
                  && back.Cards[0].cardId == first.CardId
                  && back.Cards[0].count == 2
                  && back.ChosenExtraClans.Contains("巫师");

        store2.DeleteDeck(deck.DeckId);

        return (ok, ok ? "名称/卡池/职业/卡表/副职业全部还原" : "有字段丢失");
    }

    // ---------------------------------------------------------------- UIC-2
    private static (bool, string) CaseDynamicRules()
    {
        const int expanderId = 797001;
        var pack = new CardPackDef
        {
            PackId = "uipack", NativePoolId = "uipool",
            Clans = new List<SymbolRawEntry>
            {
                new SymbolRawEntry { Name = "皇家" },
                new SymbolRawEntry { Name = "巫师" },
            },
            Cards = new List<CardRawEntry>
            {
                new CardRawEntry { CardId = expanderId, NameEng = "Expander",
                                   Kind = CardKind.Follower, Cost = 3, Attack = 3, Life = 4 },
            },
        };
        var packs = new Dictionary<string, CardPackDef>(StringComparer.OrdinalIgnoreCase)
        { { "uipack", pack } };

        var built = CardPoolBuilder.Build(new CardPoolConfig
        {
            PoolId = "uipool", EnabledPackIds = new List<string> { "uipack" },
        }, packs);
        if (!built.Ok) return (false, "建池失败");

        var view = built.Pool.ToCardPoolView();
        view.Catalog.RegisterCompiledCard(new CardDef
        {
            CardId = expanderId, Name = "Expander", NameChs = "扩容者",
            Kind = CardKind.Follower, BaseCost = 3, BaseAttack = 3, BaseLife = 4,
            PackId = "uipack", Clan = view.Catalog.Schema.Clan("皇家"),
            DeckModifiers = new List<DeckBuildModifier>
            {
                new DeckBuildModifier { Kind = DeckModKind.ModifyDeckSize, Value = 20,
                                        ExclusiveGroup = "cap" },
                new DeckBuildModifier { Kind = DeckModKind.AllowChosenClans, Value = 1 },
            },
        });

        var store = NewStore();
        var deck = new ConstructedDeck
        {
            DeckId = "uitest2", Name = "规则联动", PoolId = "uipool",
            LeaderClan = "皇家",
        };

        var vm = new DeckEditorViewModel(store, deck);

        // 用反射把 VM 的卡池换成我们造的这个（store 里没有 uipool）
        var prop = typeof(DeckEditorViewModel).GetProperty("CurrentPool");
        prop.SetValue(vm, view);
        var recalc = typeof(DeckEditorViewModel).GetMethod("RecalculateProfile",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        recalc.Invoke(vm, null);

        int baseMax = vm.CurrentProfile.MaxDeckSize;

        if (!vm.TryAddCard(expanderId, out string why))
            return (false, "投卡失败：" + why);

        int afterMax = vm.CurrentProfile.MaxDeckSize;
        int slots = vm.CurrentProfile.AllowedChosenClanSlots;

        bool expanded = afterMax == 60;
        bool slotOk = slots == 1;

        vm.ToggleChosenExtraClan("巫师");
        bool clanOk = vm.CurrentProfile.AllowedClans.Contains("巫师");

        bool ok = expanded && slotOk && clanOk;
        return (ok, "扩容 " + baseMax + "→" + afterMax + " 副职业槽=" + slots
                    + " 选巫师后解锁=" + clanOk);
    }

    // ---------------------------------------------------------------- UIC-3
    private static (bool, string) CaseFilterAndCurve()
    {
        var store = NewStore();
        var deck = store.CreateDeck("standard", "中立", "筛选测试");
        var vm = new DeckEditorViewModel(store, deck);

        vm.ActiveClanFilter = "ALL";
        foreach (int c in new[] { 0, 1, 2, 3, 5 })
        {
            vm.ActiveCostFilter = c;
            var got = vm.GetFilteredPoolCards();
            if (got.Any(x => x.BaseCost != c))
                return (false, "费用 " + c + " 筛出了异费卡");
        }

        vm.ActiveCostFilter = 8;
        if (vm.GetFilteredPoolCards().Any(x => x.BaseCost < 8))
            return (false, "8+ 档筛出低费卡");

        vm.ActiveCostFilter = null;
        vm.ActiveKindFilter = CardKind.Spell;
        var spells = vm.GetFilteredPoolCards();
        if (spells.Any(x => x.Kind != CardKind.Spell))
            return (false, "卡种筛选失效");
        vm.ActiveKindFilter = null;

        // 费用曲线总和必须等于总张数
        var all = vm.CurrentPool.EnumerateCollectibleCards().ToList();
        int added = 0;
        foreach (var c in all)
        {
            for (int i = 0; i < 2; i++)
            {
                if (vm.TryAddCard(c.CardId, out _)) added++;
            }
            if (added >= 20) break;
        }

        int total = vm.TotalCardCount;
        int curveSum = vm.GetCostCurve().Sum();
        bool curveOk = total == curveSum && total > 0;

        return (curveOk, "总张数=" + total + " 曲线和=" + curveSum
                      + " 法术筛选=" + spells.Count + " 张");
    }
}

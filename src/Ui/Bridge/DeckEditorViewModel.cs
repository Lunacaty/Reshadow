using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.Ui.Bridge;

/// <summary>
/// 卡组构筑工作台的纯 C# 视图模型。
///
/// **不引用 Godot 的任何类型** —— 这是硬约束：
/// 它能脱离场景树独立单测，也能在无头环境里跑回归门禁。
/// 一旦有人在这里 using Godot，测试工程就再也编不过了。
///
/// 铁律：构筑是否合法、能带几张、能混哪些职业，
/// 100% 由 DeckRuleResolver 实时计算，这里不做任何手写规则判断。
/// </summary>
public sealed class DeckEditorViewModel
{
    private readonly GameSessionStore _store;

    public ConstructedDeck WorkingDeck { get; private set; }
    public CardPool CurrentPool { get; private set; }
    public EffectiveDeckProfile CurrentProfile { get; private set; }

    // ---- 筛选状态 ----
    public string ActiveClanFilter { get; set; } = "SELF";
    public int? ActiveCostFilter { get; set; }
    public CardKind? ActiveKindFilter { get; set; }
    public bool OnlyDeckModCards { get; set; }
    public string SearchQuery { get; set; } = "";

    public event Action OnStateChanged;

    public DeckEditorViewModel(GameSessionStore store, ConstructedDeck deck)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        WorkingDeck = deck ?? throw new ArgumentNullException(nameof(deck));
        CurrentPool = _store.GetPoolView(deck.PoolId);
        RecalculateProfile();
    }

    public int TotalCardCount => WorkingDeck.Cards.Sum(c => c.count);

    public int GetCopyCount(int cardId)
    {
        foreach (var c in WorkingDeck.Cards)
            if (c.cardId == cardId) return c.count;
        return 0;
    }

    /// <summary>某张卡在当前动态规则下的同名投入上限。</summary>
    public int GetMaxLimitForCard(int cardId)
        => CurrentProfile.CardSpecificCopyLimits.TryGetValue(cardId, out int custom)
           ? custom
           : CurrentProfile.DefaultMaxCopiesPerCard;

    /// <summary>
    /// 尝试投入 1 张卡。
    ///
    /// 关键难点：投入的可能正好是「扩容 +20」或「同名上限改 5」的构筑修正卡，
    /// 所以判定上限时必须**先试探性加入、用新规则算一遍、不合规再回滚**。
    /// 直接用旧上限判会把这类卡永远挡在门外。
    /// </summary>
    public bool TryAddCard(int cardId, out string reason)
    {
        reason = "";
        var def = CurrentPool.Catalog.TryGetCard(cardId);
        if (def == null) { reason = "未找到卡牌定义"; return false; }

        int cur = GetCopyCount(cardId);
        SetCopyCountInternal(cardId, cur + 1);

        var trialProfile = DeckRuleResolver.ResolveAndValidate(WorkingDeck, CurrentPool);

        int allowedLimit = trialProfile.CardSpecificCopyLimits.TryGetValue(cardId, out int sp)
            ? sp : trialProfile.DefaultMaxCopiesPerCard;

        if (cur + 1 > allowedLimit)
        {
            SetCopyCountInternal(cardId, cur);
            reason = $"《{DisplayName(def)}》最多只能投入 {allowedLimit} 张";
            return false;
        }

        if (!trialProfile.AllowCrossCraft && def.Clan.IsValid
            && !trialProfile.AllowedClans.Contains(def.Clan.Name))
        {
            SetCopyCountInternal(cardId, cur);
            reason = $"当前牌组不允许投入【{SvWbClanName.Of(def.Clan.Name)}】职业卡";
            return false;
        }

        CurrentProfile = trialProfile;
        OnStateChanged?.Invoke();
        return true;
    }

    public void RemoveCard(int cardId)
    {
        int cur = GetCopyCount(cardId);
        if (cur <= 0) return;
        SetCopyCountInternal(cardId, cur - 1);
        RecalculateProfile();
        OnStateChanged?.Invoke();
    }

    public void ToggleChosenExtraClan(string clanName)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return;
        bool has = WorkingDeck.ChosenExtraClans
            .Any(c => string.Equals(c, clanName, StringComparison.OrdinalIgnoreCase));
        if (has)
            WorkingDeck.ChosenExtraClans.RemoveAll(
                c => string.Equals(c, clanName, StringComparison.OrdinalIgnoreCase));
        else if (WorkingDeck.ChosenExtraClans.Count < CurrentProfile.AllowedChosenClanSlots)
            WorkingDeck.ChosenExtraClans.Add(clanName);

        RecalculateProfile();
        OnStateChanged?.Invoke();
    }

    /// <summary>经过当前筛选器过滤后的可收藏卡列表。</summary>
    public List<CardDef> GetFilteredPoolCards()
    {
        IEnumerable<CardDef> query = CurrentPool.EnumerateCollectibleCards();

        if (ActiveClanFilter == "SELF")
            query = query.Where(c => string.Equals(c.Clan.Name, WorkingDeck.LeaderClan,
                                                   StringComparison.OrdinalIgnoreCase));
        else if (ActiveClanFilter != "ALL")
            query = query.Where(c => string.Equals(c.Clan.Name, ActiveClanFilter,
                                                   StringComparison.OrdinalIgnoreCase));

        if (ActiveCostFilter.HasValue)
        {
            int cf = ActiveCostFilter.Value;
            query = cf >= 8
                ? query.Where(c => c.BaseCost >= 8)
                : query.Where(c => c.BaseCost == cf);
        }

        if (ActiveKindFilter.HasValue)
            query = query.Where(c => c.Kind == ActiveKindFilter.Value);
        if (OnlyDeckModCards)
            query = query.Where(c => c.DeckModifiers != null && c.DeckModifiers.Count > 0);

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            string kw = SearchQuery.Trim();
            query = query.Where(c =>
                (c.NameChs != null && c.NameChs.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                (c.Name != null && c.Name.Contains(kw, StringComparison.OrdinalIgnoreCase)) ||
                _store.CardPrinter.PrintCard(c).Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderBy(c => c.BaseCost).ThenBy(c => c.CardId).ToList();
    }

    /// <summary>1~8+ 费用曲线（长度 8，下标 0..7 对应 1..8+ 费）。</summary>
    public int[] GetCostCurve()
    {
        var bins = new int[8];
        foreach (var (cid, count) in WorkingDeck.Cards)
        {
            var def = CurrentPool.Catalog.TryGetCard(cid);
            if (def == null) continue;
            int idx = Math.Clamp(def.BaseCost - 1, 0, 7);
            bins[idx] += count;
        }
        return bins;
    }

    /// <summary>牌组清单（按费用升序、卡号升序）。</summary>
    public List<(CardDef def, int count, int limit)> GetDeckEntries()
    {
        var list = new List<(CardDef, int, int)>();
        foreach (var (cid, count) in WorkingDeck.Cards)
        {
            var def = CurrentPool.Catalog.TryGetCard(cid);
            if (def == null) continue;
            list.Add((def, count, GetMaxLimitForCard(cid)));
        }
        return list.OrderBy(x => x.Item1.BaseCost).ThenBy(x => x.Item1.CardId).ToList();
    }

    private void SetCopyCountInternal(int cardId, int newCount)
    {
        int idx = WorkingDeck.Cards.FindIndex(c => c.cardId == cardId);
        if (newCount <= 0)
        {
            if (idx >= 0) WorkingDeck.Cards.RemoveAt(idx);
        }
        else
        {
            if (idx >= 0) WorkingDeck.Cards[idx] = (cardId, newCount);
            else WorkingDeck.Cards.Add((cardId, newCount));
        }
    }

    private void RecalculateProfile()
        => CurrentProfile = DeckRuleResolver.ResolveAndValidate(WorkingDeck, CurrentPool);

    public static string DisplayName(CardDef d)
        => d == null ? "?"
         : !string.IsNullOrWhiteSpace(d.NameChs) ? d.NameChs
         : (!string.IsNullOrWhiteSpace(d.Name) ? d.Name : $"#{d.CardId}");
}

/// <summary>
/// 职业英文名 → 简中名。
///
/// 独立一份而不是直接用 SvWbTheme.ClanDisplayZh：
/// 后者所在的 Theme 命名空间引用了 Godot，
/// 而本文件必须保持零 Godot 依赖才能被无头测试工程引用。
/// </summary>
public static class SvWbClanName
{
    public static string Of(string clanName) => (clanName ?? "").ToLowerInvariant() switch
    {
        "forestcraft" => "精灵",
        "swordcraft"  => "皇家",
        "runecraft"   => "巫师",
        "dragoncraft" => "龙族",
        "abysscraft" or "shadowcraft" or "bloodcraft" => "梦魇",
        "havencraft"  => "主教",
        "portalcraft" => "超越者",
        "neutral"     => "中立",
        _             => string.IsNullOrWhiteSpace(clanName) ? "中立" : clanName,
    };
}

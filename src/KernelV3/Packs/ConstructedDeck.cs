using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 局外「构筑」与局内「牌库」是两份数据，绝不能混为一谈
//
//   构筑（ConstructedDeck）—— 玩家在牌组编辑器里存下的配方：卡号 + 张数。
//                              纯静态，永不因对局而改变。
//   牌库（PlayerState.Deck）—— 对局开始时按配方 new 出来的 40 个活体实体。
//                              会被洗牌、抽走、减费、塞入衍生卡，
//                              甚至被《启示录牌组》整副替换。
//
// 混在一起的话，一局打完玩家的存档就被打乱了 —— 而这是最难查的那类 bug：
// 它不是崩溃，是「过两天玩家发现自己的牌组变了」。
// ============================================================================

/// <summary>【局外数据】玩家的构筑牌组存档（Deck Blueprint）。</summary>
public sealed class ConstructedDeck
{
    public string DeckId { get; init; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Name { get; init; } = "新建牌组";

    /// <summary>该构筑绑定的卡池 ID。</summary>
    public string PoolId { get; init; } = "standard";

    /// <summary>主战者职业（如 "皇家"、"精灵"、"中立"）。</summary>
    public string LeaderClan { get; init; } = "中立";

    /// <summary>
    /// 当牌组里带有 AllowChosenClans（允许任选额外职业）的构筑卡时，
    /// 玩家在 UI 上选定的额外副职业列表。
    /// </summary>
    public List<string> ChosenExtraClans { get; init; } = new();

    /// <summary>构筑清单：(CardId, 投入张数)。</summary>
    public List<(int cardId, int count)> Cards { get; init; } = new();

    /// <summary>展开为局内发牌器所需的卡号序列。</summary>
    public List<int> ExpandToCardIds()
    {
        var list = new List<int>();
        foreach (var (id, count) in Cards)
            for (int i = 0; i < count; i++) list.Add(id);
        return list;
    }
}

/// <summary>局外构筑合法性校验器（严格按所属卡池的 MatchRules 与卡片索引核验）。</summary>
public static class DeckValidator
{
    /// <summary>
    /// 按**动态构筑规则**校验：先跑 DeckRuleResolver 扫描牌组里的构筑修正，
    /// 再用算出的有效规则判张数/上限/职业。
    ///
    /// 直接用 pool.Rules 硬判会把「带扩容卡的 60 张牌组」误判成超限 ——
    /// 那条路径根本没扫 DeckBuildModifier。
    /// </summary>
    public static List<string> Validate(ConstructedDeck deck, CardPoolRuntime pool)
    {
        if (pool == null) return new List<string> { "卡池为空" };
        var view = pool.ToCardPoolView();
        var prof = DeckRuleResolver.ResolveAndValidate(deck, view);
        return prof.Errors;
    }


    /// <summary>展开成局内发牌序列（校验通过后才调）。</summary>
    public static List<int> Expand(ConstructedDeck deck) => deck?.ExpandToCardIds() ?? new List<int>();
}

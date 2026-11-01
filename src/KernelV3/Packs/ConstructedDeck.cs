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

    /// <summary>主战者职业（如 "Swordcraft"、"Forestcraft"、"Neutral"）。</summary>
    public string LeaderClan { get; init; } = "Neutral";

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
    public static List<string> Validate(ConstructedDeck deck, CardPoolRuntime pool)
    {
        var errors = new List<string>();
        if (deck == null) { errors.Add("构筑为空"); return errors; }
        if (pool == null) { errors.Add("卡池为空"); return errors; }

        // 构筑必须绑定它校验时用的那个卡池：串池会让「这张卡能不能用」变成两套答案。
        if (!string.IsNullOrEmpty(deck.PoolId)
            && !string.Equals(deck.PoolId, pool.PoolId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("构筑绑定的卡池是 [" + deck.PoolId + "]，与校验用的 [" + pool.PoolId + "] 不一致。");
        }

        var rules = pool.Rules;
        int totalCards = deck.Cards.Sum(x => x.count);

        if (totalCards < rules.MinDeckSize || totalCards > rules.MaxDeckSize)
        {
            errors.Add("构筑张数不合法：当前 " + totalCards + " 张，本卡池要求 "
                       + rules.MinDeckSize + "~" + rules.MaxDeckSize + " 张。");
        }

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0)
            {
                errors.Add("卡片 " + cardId + " 的投入数量必须大于 0。");
                continue;
            }
            if (!pool.AllCards.TryGetValue(cardId, out var def))
            {
                errors.Add("卡片 id=" + cardId + " 不存在于卡池 [" + pool.PoolId
                           + "] 中（可能所属卡包已被移除）。");
                continue;
            }
            if (def.IsToken && !rules.AllowTokensInConstructedDeck)
            {
                errors.Add("《" + def.Name + "》是衍生卡（Token），不能直接投入局外构筑！");
            }
            if (count > rules.MaxCopiesPerCard)
            {
                errors.Add("《" + def.Name + "》投入了 " + count + " 张，超过本卡池同名卡上限（"
                           + rules.MaxCopiesPerCard + " 张）！");
            }
            if (!rules.AllowCrossCraft && def.Clan.IsValid)
            {
                bool isNeutral = string.Equals(def.Clan.Name, "Neutral", StringComparison.OrdinalIgnoreCase);
                bool isMyClan = string.Equals(def.Clan.Name, deck.LeaderClan, StringComparison.OrdinalIgnoreCase);
                if (!isNeutral && !isMyClan)
                {
                    errors.Add("《" + def.Name + "》属于 [" + def.Clan.Name + "] 职业，不能投入 ["
                               + deck.LeaderClan + "] 职业的构筑中！");
                }
            }
        }

        return errors;
    }

    /// <summary>展开成局内发牌序列（校验通过后才调）。</summary>
    public static List<int> Expand(ConstructedDeck deck) => deck?.ExpandToCardIds() ?? new List<int>();
}

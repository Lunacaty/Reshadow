using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 两阶段动态构筑规则计算器
//
// 阶段 1：扫牌组里每张卡的 DeckBuildModifier，算出「这套牌组的有效规则」
// 阶段 2：用有效规则去校验整副牌组
//
// 为什么必须分两步：如果边扫边校验，那么「扩容 +20」这张卡自己
// 在扫描到它之前就会被「40 张」的旧规则判为超限。
// 规则要先算完，再拿来用。
// ============================================================================

/// <summary>
/// 经过卡池基础规则 + 牌组内构筑修正卡计算后的「单方有效规则与开局面板」。
/// </summary>
public sealed class EffectiveDeckProfile
{
    // ---- 构筑期有效约束 ----
    public int MinDeckSize { get; set; }
    public int MaxDeckSize { get; set; }
    public int DefaultMaxCopiesPerCard { get; set; }
    public bool AllowCrossCraft { get; set; }
    public HashSet<string> AllowedClans { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int AllowedChosenClanSlots { get; set; }
    public Dictionary<int, int> CardSpecificCopyLimits { get; } = new();

    // ---- 对局开始时的单方属性（支持双方非对称开局） ----
    public int LeaderInitialLife { get; set; }
    public int LeaderMaxLife { get; set; }
    public int StartingHandSize { get; set; }
    public int InitialEpBonus { get; set; }
    public int InitialSepBonus { get; set; }

    public List<string> Errors { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

public static class DeckRuleResolver
{
    public static EffectiveDeckProfile ResolveAndValidate(ConstructedDeck deck, CardPool pool)
    {
        var profile = new EffectiveDeckProfile();
        if (deck == null) { profile.Errors.Add("构筑为空"); return profile; }
        if (pool?.Catalog == null || pool.BaseRules == null)
        {
            profile.Errors.Add("卡池为空");
            return profile;
        }

        // 卡池绑定一致性：构筑必须用它校验时的那一个卡池。
        // 串池会让「这张卡能不能用」出现两套答案。
        if (!string.IsNullOrEmpty(deck.PoolId)
            && !string.Equals(deck.PoolId, pool.PoolId, StringComparison.OrdinalIgnoreCase))
        {
            profile.Errors.Add("构筑绑定的卡池是 [" + deck.PoolId + "]，与校验用的 ["
                               + pool.PoolId + "] 不一致。");
        }

        var baseRules = pool.BaseRules;
        profile.MinDeckSize = baseRules.MinDeckSize;
        profile.MaxDeckSize = baseRules.MaxDeckSize;
        profile.DefaultMaxCopiesPerCard = baseRules.MaxCopiesPerCard;
        profile.AllowCrossCraft = baseRules.AllowCrossCraft;
        profile.LeaderInitialLife = baseRules.LeaderInitialLife;
        profile.LeaderMaxLife = baseRules.LeaderMaxLife;
        profile.StartingHandSize = baseRules.StartingHandSize;

        if (!string.IsNullOrWhiteSpace(deck.LeaderClan)) profile.AllowedClans.Add(deck.LeaderClan.Trim());
        profile.AllowedClans.Add("中立");

        var triggeredGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ================= 阶段 1：扫描构装修正 =================
        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0) continue;
            var def = pool.Catalog.TryGetCard(cardId);
            if (def?.DeckModifiers == null || def.DeckModifiers.Count == 0) continue;

            foreach (var mod in def.DeckModifiers)
            {
                if (mod == null) continue;

                // 互斥组：同组只触发第一个，防止多张扩容卡无限叠加。
                if (!string.IsNullOrEmpty(mod.ExclusiveGroup)
                    && !triggeredGroups.Add(mod.ExclusiveGroup))
                    continue;

                int times = mod.StackPerCopy ? count : 1;

                switch (mod.Kind)
                {
                    case DeckModKind.ModifyDeckSize:
                        profile.MinDeckSize = Math.Max(1, profile.MinDeckSize + mod.Value * times);
                        profile.MaxDeckSize = Math.Max(profile.MinDeckSize,
                                                     profile.MaxDeckSize + mod.Value * times);
                        break;

                    case DeckModKind.AllowSpecificClan:
                        if (!string.IsNullOrWhiteSpace(mod.TargetClan))
                            profile.AllowedClans.Add(mod.TargetClan.Trim());
                        break;

                    case DeckModKind.AllowChosenClans:
                        profile.AllowedChosenClanSlots += mod.Value * times;
                        break;

                    case DeckModKind.OverrideSelfCopyLimit:
                        profile.CardSpecificCopyLimits[def.CardId] = mod.Value;
                        break;

                    case DeckModKind.ModifyLeaderStartLife:
                        profile.LeaderInitialLife += mod.Value * times;
                        profile.LeaderMaxLife += mod.Value * times;
                        break;

                    case DeckModKind.ModifyStartingHandSize:
                        profile.StartingHandSize = Math.Max(0, profile.StartingHandSize + mod.Value * times);
                        break;

                    case DeckModKind.ModifyStartEvolvePoints:
                        profile.InitialEpBonus += mod.Value * times;
                        profile.InitialSepBonus += mod.Value2 * times;
                        break;
                }
            }
        }

        // 玩家任选的额外副职业
        var chosen = (deck.ChosenExtraClans ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (chosen.Count > profile.AllowedChosenClanSlots)
        {
            profile.Errors.Add("构筑选择了 " + chosen.Count + " 个额外副职业（"
                               + string.Join("/", chosen) + "），但牌组中的构筑卡只允许任选 "
                               + profile.AllowedChosenClanSlots + " 个额外职业！");
        }
        else
        {
            foreach (var c in chosen) profile.AllowedClans.Add(c);
        }

        // ================= 阶段 2：用有效规则校验 =================
        int totalCards = deck.Cards.Sum(x => x.count);
        if (totalCards < profile.MinDeckSize || totalCards > profile.MaxDeckSize)
        {
            string need = profile.MinDeckSize == profile.MaxDeckSize
                ? profile.MinDeckSize + " 张"
                : profile.MinDeckSize + "~" + profile.MaxDeckSize + " 张";
            profile.Errors.Add("构筑总张数不合法：当前 " + totalCards + " 张，有效规则要求 " + need + "。");
        }

        foreach (var (cardId, count) in deck.Cards)
        {
            if (count <= 0)
            {
                profile.Errors.Add("卡片 " + cardId + " 数量必须为正整数。");
                continue;
            }
            var def = pool.Catalog.TryGetCard(cardId);
            if (def == null)
            {
                profile.Errors.Add("卡片 id=" + cardId + " 未在全局总表中注册。");
                continue;
            }
            if (!pool.ContainsCard(cardId))
            {
                profile.Errors.Add("《" + def.Name + "》（来自卡包 [" + def.PackId
                                   + "]）不属于当前卡池 [" + pool.PoolId + "]。");
            }
            if (def.IsToken && !baseRules.AllowTokensInConstructedDeck)
            {
                profile.Errors.Add("《" + def.Name + "》是衍生卡（Token），禁止直接投入构筑。");
            }

            int maxCopy = profile.CardSpecificCopyLimits.TryGetValue(cardId, out var spec)
                ? spec : profile.DefaultMaxCopiesPerCard;
            if (count > maxCopy)
            {
                profile.Errors.Add("《" + def.Name + "》投入了 " + count
                                   + " 张，超过该卡上限（" + maxCopy + " 张）。");
            }

            if (!profile.AllowCrossCraft && def.Clan.IsValid
                && !profile.AllowedClans.Contains(def.Clan.Name))
            {
                profile.Errors.Add("《" + def.Name + "》属于 [" + def.Clan.Name
                                   + "] 职业，不在当前构筑允许的职业范围 ["
                                   + string.Join(", ", profile.AllowedClans) + "] 内！");
            }
        }

        return profile;
    }
}

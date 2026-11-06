using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.Ui.Bridge;

/// ============================================================================
/// 职业依赖扫描器（167 专项一）
///
/// 从卡池删掉一个职业之前必须知道有没有卡在用它 —— 否则删完那一瞬看不出异常，
/// 等到构筑或对局才炸，而且报错指向的是「符号无效」，跟这次删除毫无关联。
///
/// 三层扫描：
///   1. 卡头职业归属
///   2. 构筑期修正（AllowSpecificClan 的 TargetClan）
///   3. AST 效果树（条件与目标筛选里的 HasClanExpr）
/// ============================================================================
public static class ClanDependencyScanner
{
    /// <summary>返回依赖该职业的卡牌显示名列表；空列表表示可以安全删除。</summary>
    public static List<string> FindCardsDependingOnClan(CardPoolRuntime rt, string clanName)
    {
        var dependents = new List<string>();
        if (rt == null || string.IsNullOrWhiteSpace(clanName)) return dependents;

        foreach (var card in rt.AllCards.Values)
        {
            bool hit =
                // 第 1 层：卡头职业归属
                (card.Clan.IsValid && string.Equals(card.Clan.Name, clanName, StringComparison.OrdinalIgnoreCase))
                // 第 2 层：构筑期修正引用
                || (card.DeckModifiers != null && card.DeckModifiers.Any(dm =>
                        dm != null && dm.Kind == DeckModKind.AllowSpecificClan &&
                        string.Equals(dm.TargetClan, clanName, StringComparison.OrdinalIgnoreCase)))
                // 第 3 层：AST 效果树
                || MentionsInCard(card, clanName);

            if (hit) dependents.Add("《" + DeckEditorViewModel.DisplayName(card) + "》(#" + card.CardId + ")");
        }
        return dependents;
    }

    private static bool MentionsInCard(CardDef card, string clanName)
    {
        bool AnyAbilities(IReadOnlyList<AbilityDef> abs)
        {
            if (abs == null) return false;
            foreach (var ab in abs)
            {
                if (ab == null) continue;
                if (MentionsBool(ab.Condition, clanName)) return true;
                if (ab.Body != null && ab.Body.Any(n => MentionsNode(n, clanName))) return true;
            }
            return false;
        }

        return AnyAbilities(card.Abilities)
            || AnyAbilities(card.AccelerateDef?.Abilities)
            || AnyAbilities(card.CrystallizeDef?.Abilities)
            || AnyAbilities(card.CrestDef?.Abilities)
            || AnyAbilities(card.FaithDef?.Abilities);
    }

    private static bool MentionsBool(BoolExpr e, string clanName)
    {
        if (e == null) return false;
        switch (e)
        {
            case HasClanExpr hc:
                if (hc.Clan.IsValid && string.Equals(hc.Clan.Name, clanName, StringComparison.OrdinalIgnoreCase)) return true;
                if (hc.Clans != null && hc.Clans.Any(c =>
                        c.IsValid && string.Equals(c.Name, clanName, StringComparison.OrdinalIgnoreCase))) return true;
                return false;
            case AndExpr a: return a.Items != null && a.Items.Any(x => MentionsBool(x, clanName));
            case OrExpr o: return o.Items != null && o.Items.Any(x => MentionsBool(x, clanName));
            case NotExpr n: return MentionsBool(n.Inner, clanName);
            case ExistsExpr ex: return MentionsBool(ex.Target?.Where, clanName);
            default: return false;
        }
    }

    private static bool MentionsNode(EffectNode node, string clanName)
    {
        if (node == null) return false;
        if (MentionsBool(node.If, clanName)) return true;
        switch (node)
        {
            case SelectTargetNode s: return MentionsBool(s.Target?.Where, clanName);
            case DealDamageNode d: return MentionsBool(d.Target?.Where, clanName);
            case RestoreLifeNode r: return MentionsBool(r.Target?.Where, clanName);
            case ModifyStatsNode m: return MentionsBool(m.Target?.Where, clanName);
            case MoveZoneNode mv: return MentionsBool(mv.Target?.Where, clanName);
            case SpawnCardNode sp: return MentionsBool(sp.CopyFrom?.Where, clanName);
            case ModifyKeywordNode mk: return MentionsBool(mk.Target?.Where, clanName);
            case ConditionalNode c:
                return MentionsBool(c.Condition, clanName)
                       || (c.Then != null && c.Then.Any(x => MentionsNode(x, clanName)))
                       || (c.Else != null && c.Else.Any(x => MentionsNode(x, clanName)));
            case LoopNode l:
                return MentionsBool(l.Collection?.Where, clanName)
                       || MentionsBool(l.Until, clanName)
                       || (l.Body != null && l.Body.Any(x => MentionsNode(x, clanName)));
            case BranchModeNode bm:
                return bm.Options != null && bm.Options.Any(opt => opt != null && opt.Any(x => MentionsNode(x, clanName)));
            default: return false;
        }
    }
}

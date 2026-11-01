using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 卡池依赖完整性校验器（PoolDependencyVerifier）
//
// 第一重（两遍编译）保证「编译时能认出别的包的卡名」；
// 第二重就是这里：编译**完成后**再扫一遍 AST，确认每个被引用的
// 卡号 / 卡名 / 具名牌组都真的在本卡池里。
//
// 为什么两重都要：两遍编译只能保证「认得出名字」，
// 认出来了不代表那张卡**在卡池里** —— 它可能在总表里、但所属的包没被启用。
// 那种情况下编译不会报错，对局打到一半才 summon 出空壳。
// ============================================================================

public sealed class DependencyReport
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Errors.Count == 0;
}

public static class PoolDependencyVerifier
{
    /// <summary>扫整个卡池的 AST，确认所有具名引用都在池内。</summary>
    public static DependencyReport Verify(
        IReadOnlyDictionary<int, CardDef> poolCards,
        DeckDefTable deckDefs,
        IReadOnlySet<int> poolCardIds,
        Func<int, CardDef> allCardsLookup)
    {
        var rep = new DependencyReport();
        if (poolCards == null) return rep;

        // ---- 1. 卡片 AST 里的具名引用 ----
        foreach (var def in poolCards.Values)
        {
            if (def?.Abilities == null) continue;
            foreach (var ab in def.Abilities)
            {
                WalkAbility(ab, def, poolCardIds, allCardsLookup, deckDefs, rep);

                // 分栏（激奏 / 结晶 / 纹章 / 信仰）里的 AST 同样要扫。
                WalkSubDef(def.AccelerateDef, def, poolCardIds, allCardsLookup, deckDefs, rep);
                WalkSubDef(def.CrystallizeDef, def, poolCardIds, allCardsLookup, deckDefs, rep);
                WalkSubDef(def.CrestDef, def, poolCardIds, allCardsLookup, deckDefs, rep);
                WalkSubDef(def.FaithDef, def, poolCardIds, allCardsLookup, deckDefs, rep);
            }
        }

        // ---- 2. 具名牌组的成员卡 ----
        if (deckDefs != null)
        {
            foreach (var name in deckDefs.Names())
            {
                var members = deckDefs.TryGet(name);
                if (members == null) continue;
                foreach (var entry in members)
                {
                    if (poolCardIds.Contains(entry.CardId)) continue;
                    var owner = allCardsLookup?.Invoke(entry.CardId);
                    rep.Errors.Add("具名牌组《" + name + "》需要的卡片 "
                                   + (owner != null ? "《" + owner.Name + "》(id=" + entry.CardId + ")" : "id=" + entry.CardId)
                                   + " 位于未启用的卡包"
                                   + (owner != null && !string.IsNullOrEmpty(owner.PackId) ? " [" + owner.PackId + "]" : "")
                                   + " 中！请将那个卡包一并加入本卡池。");
                }
            }
        }

        return rep;
    }

    private static void WalkSubDef(
        CardDef sub, CardDef host, IReadOnlySet<int> poolIds,
        Func<int, CardDef> lookup, DeckDefTable deckDefs, DependencyReport rep)
    {
        if (sub?.Abilities == null) return;
        foreach (var ab in sub.Abilities)
            WalkAbility(ab, host, poolIds, lookup, deckDefs, rep);
    }

    private static void WalkAbility(
        AbilityDef ab, CardDef host, IReadOnlySet<int> poolIds,
        Func<int, CardDef> lookup, DeckDefTable deckDefs, DependencyReport rep)
    {
        if (ab?.Body == null) return;
        foreach (var node in ab.Body) WalkNode(node, host, poolIds, lookup, deckDefs, rep);
    }

    private static void WalkNode(
        EffectNode node, CardDef host, IReadOnlySet<int> poolIds,
        Func<int, CardDef> lookup, DeckDefTable deckDefs, DependencyReport rep)
    {
        if (node == null) return;

        switch (node)
        {
            case SpawnCardNode sp:
                // 只有「按卡号指名」的才需要检查；复制体 / 卡池检索不指名。
                // IsCrest 的 SpawnCardNode 走 GainCrest，不是「造一张卡」：
                // 它的 CardId 可能是 0（纹章按名字生成），不该被判成悬空引用。
                if (sp.SourceKind == CardSourceKind.CardId && sp.CardId > 0
                    && !sp.IsCrest && !poolIds.Contains(sp.CardId))
                {
                    var owner = lookup?.Invoke(sp.CardId);
                    // Token 类引用缺失才是硬错误：它是「点名要的那张卡」。
                    if (owner != null && (owner.IsToken || !string.IsNullOrEmpty(owner.PackId)))
                    {
                        rep.Errors.Add("卡包 [" + host.PackId + "] 的卡片《" + host.Name
                                       + "》引用了卡片《" + owner.Name + "》(id=" + sp.CardId
                                       + ")，但该卡片位于未启用的卡包 [" + owner.PackId + "] 中！");
                    }
                }
                break;

            case TransformNode tf when tf.IntoCardId > 0 && !poolIds.Contains(tf.IntoCardId):
            {
                var owner = lookup?.Invoke(tf.IntoCardId);
                rep.Errors.Add("卡包 [" + host.PackId + "] 的卡片《" + host.Name
                               + "》变身目标《" + (owner?.Name ?? tf.IntoCardName)
                               + "》(id=" + tf.IntoCardId + ") 位于未启用的卡包"
                               + (owner != null ? " [" + owner.PackId + "]" : "") + " 中！");
                break;
            }

            case ReplaceDeckNode rd:
            {
                // 换整副牌库是跨包依赖最容易断的地方：
                // 卡面写「替换为《Custom Deck》」，而那份牌组的成员卡
                // 可能定义在另一个包里。只查牌组名存在是不够的 ——
                // 名字存在但成员卡缺一张，照样会在换牌库时造出空壳。
                var expanded = deckDefs?.Expand(rd.DeckName);
                if (expanded == null || expanded.Count == 0)
                {
                    rep.Errors.Add("悬空牌组引用：卡包 [" + host.PackId + "] 的《" + host.Name
                                   + "》试图替换牌库为《" + rd.DeckName + "》，但本卡池未定义该牌组！");
                    break;
                }
                foreach (var cid in expanded.Distinct())
                {
                    if (poolIds.Contains(cid)) continue;
                    var owner = lookup?.Invoke(cid);
                    rep.Errors.Add("跨包牌组残缺：具名牌组《" + rd.DeckName + "》包含卡片 "
                                   + (owner != null ? "《" + owner.Name + "》" : "")
                                   + "(id=" + cid + ")，但提供该卡片的卡包"
                                   + (owner != null && !string.IsNullOrEmpty(owner.PackId) ? " [" + owner.PackId + "]" : "")
                                   + "未加入本卡池！");
                }
                break;
            }
        }

        // 递归进容器节点
        foreach (var child in Children(node)) WalkNode(child, host, poolIds, lookup, deckDefs, rep);
    }

    /// <summary>取节点的直接子节点（容器类节点才有）。</summary>
    private static IEnumerable<EffectNode> Children(EffectNode n)
    {
        switch (n)
        {
            case ConditionalNode c:
                if (c.Then != null) foreach (var x in c.Then) yield return x;
                if (c.Else != null) foreach (var x in c.Else) yield return x;
                break;
            case LoopNode l:
                if (l.Body != null) foreach (var x in l.Body) yield return x;
                break;
            case BranchModeNode b:
                // Options 是「每个模式一个动作列表」，二维。
                if (b.Options != null)
                    foreach (var opt in b.Options)
                        if (opt != null)
                            foreach (var x in opt) yield return x;
                break;
        }
    }
}

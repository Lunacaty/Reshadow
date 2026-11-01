using System;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Diy;

// ============================================================================
// 亡者召还（Reanimate N）的标准 AST 构造与解构
//
// 为什么必须收成一个助手：这条节点有 9 个字段缺一不可
// （SourceKind / CopyFrom 的六个子字段 / ToZone / ToOwner / Count / AddDepartedTrait），
// 而英文编译器、积木、脚本、表单四条路各写一遍，必然有人漏。
//
// 漏掉它的表现不是「报错」，而是**运行时召唤出一个 CardId=0 的白板空壳** ——
// 卡面文字完全正确，引擎也不报错，只有 UnresolvedSpawnCount 悄悄 +1。
// 所以这里把「标准形状」收成唯一入口，谁都不必再手抄。
// ============================================================================
public static class ReanimateHelper
{
    /// <summary>
    /// 构造 Reanimate(N) 节点。
    ///
    /// 语义（76-c 第 6.2-3 条）：从本局被破坏的**随从**里，自动取一个
    /// 基础费用 ≤ N 且基础费用最高的出场，同费用随机。绝不弹框让玩家手选。
    ///
    /// 口径必须是 BaseCost 而不是 Cost：被减费过的随从，当前费用与基础费用不同，
    /// 按当前费用筛会把本该召回的卡挡在门外。
    /// </summary>
    public static SpawnCardNode Build(NumExpr costCap)
    {
        var cap = costCap ?? NumExpr.Const(0);
        return new SpawnCardNode
        {
            SourceKind = CardSourceKind.CopyOfTarget,
            CopyFrom = new TargetSpec
            {
                SourceKind = TargetSourceKind.Zone,
                Zone = ZoneRef.Cemetery,
                Owner = ZoneScope.Self,
                Mode = SelectMode.Random,
                Count = NumExpr.Const(1),
                History = HistoryScope.DestroyedThisMatch,
                Sort = SortKey.BaseCost,
                SortDescending = true,
                Where = BoolExpr.And(
                    new CompareExpr
                    {
                        Left = NumExpr.Prop(StatKey.BaseCost, TargetSpec.Item),
                        Op = CompareOp.Le,
                        Right = cap,
                    },
                    new HasKindExpr { Kind = CardKind.Follower, Of = TargetSpec.Item }),
            },
            ToZone = ZoneRef.Board,
            ToOwner = ZoneScope.Self,
            Count = NumExpr.Const(1),
            AddDepartedTrait = true,
        };
    }

    /// <summary>判断这是不是一个标准的 Reanimate 节点。</summary>
    public static bool IsReanimate(SpawnCardNode sp)
        => sp != null && sp.AddDepartedTrait && sp.SourceKind == CardSourceKind.CopyOfTarget;

    /// <summary>
    /// 从 Reanimate 节点里取出费用上限 N。
    /// 取不到返回 false —— 调用方应当报错，而不是拿 Count 顶替
    /// （Count 恒为 1，那是「召一个」，不是「费用上限 1」）。
    /// </summary>
    public static bool TryExtractCostCap(SpawnCardNode sp, out NumExpr costCap)
    {
        costCap = null;
        if (!IsReanimate(sp)) return false;

        var w = sp.CopyFrom?.Where;
        var items = w is AndExpr a ? a.Items : new[] { w };
        foreach (var x in items)
        {
            if (x is CompareExpr c && c.Left != null
                && c.Left.Op == NumOp.Property && c.Left.Stat == StatKey.BaseCost
                && c.Op == CompareOp.Le)
            {
                costCap = c.Right;
                return true;
            }
        }
        return false;
    }
}

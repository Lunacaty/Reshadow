using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

// ============================================================================
// 布尔表达式树 (BoolExpr)
//
// 这是 V3 对 V2 Predicate 的重做。V2 的问题：
//   · CarriesFlag("state:damaged") 这类魔法字符串 —— 拼错不报错
//   · 48 个 PropertyKey 混了「实体属性」与「玩家计数」
//   · And/Or/Not 与叶子节点共用一个类，字段大量闲置
//
// V3 的解法：
//   · 逻辑连接词用独立类（AndExpr / OrExpr / NotExpr）
//   · 叶子用强类型（HasKeyword / HasState / StatCompare …）
//   · 一个 union 都不留
// ============================================================================

public abstract class BoolExpr
{
    public static readonly BoolExpr True = new ConstBoolExpr(true);
    public static readonly BoolExpr False = new ConstBoolExpr(false);

    public static BoolExpr And(params BoolExpr[] items) => new AndExpr(items);
    public static BoolExpr Or(params BoolExpr[] items) => new OrExpr(items);
    public static BoolExpr Not(BoolExpr e) => new NotExpr(e);
}

public sealed class ConstBoolExpr : BoolExpr
{
    public bool Value { get; }
    public ConstBoolExpr(bool value) => Value = value;
}

public sealed class AndExpr : BoolExpr
{
    public IReadOnlyList<BoolExpr> Items { get; }
    public AndExpr(IReadOnlyList<BoolExpr> items) => Items = items ?? Array.Empty<BoolExpr>();
}

public sealed class OrExpr : BoolExpr
{
    public IReadOnlyList<BoolExpr> Items { get; }
    public OrExpr(IReadOnlyList<BoolExpr> items) => Items = items ?? Array.Empty<BoolExpr>();
}

public sealed class NotExpr : BoolExpr
{
    public BoolExpr Inner { get; }
    public NotExpr(BoolExpr inner) => Inner = inner;
}

/// <summary>数值比较。左侧是一个 NumExpr，不是一个裸字段。</summary>
public sealed class CompareExpr : BoolExpr
{
    public NumExpr Left { get; init; }
    public Core.CompareOp Op { get; init; }
    public NumExpr Right { get; init; }
}

/// <summary>某个目标集合里是否存在满足条件的实体（或数量比较）。</summary>
public sealed class ExistsExpr : BoolExpr
{
    public TargetSpec Target { get; init; }
    public Core.CompareOp Op { get; init; } = Core.CompareOp.Ge;
    public NumExpr Count { get; init; } = NumExpr.Const(1);
}

/// <summary>实体拥有某关键词。</summary>
public sealed class HasKeywordExpr : BoolExpr
{
    public Core.SymbolId<Core.KeywordTag> Keyword { get; init; }
    public TargetSpec? Of { get; init; }
}

/// <summary>实体处于某状态。</summary>
public sealed class HasStateExpr : BoolExpr
{
    public Core.UnitState State { get; init; }
    public TargetSpec? Of { get; init; }
}

/// <summary>实体拥有某兵种。</summary>
public sealed class HasTraitExpr : BoolExpr
{
    public Core.SymbolId<Core.TraitTag> Trait { get; init; }
    public TargetSpec? Of { get; init; }
}

/// <summary>实体属于某职业。</summary>
public sealed class HasClanExpr : BoolExpr
{
    public Core.SymbolId<Core.ClanTag> Clan { get; init; }

    /// <summary>多职业区间（如同时允许皇家或巫师）。为空时只看 Clan。</summary>
    public IReadOnlyList<Core.SymbolId<Core.ClanTag>> Clans { get; init; }

    /// <summary>
    /// true = 动态匹配**我方主战者**的职业（「本职业」）。
    /// 主战者没设职业时回退到来源卡的职业。
    /// </summary>
    public bool IsSelfClan { get; init; }

    public TargetSpec? Of { get; init; }
}

/// <summary>牌库满足某规则。</summary>
public sealed class DeckRuleExpr : BoolExpr
{
    public Core.DeckRule Rule { get; init; }
    public Core.ZoneScope Owner { get; init; } = Core.ZoneScope.Self;
}

/// <summary>实体属于某卡种（随从 / 法术 / 护符）。</summary>
/// <remarks>
/// 旧实现用 ExistsExpr($item >= 1) 代替随从判定，那恒为真，等于没过滤。
/// </remarks>
public sealed class HasKindExpr : BoolExpr
{
    public CardKind Kind { get; init; }
    public TargetSpec? Of { get; init; }
}

/// <summary>当前候选实体与某槽位里的实体**不是同一个**（"another / other" 的真实语义）。</summary>
/// <remarks>
/// 旧实现塞 UnitState.None，而 MatchState 对 None 返回 false —— 导致 all "another" 目标恒空。
/// </remarks>
public sealed class NotSameEntityExpr : BoolExpr
{
    public string ExcludeSlot { get; init; } = TargetSpec.SlotSelf;
    public TargetSpec? Of { get; init; }
}

/// <summary>本次效果的上一条动作是否影响了某实体（"it" 的存在性检查）。</summary>
public sealed class IsPreviousExpr : BoolExpr
{
    public string Slot { get; init; } = "$selected";
}

/// <summary>实体所属卡包匹配指定 PackId（用于「只从某个卡包发现卡片」）。</summary>
public sealed class HasPackExpr : BoolExpr
{
    public string PackId { get; init; } = "";
    public TargetSpec Of { get; init; }
}

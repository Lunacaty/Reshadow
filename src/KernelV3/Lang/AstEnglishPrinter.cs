using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Stage 6.1 · AST → 规范英文渲染器
//
// 对照《英文引擎设计3》§六：
//   反向渲染一个规范化英文句子，与原句做表面形式归一后比对。
//   多渲染出一个动作（或多吃掉一个动作）都会立刻被看到。
//
// 本渲染器的取舍：**它不追求读起来像人写的英文**，只追求
//   「同样的 AST 一定渲染出同样的字符串」且「渲染出的字符串里
//    每一个实词都对应 AST 里一个真实存在的成分」。
// 因此它把所有语义成分按固定顺序、用固定词表吐出来。
// 任何在 AST 里存在、却没有渲染出口的字段，都是 Stage 6 要抓的 bug。
// ============================================================================

public static class AstEnglishPrinter
{
    public static string PrintCard(IReadOnlyList<AbilityDef> abilities)
    {
        if (abilities == null || abilities.Count == 0) return "";
        return string.Join(" ", abilities.Select(PrintAbility));
    }

    public static string PrintAbility(AbilityDef a)
    {
        if (a == null) return "";
        var sb = new StringBuilder();

        sb.Append(TriggerWord(a.Trigger));

        // Trigger 的限定字段必须打印，否则属于「数据存了但看不见」，
        // 会把编译器的正确输出误报成错误。
        if (a.Trigger != null)
        {
            if (a.Trigger.ActiveZone != Core.ActiveZoneScope.Board)
                sb.Append(" zone=" + a.Trigger.ActiveZone);
            if (a.Trigger.DuringTurn != Core.TurnOwnerScope.Any)
                sb.Append(" turn=" + a.Trigger.DuringTurn);
            if (a.Trigger.MaxTriggersPerTurn > 0)
                sb.Append(" max=" + a.Trigger.MaxTriggersPerTurn);
            if (a.Trigger.SubjectFilter != null)
                sb.Append(" on(" + PrintTarget(a.Trigger.SubjectFilter) + ")");
            if (a.Trigger.SecondaryFilter != null)
                sb.Append(" vs(" + PrintTarget(a.Trigger.SecondaryFilter) + ")");
        }


        if (a.Costs != null && a.Costs.Count > 0)
            sb.Append(" [" + string.Join("+", a.Costs.Select(c => c.Kind + "(" + PrintNum(c.Amount) + ")")) + "]");

        if (a.Condition != null && a.Condition is not ConstBoolExpr)
            sb.Append(" if " + PrintBool(a.Condition));

        if (a.Body != null && a.Body.Count > 0)
            sb.Append(": " + string.Join(" ", a.Body.Select(PrintNode)));

        return sb.ToString().Trim();
    }

    private static string TriggerWord(TriggerSpec t)
    {
        var ev = t?.Event ?? CoreEvent.OnPlay;
        return ev switch
        {
            CoreEvent.OnPlay => "fanfare",
            CoreEvent.OnEvolve => "evolve",
            CoreEvent.OnSuperEvolve => "superevolve",
            CoreEvent.PassiveAura => "aura",
            CoreEvent.OnDestroy => "destroyed",
            CoreEvent.OnEnterField => "enters",
            CoreEvent.OnLeaveField => "leaves",
            CoreEvent.OnAttack => "attack",
            CoreEvent.OnClash => "clash",
            CoreEvent.OnDamaged => "damaged",
            CoreEvent.OnHealed => "healed",
            CoreEvent.OnSummon => "summon",
            CoreEvent.OnDraw => "draw",
            CoreEvent.OnDiscard => "discard",
            CoreEvent.TurnStart => "turnstart",
            CoreEvent.TurnEnd => "turnend",
            _ => ev.ToString().ToLowerInvariant(),
        };
    }

    public static string PrintNode(EffectNode n)
    {
        if (n == null) return "";
        var sb = new StringBuilder();

        switch (n)
        {
            case SelectTargetNode s:
                sb.Append("select " + PrintTarget(s.Target));
                break;
            case DealDamageNode d:
                sb.Append("deal " + PrintNum(d.Amount) + " damage to " + PrintTarget(d.Target));
                if (d.IsSplit) sb.Append(" split");
                break;
            case RestoreLifeNode r:
                sb.Append((r.FullRestore ? "fully restore " : "restore " + PrintNum(r.Amount) + " ") + "to " + PrintTarget(r.Target));
                break;
            case ModifyStatsNode m:
                // Swap 没有数值，打 0/0 会让审查误以为是编译器丢了值。
                if (m.Op == Ast.StatModOp.Swap)
                    sb.Append("modifystats Swap to " + PrintTarget(m.Target));
                else
                    sb.Append("modifystats " + m.Op + " " + PrintNum(m.Attack) + "/" + PrintNum(m.Life) + " to " + PrintTarget(m.Target));
                break;
            case ModifyCostNode mc:
                sb.Append("modifycost " + mc.Op + " " + PrintNum(mc.Value) + " to " + PrintTarget(mc.Target));
                break;
            case ModifyKeywordNode k:
                sb.Append("keyword " + k.Op + " " + k.Keyword.Name + " to " + PrintTarget(k.Target));
                break;
            case GrantAbilityNode g:
                sb.Append("grant to " + PrintTarget(g.Target) + " \"" + PrintAbility(g.Granted) + "\"");
                break;
            case SpawnCardNode sp:
                // CopyOfPrevious （"Summon 2 instead"）没有 CardName，
                // 直接拼 CardName 会得到空串，看起来像卡名丢了。
                var what = sp.SourceKind switch
                {
                    CardSourceKind.CopyOfTarget => "copy of " + PrintTarget(sp.CopyFrom),
                    CardSourceKind.CopyOfPrevious => "copy of previous (same card)",
                    _ => sp.CardName,
                };
                // 纹章与普通召唤区分开打印：
                // 二者都是「在一个区域里生成实体」，
                // 但纹章有同名唯一与区域上限，不能当普通召唤看。
                if (sp.IsCrest)
                    sb.Append("gain crest " + what);
                else
                    sb.Append("spawn " + PrintNum(sp.Count) + " " + what + " to " + sp.ToZone);
                break;
            case MoveZoneNode mv:
                sb.Append("move " + PrintTarget(mv.Target) + " to " + mv.ToZone);
                break;
            case TransformNode tr:
                sb.Append("transform " + PrintTarget(tr.Target) + " into " + (tr.CopySource != null ? "copy of " + PrintTarget(tr.CopySource) : tr.IntoCardName));
                break;
            case EvolveNode ev:
                sb.Append((ev.IsSuperEvolve ? "superevolve " : "evolve ") + PrintTarget(ev.Target));
                break;
            case ModifyCounterNode ctr:
                sb.Append("counter " + ctr.Counter.Name + " " + ctr.Op + " " + PrintNum(ctr.Delta)
                          + (ctr.Target != null ? " of " + PrintTarget(ctr.Target) : ""));
                break;
            case InvokeAbilityNode inv:
                sb.Append("invoke " + inv.Event);
                break;
            case ConditionalNode cn:
                sb.Append("if " + PrintBool(cn.Condition)
                          + " then " + string.Join(" ", cn.Then.Select(PrintNode)));
                if (cn.Else != null && cn.Else.Count > 0)
                    sb.Append(" else " + string.Join(" ", cn.Else.Select(PrintNode)));
                break;
            case LoopNode lp:
                sb.Append("loop " + lp.Kind + " " + PrintNum(lp.Count) + " do " + string.Join(" ", lp.Body.Select(PrintNode)));
                break;
            case BranchModeNode bm:
                sb.Append("modes " + bm.Kind + " pick " + PrintNum(bm.PickCount) + " of");
                for (int i = 0; i < bm.Options.Count; i++)
                    sb.Append(" (" + (i + 1) + ") " + string.Join(" ", bm.Options[i].Select(PrintNode)));
                break;
            case ReplaceDeckNode rd:
                sb.Append("replace deck with " + rd.DeckName);
                break;
            case MacroNode mac:
                sb.Append("macro " + mac.Macro.Name);
                break;
            case GrantCounterAbilityNode gc:
                sb.Append("grant to counter:" + gc.Counter.Name + " owner=" + gc.Owner
                          + " \"" + PrintAbility(gc.Granted) + "\"");
                break;
            default:
                sb.Append(n.GetType().Name.ToLowerInvariant());
                break;
        }

        return sb.ToString();
    }

    public static string PrintTarget(TargetSpec t)
    {
        if (t == null) return "null";

        var sb = new StringBuilder();
        switch (t.SourceKind)
        {
            case TargetSourceKind.Slot:
                sb.Append("slot:" + t.Slot);
                break;
            case TargetSourceKind.CardId:
                sb.Append("card:" + (string.IsNullOrEmpty(t.CardName) ? t.CardId.ToString() : t.CardName));
                break;
            default:
                sb.Append(t.Owner.ToString().ToLowerInvariant() + ":" + t.Zone.ToString().ToLowerInvariant());
                break;
        }

        if (t.History != HistoryScope.None) sb.Append(" history=" + t.History);
        if (t.IncludeLeader) sb.Append(" +leader");
        if (t.DistinctByName) sb.Append(" distinct");
        if (t.Sort != SortKey.None) sb.Append(" sort=" + t.Sort + (t.SortDescending ? "-desc" : ""));
        sb.Append(" " + t.Mode + " n=" + PrintNum(t.Count));
        if (t.Where != null && t.Where is not ConstBoolExpr) sb.Append(" where " + PrintBool(t.Where));
        return sb.ToString();
    }

    public static string PrintNum(NumExpr e)
    {
        if (e == null) return "null";
        return e.Op switch
        {
            NumOp.Const => e.Value.ToString(),
            NumOp.Counter => "ctr:" + e.Counter.Name,
            NumOp.Property => "prop:" + e.Stat + (e.Of != null ? "(" + PrintTarget(e.Of) + ")" : ""),
            NumOp.Count => "count(" + PrintTarget(e.Of) + ")",
            // 必须打印 Stat，否则 sum(攻击) 与 sum(防御) 看起来完全一样。
            NumOp.Sum => "sum:" + e.Stat + "(" + PrintTarget(e.Of) + ")",
            NumOp.Arith => PrintArith(e),
            NumOp.Cond => "(if " + PrintBool(e.Cond) + " then " + PrintNum(e.Then) + " else " + PrintNum(e.Else) + ")",
            NumOp.Clamp => "clamp(" + PrintNum(e.Args[0]) + "," + PrintNum(e.Args[1]) + "," + PrintNum(e.Args[2]) + ")",
            NumOp.Extreme => (e.TakeMax ? "max(" : "min(") + string.Join(",", e.Args.Select(PrintNum)) + ")",
            NumOp.SplitOf => "split:" + e.Counter.Name + "#" + e.Value,
            NumOp.DistinctCount => "distinct(" + PrintTarget(e.Of) + ")",
            NumOp.MaxGroupSize => "maxgroup(" + PrintTarget(e.Of) + ")",
            _ => e.Op.ToString().ToLowerInvariant(),
        };
    }

    private static string PrintArith(NumExpr e)
    {
        if (e.Arith == ArithOp.Sub && e.Args != null && e.Args.Count == 2
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
            return "-" + PrintNum(e.Args[1]);
        var op = e.Arith switch
        {
            ArithOp.Add => " + ",
            ArithOp.Sub => " - ",
            ArithOp.Mul => " * ",
            ArithOp.Div => " / ",
            ArithOp.Mod => " % ",
            _ => " + ",
        };
        return "(" + string.Join(op, e.Args.Select(PrintNum)) + ")";
    }

    private static string CmpWord(Core.CompareOp op) => op switch
    {
        Core.CompareOp.Eq => "=",
        Core.CompareOp.Ne => "!=",
        Core.CompareOp.Gt => ">",
        Core.CompareOp.Ge => ">=",
        Core.CompareOp.Lt => "<",
        Core.CompareOp.Le => "<=",
        _ => "=",
    };

    public static string PrintBool(BoolExpr e)
    {
        if (e == null) return "null";
        return e switch
        {
            ConstBoolExpr c => c.Value ? "true" : "false",
            CompareExpr cmp => PrintNum(cmp.Left) + " " + CmpWord(cmp.Op) + " " + PrintNum(cmp.Right),
            HasStateExpr hs => "is " + hs.State,
            HasKindExpr hk => "is " + hk.Kind,
            HasKeywordExpr hk => "has " + hk.Keyword.Name,
            HasTraitExpr ht => "has trait " + ht.Trait.Name,
            HasClanExpr hc => "has clan " + hc.Clan.Name,
            NotSameEntityExpr => "other than itself",
            IsPreviousExpr ip => "is selected",
            AndExpr a => "(" + string.Join(" and ", a.Items.Select(PrintBool)) + ")",
            OrExpr o => "(" + string.Join(" or ", o.Items.Select(PrintBool)) + ")",
            NotExpr n => "not " + PrintBool(n.Inner),
            _ => e.GetType().Name.ToLowerInvariant(),
        };
    }
}

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

            // 并列事件与「仅手动进化」是**运行期判定依据**：
            // Octrice 的「打出**或融合**」全靠 SecondaryEvent。
            // 不打出来的话，往返丢了这个字段也只会表现为「融合时不触发」。
            if (a.Trigger.SecondaryEvent != null)
                sb.Append(" or=" + a.Trigger.SecondaryEvent.Value);
            if (a.Trigger.ManualEvolveOnly)
                sb.Append(" manual");
        }


        if (a.Costs != null && a.Costs.Count > 0)
            // Label 必须打出来：TurnRunner.EnhanceTiers 只认 Label=="enhance"，
            // 丢了它就认不出爆能强化，而卡面文字一模一样。
            sb.Append(" [" + string.Join("+", a.Costs.Select(c =>
                // Label 归一成下划线：EnglishCompiler 写 "earth rite"，
                // 脚本端写 "earth_rite"，两者语义相同，不该因为空格判成不同。
                c.Kind + (string.IsNullOrEmpty(c.Label)
                    ? "" : ":" + c.Label.ToLowerInvariant().Replace(' ', '_'))
                + "(" + PrintNum(c.Amount) + ")")) + "]");

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
                {
                    sb.Append("modifystats Swap to " + PrintTarget(m.Target));
                }
                else
                {
                    // 攻/防分隔符用 "/"，但两侧若是复合表达式必须加括号：
                    //   "+X/+X" 会打成 "count(...)/count(...)"，看上去和真除法一模一样，
                    //   审计时必然误判成算错（本会话已经栽过一次）。
                    //   真除法在 PrintNum 里本身带空格（"A / B"），再加上括号就彻底分得开。
                    string atk = PrintNum(m.Attack), life = PrintNum(m.Life);
                    if (NeedsParen(m.Attack)) atk = "(" + atk + ")";
                    if (NeedsParen(m.Life)) life = "(" + life + ")";
                    sb.Append("modifystats " + m.Op + " " + atk + "/" + life + " to " + PrintTarget(m.Target));
                }
                // Duration 必须打出来：「直到回合结束」与「永久」在 AST 上只差这一个字段，
                // 不打它，往返把限时效果变成永久也会被判成等价。
                if (m.Duration != Ast.DurationKind.Permanent) sb.Append(" dur=" + m.Duration);
                break;
            case ModifyCostNode mc:
                sb.Append("modifycost " + mc.Op + " " + PrintNum(mc.Value) + " to " + PrintTarget(mc.Target));
                if (mc.Duration != Ast.DurationKind.Permanent) sb.Append(" dur=" + mc.Duration);
                break;
            case ModifyKeywordNode k:
                sb.Append("keyword " + k.Op + " " + k.Keyword.Name
                          + (k.Value != null ? "(" + PrintNum(k.Value) + ")" : "")
                          + " to " + PrintTarget(k.Target));
                if (k.Duration != Ast.DurationKind.Permanent) sb.Append(" dur=" + k.Duration);
                break;
            case GrantAbilityNode g:
                sb.Append("grant to " + PrintTarget(g.Target) + " \"" + PrintAbility(g.Granted) + "\"");
                if (g.Duration != Ast.DurationKind.Permanent) sb.Append(" dur=" + g.Duration);
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

                // ToOwner 必须打出来：Slaus 的「给**对手**贴纹章」与「给自己贴」
                // 在 AST 上只差这一个字段，不打它就会把前者判成后者。
                if (sp.ToOwner != Core.ZoneScope.Self) sb.Append(" owner=" + sp.ToOwner);
                // CardId 决定运行期召出的是真卡还是空壳，必须可见。
                if (sp.CardId > 0) sb.Append(" id=" + sp.CardId);
                break;
            case MoveZoneNode mv:
                sb.Append("move " + PrintTarget(mv.Target) + " to " + mv.ToZone);
                break;
            case TransformNode tr:
                sb.Append("transform " + PrintTarget(tr.Target) + " into "
                          + (tr.CopySource != null ? "copy of " + PrintTarget(tr.CopySource) : tr.IntoCardName));
                // IntoCardId 是变身能否生效的唯一依据，必须可见。
                if (tr.CopySource == null) sb.Append(" id=" + tr.IntoCardId);
                break;
            case EvolveNode ev:
                sb.Append((ev.IsSuperEvolve ? "superevolve " : "evolve ") + PrintTarget(ev.Target));
                break;
            case ModifyCounterNode ctr:
                sb.Append("counter " + ctr.Counter.Name + " " + ctr.Op + " " + PrintNum(ctr.Delta)
                          + (ctr.Target != null ? " of " + PrintTarget(ctr.Target) : ""));
                break;
            case InvokeAbilityNode inv:
                // Target 是 DoInvoke 的第一行依赖，丢了就是空操作。
                sb.Append("invoke " + inv.Event + " on " + PrintTarget(inv.Target ?? TargetSpec.Self));
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

    /// <summary>条件的 Of 后缀：$item 是缺省，不打；其余必须可见。</summary>
    private static string OfSuffix(TargetSpec of)
    {
        if (of == null) return "";
        if (of.SourceKind == TargetSourceKind.Slot && of.Slot == TargetSpec.SlotItem) return "";
        return "(" + PrintTarget(of) + ")";
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
                // 必须打 Owner 与 Zone：不然 "hand" / "deck" / "hand and deck"
                // 三种写法的 AST 打印完全一样，审计时分不出编译器到底取了哪个区域。
                // 卡名与卡号都要打：CardId==0 时运行期会退化成「任意卡」
                // （VmEval 的历史池分支写的是 `t.CardId == 0 || …`），
                // 只打卡名会让 Obsessed Test Subject 的「同名卡 5 张」变成「任意随从 5 张」。
                sb.Append("card:" + (string.IsNullOrEmpty(t.CardName) ? t.CardId.ToString() : t.CardName)
                          + "#" + t.CardId
                          + "@" + t.Owner.ToString().ToLowerInvariant()
                          + ":" + t.Zone.ToString().ToLowerInvariant());
                if (t.History != Core.HistoryScope.None) sb.Append(" history=" + t.History);
                break;
            default:
                sb.Append(t.Owner.ToString().ToLowerInvariant() + ":" + t.Zone.ToString().ToLowerInvariant());
                break;
        }

        if (t.History != HistoryScope.None) sb.Append(" history=" + t.History);
        if (t.IncludeLeader) sb.Append(" +leader");
        if (t.DistinctByName) sb.Append(" distinct");
        if (t.Sort != SortKey.None) sb.Append(" sort=" + t.Sort + (t.SortDescending ? "-desc" : ""));
        if (t.TopN > 0) sb.Append(" topN=" + t.TopN);
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
            // 显式取值玩家必须打印出来：否则两侧读同一个计数器名的条件
            // （"both players have N max play points"）打印成完全相同的两半，
            // 验收时看不出它到底只判了一侧还是判了两侧。
            NumOp.Counter => "ctr:" + e.Counter.Name + CounterOwnerSuffix(e),
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

    /// <summary>Counter 的取值玩家后缀：未指定（=自我侧默认）时不打。</summary>
    private static string CounterOwnerSuffix(NumExpr e)
        => e.Of == null ? "" : e.Of.Owner switch
        {
            Core.ZoneScope.Self => "@self",
            Core.ZoneScope.Opponent => "@opp",
            _ => "@any",
        };

    /// <summary>
    /// 该数值表达式打印出来是否需要加括号。
    /// 常量与单个名字不需要；任何复合式（count / prop / 算术）都需要 ——
    /// 否则在 "攻/防" 这种以斜杠分隔的位置上会被读成除法。
    /// </summary>
    private static bool NeedsParen(NumExpr e) => e != null
        && e.Op != NumOp.Const && e.Op != NumOp.Counter && e.Op != NumOp.Property;

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
            // Of 必须打出来：能力级条件判的是 $self，where 里判的是 $item。
            // 不打它，「本卡是否已进化」与「被筛的那张是否已进化」打印完全一样，
            // 而前者在触发时作用域里只有 $self，写成 $item 会恒为 false。
            HasStateExpr hs => "is " + hs.State + OfSuffix(hs.Of),
            HasKindExpr hk => "is " + hk.Kind + OfSuffix(hk.Of),
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

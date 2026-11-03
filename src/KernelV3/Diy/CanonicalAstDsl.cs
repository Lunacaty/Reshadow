using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// 规范 AST 效果语言（Canonical AST DSL）。
///
/// 与 AstEnglishPrinter 的区别：
///   · 只描述**效果语法树**，不含卡名/费用/身材/职业等卡头元数据；
///   · 触发头首字母大写，原子指令一行一条、缩进 2 格；
///   · 剥离冗余：true and 已在 BoolExpr.And 源头折叠，
///     slot:$self 后不再拖 All n=1，Evolve 头不再重复打印 is Evolved。
/// </summary>
public static class CanonicalAstDsl
{
    public static string Print(CardDef card)
    {
        if (card == null) return "";
        var sb = new StringBuilder();

        foreach (var dm in card.DeckModifiers ?? new List<DeckBuildModifier>())
        {
            sb.Append("DeckMod ").Append(dm.Kind).Append('(').Append(dm.Value);
            if (dm.Value2 != 0) sb.Append(", ").Append(dm.Value2);
            if (!string.IsNullOrEmpty(dm.TargetClan)) sb.Append(", clan=").Append(dm.TargetClan);
            if (dm.StackPerCopy) sb.Append(", per_copy");
            if (!string.IsNullOrEmpty(dm.ExclusiveGroup)) sb.Append(", group=").Append(dm.ExclusiveGroup);
            sb.Append('\n');
        }

        if (card.HasFuse)
        {
            sb.Append("Fuse: ")
              .Append(card.FuseMaterialFilter != null ? PrintTargetClean(card.FuseMaterialFilter) : "any")
              .Append('\n');
        }

        PrintAbilitySection(sb, card.Abilities, card.InnateKeywords);

        void Sub(string head, CardDef d)
        {
            if (d == null) return;
            sb.Append('\n').Append('[').Append(head).Append("]\n");
            PrintAbilitySection(sb, d.Abilities, null);
        }
        if (card.AccelerateDef != null) Sub("Accelerate " + card.AccelerateDef.BaseCost, card.AccelerateDef);
        if (card.CrystallizeDef != null) Sub("Crystallize " + card.CrystallizeDef.BaseCost, card.CrystallizeDef);
        Sub("Crest", card.CrestDef);
        Sub("Faith", card.FaithDef);

        return sb.ToString().TrimEnd();
    }

    private static void PrintAbilitySection(StringBuilder sb, IReadOnlyList<AbilityDef> abilities,
                                             IReadOnlyList<SymbolId<KeywordTag>> innateKws)
    {
        var bareKws = new List<string>();
        if (innateKws != null)
            foreach (var k in innateKws) if (k.IsValid) bareKws.Add(CapKeyword(k.Name));

        var normal = new List<AbilityDef>();
        foreach (var ab in abilities ?? Array.Empty<AbilityDef>())
        {
            if (IsBareKeyword(ab, out string kw)) bareKws.Add(CapKeyword(kw));
            else normal.Add(ab);
        }

        var kws = bareKws.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (kws.Count > 0)
            sb.Append("Keywords: ").Append(string.Join(", ", kws)).Append('\n');

        foreach (var ab in normal)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            PrintAbilityBlock(sb, ab, 0);
        }
    }

    /// <summary>只有「被动地挂一条关键词」的能力才算裸关键词，其余照常打印。</summary>
    private static bool IsBareKeyword(AbilityDef a, out string kw)
    {
        kw = null;
        if (a?.Trigger == null || a.Trigger.Event != CoreEvent.PassiveAura) return false;
        if (a.Body == null || a.Body.Count != 1) return false;
        if (a.Body[0] is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant
            && mk.Target != null && mk.Target.SourceKind == TargetSourceKind.Slot
            && mk.Target.Slot == TargetSpec.SlotSelf)
        {
            kw = mk.Keyword.Name ?? "";
            return kw.Length > 0;
        }
        return false;
    }

    private static void PrintAbilityBlock(StringBuilder sb, AbilityDef a, int indent)
    {
        if (a == null) return;
        string pad = new string(' ', indent * 2);
        sb.Append(pad).Append(TriggerHeadWord(a.Trigger));

        if (a.Trigger != null)
        {
            if (a.Trigger.ManualEvolveOnly) sb.Append(" manual");
            if (a.Trigger.ActiveZone != ActiveZoneScope.Board) sb.Append(" zone=").Append(a.Trigger.ActiveZone);
            if (a.Trigger.DuringTurn != TurnOwnerScope.Any) sb.Append(" turn=").Append(a.Trigger.DuringTurn);
            if (a.Trigger.MaxTriggersPerTurn > 0) sb.Append(" max=").Append(a.Trigger.MaxTriggersPerTurn);
            if (a.Trigger.SecondaryEvent != null) sb.Append(" or=").Append(a.Trigger.SecondaryEvent.Value);
            if (a.Trigger.SubjectFilter != null)
                sb.Append(" on(").Append(PrintTargetClean(a.Trigger.SubjectFilter)).Append(')');
        }

        if (a.Costs != null && a.Costs.Count > 0)
            sb.Append(" [").Append(string.Join(" + ", a.Costs.Select(PrintCostClean))).Append(']');

        var cleanCond = StripRedundantCondition(a.Condition, a.Trigger);
        if (cleanCond != null)
            sb.Append(" if ").Append(PrintBoolClean(cleanCond));

        sb.Append(":\n");
        foreach (var n in a.Body ?? Array.Empty<EffectNode>())
            PrintNodeIndented(sb, n, indent + 1);
    }

    /// <summary>
    /// 剥掉「触发头已经表达过」的条件。
    /// `Evolve:` 头本身就意味着 is Evolved($self)，再打一遍只是噪音。
    /// </summary>
    private static BoolExpr StripRedundantCondition(BoolExpr cond, TriggerSpec trig)
    {
        if (cond == null) return null;
        if (cond is ConstBoolExpr cb) return cb.Value ? null : cond;

        if (cond is AndExpr and)
        {
            var keep = new List<BoolExpr>();
            foreach (var it in and.Items)
            {
                if (IsSelfEvolvedCheck(it, trig)) continue;
                keep.Add(it);
            }
            if (keep.Count == 0) return null;
            return keep.Count == 1 ? keep[0] : BoolExpr.And(keep.ToArray());
        }
        return IsSelfEvolvedCheck(cond, trig) ? null : cond;
    }

    private static bool IsSelfEvolvedCheck(BoolExpr e, TriggerSpec trig)
    {
        if (trig == null || e is not HasStateExpr hs) return false;
        bool isSelf = hs.Of == null
                      || (hs.Of.SourceKind == TargetSourceKind.Slot && hs.Of.Slot == TargetSpec.SlotSelf);
        if (!isSelf) return false;
        return (hs.State == UnitState.Evolved && trig.Event == CoreEvent.OnEvolve)
               || (hs.State == UnitState.SuperEvolved && trig.Event == CoreEvent.OnSuperEvolve);
    }

    /// <summary>触发时机 -> 首字母大写的头词。</summary>
    public static string TriggerHeadWord(TriggerSpec t)
    {
        if (t == null) return "Always";
        return t.Event switch
        {
            CoreEvent.OnPlay => "Fanfare",
            CoreEvent.OnEvolve => "Evolve",
            CoreEvent.OnSuperEvolve => "SuperEvolve",
            CoreEvent.OnDestroy => "LastWords",
            CoreEvent.OnLeaveField => "LeaveField",
            CoreEvent.OnEnterField => "EnterField",
            CoreEvent.OnSummon => "OnSummon",
            CoreEvent.OnBanish => "OnBanish",
            CoreEvent.OnAttack => "OnAttack",
            CoreEvent.OnClash => "OnClash",
            CoreEvent.OnDamaged => "OnDamaged",
            CoreEvent.OnHealed => "OnHealed",
            CoreEvent.OnDraw => "OnDraw",
            CoreEvent.OnDiscard => "OnDiscard",
            CoreEvent.OnSpellboost => "OnSpellboost",
            CoreEvent.OnFuse => "OnFuse",
            CoreEvent.OnEngage => "OnEngage",
            CoreEvent.OnTransform => "OnTransform",
            CoreEvent.OnCountdownExpire => "CountdownExpire",
            CoreEvent.OnSelectMode => "OnSelectMode",
            CoreEvent.OnEarthRite => "OnEarthRite",
            CoreEvent.TurnStart => "TurnStart",
            CoreEvent.TurnEnd => "TurnEnd",
            CoreEvent.BattleStart => "BattleStart",
            CoreEvent.PassiveAura => "PassiveAura",
            _ => t.Event.ToString(),
        };
    }

    private static string CapKeyword(string en)
    {
        if (string.IsNullOrEmpty(en)) return "";
        return char.ToUpperInvariant(en[0]) + en.Substring(1).ToLowerInvariant();
    }

    private static string PrintCostClean(CostSpec c)
    {
        string kind = c.Kind switch
        {
            CostKind.UsePp => "Enhance",
            CostKind.Necromance => "Necromancy",
            CostKind.DestroyAlly => "EarthRite",
            CostKind.TurnPlayCount => "Combo",
            CostKind.MatchPlayCount => "Rally",
            CostKind.Countdown => "Countdown",
            _ => c.Kind.ToString(),
        };
        if (!string.IsNullOrEmpty(c.Label) && c.Label != kind.ToLowerInvariant())
            kind = c.Label;
        return kind + "(" + CanonicalAstDsl.PrintNumClean(c.Amount) + ")";
    }

    private static void PrintNodeIndented(StringBuilder sb, EffectNode n, int indent)
    {
        if (n == null) return;
        string pad = new string(' ', indent * 2);

        switch (n)
        {
            case ConditionalNode cn:
                sb.Append(pad).Append("If ").Append(PrintBoolClean(cn.Condition)).Append(":\n");
                foreach (var x in cn.Then ?? Array.Empty<EffectNode>()) PrintNodeIndented(sb, x, indent + 1);
                if (cn.Else != null && cn.Else.Count > 0)
                {
                    sb.Append(pad).Append("Else:\n");
                    foreach (var x in cn.Else) PrintNodeIndented(sb, x, indent + 1);
                }
                return;

            case LoopNode lp:
                sb.Append(pad).Append("Loop ").Append(lp.Kind).Append(' ')
                  .Append(lp.Kind == LoopKind.ForEach ? PrintTargetClean(lp.Collection) : PrintNumClean(lp.Count))
                  .Append(":\n");
                foreach (var x in lp.Body ?? Array.Empty<EffectNode>()) PrintNodeIndented(sb, x, indent + 1);
                return;

            case BranchModeNode bm:
                sb.Append(pad).Append("Modes ").Append(bm.Kind).Append('(')
                  .Append(PrintNumClean(bm.PickCount)).Append("):\n");
                for (int i = 0; i < bm.Options.Count; i++)
                {
                    sb.Append(pad).Append("  (").Append(i + 1).Append("):\n");
                    foreach (var x in bm.Options[i]) PrintNodeIndented(sb, x, indent + 2);
                }
                return;

            case SelectTargetNode s:
                sb.Append(pad).Append("Select ").Append(string.IsNullOrEmpty(s.OutSlot) ? "$selected" : s.OutSlot)
                  .Append(" = ").Append(PrintTargetClean(s.Target)).Append('\n');
                return;

            case DealDamageNode d:
                sb.Append(pad).Append("Deal ").Append(PrintNumClean(d.Amount))
                  .Append(" damage to ").Append(PrintTargetClean(d.Target))
                  .Append(d.IsSplit ? " split\n" : "\n");
                return;

            case RestoreLifeNode r:
                sb.Append(pad).Append(r.FullRestore
                    ? "FullyRestore " + PrintTargetClean(r.Target)
                    : "Restore " + PrintNumClean(r.Amount) + " to " + PrintTargetClean(r.Target))
                  .Append('\n');
                return;

            case MoveZoneNode mv when mv.ToZone == ZoneRef.Hand
                                   && mv.Target != null
                                   && mv.Target.Zone == ZoneRef.Deck
                                   && mv.Target.Mode == SelectMode.First:
                sb.Append(pad).Append("Draw ").Append(PrintNumClean(mv.Target.Count)).Append('\n');
                return;

            case MoveZoneNode mv:
                sb.Append(pad).Append(mv.IsDestroy ? "Destroy " : "Move ")
                  .Append(PrintTargetClean(mv.Target))
                  .Append(mv.IsDestroy ? "\n" : " to " + mv.ToZone + "\n");
                return;

            case SpawnCardNode sp when ReanimateHelper.IsReanimate(sp):
                ReanimateHelper.TryExtractCostCap(sp, out var cap);
                sb.Append(pad).Append("Reanimate(").Append(PrintNumClean(cap)).Append(")\n");
                return;

            case SpawnCardNode sp:
                if (sp.IsCrest)
                {
                    sb.Append(pad).Append("GainCrest \"").Append(sp.CardName).Append('"')
                      .Append(sp.ToOwner != ZoneScope.Self ? " owner=" + sp.ToOwner : "").Append('\n');
                }
                else
                {
                    sb.Append(pad).Append("Spawn ").Append(PrintNumClean(sp.Count))
                      .Append(" \"").Append(sp.CardName).Append("\" to ").Append(sp.ToZone)
                      .Append(sp.CardId > 0 ? " (id=" + sp.CardId + ")" : "").Append('\n');
                }
                return;

            case ModifyStatsNode m:
                if (m.Op == StatModOp.Swap)
                    sb.Append(pad).Append("SwapStats ").Append(PrintTargetClean(m.Target)).Append('\n');
                else
                    sb.Append(pad).Append("ModifyStats ").Append(m.Op).Append(' ')
                      .Append(PrintNumClean(m.Attack)).Append('/').Append(PrintNumClean(m.Life))
                      .Append(" to ").Append(PrintTargetClean(m.Target))
                      .Append(m.Duration != DurationKind.Permanent ? " dur=" + m.Duration : "").Append('\n');
                return;

            case ModifyCostNode mc:
                sb.Append(pad).Append("ModifyCost ").Append(mc.Op).Append(' ')
                  .Append(PrintNumClean(mc.Value)).Append(" to ").Append(PrintTargetClean(mc.Target))
                  .Append(mc.Duration != DurationKind.Permanent ? " dur=" + mc.Duration : "").Append('\n');
                return;

            case ModifyKeywordNode k:
                sb.Append(pad).Append("Keyword ").Append(k.Op).Append(' ').Append(k.Keyword.Name)
                  .Append(k.Value != null ? "(" + PrintNumClean(k.Value) + ")" : "")
                  .Append(" to ").Append(PrintTargetClean(k.Target))
                  .Append(k.Duration != DurationKind.Permanent ? " dur=" + k.Duration : "").Append('\n');
                return;

            case ModifyCounterNode ctr:
                sb.Append(pad).Append("Counter ").Append(ctr.Counter.Name).Append(' ')
                  .Append(ctr.Op).Append(' ').Append(PrintNumClean(ctr.Delta))
                  .Append(ctr.Target != null ? " of " + PrintTargetClean(ctr.Target) : "").Append('\n');
                return;

            case EvolveNode ev:
                sb.Append(pad).Append(ev.IsSuperEvolve ? "SuperEvolve " : "Evolve ")
                  .Append(PrintTargetClean(ev.Target)).Append('\n');
                return;

            case TransformNode tr:
                sb.Append(pad).Append("Transform ").Append(PrintTargetClean(tr.Target))
                  .Append(" into \"").Append(tr.IntoCardName).Append('"')
                  .Append(tr.IntoCardId > 0 ? " (id=" + tr.IntoCardId + ")" : "").Append('\n');
                return;

            case DamageModifierNode dm:
                sb.Append(pad).Append("DamageAura ").Append(dm.Kind).Append(' ').Append(dm.SourceMask)
                  .Append(' ').Append(dm.IsReduction ? "reduce " : "add ")
                  .Append(PrintNumClean(dm.Amount)).Append(" to ").Append(PrintTargetClean(dm.Target))
                  .Append('\n');
                return;

            case MarkVictoryCardNode mv2:
                sb.Append(pad).Append("MarkVictory owner=").Append(mv2.Owner).Append('\n');
                return;

            default:
                sb.Append(pad).Append(AstEnglishPrinter.PrintNode(n)).Append('\n');
                return;
        }
    }

    /// <summary>
    /// 干净的目标打印：槽位直接打 $self / $selected，All 不再拖 n=1，
    /// 默认的 Where=true / Sort=None / History=None 一律不打印。
    /// </summary>
    public static string PrintTargetClean(TargetSpec t)
    {
        if (t == null) return "$self";
        if (t.SourceKind == TargetSourceKind.Slot) return string.IsNullOrEmpty(t.Slot) ? "$self" : t.Slot;

        var sb = new StringBuilder();
        if (t.SourceKind == TargetSourceKind.CardId)
            sb.Append("card(\"").Append(t.CardName).Append("\")@");
        sb.Append(t.Owner.ToString().ToLowerInvariant()).Append(':').Append(t.Zone.ToString().ToLowerInvariant());
        if (t.History != HistoryScope.None) sb.Append(" history=").Append(t.History);
        if (t.IncludeLeader) sb.Append(" +leader");
        if (t.DistinctByName) sb.Append(" distinct");
        if (t.Sort != SortKey.None) sb.Append(" sort=").Append(t.Sort).Append(t.SortDescending ? "-desc" : "");

        if (t.Mode == SelectMode.All) sb.Append(" All");
        else sb.Append(' ').Append(t.Mode).Append('(').Append(PrintNumClean(t.Count)).Append(')');

        if (t.Where != null && !(t.Where is ConstBoolExpr cb && cb.Value))
            sb.Append(" where ").Append(PrintBoolClean(t.Where));
        return sb.ToString();
    }

    /// <summary>数值打印：常量直接写数字，其余复用 AstEnglishPrinter 的紧凑形式。</summary>
    public static string PrintNumClean(NumExpr e)
    {
        if (e == null) return "1";
        if (e.Op == NumOp.Const) return e.Value.ToString();
        return AstEnglishPrinter.PrintNum(e);
    }

    /// <summary>条件打印：剥掉恒真/恒假，其余复用。</summary>
    public static string PrintBoolClean(BoolExpr e)
    {
        if (e == null) return "true";
        if (e is ConstBoolExpr cb) return cb.Value ? "true" : "false";
        return AstEnglishPrinter.PrintBool(e);
    }
}
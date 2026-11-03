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
                    // CardName 可能为空（卡号优先的来源）：退回用 id 表达，
                    // 否则会打印出 `Spawn 2 "" to Board`，回读时正则匹配失败。
                    string nm = sp.CardName ?? "";
                    sb.Append(pad).Append("Spawn ").Append(PrintNumClean(sp.Count));
                    if (nm.Length > 0) sb.Append(" \"").Append(nm).Append('"');
                    sb.Append(" to ").Append(sp.ToZone);
                    if (sp.CardId > 0) sb.Append(" (id=").Append(sp.CardId).Append(')');
                    sb.Append('\n');
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


            case GrantAbilityNode ga:
            {
                // 子能力必须打成分块，而不是塞进一行 ——
                // 塞一行时子能力里的 move/deal 会顶格冒出来，回读就认不出了。
                sb.Append(pad).Append("Grant to ").Append(PrintTargetClean(ga.Target));
                if (ga.Duration != DurationKind.Permanent) sb.Append(" dur=").Append(ga.Duration);
                sb.Append(":\n");
                if (ga.Granted != null) PrintAbilityBlock(sb, ga.Granted, indent + 1);
                return;
            }
            case GrantCounterAbilityNode gca:
            {
                sb.Append(pad).Append("GrantCounter ").Append(gca.Counter.Name).Append(":\n");
                if (gca.Granted != null) PrintAbilityBlock(sb, gca.Granted, indent + 1);
                return;
            }
            case InvokeAbilityNode ia:
                sb.Append(pad).Append("invoke ").Append(ia.Event).Append(" on ")
                  .Append(PrintTargetClean(ia.Target)).Append('\n');
                return;
            case ReplaceDeckNode rd2:
                sb.Append(pad).Append("replace deck with ").Append(rd2.DeckName).Append('\n');
                return;
            case MacroNode mac:
                sb.Append(pad).Append("macro ").Append(mac.Macro.Name).Append('\n');
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

    // ---------------------------------------------------------------- 反向解析（ParseInto）

    /// <summary>解析过程中的可变草稿节点。构造完再一次性生成 init-only 的 AST。</summary>
    private sealed class Draft
    {
        public string Kind;                 // Fanfare / Deal / Select / If / Else / Modes ...
        public string Head;                 // 触发头原文（Kind 为 trigger 时用）
        public string Raw;                  // 整行原文
        public List<Draft> Children = new();
        public List<List<Draft>> Branches = new();
    }

    /// <summary>
    /// 把规范 AST 文本解析回能力列表。
    ///
    /// 只解析**效果**（触发头 + 缩进指令 + 子卡分栏），卡头仍由模板卡提供。
    /// 采用「先建可变草稿树、再自底向上构造 AST」的方式，
    /// 因为 IReadOnlyList 这类 init-only 字段没法边解析边追加。
    /// </summary>
    public static List<AbilityDef>? ParseAbilities(string text, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<AbilityDef>();

        var roots = new List<Draft>();
        var stack = new List<(int Indent, Draft Node)>();

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = rawLine.TrimEnd();
            string body = line.Trim();
            if (body.Length == 0 || body.StartsWith("//")) continue;

            // 子卡分栏：只标记，不重建子卡（子卡归属由模板卡决定）
            if (body.StartsWith("[") && body.EndsWith("]")) { stack.Clear(); continue; }
            if (body.StartsWith("Keywords:") || body.StartsWith("DeckMod ") || body.StartsWith("Fuse:")) continue;

            int indent = line.Length - line.TrimStart().Length;
            var d = new Draft { Raw = body };

            // 判断是「块头」（以冒号结尾）还是叶子指令
            bool isBlockHead = body.EndsWith(":");
            if (isBlockHead) body = body.Substring(0, body.Length - 1).Trim();

            string kind = KindOf(body, isBlockHead, out string head);
            if (kind == null) { errors?.Add("无法识别：" + line.Trim()); continue; }
            d.Kind = kind;
            d.Head = head;

            // 触发头：缩进 0 的块头一律视为新能力的开始
            if (kind == "trigger")
            {
                roots.Add(d);
                stack.Clear();
                stack.Add((indent, d));
                continue;
            }

            // 找父节点
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            var parent = stack.Count > 0 ? stack[^1].Node : null;
            if (parent == null) { errors?.Add("指令出现在触发头之前：" + body); continue; }

            // Modes 的分支："(1):" 形式
            if (body.StartsWith("(") && body.EndsWith(")") && body.Length > 2
                && int.TryParse(body.Substring(1, body.Length - 2), out _))
            {
                var br = new List<Draft>();
                parent.Branches.Add(br);
                stack.Add((indent, new Draft { Kind = "branch", Children = br }));
                continue;
            }

            parent.Children.Add(d);
            if (isBlockHead) stack.Add((indent, d));
        }

        var result = new List<AbilityDef>();
        foreach (var r in roots)
        {
            var ab = BuildAbility(r, errors);
            if (ab != null) result.Add(ab);
        }
        return result;
    }

    /// <summary>判断一行属于哪类节点。返回 null 表示不认识。</summary>
    private static string KindOf(string body, bool isBlockHead, out string head)
    {
        head = null;
        string first = body.Split(' ')[0];

        if (isBlockHead)
        {
            if (first is "If" or "Else" or "Loop" or "Modes") return first.ToLowerInvariant();
            if (first == "Grant") return "grantblock";
            if (first == "GrantCounter") return "grantcounterblock";
            head = body;
            return "trigger";
        }

        return first switch
        {
            "Deal" or "Restore" or "FullyRestore" or "Draw" or "Destroy" or "Move"
            or "Spawn" or "GainCrest" or "ModifyStats" or "SwapStats" or "ModifyCost"
            or "Keyword" or "Counter" or "Evolve" or "SuperEvolve" or "Transform"
            or "DamageAura" or "Select" or "Reanimate" or "MarkVictory" => first.ToLowerInvariant(),
            "invoke" => "invoke",
            "grant" => "grant",
            "macro" => "macro",
            "replace" when body.StartsWith("replace deck with ") => "replacedeck",
            _ when body.StartsWith("Reanimate(") => "reanimate",
            _ => null,
        };
    }

    private static AbilityDef BuildAbility(Draft d, List<string> errors)
    {
        var trig = ParseTriggerHead(d.Head, errors);
        if (trig == null) return null;

        // 触发头自带的条件（if 子句）；Evolve 头会自动补 is Evolved
        BoolExpr cond = null;
        int ifIdx = d.Head.IndexOf(" if ", StringComparison.Ordinal);
        if (ifIdx >= 0)
            cond = ParseBoolExpr(d.Head.Substring(ifIdx + 4).Trim(), errors);

        if (trig.Event == CoreEvent.OnEvolve && cond == null)
            cond = new HasStateExpr { State = UnitState.Evolved, Of = TargetSpec.Self };
        if (trig.Event == CoreEvent.OnSuperEvolve && cond == null)
            cond = new HasStateExpr { State = UnitState.SuperEvolved, Of = TargetSpec.Self };

        return new AbilityDef
        {
            Trigger = trig,
            Condition = cond,
            Costs = ParseCosts(d.Head),
            Body = BuildNodes(d.Children, errors),
        };
    }

    private static List<EffectNode> BuildNodes(List<Draft> drafts, List<string> errors)
    {
        var list = new List<EffectNode>();
        foreach (var d in drafts)
        {
            var n = BuildNode(d, errors);
            if (n != null) list.Add(n);
        }
        return list;
    }

    private static EffectNode BuildNode(Draft d, List<string> errors)
    {
        switch (d.Kind)
        {
            case "if":
                return new ConditionalNode
                {
                    Condition = ParseBoolExpr(Strip("If ", d.Raw), errors),
                    Then = BuildNodes(d.Children, errors),
                    Else = new List<EffectNode>(),
                };
            case "else":
                return null;   // Else 由父 If 吸收，见 BuildWithElse
            case "loop":
            {
                string rest = Strip("Loop ", d.Raw);
                var parts = rest.Split(' ', 2);
                var lk = Enum.TryParse<LoopKind>(parts[0], true, out var lkv) ? lkv : LoopKind.Times;
                return new LoopNode
                {
                    Kind = lk,
                    Count = lk == LoopKind.ForEach ? null : ParseNum(parts.Length > 1 ? parts[1] : "1"),
                    Collection = lk == LoopKind.ForEach && parts.Length > 1 ? ParseTarget(parts[1]) : null,
                    Body = BuildNodes(d.Children, errors),
                };
            }
            case "modes":
            {
                string rest = Strip("Modes ", d.Raw);
                var mp = rest.Split(' ');
                var mk = Enum.TryParse<ModeDispatchKind>(mp[0], true, out var mkv) ? mkv : ModeDispatchKind.Choose;
                string pick = mp.Length > 1 ? mp[1].Trim('(', ')') : "1";
                var opts = d.Branches.Select(b => (IReadOnlyList<EffectNode>)BuildNodes(b, errors)).ToList();
                return new BranchModeNode { Kind = mk, PickCount = ParseNum(pick), Options = opts };
            }
            case "select":
            {
                // Select $selected = opponent:board Choose(1) where is Follower
                string rest = Strip("Select ", d.Raw);
                int eq = rest.IndexOf('=');
                string slot = eq > 0 ? rest.Substring(0, eq).Trim() : TargetSpec.SlotSelected;
                string rhs = eq > 0 ? rest.Substring(eq + 1).Trim() : rest;
                return new SelectTargetNode { Target = ParseTarget(rhs), OutSlot = slot };
            }
            case "deal":
            {
                // Deal 3 damage to opponent:board Choose(1) where is Follower [split]
                string rest = Strip("Deal ", d.Raw);
                int di = rest.IndexOf(" damage to ", StringComparison.Ordinal);
                if (di < 0) { errors?.Add("Deal 缺少 'damage to'：" + d.Raw); return null; }
                var amount = ParseNum(rest.Substring(0, di));
                string tgt = rest.Substring(di + " damage to ".Length).Trim();
                bool split = tgt.EndsWith(" split");
                if (split) tgt = tgt.Substring(0, tgt.Length - " split".Length);
                return new DealDamageNode { Amount = amount, Target = ParseTarget(tgt), IsSplit = split };
            }
            case "restore":
            {
                string rest = Strip("Restore ", d.Raw);
                int ti = rest.IndexOf(" to ", StringComparison.Ordinal);
                return new RestoreLifeNode
                {
                    Amount = ParseNum(ti > 0 ? rest.Substring(0, ti) : rest),
                    Target = ti > 0 ? ParseTarget(rest.Substring(ti + 4)) : TargetSpec.Self,
                };
            }
            case "fullyrestore":
                return new RestoreLifeNode { Target = ParseTarget(Strip("FullyRestore ", d.Raw)), FullRestore = true };
            case "draw":
                return new MoveZoneNode
                {
                    Target = new TargetSpec { Zone = ZoneRef.Deck, Mode = SelectMode.First, Count = ParseNum(Strip("Draw ", d.Raw)) },
                    ToZone = ZoneRef.Hand,
                };
            case "destroy":
                return new MoveZoneNode { Target = ParseTarget(Strip("Destroy ", d.Raw)), ToZone = ZoneRef.Cemetery, IsDestroy = true };
            case "move":
            {
                string rest = Strip("Move ", d.Raw);
                int ti = rest.IndexOf(" to ", StringComparison.Ordinal);
                if (ti < 0) { errors?.Add("Move 缺少 'to zone'：" + d.Raw); return null; }
                var z = Enum.TryParse<ZoneRef>(rest.Substring(ti + 4).Trim(), true, out var zv) ? zv : ZoneRef.Hand;
                return new MoveZoneNode { Target = ParseTarget(rest.Substring(0, ti)), ToZone = z };
            }
            case "spawn":
            {
                // Spawn 1 "Anne's Summoning" to Board (id=90034130)
                string rest = Strip("Spawn ", d.Raw);
                // 卡名可省略（只剩 id 的来源卡）：
                //   Spawn 1 "Fairy" to Board (id=123)
                //   Spawn 2 to Board (id=456)
                var mn = System.Text.RegularExpressions.Regex.Match(rest,
                    "^(\\S+)(?:\\s+\"(.*?)\")?\\s+to\\s+(\\w+)(?:\\s+\\(id=(\\d+)\\))?$");
                if (!mn.Success) { errors?.Add("Spawn 语法错误：" + d.Raw); return null; }
                var z = Enum.TryParse<ZoneRef>(mn.Groups[3].Value, true, out var zv2) ? zv2 : ZoneRef.Board;
                return new SpawnCardNode
                {
                    Count = ParseNum(mn.Groups[1].Value),
                    CardName = mn.Groups[2].Value,
                    CardId = mn.Groups[4].Success ? int.Parse(mn.Groups[4].Value) : 0,
                    ToZone = z,
                };
            }
            case "gaincrest":
            {
                string rest = Strip("GainCrest ", d.Raw).Replace("\"", "");
                return new SpawnCardNode { IsCrest = true, CardName = rest.Trim(), ToZone = ZoneRef.CrestArea };
            }
            case "modifystats":
            {
                // ModifyStats Add 2/2 to $self
                string rest = Strip("ModifyStats ", d.Raw);
                var mp = rest.Split(' ', 3);
                if (mp.Length < 3) { errors?.Add("ModifyStats 语法错误：" + d.Raw); return null; }
                var atkLife = mp[1].Split('/');
                string tail = mp[2];
                int ti = tail.IndexOf(" to ", StringComparison.Ordinal);
                string tgtS = ti >= 0 ? tail.Substring(ti + 4) : tail;
                int durI = tgtS.IndexOf(" dur=", StringComparison.Ordinal);
                if (durI >= 0) tgtS = tgtS.Substring(0, durI);
                var op = Enum.TryParse<StatModOp>(mp[0], true, out var opv) ? opv : StatModOp.Add;
                return new ModifyStatsNode
                {
                    Op = op,
                    Attack = ParseNum(atkLife[0]),
                    Life = ParseNum(atkLife.Length > 1 ? atkLife[1] : atkLife[0]),
                    Target = ParseTarget(tgtS.Trim()),
                };
            }
            case "keyword":
            {
                // Keyword Grant ward to $self
                string rest = Strip("Keyword ", d.Raw);
                var mp = rest.Split(' ', 3);
                if (mp.Length < 3) { errors?.Add("Keyword 语法错误：" + d.Raw); return null; }
                string kw = mp[1];
                string tail = mp[2];
                int ti = tail.IndexOf(" to ", StringComparison.Ordinal);
                string tgtS = ti >= 0 ? tail.Substring(ti + 4) : tail;
                int durI = tgtS.IndexOf(" dur=", StringComparison.Ordinal);
                if (durI >= 0) tgtS = tgtS.Substring(0, durI);
                var op = Enum.TryParse<KeywordOp>(mp[0], true, out var okv) ? okv : KeywordOp.Grant;
                return new ModifyKeywordNode
                {
                    Op = op,
                    Keyword = new SymbolId<KeywordTag>(0, kw),
                    Target = ParseTarget(tgtS.Trim()),
                };
            }
            case "counter":
            {
                // Counter spellboost Add 3 of self:hand All
                string rest = Strip("Counter ", d.Raw);
                var mp = rest.Split(' ', 3);
                if (mp.Length < 3) { errors?.Add("Counter 语法错误：" + d.Raw); return null; }
                var op = Enum.TryParse<CounterOp>(mp[1], true, out var cov) ? cov : CounterOp.Add;
                string tail = mp[2];
                int ofI = tail.IndexOf(" of ", StringComparison.Ordinal);
                string deltaS = ofI >= 0 ? tail.Substring(0, ofI) : tail;
                return new ModifyCounterNode
                {
                    Counter = new SymbolId<CounterTag>(0, mp[0]),
                    Op = op,
                    Delta = ParseNum(deltaS),
                    Target = ofI >= 0 ? ParseTarget(tail.Substring(ofI + 4)) : null,
                };
            }

            case "modifycost":
            {
                // ModifyCost Add -1 to $self
                string rest = Strip("ModifyCost ", d.Raw);
                var mp = rest.Split(' ', 3);
                if (mp.Length < 3) { errors?.Add("ModifyCost 语法错误：" + d.Raw); return null; }
                int ti = mp[2].IndexOf(" to ", StringComparison.Ordinal);
                string tgtS = ti >= 0 ? mp[2].Substring(ti + 4) : mp[2];
                int durI = tgtS.IndexOf(" dur=", StringComparison.Ordinal);
                if (durI >= 0) tgtS = tgtS.Substring(0, durI);
                var op = Enum.TryParse<CostModOp>(mp[0], true, out var opv) ? opv : CostModOp.Add;
                return new ModifyCostNode
                {
                    Op = op,
                    Value = ParseNum(mp[1]),
                    Target = ParseTarget(tgtS.Trim()),
                };
            }
            case "transform":
            {
                // Transform $self into "Fairy" (id=123)
                string rest = Strip("Transform ", d.Raw);
                int ii = rest.IndexOf(" into ", StringComparison.Ordinal);
                if (ii < 0) { errors?.Add("Transform 缺少 'into'：" + d.Raw); return null; }
                string tgtS = rest.Substring(0, ii);
                string rhs = rest.Substring(ii + 6).Trim();
                var mn = System.Text.RegularExpressions.Regex.Match(rhs, "^\"(.+?)\"(?:\\s+\\(id=(\\d+)\\))?$");
                return new TransformNode
                {
                    Target = ParseTarget(tgtS),
                    IntoCardName = mn.Success ? mn.Groups[1].Value : rhs.Trim('\"'),
                    IntoCardId = mn.Success && mn.Groups[2].Success ? int.Parse(mn.Groups[2].Value) : 0,
                };
            }
            case "damageaura":
            {
                // DamageAura TakenCap All reduce 3 to $self
                string rest = Strip("DamageAura ", d.Raw);
                var mp = rest.Split(' ', 5);
                if (mp.Length < 5) { errors?.Add("DamageAura 语法错误：" + d.Raw); return null; }
                var kind = Enum.TryParse<DamageAuraKind>(mp[0], true, out var kv) ? kv : DamageAuraKind.TakenDelta;
                var mask = Enum.TryParse<DamageSourceMask>(mp[1], true, out var mv) ? mv : DamageSourceMask.All;
                bool red = mp[2].Equals("reduce", StringComparison.OrdinalIgnoreCase);
                string tail = mp[3] + (mp.Length > 4 ? " " + mp[4] : "");
                int ti = tail.IndexOf(" to ", StringComparison.Ordinal);
                return new DamageModifierNode
                {
                    Kind = kind, SourceMask = mask, IsReduction = red,
                    Amount = ParseNum(ti > 0 ? tail.Substring(0, ti) : tail),
                    Target = ti > 0 ? ParseTarget(tail.Substring(ti + 4)) : TargetSpec.Self,
                };
            }
            case "reanimate":
            {
                // Reanimate(2) —— 亡者召还：从墓地按费用上限召唤
                var mn = System.Text.RegularExpressions.Regex.Match(d.Raw, @"^Reanimate\((.+)\)$");
                if (!mn.Success) { errors?.Add("Reanimate 语法错误：" + d.Raw); return null; }
                return ReanimateHelper.Build(ParseNum(mn.Groups[1].Value));
            }
            case "invoke":
            {
                // invoke OnPlay on slot:$self All n=1
                string rest = Strip("invoke ", d.Raw);
                var mp = rest.Split(' ', 3);
                var ev = Enum.TryParse<CoreEvent>(mp[0], true, out var evv) ? evv : CoreEvent.OnPlay;
                string tgt = mp.Length > 1 ? mp[1] : "";
                if (tgt.StartsWith("on ")) tgt = tgt.Substring(3);
                return new InvokeAbilityNode { Event = ev, Target = ParseTarget(tgt) };
            }

            case "grant":
            {
                // grant to slot:$created All n=1 "turnend turn=Opponent: ..."
                string rest = Strip("grant ", d.Raw);
                int qi = rest.IndexOf('"');
                string tgtS = qi > 0 ? rest.Substring(0, qi) : rest;
                if (tgtS.StartsWith("to ")) tgtS = tgtS.Substring(3);
                string inner = "";
                if (qi >= 0)
                {
                    int q2 = rest.LastIndexOf('"');
                    inner = q2 > qi ? rest.Substring(qi + 1, q2 - qi - 1) : rest.Substring(qi + 1);
                }
                var granted = ParseNested(inner, errors);
                return new GrantAbilityNode
                {
                    Target = ParseTarget(tgtS.Trim()),
                    Granted = granted.Count > 0 ? granted[0] : null,
                };
            }
            case "macro":
            {
                string name = Strip("macro ", d.Raw).Trim();
                return new MacroNode { Macro = new SymbolId<MacroTag>(0, name) };
            }

            case "grantblock":
            {
                // Grant to $self [dur=...]: <子能力块>
                string rest = Strip("Grant to ", d.Raw);
                int durI = rest.IndexOf(" dur=", StringComparison.Ordinal);
                var dur = DurationKind.Permanent;
                if (durI >= 0)
                {
                    if (Enum.TryParse<DurationKind>(rest.Substring(durI + 5).Trim(), true, out var dv)) dur = dv;
                    rest = rest.Substring(0, durI);
                }
                var kids = BuildNodes(d.Children, errors);
                return new GrantAbilityNode
                {
                    Target = ParseTarget(rest.Trim()),
                    Duration = dur,
                    Granted = kids.Count > 0 ? AbilityFromNodes(kids) : null,
                };
            }
            case "grantcounterblock":
            {
                string cn = Strip("GrantCounter ", d.Raw).Trim();
                var kids = BuildNodes(d.Children, errors);
                return new GrantCounterAbilityNode
                {
                    Counter = new SymbolId<CounterTag>(0, cn),
                    Granted = kids.Count > 0 ? AbilityFromNodes(kids) : null,
                };
            }
            case "replacedeck":
                return new ReplaceDeckNode { DeckName = Strip("replace deck with ", d.Raw) };
            case "swapstats":
                return new ModifyStatsNode { Op = StatModOp.Swap, Target = ParseTarget(Strip("SwapStats ", d.Raw)) };
            case "select2":
                return null;
            case "evolve":
                return new EvolveNode { Target = ParseTarget(Strip("Evolve ", d.Raw)) };
            case "superevolve":
                return new EvolveNode { Target = ParseTarget(Strip("SuperEvolve ", d.Raw)), IsSuperEvolve = true };
            case "markvictory":
            {
                string rest = Strip("MarkVictory", d.Raw).Replace("owner=", "").Trim();
                var ow = Enum.TryParse<ZoneScope>(rest, true, out var owv) ? owv : ZoneScope.Self;
                return new MarkVictoryCardNode { Owner = ow };
            }
            default:
                errors?.Add("暂不支持解析的指令：" + d.Raw);
                return null;
        }
    }


    /// <summary>
    /// 把 `granted` 子能力的规范文本递归解析回来。
    /// 打印端把子能力打成了 `grant to X "turnend: ..."` 这种内联引号形式，
    /// 解析时把引号里的内容当成一段独立的能力文本再走一遍 ParseAbilities。
    /// </summary>
    public static List<AbilityDef> ParseNested(string inner, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(inner)) return new List<AbilityDef>();
        // 内联子能力没有换行：把「: 」之后的内容补成缩进行再解析。
        var sb = new System.Text.StringBuilder();
        var parts = inner.Split(':');
        if (parts.Length >= 2)
        {
            sb.Append(parts[0].Trim()).Append(":\n");
            string tail = string.Join(":", parts.Skip(1)).Trim();
            foreach (var one in SplitTop(tail, " "))
                if (one.Trim().Length > 0) sb.Append("  ").Append(one.Trim()).Append('\n');
        }
        else sb.Append(inner);
        return ParseAbilities(sb.ToString(), errors) ?? new List<AbilityDef>();
    }
    /// <summary>把一串动作节点包成一条无触发头的子能力（Grant 用）。</summary>
    private static AbilityDef AbilityFromNodes(List<EffectNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) return null;
        return new AbilityDef { Body = nodes };
    }
    private static string Strip(string prefix, string s)
        => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? s.Substring(prefix.Length).Trim() : s.Trim();

    /// <summary>触发头：Fanfare / Evolve [manual] / LastWords / TurnEnd turn=Self …</summary>
    private static TriggerSpec ParseTriggerHead(string head, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(head)) return null;
        var parts = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var ev = parts[0] switch
        {
            "Fanfare" => CoreEvent.OnPlay,
            "Evolve" => CoreEvent.OnEvolve,
            "SuperEvolve" => CoreEvent.OnSuperEvolve,
            "LastWords" => CoreEvent.OnDestroy,
            "LeaveField" => CoreEvent.OnLeaveField,
            "EnterField" => CoreEvent.OnEnterField,
            "OnSummon" => CoreEvent.OnSummon,
            "OnBanish" => CoreEvent.OnBanish,
            "OnAttack" => CoreEvent.OnAttack,
            "OnClash" => CoreEvent.OnClash,
            "OnDamaged" => CoreEvent.OnDamaged,
            "OnHealed" => CoreEvent.OnHealed,
            "OnDraw" => CoreEvent.OnDraw,
            "OnDiscard" => CoreEvent.OnDiscard,
            "OnSpellboost" => CoreEvent.OnSpellboost,
            "OnFuse" => CoreEvent.OnFuse,
            "OnEngage" => CoreEvent.OnEngage,
            "OnTransform" => CoreEvent.OnTransform,
            "CountdownExpire" => CoreEvent.OnCountdownExpire,
            "OnSelectMode" => CoreEvent.OnSelectMode,
            "OnEarthRite" => CoreEvent.OnEarthRite,
            "TurnStart" => CoreEvent.TurnStart,
            "TurnEnd" => CoreEvent.TurnEnd,
            "BattleStart" => CoreEvent.BattleStart,
            "PassiveAura" => CoreEvent.PassiveAura,
            _ => CoreEvent.PassiveAura,
        };

        // TriggerSpec 的字段是 init-only：先收集局部量，最后一次性构造。
        bool manual = false;
        var zone = ActiveZoneScope.Board;
        var turn = TurnOwnerScope.Any;
        int maxPerTurn = 0;
        CoreEvent? secondary = null;

        foreach (var p in parts.Skip(1))
        {
            if (p == "manual") { manual = true; continue; }
            if (p.StartsWith("zone="))
            {
                if (Enum.TryParse<ActiveZoneScope>(p.Substring(5), true, out var z)) zone = z;
                continue;
            }
            if (p.StartsWith("turn="))
            {
                if (Enum.TryParse<TurnOwnerScope>(p.Substring(5), true, out var t)) turn = t;
                continue;
            }
            if (p.StartsWith("max="))
            {
                int.TryParse(p.Substring(4), out maxPerTurn);
                continue;
            }
            if (p.StartsWith("or="))
            {
                if (Enum.TryParse<CoreEvent>(p.Substring(3), true, out var se)) secondary = se;
                continue;
            }
        }

        return new TriggerSpec
        {
            Event = ev,
            ManualEvolveOnly = manual,
            ActiveZone = zone,
            DuringTurn = turn,
            MaxTriggersPerTurn = maxPerTurn,
            SecondaryEvent = secondary,
        };
    }

    private static List<CostSpec> ParseCosts(string head)
    {
        var list = new List<CostSpec>();
        int lb = head.IndexOf('['), rb = head.IndexOf(']');
        if (lb < 0 || rb <= lb) return list;
        string inner = head.Substring(lb + 1, rb - lb - 1);
        foreach (var one in inner.Split('+'))
        {
            string t = one.Trim();
            int p1 = t.IndexOf('('), p2 = t.LastIndexOf(')');
            if (p1 < 0 || p2 <= p1) continue;
            string kind = t.Substring(0, p1).Trim();
            int amt = int.TryParse(t.Substring(p1 + 1, p2 - p1 - 1), out var a) ? a : 1;
            var ck = kind switch
            {
                "Enhance" => CostKind.UsePp,
                "Necromancy" => CostKind.Necromance,
                "EarthRite" => CostKind.DestroyAlly,
                "Combo" => CostKind.TurnPlayCount,
                "Rally" => CostKind.MatchPlayCount,
                "Countdown" => CostKind.Countdown,
                _ => CostKind.UsePp,
            };
            list.Add(new CostSpec { Kind = ck, Amount = NumExpr.Const(amt), Label = kind.ToLowerInvariant() });
        }
        return list;
    }

    /// <summary>目标：$self / opponent:board Choose(1) where is Follower / card(\"X\")@self:hand</summary>
    private static TargetSpec ParseTarget(string s)
    {
        s = s.Trim();
        if (s.Length == 0) return TargetSpec.Self;
        if (s.StartsWith("$")) return new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = s };

        var t = new TargetSpec();
        string rest = s;

        // where 子句
        int wi = rest.IndexOf(" where ", StringComparison.Ordinal);
        string whereS = null;
        if (wi >= 0) { whereS = rest.Substring(wi + 7); rest = rest.Substring(0, wi).Trim(); }

        // owner:zone
        int ci = rest.IndexOf(':');
        if (ci > 0)
        {
            var ow = rest.Substring(0, ci);
            rest = rest.Substring(ci + 1);
            var owner = ow switch
            {
                "self" => ZoneScope.Self,
                "opponent" => ZoneScope.Opponent,
                "any" => ZoneScope.Any,
                _ => ZoneScope.Any,
            };
            var zp = rest.Split(' ')[0];
            var zone = Enum.TryParse<ZoneRef>(zp, true, out var zv) ? zv : ZoneRef.Board;
            rest = rest.Substring(zp.Length).Trim();
            t = new TargetSpec { Owner = owner, Zone = zone };
        }

        // 选法：(All) / Choose(N) / Random(N) / First(N)
        if (rest.StartsWith("All"))
        {
            t = new TargetSpec { Owner = t.Owner, Zone = t.Zone, Mode = SelectMode.All };
        }
        else
        {
            var mm = System.Text.RegularExpressions.Regex.Match(rest, "^(\\w+)\\((\\d+)\\)");
            if (mm.Success)
            {
                var mode = Enum.TryParse<SelectMode>(mm.Groups[1].Value, true, out var mv) ? mv : SelectMode.All;
                t = new TargetSpec
                {
                    Owner = t.Owner, Zone = t.Zone, Mode = mode,
                    Count = NumExpr.Const(int.Parse(mm.Groups[2].Value)),
                };
            }
        }

        if (!string.IsNullOrWhiteSpace(whereS))
            t = new TargetSpec { Owner = t.Owner, Zone = t.Zone, Mode = t.Mode, Count = t.Count,
                                 Where = ParseBoolExpr(whereS, null) ?? BoolExpr.True };
        return t;
    }

    private static NumExpr ParseNum(string s)
    {
        s = s.Trim();
        if (int.TryParse(s, out int v)) return NumExpr.Const(v);
        var mm = System.Text.RegularExpressions.Regex.Match(s, @"^ctr[:（(](\w+)");
        if (mm.Success) return NumExpr.Ctr(new SymbolId<CounterTag>(0, mm.Groups[1].Value));
        return NumExpr.Const(1);
    }

    /// <summary>条件表达式：is Follower / has ward / (A and B) / not X …</summary>
    private static BoolExpr ParseBoolExpr(string s, List<string> errors)
    {
        s = (s ?? "").Trim();
        if (s.Length == 0) return BoolExpr.True;
        if (s == "true") return BoolExpr.True;
        if (s == "false") return BoolExpr.False;

        if (s.StartsWith("(") && s.EndsWith(")"))
        {
            string inner = s.Substring(1, s.Length - 2);
            var andParts = SplitTop(inner, " and ");
            if (andParts.Count > 1)
                return BoolExpr.And(andParts.Select(x => ParseBoolExpr(x, errors)).ToArray());
            var orParts = SplitTop(inner, " or ");
            if (orParts.Count > 1)
                return BoolExpr.Or(orParts.Select(x => ParseBoolExpr(x, errors)).ToArray());
            return ParseBoolExpr(inner, errors);
        }
        if (s.StartsWith("not ")) return BoolExpr.Not(ParseBoolExpr(s.Substring(4), errors));
        if (s.StartsWith("is "))
        {
            string w = s.Substring(3).Trim();
            if (Enum.TryParse<UnitState>(w, true, out var st))
                return new HasStateExpr { State = st, Of = TargetSpec.Item };
            if (Enum.TryParse<CardKind>(w, true, out var kd))
                return new HasKindExpr { Kind = kd, Of = TargetSpec.Item };
            return BoolExpr.True;
        }
        if (s.StartsWith("has "))
        {
            string w = s.Substring(4).Trim();
            if (w.StartsWith("trait ")) return new HasTraitExpr { Trait = new SymbolId<TraitTag>(0, w.Substring(6)), Of = TargetSpec.Item };
            if (w.StartsWith("clan ")) return new HasClanExpr { Clan = new SymbolId<ClanTag>(0, w.Substring(5)), Of = TargetSpec.Item };
            return new HasKeywordExpr { Keyword = new SymbolId<KeywordTag>(0, w), Of = TargetSpec.Item };
        }
        return BoolExpr.True;
    }

    private static List<string> SplitTop(string s, string sep)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
            else if (depth == 0 && i + sep.Length <= s.Length && s.Substring(i, sep.Length) == sep)
            {
                parts.Add(s.Substring(start, i - start));
                i += sep.Length - 1;
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Python 风格游戏术语 AST 打印器（204 号文档第二至四节）
//
// 与 AstEnglishPrinter 的分工：
//   · AstEnglishPrinter —— 单行、供 DIY 四端同构门禁比对，必须与解析端严格配对；
//   · AstCanonicalPrinter —— 多行缩进、供人读与审计，用游戏术语替代物理指令。
//
// 三条硬约束（文档第三节思路 4）：
//   1. 确定性：同一 AST 永远打出同一段文本；
//   2. 单射性：不同 AST 绝不打出相同文本；
//   3. 零信息丢失：任何底层字段都必须能表达，必要时用兜底语法。
// ============================================================================

public static class AstCanonicalPrinter
{
    private static string Pad(int indent) => new string(' ', indent * 4);

    /// <summary>
    /// 是否正在打印 where 子句内部。
    ///
    /// where 里的 prop:Cost / is Evolved 默认主语是 $item（被筛的那张），
    /// 而顶层 if 里默认是 $self（本卡）。两者打印时都隐藏括号，
    /// 只在主语不是默认值时才印出来 —— 否则人类看到满屏的 ($item) 会被劝退。
    /// </summary>
    [ThreadStatic] private static bool _inWherePrint;

    /// <summary>打印整张卡（含子定义）。</summary>
    public static string PrintCard(Ast.CardDef card, bool withSubDefs = true)
    {
        if (card == null) return "";
        var sb = new StringBuilder();

        string kind = card.Kind switch
        {
            CardKind.Follower => "Follower",
            CardKind.Spell => "Spell",
            CardKind.Amulet => "Amulet",
            CardKind.Crest => "Crest",
            CardKind.Faith => "Faith",
            _ => card.Kind.ToString(),
        };
        var attrs = new List<string> { kind };
        if (card.Clan.IsValid) attrs.Add("clan=" + card.Clan.Name);
        attrs.Add("cost=" + card.BaseCost);
        if (card.Kind == CardKind.Follower)
        {
            attrs.Add("atk=" + card.BaseAttack);
            attrs.Add("life=" + card.BaseLife);
        }
        if (card.IsToken) attrs.Add("token");
        // 稀有度不打印的话往返后归 0（青铜框），而卡面图一模一样，
        // 只有组牌界面才会发现框色变了。
        if (card.Rarity > 0) attrs.Add("rarity=" + card.Rarity);

        sb.Append("card ").Append(card.CardId).Append(" \"").Append(card.Name).Append("\" (")
          .Append(string.Join(", ", attrs)).Append("):");

        var traits = (card.Traits ?? Array.Empty<SymbolId<TraitTag>>())
            .Where(t => t.IsValid && !string.IsNullOrEmpty(t.Name)).Select(t => t.Name).ToList();
        if (traits.Count > 0)
            sb.Append('\n').Append(Pad(1)).Append("traits: [").Append(string.Join(", ", traits)).Append(']');

        var kws = (card.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
            .Where(k => k.IsValid && !string.IsNullOrEmpty(k.Name)).Select(k => k.Name).ToList();
        if (kws.Count > 0)
            sb.Append('\n').Append(Pad(1)).Append("keywords: [").Append(string.Join(", ", kws)).Append(']');

        if (card.HasFuse)
        {
            sb.Append('\n').Append(Pad(1)).Append("fuse: ");
            sb.Append(card.FuseMaterialFilter == null ? "any" : PrintT(card.FuseMaterialFilter));
        }


        // 构筑期修正（capacity +20 / max cop 10 / 任选 N 个职业 …）。
        // 不打出来的话，往返后牌组上限、同名上限这些全部静默丢失 ——
        // 而卡面中文读起来完全一样，只有进入组牌界面才会发现数值没变。
        if (card.DeckModifiers != null && card.DeckModifiers.Count > 0)
        {
            foreach (var dm in card.DeckModifiers)
            {
                if (dm == null) continue;
                sb.Append('\n').Append(Pad(1)).Append("deck_mod: ").Append(dm.Kind)
                  .Append(' ').Append(dm.Value)
                  .Append(' ').Append(dm.Value2);
                if (!string.IsNullOrEmpty(dm.TargetClan)) sb.Append(" clan=").Append(dm.TargetClan);
                if (dm.StackPerCopy) sb.Append(" stack");
                if (!string.IsNullOrEmpty(dm.ExclusiveGroup)) sb.Append(" group=").Append(dm.ExclusiveGroup);
            }
        }

        // 固有个数计数器（纹章 / 信仰的初始层数）必须落盘，
        // 否则往返后纹章从 3 层变 0 层。
        if (card.InnateCounters != null && card.InnateCounters.Count > 0)
        {
            foreach (var kv in card.InnateCounters.OrderBy(k => k.Key.Name, StringComparer.Ordinal))
            {
                if (!kv.Key.IsValid || string.IsNullOrEmpty(kv.Key.Name)) continue;
                sb.Append('\n').Append(Pad(1))
                  .Append("innate_counter: ").Append(kv.Key.Name).Append(' ').Append(kv.Value);
            }
        }

        if (card.Abilities != null && card.Abilities.Count > 0)
        {
            sb.Append('\n');
            sb.Append(PrintAbilities(card.Abilities, 1));
        }

        if (withSubDefs)
        {
            AppendSub(sb, "crest", card.CrestDef);
            AppendSub(sb, "faith", card.FaithDef);
            AppendSub(sb, "accelerate", card.AccelerateDef);
            AppendSub(sb, "crystallize", card.CrystallizeDef);
        }

        return sb.ToString();
    }

    private static void AppendSub(StringBuilder sb, string label, Ast.CardDef sub)
    {
        if (sub == null) return;
        sb.Append('\n').Append('\n').Append(Pad(1))
          .Append(label).Append(" \"").Append(sub.Name ?? "").Append("\" (cost=").Append(sub.BaseCost).Append("):");
        if (sub.Abilities != null && sub.Abilities.Count > 0)
        {
            sb.Append('\n').Append(PrintAbilities(sub.Abilities, 2));
        }
        else
        {
            sb.Append('\n').Append(Pad(2)).Append("pass");
        }
    }

    /// <summary>打印能力列表（Python 缩进风格，无花括号）。</summary>
    public static string PrintAbilities(IReadOnlyList<AbilityDef> abilities, int indent = 0)
    {
        if (abilities == null || abilities.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < abilities.Count; i++)
        {
            if (i > 0) sb.Append('\n');
            PrintAbility(abilities[i], sb, indent);
        }
        return sb.ToString().TrimEnd();
    }

    private static void PrintAbility(AbilityDef a, StringBuilder sb, int indent)
    {
        if (a == null) return;

        // 护符/纹章的「初始吟唱」本质只是一条 PassiveAura + Countdown 代价，
        // 没有效果体。打成 "aura [countdown(3)]:\n    pass" 全是编译器内部味，
        // 这里美化成单行声明 countdown(3)。
        if (a.Trigger != null
            && a.Trigger.Event == CoreEvent.PassiveAura
            && a.Trigger.SubjectFilter == null
            && (a.Body == null || a.Body.Count == 0)
            && (a.Condition == null || a.Condition is ConstBoolExpr { Value: true })
            && a.Costs is { Count: 1 }
            && a.Costs[0].Kind == CostKind.Countdown)
        {
            sb.Append(Pad(indent)).Append("countdown(")
              .Append(PrintN(a.Costs[0].Amount)).Append(')');
            return;
        }

        sb.Append(Pad(indent)).Append(FormatTriggerHeader(a)).Append(':');
        if (a.Body == null || a.Body.Count == 0)
        {
            sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
            return;
        }
        foreach (var node in a.Body)
        {
            sb.Append('\n');
            PrintNode(node, sb, indent + 1);
        }
    }

    /// <summary>触发头格式化：区分真 fanfare 与普通 on_play，内联代价与条件。</summary>

    /// <summary>
    /// 触发头里的条件是否与某个代价标签重复。
    ///
    /// 编译器会把「连击 3」同时写进 Costs（[combo(3)]）和 Condition
    /// （ctr:combo >= 3），打印出来就是同一件事在一行里写了两遍。
    /// 这里判断能否安全隐藏那个 if —— 只认严格同形的比较式。
    /// </summary>
    private static bool IsConditionRedundantWithCosts(BoolExpr cond, IReadOnlyList<CostSpec> costs)
    {
        if (cond is not CompareExpr cmp || costs == null || costs.Count == 0) return false;
        if (cmp.Left is not NumExpr { Op: NumOp.Counter } l) return false;
        if (cmp.Right is not NumExpr { Op: NumOp.Const } r) return false;

        string ctrName = l.Counter.Name ?? "";
        foreach (var c in costs)
        {
            if (c?.Amount is not NumExpr { Op: NumOp.Const } amt) continue;
            if (amt.Value != r.Value) continue;
            string lbl = (c.Label ?? "").ToLowerInvariant().Replace("_", "");
            // 代价标签 ↔ 计数器名 的对应关系（只列确定成立的几组）
            bool match =
                (lbl == "combo" && ctrName == "combo") ||
                (lbl == "rally" && (ctrName == "rally" || ctrName == "match_play_count")) ||
                (lbl == "necromancy" && (ctrName == "shadow" || ctrName == "necromancy")) ||
                (lbl == "earth_rite" && (ctrName == "earth_sigil" || ctrName == "earth_rite")) ||
                (lbl == "countdown" && ctrName == "countdown");
            if (match && (cmp.Op == CompareOp.Ge || cmp.Op == CompareOp.Eq)) return true;
        }
        return false;
    }

    private static string FormatTriggerHeader(AbilityDef a)
    {
        var t = a.Trigger ?? new TriggerSpec();
        var sb = new StringBuilder();

        string evName = (t.Event, t.IsFanfare, t.ManualEvolveOnly) switch
        {
            (CoreEvent.OnPlay, true, _) => "fanfare",
            (CoreEvent.OnPlay, false, _) => "on_play",
            (CoreEvent.OnDestroy, _, _) when t.SubjectFilter == null => "last_words",
            (CoreEvent.OnDestroy, _, _) => "on_destroy",
            (CoreEvent.OnEvolve, _, true) => "evolve",
            (CoreEvent.OnEvolve, _, false) => "on_evolve",
            (CoreEvent.OnSuperEvolve, _, true) => "super_evolve",
            (CoreEvent.OnSuperEvolve, _, false) => "on_super_evolve",
            (CoreEvent.OnAttack, _, _) => "strike",
            (CoreEvent.OnClash, _, _) => "clash",
            (CoreEvent.OnEngage, _, _) => "engage",
            (CoreEvent.PassiveAura, _, _) => "aura",
            (CoreEvent.TurnStart, _, _) => "turn_start",
            (CoreEvent.TurnEnd, _, _) => "turn_end",
            (CoreEvent.OnEnterField, _, _) => "on_enter",
            (CoreEvent.OnLeaveField, _, _) => "on_leave",
            (CoreEvent.OnSummon, _, _) => "on_summon",
            (CoreEvent.OnDraw, _, _) => "on_draw",
            (CoreEvent.OnDiscard, _, _) => "on_discard",
            (CoreEvent.OnDamaged, _, _) => "on_damaged",
            (CoreEvent.OnHealed, _, _) => "on_healed",
            (CoreEvent.OnBanish, _, _) => "on_banish",
            (CoreEvent.OnFuse, _, _) => "on_fuse",
            (CoreEvent.OnSelectMode, _, _) => "on_select_mode",
            (CoreEvent.OnEarthRite, _, _) => "on_earth_rite",
            (CoreEvent.OnDeckOut, _, _) => "on_deck_out",
            (CoreEvent.OnCountdownExpire, _, _) => "on_countdown_expire",
            (CoreEvent.OnCounterChange, _, _) => "on_counter_change",
            _ => "on_" + t.Event.ToString().Replace("On", "").ToLowerInvariant(),
        };
        sb.Append(evName);

        if (t.SecondaryEvent != null) sb.Append(" or=").Append(t.SecondaryEvent.Value);
        if (t.ActiveZone != ActiveZoneScope.Board) sb.Append(" zone=").Append(t.ActiveZone);
        if (t.DuringTurn != TurnOwnerScope.Any) sb.Append(" turn=").Append(t.DuringTurn);
        if (t.MaxTriggersPerTurn > 0) sb.Append(" max=").Append(t.MaxTriggersPerTurn);
        if (t.SubjectFilter != null) sb.Append(" on(").Append(PrintT(t.SubjectFilter)).Append(')');
        if (t.SecondaryFilter != null) sb.Append(" vs(").Append(PrintT(t.SecondaryFilter)).Append(')');

        if (a.Costs is { Count: > 0 })
        {
            var costs = a.Costs.Select(c =>
            {
                string name = !string.IsNullOrEmpty(c.Label)
                    ? c.Label.ToLowerInvariant().Replace(' ', '_')
                    : c.Kind.ToString().ToLowerInvariant();
                return name + "(" + PrintN(c.Amount) + ")";
            });
            sb.Append(" [").Append(string.Join(", ", costs)).Append(']');
        }

        // 与代价标签重复的条件不打印：编译器把「连击 3」同时写进了 Costs 和 Condition，
        // 同一件事在同一行写两遍（[combo(3)] if ctr:combo >= 3）纯属噪音。
        // 解析端读到 [combo(3)] 时会自动补回条件，所以省略是安全的。
        if (a.Condition != null && a.Condition is not ConstBoolExpr { Value: true }
            && !IsConditionRedundantWithCosts(a.Condition, a.Costs))
            sb.Append(" if ").Append(PrintB(a.Condition));

        return sb.ToString();
    }


    /// <summary>
    /// 打印 if / elif / else 链。
    ///
    /// 当 Else 恰好是单个 ConditionalNode 时，原样输出会是
    ///   else:
    ///       if ...:
    /// 多档爆能一叠就是往右缩进的「箭头型反模式」。
    /// 这里把它压平成同级的 elif，解析端再折回嵌套结构。
    /// </summary>

    /// <summary>
    /// 把「检查资源足够 + 立即扣除」的两行样板识别成 pay 块：
    ///
    ///   if ctr:faith >= 5:          pay faith(5):
    ///       counter faith by -5         discover 1<Amulet> ...
    ///       <后续动作>
    ///
    /// 判据严格：无 else、条件是 ctr:X >= N、Then[0] 恰好是 counter X by -N。
    /// 任何一条不满足就原样打印 —— 美化不能改变语义。
    /// </summary>
    private static bool TryPrintPayBlock(ConditionalNode cn, StringBuilder sb, int indent)
    {
        if (cn.Else != null && cn.Else.Count > 0) return false;
        if (cn.Then == null || cn.Then.Count < 2) return false;
        if (cn.Condition is not CompareExpr cmp || cmp.Op != CompareOp.Ge) return false;
        if (cmp.Left is not NumExpr { Op: NumOp.Counter } l) return false;
        if (cmp.Right is not NumExpr { Op: NumOp.Const } n) return false;
        if (cn.Then[0] is not ModifyCounterNode ctr) return false;
        if (ctr.Op != CounterOp.Add || ctr.Target != null) return false;
        // Delta 有两种合法表示：Const(-N) 与 Arith(0 - N)。
        // 编译器产的是后者，解析端补的是前者 —— 只认一种会让 pay 在第一遍打不出来。
        int negMag;
        if (ctr.Delta is NumExpr { Op: NumOp.Const } d0) negMag = -d0.Value;
        else if (ctr.Delta is { Op: NumOp.Arith, Arith: ArithOp.Sub } ds
                 && ds.Args.Count == 2
                 && ds.Args[0] is NumExpr { Op: NumOp.Const } z && z.Value == 0
                 && ds.Args[1] is NumExpr { Op: NumOp.Const } m0) negMag = m0.Value;
        else return false;
        if (negMag != n.Value) return false;
        if ((ctr.Counter.Name ?? "") != (l.Counter.Name ?? "")) return false;


        sb.Append(Pad(indent)).Append("pay ").Append(l.Counter.Name)
          .Append('(').Append(n.Value).Append("):");
        PrintBlockBody(cn.Then.Skip(1).ToList(), sb, indent + 1);
        return true;
    }
    private static void PrintIfChain(ConditionalNode cn, StringBuilder sb, int indent, bool isElif)
    {
        string p = Pad(indent);
        sb.Append(p).Append(isElif ? "elif " : "if ").Append(PrintB(cn.Condition)).Append(':');
        PrintBlockBody(cn.Then, sb, indent + 1);

        if (cn.Else is { Count: 1 } && cn.Else[0] is ConditionalNode next && next.If == null)
        {
            sb.Append('\n');
            PrintIfChain(next, sb, indent, isElif: true);
        }
        else if (cn.Else is { Count: > 0 })
        {
            sb.Append('\n').Append(p).Append("else:");
            PrintBlockBody(cn.Else, sb, indent + 1);
        }
    }

    private static void PrintBlockBody(IReadOnlyList<EffectNode> body, StringBuilder sb, int indent)
    {
        if (body == null || body.Count == 0)
        {
            sb.Append('\n').Append(Pad(indent)).Append("pass");
            return;
        }
        foreach (var n in body)
        {
            sb.Append('\n');
            PrintNode(n, sb, indent);
        }
    }

    private static string DurSuffix(DurationKind d)
        => d == DurationKind.Permanent ? "" : " [dur=" + d + "]";

    /// <summary>动作节点打印：语义升级 + 缩进控制流。</summary>
    private static void PrintNode(EffectNode n, StringBuilder sb, int indent)
    {
        if (n == null) return;

        if (n.If != null && n.If is not ConstBoolExpr { Value: true })
        {
            sb.Append(Pad(indent)).Append("@if ").Append(PrintB(n.If)).Append(":\n");
            indent++;
        }

        string p = Pad(indent);
        switch (n)
        {
            case MoveZoneNode mv:
                sb.Append(p).Append(FormatMoveZone(mv));
                break;

            case SpawnCardNode sp:
                sb.Append(p).Append(FormatSpawnCard(sp));
                break;

            case ModifyCounterNode ctr:
                sb.Append(p).Append(FormatModifyCounter(ctr));
                break;

            case SelectTargetNode s:
                sb.Append(p).Append("select ").Append(PrintT(s.Target));
                if (!string.IsNullOrEmpty(s.OutSlot) && s.OutSlot != TargetSpec.SlotSelected)
                    sb.Append(" -> ").Append(s.OutSlot);
                if (s.AppendToSlot) sb.Append(" +append");
                break;

            case DealDamageNode d:
                sb.Append(p).Append("deal ").Append(PrintN(d.Amount))
                  .Append(" damage to ").Append(PrintT(d.Target));
                if (d.IsSplit) sb.Append(" split");
                if (!d.IsAbility) sb.Append(" combat");
                break;

            case RestoreLifeNode r:
                sb.Append(p).Append(r.FullRestore
                    ? "fully_restore " + PrintT(r.Target)
                    : "restore " + PrintN(r.Amount) + " life to " + PrintT(r.Target));
                break;

            case ModifyStatsNode m:
            {
                string atk = m.Attack == null ? "_" : FormatSignedNum(m.Attack, m.Op == StatModOp.Add);
                string life = m.Life == null ? "_" : FormatSignedNum(m.Life, m.Op == StatModOp.Add);
                string statCmd = m.Op switch
                {
                    StatModOp.Swap => "swap_stats " + PrintT(m.Target),
                    StatModOp.Set => "set_stats " + PrintT(m.Target) + " to " + atk + "/" + life,
                    _ => "buff " + PrintT(m.Target) + " " + atk + "/" + life,
                };
                sb.Append(p).Append(statCmd).Append(DurSuffix(m.Duration));
                break;
            }

            case ModifyCostNode mc:
                sb.Append(p).Append(FormatModifyCost(mc));
                break;

            case ModifyKeywordNode k:
            {
                string kwCmd = k.Op switch
                {
                    KeywordOp.SilenceAll => "silence " + PrintT(k.Target),
                    KeywordOp.Remove => "remove_keyword " + k.Keyword.Name + " from " + PrintT(k.Target),
                    _ => "grant_keyword " + k.Keyword.Name
                         + (k.Value != null ? "(" + PrintN(k.Value) + ")" : "")
                         + " to " + PrintT(k.Target) + DurSuffix(k.Duration),
                };
                sb.Append(p).Append(kwCmd);
                break;
            }

            case GrantAbilityNode g:
                sb.Append(p).Append("grant_ability to ").Append(PrintT(g.Target))
                  .Append(DurSuffix(g.Duration)).Append(':');
                // 冒号后必须换行：否则子能力的触发头紧贴冒号同一行，
                // 解析端拿不到 Indent，整块被当成无缩进的单行。
                if (g.Granted == null) sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
                else { sb.Append('\n'); PrintAbility(g.Granted, sb, indent + 1); }
                break;

            case GrantCounterAbilityNode gc:
                sb.Append(p).Append("grant_counter_ability to ").Append(gc.Counter.Name);
                if (gc.Owner != ZoneScope.Self) sb.Append(" owner=").Append(gc.Owner);
                sb.Append(':');
                if (gc.Granted == null) sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
                else { sb.Append('\n'); PrintAbility(gc.Granted, sb, indent + 1); }
                break;

            case ConditionalNode cn:
                // 「检查足够并消耗」的样板：if ctr:X >= N: counter X by -N ...
                // 人类漏写那个 if 就会 0 资源白嫖，所以提炼成 pay X(N): 块。
                if (TryPrintPayBlock(cn, sb, indent)) break;
                PrintIfChain(cn, sb, indent, isElif: false);
                break;

            case LoopNode lp:
            {
                string loopHead = lp.Kind switch
                {
                    LoopKind.ForEach => "for_each in " + PrintT(lp.Collection),
                    LoopKind.RepeatUntil => "repeat_until " + PrintB(lp.Until),
                    _ => "repeat " + PrintN(lp.Count) + " times",
                };
                if (lp.MaxIterations != 30) loopHead += " (max=" + lp.MaxIterations + ")";
                sb.Append(p).Append(loopHead).Append(':');
                PrintBlockBody(lp.Body, sb, indent + 1);
                break;
            }

            case BranchModeNode bm:
            {
                sb.Append(p).Append("modes ").Append(bm.Kind)
                  .Append("(pick=").Append(PrintN(bm.PickCount)).Append(')');
                if (bm.GrantTarget != null) sb.Append(" grant_to=").Append(PrintT(bm.GrantTarget));
                sb.Append(':');
                if (bm.Options == null || bm.Options.Count == 0)
                {
                    sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
                    break;
                }
                for (int i = 0; i < bm.Options.Count; i++)
                {
                    sb.Append('\n').Append(Pad(indent + 1)).Append("option ").Append(i + 1).Append(':');
                    PrintBlockBody(bm.Options[i], sb, indent + 2);
                }
                break;
            }

            case EvolveNode ev:
                sb.Append(p).Append(ev.IsSuperEvolve ? "super_evolve " : "evolve ")
                  .Append(PrintT(ev.Target));
                break;

            case TransformNode tr:
                sb.Append(p).Append("transform ").Append(PrintT(tr.Target)).Append(" into ");
                if (tr.CopySource != null)
                    sb.Append("copy of ").Append(PrintT(tr.CopySource));
                else
                    sb.Append(FormatCardRef(tr.IntoCardId, tr.IntoCardName));
                break;

            case InvokeAbilityNode ia:
                sb.Append(p).Append("invoke_ability ").Append(ia.Event).Append(" on ")
                  .Append(PrintT(ia.Target));
                break;

            case DamageModifierNode dm:
                sb.Append(p).Append("damage_modifier ").Append(dm.Kind)
                  .Append(" amount=").Append(PrintN(dm.Amount))
                  .Append(" mask=").Append(dm.SourceMask)
                  .Append(dm.IsReduction ? " reduce" : " increase")
                  .Append(" on ").Append(PrintT(dm.Target))
                  .Append(DurSuffix(dm.Duration));
                break;

            case RevealCardNode rv:
                sb.Append(p).Append("reveal ").Append(PrintT(rv.Target));
                break;

            case ReplaceDeckNode rd:
                sb.Append(p).Append("replace_deck \"").Append(rd.DeckName).Append('"');
                if (rd.Owner != ZoneScope.Self) sb.Append(" owner=").Append(rd.Owner);
                break;

            case MarkVictoryCardNode mvc:
                sb.Append(p).Append("mark_victory_card owner=").Append(mvc.Owner);
                break;

            case MacroNode mac:
                sb.Append(p).Append("macro ").Append(mac.Macro.Name);
                if (mac.Args is { Count: > 0 })
                    sb.Append('(').Append(string.Join(", ", mac.Args.Select(PrintN))).Append(')');
                break;

            default:
                // 兜底：保证任何未来新增节点都不会被静默丢弃。
                sb.Append(p).Append("node ").Append(n.GetType().Name);
                break;
        }
    }

    /// <summary>加法用带符号显示（"+2"/"-1"），绝对值用原样。</summary>
    private static string FormatSignedNum(NumExpr e, bool signed)
    {
        if (e == null) return "_";
        string s = PrintN(e);
        if (!signed || s.StartsWith("-")) return s;
        return "+" + s;
    }

    /// <summary>MoveZone 语义升级：destroy / discard / banish / draw / tutor / bounce / return_to_deck / invoke_self。</summary>
    private static string FormatMoveZone(MoveZoneNode mv)
    {
        var t = mv.Target;
        string slotSuffix = !string.IsNullOrEmpty(mv.OutSlot) && mv.OutSlot != TargetSpec.SlotCreated
            ? " -> " + mv.OutSlot : "";

        if (t == null) return "move <null> to " + mv.ToZone + slotSuffix;

        // 1. 普通抽牌
        if (mv.ToZone == ZoneRef.Hand
            && t.SourceKind == TargetSourceKind.Zone && t.Zone == ZoneRef.Deck && t.Mode == SelectMode.First
            && (t.Where == null || t.Where is ConstBoolExpr { Value: true })
            && t.CardId == 0 && string.IsNullOrEmpty(t.CardName))
        {
            string owner = t.Owner == ZoneScope.Opponent ? " [owner=Opponent]" : "";
            string cnt = t.Count != null ? PrintN(t.Count) : "1";
            return "draw " + cnt + owner + slotSuffix;
        }
        // 2. 定向检索
        if (mv.ToZone == ZoneRef.Hand && t.SourceKind == TargetSourceKind.Zone && t.Zone == ZoneRef.Deck)
            return "tutor " + PrintT(t) + slotSuffix;
        // 3. 瞬念召唤自身
        if (mv.ToZone == ZoneRef.Board && t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelf && !mv.IsDestroy)
            return "invoke_self" + slotSuffix;
        // 4. 破坏
        if (mv.ToZone == ZoneRef.Cemetery && mv.IsDestroy)
            return "destroy " + PrintT(t) + slotSuffix;
        // 5. 舍弃手牌
        if (mv.ToZone == ZoneRef.Cemetery && !mv.IsDestroy
            && t.SourceKind == TargetSourceKind.Zone && t.Zone == ZoneRef.Hand)
            return "discard " + PrintT(t) + slotSuffix;
        // 6. 消失
        if (mv.ToZone == ZoneRef.Banished && !mv.IsDestroy)
            return "banish " + PrintT(t) + slotSuffix;
        // 7. 回手
        if (mv.ToZone == ZoneRef.Hand && !mv.IsDestroy)
            return "bounce " + PrintT(t) + slotSuffix;
        // 8. 洗回牌库
        if (mv.ToZone == ZoneRef.Deck && !mv.IsDestroy)
            return "return_to_deck " + PrintT(t) + slotSuffix;

        // 9. 兜底通用移区
        return "move " + PrintT(t) + " to " + mv.ToZone
             + (mv.IsDestroy ? " destroy" : "") + slotSuffix;
    }

    /// <summary>SpawnCard 语义升级：reanimate / gain_crest / discover / chaos_discover / summon / add_to_hand / spawn_copy。</summary>

    /// <summary>
    /// 卡引用统一打印：有卡号写 #id，没有卡号就退回引号卡名。
    ///
    /// 绝不打 #0 —— 纹章的 CardId 在编译期恒为 0（只填了 CardName 与 CrestDef），
    /// 打成 #0 会让读回来的卡号 0、卡名为空、CrestDef 丢失，全游戏的纹章变空壳。
    /// </summary>
    private static string FormatCardRef(int cardId, string cardName)
        => cardId > 0 ? "#" + cardId : "\"" + (cardName ?? "") + "\"";

    private static string FormatSpawnCard(SpawnCardNode sp)
    {
        string slotSuffix = !string.IsNullOrEmpty(sp.OutSlot) && sp.OutSlot != TargetSpec.SlotCreated
            ? " -> " + sp.OutSlot : "";
        string ownerSuffix = sp.ToOwner != ZoneScope.Self ? " [owner=" + sp.ToOwner + "]" : "";

        // 1. 亡者召还
        if (Diy.ReanimateHelper.IsReanimate(sp) && Diy.ReanimateHelper.TryExtractCostCap(sp, out var cap)
            && sp.ToZone == ZoneRef.Board && sp.ToOwner == ZoneScope.Self)
        {
            string dep = sp.AddDepartedTrait ? " +departed" : "";
            return "reanimate(" + PrintN(cap) + ")" + dep + slotSuffix;
        }

        // 2. 获得纹章
        if (sp.IsCrest)
        {
            // 纹章优先用 CardDef 上的美术卡号，其次退回引号卡名 —— 绝不打出 #0。
            string crestRef = sp.CardId > 0 ? "#" + sp.CardId
                : (sp.CrestDef != null && sp.CrestDef.ArtCardId > 0 ? "#" + sp.CrestDef.ArtCardId
                   : "\"" + (sp.CardName ?? sp.CrestDef?.Name ?? "") + "\"");
            return "gain_crest " + crestRef + ownerSuffix + slotSuffix;
        }

        // 3. 发现 / 混沌发现 / 池随机生成
        if (sp.SourceKind == CardSourceKind.PoolQuery && sp.CopyFrom != null)
        {
            bool chaos = sp.CopyFrom.Zone == ZoneRef.GlobalPool;
            bool choose = sp.CopyFrom.Mode == SelectMode.Choose;
            string verb = choose ? (chaos ? "chaos_discover" : "discover") : "pool_spawn";
            string offer = choose && sp.OfferCount != 3 ? " offer=" + sp.OfferCount : "";
            string toZone = sp.ToZone != ZoneRef.Hand ? " to=" + sp.ToZone : "";

            // 动词已经决定了卡池（discover=当前卡池 / chaos_discover=全宇宙），
            // 数量也已经写在动词后面，再让人类重复写 from self:cardpool Choose(1) 纯属噪音。
            // 只在「不是默认形态」时才补 from 子句。
            var f = sp.CopyFrom;
            bool defaultPool =
                f.SourceKind == TargetSourceKind.Zone
                && f.Zone == (chaos ? ZoneRef.GlobalPool : ZoneRef.CardPool)
                && f.Mode == SelectMode.Choose
                && f.Owner == ZoneScope.Self
                && f.Sort == SortKey.None && f.TopN == 0
                && !f.DistinctByName && !f.DuplicatesOnly && !f.IncludeLeader;
            if (choose && defaultPool)
            {
                // 卡种与过滤条件用泛型标签内联：discover 1<Amulet> where ...
                // 卡种提到尖括号里，其余过滤条件留在 where。
                var remW = ExtractInlineTargetModifiers(f.Where, true, out var nKind, out _);
                string kindTag = nKind.HasValue ? "<" + nKind.Value + ">" : "";
                string tail = (remW != null && remW is not ConstBoolExpr) ? " where " + PrintB(remW) : "";
                return verb + " " + PrintN(sp.Count) + kindTag + offer + toZone + ownerSuffix + slotSuffix + tail;
            }

            return verb + " " + PrintN(sp.Count) + " from "
                 + PrintT(sp.CopyFrom) + offer + toZone + ownerSuffix + slotSuffix;
        }

        // 4. 复制体
        if (sp.SourceKind == CardSourceKind.CopyOfTarget)
        {
            string exact = sp.IsExactCopy ? "exact_copy" : "copy";
            string dep = sp.AddDepartedTrait ? " +departed" : "";
            return "spawn_" + exact + " " + PrintN(sp.Count) + " of "
                 + PrintT(sp.CopyFrom) + " to " + sp.ToZone
                 + ownerSuffix + dep + slotSuffix;
        }

        // 5. 延续上一次生成
        if (sp.SourceKind == CardSourceKind.CopyOfPrevious)
            return "spawn_previous " + PrintN(sp.Count) + " to " + sp.ToZone
                 + ownerSuffix + slotSuffix;

        // 6. 常规具名召唤 / 加手牌
        string idTag = " " + FormatCardRef(sp.CardId, sp.CardName);
        string depTag = sp.AddDepartedTrait ? " +departed" : "";
        if (sp.ToZone == ZoneRef.Board)
            return "summon " + PrintN(sp.Count) + idTag + ownerSuffix + depTag + slotSuffix;
        if (sp.ToZone == ZoneRef.Hand)
            return "add_to_hand " + PrintN(sp.Count) + idTag + ownerSuffix + depTag + slotSuffix;

        return "spawn " + PrintN(sp.Count) + idTag
             + " to " + sp.ToZone + ownerSuffix + depTag + slotSuffix;
    }

    /// <summary>计数器语义升级：spellboost / advance_countdown / delay_countdown / counter。</summary>
    private static string FormatModifyCounter(ModifyCounterNode ctr)
    {
        string name = ctr.Counter.Name ?? "";
        string opWord = ctr.Op switch
        {
            CounterOp.Set => " = ",
            CounterOp.Fill => " fill ",
            _ => " by ",
        };
        string delta = PrintN(ctr.Delta);

        // 实体级
        if (ctr.Target != null)
        {
            string tgt = PrintT(ctr.Target);
            if (name == "spellboost" && ctr.Op == CounterOp.Add)
                return "spellboost " + tgt + " by " + delta;
            if (name == "countdown" && ctr.Op == CounterOp.Add)
            {
                // 负增量 = 推进（加速归零），正增量 = 推迟。
                if (delta.StartsWith("-"))
                    return "advance_countdown " + tgt + " by " + delta.Substring(1);
                return "delay_countdown " + tgt + " by " + delta;
            }
            return "counter " + name + " on " + tgt + opWord + delta;
        }

        // 主战者级
        string owner = ctr.Owner != ZoneScope.Self ? " [owner=" + ctr.Owner + "]" : "";
        if (name == "spellboost" && ctr.Op == CounterOp.Add)
            return "spellboost self by " + delta + owner;

        // 内置资源给一等公民动词，省掉千篇一律的 counter xxx by N。
        // 只在 Add 且增量为纯常量时启用；动态表达式与 Set/Fill 仍走 counter。
        if (ctr.Op == CounterOp.Add && ctr.Owner == ZoneScope.Self && delta != null)
        {
            bool neg = delta.StartsWith("-");
            string mag = neg ? delta.Substring(1) : delta;
            bool isConst = mag.Length > 0 && mag.All(char.IsDigit);
            if (isConst)
            {
                switch (name)
                {
                    case "pp": return neg ? "spend_pp " + mag : "recover_pp " + mag;
                    case "max_pp": return "gain_max_pp " + mag;
                    case "ep": return "recover_ep " + mag;
                    case "sep": return "recover_sep " + mag;
                    case "earth_sigil": return "gain_earth_sigil " + mag;
                    case "clues": return "gain_clues " + mag;
                    case "shadow": return "gain_shadows " + mag;
                    case "faith": return neg ? "spend_faith " + mag : "gain_faith " + mag;
                }
            }
        }

        return "counter " + name + opWord + delta + owner;
    }

    private static string FormatModifyCost(ModifyCostNode mc)
    {
        string tgt = PrintT(mc.Target);
        string val = PrintN(mc.Value);
        string cmd = mc.Op switch
        {
            CostModOp.Set => "set_cost " + tgt + " to " + val,
            CostModOp.Halve => "halve_cost " + tgt,
            _ => "modify_cost " + tgt + " by " + val,
        };
        return cmd + DurSuffix(mc.Duration);
    }


    // ============================================================
    // 目标 / 数值 / 布尔 的规范化打印（204 号文档要求自给自足）
    //
    // 这三个子树原先直接调 AstEnglishPrinter —— 那意味着 PyAST 的语法
    // 随时会被旧打印器的改动带偏，而且解析端不得不去对齐一份自己没有
    // 定义权的格式。搬进来之后，打印器与解析器共用同一份语法约定。
    // ============================================================

    private static string OfSuffix(TargetSpec of)
    {
        if (of == null) return "";
        // 隐藏「当前上下文的默认主语」：
        //   where 内 -> $item（被筛的那张）；where 外 -> $self（本卡）。
        // 人类因此既不用写 ($item) 也不用写 ($self)，语义由上下文唯一确定。
        string defaultSlot = _inWherePrint ? TargetSpec.SlotItem : TargetSpec.SlotSelf;
        if (of.SourceKind == TargetSourceKind.Slot && of.Slot == defaultSlot) return "";
        if (of.SourceKind == TargetSourceKind.Slot && of.Slot == TargetSpec.SlotItem) return "";
        return "(" + PrintT(of) + ")";
    }



    /// <summary>
    /// 从 Where 里提取可内联的卡种与 other 修饰词。
    ///
    /// 影之诗 90% 的目标都是「随从」或「其他随从」，写成
    ///   self:board All where (other than itself and is Follower)
    /// 又长又噪。这里把它们提权成
    ///   self:board<Follower> other All
    /// 剩下的条件若只有一条，连括号都省掉。
    /// </summary>
    private static BoolExpr ExtractInlineTargetModifiers(
        BoolExpr where, bool allowKindLift, out CardKind? kind, out bool isOther)
    {
        kind = null;
        isOther = false;
        if (where == null || where is ConstBoolExpr) return null;

        var items = where is AndExpr a ? a.Items.ToList() : new List<BoolExpr> { where };
        var kept = new List<BoolExpr>();

        foreach (var it in items)
        {
            if (allowKindLift && !kind.HasValue && it is HasKindExpr hk
                && hk.Kind is CardKind.Follower or CardKind.Spell or CardKind.Amulet
                && (hk.Of == null || hk.Of.SourceKind != TargetSourceKind.Zone))
            {
                kind = hk.Kind;
                continue;
            }
            // 编译器会把 "other than itself" 具体化成 $self（而不是保留
            // $excludeTriggerOrSelf 这个待定值），两种都要认。
            if (!isOther && it is NotSameEntityExpr nse
                && (nse.ExcludeSlot == TargetSpec.SlotExcludeTriggerOrSelf
                    || nse.ExcludeSlot == TargetSpec.SlotSelf)
                && (nse.Of == null || nse.Of.SourceKind != TargetSourceKind.Zone))
            {
                isOther = true;
                continue;
            }
            kept.Add(it);
        }

        if (kept.Count == 0) return null;
        return kept.Count == 1 ? kept[0] : new AndExpr(kept);
    }

    private static string PrintT(TargetSpec t)
    {
        if (t == null) return "null";

        // 槽位引用就是 $self / $selected / $created 本身，
        // 再套一层 slot: 前缀、再拖一句 All n=1，纯属噪音。
        if (t.SourceKind == TargetSourceKind.Slot)
        {
            var s = new StringBuilder(t.Slot ?? TargetSpec.SlotSelf);
            if (t.Where != null && t.Where is not ConstBoolExpr) s.Append(" where " + PrintB(t.Where));
            return s.ToString();
        }

        // 先尝试把 <Kind> 与 other 从 Where 里提权出来
        var remWhere = ExtractInlineTargetModifiers(t.Where,
            t.SourceKind == TargetSourceKind.Zone, out var inlineKind, out var inlineOther);

        var sb = new StringBuilder();
        switch (t.SourceKind)
        {
            case TargetSourceKind.Slot:
                sb.Append(t.Slot ?? TargetSpec.SlotSelf);
                break;
            case TargetSourceKind.CardId:
                // 必须打 Owner 与 Zone：不然 "hand" / "deck" / "hand and deck"
                // 三种写法的 AST 打印完全一样，审计时分不出编译器到底取了哪个区域。
                // 卡名与卡号都要打：CardId==0 时运行期会退化成「任意卡」
                // （VmEval 的历史池分支写的是 `t.CardId == 0 || …`），
                // 只打卡名会让 Obsessed Test Subject 的「同名卡 5 张」变成「任意随从 5 张」。
                // 卡名必须加引号：名字里可能含 where / is / and 这类词法关键字
                // （"Where Is My Mind"），裸写会把词法器搞崩。
                // 具名卡统一只写卡号：card:#12345678@self:board
                // 卡名可能含 where / is / and 这类词法关键字，加引号又牵出转义与
                // 可选 #id 的组合问题；卡号唯一且由 DIY 编辑器自动填，人类不必手写。
                sb.Append("card:#" + t.CardId);
                sb.Append("@" + t.Owner.ToString().ToLowerInvariant()
                          + ":" + t.Zone.ToString().ToLowerInvariant());
                // history 由下面的统一出口打印一次即可。
                // 这里再打一次会变成 "history=EnteredThisMatch history=EnteredThisMatch"，
                // 审计时看不出是不是重复限定。
                break;
            default:
                sb.Append(t.Owner.ToString().ToLowerInvariant() + ":" + t.Zone.ToString().ToLowerInvariant());
                // 卡种提权成尖括号泛型标签：self:board<Follower>
                if (inlineKind.HasValue) sb.Append('<').Append(inlineKind.Value).Append('>');
                break;
        }

        // other 提权成修饰词，紧跟区域之后
        if (inlineOther) sb.Append(" other");

        if (t.History != HistoryScope.None) sb.Append(" history=" + t.History);
        if (t.IncludeLeader) sb.Append(" +leader");
        if (t.DistinctByName) sb.Append(" distinct");
        // "Banish all duplicates"：只处理重复份。不打印的话这个属性在 AST 里完全不可见，
        // 排查「为什么只放逐了一张」时没有任何线索。
        if (t.DuplicatesOnly) sb.Append(" duplicates");
        if (t.Sort != SortKey.None) sb.Append(" sort=" + t.Sort + (t.SortDescending ? "-desc" : ""));
        if (t.TopN > 0) sb.Append(" topN=" + t.TopN);

        // All 不需要数量；只有 Choose / Random / First 才印 (n)。
        // 主战者只有 1 个，再跟一个 All 纯属噪音 —— 省略它（解析端默认 All）。
        if (t.Mode == SelectMode.All)
        {
            if (!(t.Zone == ZoneRef.Leader && t.Sort == SortKey.None && !t.IncludeLeader))
                sb.Append(" All");
        }
        else
            sb.Append(' ').Append(t.Mode).Append('(').Append(PrintN(t.Count ?? NumExpr.Const(1))).Append(')');

        if (remWhere != null && remWhere is not ConstBoolExpr)
        {
            bool prev = _inWherePrint;
            _inWherePrint = true;
            try { sb.Append(" where " + PrintB(remWhere)); }
            finally { _inWherePrint = prev; }
        }
        return sb.ToString();
    }


    private static string PrintN(NumExpr e)
    {
        if (e == null) return "null";
        return e.Op switch
        {
            NumOp.Const => e.Value.ToString(),
            // 锁定随机数必须打出上下界与变量槽，否则两边读回来的
            // rand(2,10,X) 全变成同一个 "randomrange"，往返对不上。
            NumOp.RandomRange => "rand(" + e.Value + ","
                + (e.Args != null && e.Args.Count > 0 ? PrintN(e.Args[0]) : e.Value.ToString())
                + ")#" + e.RollId,
            // 显式取值玩家必须打印出来：否则两侧读同一个计数器名的条件
            // （"both players have N max play points"）打印成完全相同的两半，
            // 验收时看不出它到底只判了一侧还是判了两侧。
            NumOp.Counter => "ctr:" + e.Counter.Name + CounterOwnerSuffix(e),
            NumOp.Property => "prop:" + e.Stat + (e.Of != null ? "(" + PrintT(e.Of) + ")" : ""),
            NumOp.Count => "count(" + PrintT(e.Of) + ")",
            // 必须打印 Stat，否则 sum(攻击) 与 sum(防御) 看起来完全一样。
            NumOp.Sum => "sum:" + e.Stat + "(" + PrintT(e.Of) + ")",
            NumOp.Arith => PrintArith(e),
            NumOp.Cond => "(if " + PrintB(e.Cond) + " then " + PrintN(e.Then) + " else " + PrintN(e.Else) + ")",
            NumOp.Clamp => "clamp(" + PrintN(e.Args[0]) + "," + PrintN(e.Args[1]) + "," + PrintN(e.Args[2]) + ")",
            NumOp.Extreme => (e.TakeMax ? "max(" : "min(") + string.Join(",", e.Args.Select(PrintN)) + ")",
            NumOp.SplitOf => "split:" + e.Counter.Name + "#" + e.Value,
            NumOp.DistinctCount => "distinct(" + PrintT(e.Of) + ")",
            NumOp.MaxGroupSize => "maxgroup(" + PrintT(e.Of) + ")",
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
            return "-" + PrintN(e.Args[1]);
        var op = e.Arith switch
        {
            ArithOp.Add => " + ",
            ArithOp.Sub => " - ",
            ArithOp.Mul => " * ",
            ArithOp.Div => " / ",
            ArithOp.Mod => " % ",
            _ => " + ",
        };
        return "(" + string.Join(op, e.Args.Select(PrintN)) + ")";
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


    /// <summary>归属方的人读写法（供条件表达式打印用）。</summary>

    private static string OwnerWord(Core.ZoneScope o) => o switch
    {
        Core.ZoneScope.Self => "your",
        Core.ZoneScope.Opponent => "enemy",
        _ => "either",
    };



    /// <summary>
    /// 把「计数器比较」翻译成游戏术语。
    ///
    /// 人类不该被要求记住 Overflow 是 ctr:max_pp >= 7、
    /// 奥义是 ctr:skybound_gauge >= 10 这种编译器内部约定。
    /// 只在阈值精确匹配时才翻译，其余情况原样保留 —— 名词化不能改变语义。
    /// </summary>
    private static string TermForCompare(CompareExpr cmp)
    {
        if (cmp.Left is not NumExpr { Op: NumOp.Counter } l) return null;
        if (cmp.Right is not NumExpr { Op: NumOp.Const } r) return null;
        if (cmp.Op != CompareOp.Ge) return null;
        string n = l.Counter.Name ?? "";
        return (n, r.Value) switch
        {
            ("max_pp", 7) => "overflow",
            ("wrath", 7) => "wrath",
            ("skybound_gauge", 10) => "skybound_art",
            ("skybound_gauge", 15) => "super_skybound_art",
            _ => null,
        };
    }

    private static string PrintB(BoolExpr e)
    {
        if (e == null) return "null";
        return e switch
        {
            ConstBoolExpr c => c.Value ? "true" : "false",
            CompareExpr cmp => TermForCompare(cmp) ?? (PrintN(cmp.Left) + " " + CmpWord(cmp.Op) + " " + PrintN(cmp.Right)),
            // Of 必须打出来：能力级条件判的是 $self，where 里判的是 $item。
            // 不打它，「本卡是否已进化」与「被筛的那张是否已进化」打印完全一样，
            // 而前者在触发时作用域里只有 $self，写成 $item 会恒为 false。
            HasStateExpr hs => "is " + hs.State + OfSuffix(hs.Of),
            HasKindExpr hk => "is " + hk.Kind + OfSuffix(hk.Of),
            // Of 必须打出来：能力级条件判的是 $self，where 里判的是 $item。
            // 不打它，「本卡是否有 ward」与「被筛的那张是否有 ward」打印完全一样，
            // 而前者在触发时作用域里只有 $self，写成 $item 会恒为 false。
            HasKeywordExpr hk => "has " + hk.Keyword.Name + OfSuffix(hk.Of),
            HasTraitExpr ht => "has trait " + ht.Trait.Name + OfSuffix(ht.Of),
            HasPackExpr hp => "in pack " + hp.PackId,
            // CardId 与 CardName 都为空时，Of 是「参照槽位」——
            //   "Banish all enemy copies of it" 要印成 same name as $selected，
            //   而不是空洞的 "named "。
            // CardId 为 0 但 Of 指向某槽位 = 「同名卡」判定（同一张卡的副本），
            // 这时打 same name as ...；否则是「按卡号指定名字」。
            // 注意：CardName 非空但 CardId 为 0 的历史数据也要走 same name 分支，
            // 否则会打成裸的 "named"，解析端根本读不出它指谁。
            HasCardNameExpr hcn => (hcn.CardId == 0 && hcn.Of != null)
                ? "same name as " + PrintT(hcn.Of)
                : "named #" + hcn.CardId,
            HasClanExpr hc => hc.IsSelfClan
                ? "has clan $self"
                : (hc.Clans != null && hc.Clans.Count > 0
                    ? "has clan " + string.Join("|", hc.Clans.Select(x => x.Name))
                    : "has clan " + hc.Clan.Name),
            // 排除的是「某槽位里的那个实体」，不是本卡自己。
            // 一律印 other than itself 会让 "other than it"（排除被使用的那张卡）
            // 在 AST 上与 "other than this follower" 完全无法区分。
            NotSameEntityExpr nse => nse.ExcludeSlot == TargetSpec.SlotTrigger
                ? "other than $trigger"
                : nse.ExcludeSlot == TargetSpec.SlotOpposing
                    ? "other than $opposing"
                    : TargetSpec.TryParseChooseSlotIndex(nse.ExcludeSlot, out _)
                        ? "other than " + nse.ExcludeSlot
                        : "other than itself",
            IsPreviousExpr ip => "is selected",
            // 牌库规则条件。缺这条会落进默认分支，打印成裸的 "deckruleexpr" ——
            // Rule 与 Owner 两个字段全部丢失，审计时只能靠反射挖对象值才能判断
            // 它查的到底是「无重复」「全偶数费」还是「全奇数费」。
            DeckRuleExpr dr => dr.Rule switch
            {
                DeckRule.NoDuplicates => "no duplicates in " + OwnerWord(dr.Owner) + " deck",
                DeckRule.EvenCostOnly => "only even-cost cards in " + OwnerWord(dr.Owner) + " deck",
                DeckRule.OddCostOnly => "only odd-cost cards in " + OwnerWord(dr.Owner) + " deck",
                _ => "deck rule " + dr.Rule,
            },
            // "If you selected an allied amulet" —— 对 $selected 槽位做存在性检验。
            // 缺这条会落进默认分支，打印成裸的 "existsexpr"，
            // AST 上完全看不出它在校验什么。
            ExistsExpr ex => PrintExists(ex),
            AndExpr a => "(" + string.Join(" and ", a.Items.Select(PrintB)) + ")",
            OrExpr o => "(" + string.Join(" or ", o.Items.Select(PrintB)) + ")",
            NotExpr n => "not " + PrintB(n.Inner),
            _ => e.GetType().Name.ToLowerInvariant(),
        };
    }


    private static string PrintExists(ExistsExpr ex)
    {
        var t = ex.Target;
        if (t == null) return "exists";
        string who = t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelected
            ? "$selected" : PrintT(t);
        string cond = (t.Where != null && t.Where is not ConstBoolExpr)
            ? " where " + PrintB(t.Where) : "";
        return "exists(" + who + cond + ")";
    }
}

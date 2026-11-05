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
            sb.Append(card.FuseMaterialFilter == null ? "any" : AstEnglishPrinter.PrintTarget(card.FuseMaterialFilter));
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
        if (t.SubjectFilter != null) sb.Append(" on(").Append(AstEnglishPrinter.PrintTarget(t.SubjectFilter)).Append(')');
        if (t.SecondaryFilter != null) sb.Append(" vs(").Append(AstEnglishPrinter.PrintTarget(t.SecondaryFilter)).Append(')');

        if (a.Costs is { Count: > 0 })
        {
            var costs = a.Costs.Select(c =>
            {
                string name = !string.IsNullOrEmpty(c.Label)
                    ? c.Label.ToLowerInvariant().Replace(' ', '_')
                    : c.Kind.ToString().ToLowerInvariant();
                return name + "(" + AstEnglishPrinter.PrintNum(c.Amount) + ")";
            });
            sb.Append(" [").Append(string.Join(", ", costs)).Append(']');
        }

        if (a.Condition != null && a.Condition is not ConstBoolExpr { Value: true })
            sb.Append(" if ").Append(AstEnglishPrinter.PrintBool(a.Condition));

        return sb.ToString();
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
            sb.Append(Pad(indent)).Append("@if ").Append(AstEnglishPrinter.PrintBool(n.If)).Append(":\n");
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
                sb.Append(p).Append("select ").Append(AstEnglishPrinter.PrintTarget(s.Target));
                if (!string.IsNullOrEmpty(s.OutSlot) && s.OutSlot != TargetSpec.SlotSelected)
                    sb.Append(" -> ").Append(s.OutSlot);
                if (s.AppendToSlot) sb.Append(" +append");
                break;

            case DealDamageNode d:
                sb.Append(p).Append("deal ").Append(AstEnglishPrinter.PrintNum(d.Amount))
                  .Append(" damage to ").Append(AstEnglishPrinter.PrintTarget(d.Target));
                if (d.IsSplit) sb.Append(" split");
                if (!d.IsAbility) sb.Append(" combat");
                break;

            case RestoreLifeNode r:
                sb.Append(p).Append(r.FullRestore
                    ? "fully_restore " + AstEnglishPrinter.PrintTarget(r.Target)
                    : "restore " + AstEnglishPrinter.PrintNum(r.Amount) + " life to " + AstEnglishPrinter.PrintTarget(r.Target));
                break;

            case ModifyStatsNode m:
            {
                string atk = m.Attack == null ? "_" : FormatSignedNum(m.Attack, m.Op == StatModOp.Add);
                string life = m.Life == null ? "_" : FormatSignedNum(m.Life, m.Op == StatModOp.Add);
                string statCmd = m.Op switch
                {
                    StatModOp.Swap => "swap_stats " + AstEnglishPrinter.PrintTarget(m.Target),
                    StatModOp.Set => "set_stats " + AstEnglishPrinter.PrintTarget(m.Target) + " to " + atk + "/" + life,
                    _ => "buff " + AstEnglishPrinter.PrintTarget(m.Target) + " " + atk + "/" + life,
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
                    KeywordOp.SilenceAll => "silence " + AstEnglishPrinter.PrintTarget(k.Target),
                    KeywordOp.Remove => "remove_keyword " + k.Keyword.Name + " from " + AstEnglishPrinter.PrintTarget(k.Target),
                    _ => "grant_keyword " + k.Keyword.Name
                         + (k.Value != null ? "(" + AstEnglishPrinter.PrintNum(k.Value) + ")" : "")
                         + " to " + AstEnglishPrinter.PrintTarget(k.Target) + DurSuffix(k.Duration),
                };
                sb.Append(p).Append(kwCmd);
                break;
            }

            case GrantAbilityNode g:
                sb.Append(p).Append("grant_ability to ").Append(AstEnglishPrinter.PrintTarget(g.Target))
                  .Append(DurSuffix(g.Duration)).Append(':');
                if (g.Granted == null) sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
                else PrintAbility(g.Granted, sb, indent + 1);
                break;

            case GrantCounterAbilityNode gc:
                sb.Append(p).Append("grant_counter_ability to ").Append(gc.Counter.Name);
                if (gc.Owner != ZoneScope.Self) sb.Append(" owner=").Append(gc.Owner);
                sb.Append(':');
                if (gc.Granted == null) sb.Append('\n').Append(Pad(indent + 1)).Append("pass");
                else PrintAbility(gc.Granted, sb, indent + 1);
                break;

            case ConditionalNode cn:
                sb.Append(p).Append("if ").Append(AstEnglishPrinter.PrintBool(cn.Condition)).Append(':');
                PrintBlockBody(cn.Then, sb, indent + 1);
                if (cn.Else is { Count: > 0 })
                {
                    sb.Append('\n').Append(p).Append("else:");
                    PrintBlockBody(cn.Else, sb, indent + 1);
                }
                break;

            case LoopNode lp:
            {
                string loopHead = lp.Kind switch
                {
                    LoopKind.ForEach => "for_each in " + AstEnglishPrinter.PrintTarget(lp.Collection),
                    LoopKind.RepeatUntil => "repeat_until " + AstEnglishPrinter.PrintBool(lp.Until),
                    _ => "repeat " + AstEnglishPrinter.PrintNum(lp.Count) + " times",
                };
                if (lp.MaxIterations != 30) loopHead += " (max=" + lp.MaxIterations + ")";
                sb.Append(p).Append(loopHead).Append(':');
                PrintBlockBody(lp.Body, sb, indent + 1);
                break;
            }

            case BranchModeNode bm:
            {
                sb.Append(p).Append("modes ").Append(bm.Kind)
                  .Append("(pick=").Append(AstEnglishPrinter.PrintNum(bm.PickCount)).Append(')');
                if (bm.GrantTarget != null) sb.Append(" grant_to=").Append(AstEnglishPrinter.PrintTarget(bm.GrantTarget));
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
                  .Append(AstEnglishPrinter.PrintTarget(ev.Target));
                break;

            case TransformNode tr:
                sb.Append(p).Append("transform ").Append(AstEnglishPrinter.PrintTarget(tr.Target)).Append(" into ");
                if (tr.CopySource != null)
                    sb.Append("copy of ").Append(AstEnglishPrinter.PrintTarget(tr.CopySource));
                else
                    sb.Append('"').Append(tr.IntoCardName).Append("\" id=").Append(tr.IntoCardId);
                break;

            case InvokeAbilityNode ia:
                sb.Append(p).Append("invoke_ability ").Append(ia.Event).Append(" on ")
                  .Append(AstEnglishPrinter.PrintTarget(ia.Target));
                break;

            case DamageModifierNode dm:
                sb.Append(p).Append("damage_modifier ").Append(dm.Kind)
                  .Append(" amount=").Append(AstEnglishPrinter.PrintNum(dm.Amount))
                  .Append(" mask=").Append(dm.SourceMask)
                  .Append(dm.IsReduction ? " reduce" : " increase")
                  .Append(" on ").Append(AstEnglishPrinter.PrintTarget(dm.Target))
                  .Append(DurSuffix(dm.Duration));
                break;

            case RevealCardNode rv:
                sb.Append(p).Append("reveal ").Append(AstEnglishPrinter.PrintTarget(rv.Target));
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
                    sb.Append('(').Append(string.Join(", ", mac.Args.Select(AstEnglishPrinter.PrintNum))).Append(')');
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
        string s = AstEnglishPrinter.PrintNum(e);
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
            string cnt = t.Count != null ? AstEnglishPrinter.PrintNum(t.Count) : "1";
            return "draw " + cnt + owner + slotSuffix;
        }
        // 2. 定向检索
        if (mv.ToZone == ZoneRef.Hand && t.SourceKind == TargetSourceKind.Zone && t.Zone == ZoneRef.Deck)
            return "tutor " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;
        // 3. 瞬念召唤自身
        if (mv.ToZone == ZoneRef.Board && t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelf && !mv.IsDestroy)
            return "invoke_self" + slotSuffix;
        // 4. 破坏
        if (mv.ToZone == ZoneRef.Cemetery && mv.IsDestroy)
            return "destroy " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;
        // 5. 舍弃手牌
        if (mv.ToZone == ZoneRef.Cemetery && !mv.IsDestroy
            && t.SourceKind == TargetSourceKind.Zone && t.Zone == ZoneRef.Hand)
            return "discard " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;
        // 6. 消失
        if (mv.ToZone == ZoneRef.Banished && !mv.IsDestroy)
            return "banish " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;
        // 7. 回手
        if (mv.ToZone == ZoneRef.Hand && !mv.IsDestroy)
            return "bounce " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;
        // 8. 洗回牌库
        if (mv.ToZone == ZoneRef.Deck && !mv.IsDestroy)
            return "return_to_deck " + AstEnglishPrinter.PrintTarget(t) + slotSuffix;

        // 9. 兜底通用移区
        return "move " + AstEnglishPrinter.PrintTarget(t) + " to " + mv.ToZone
             + (mv.IsDestroy ? " destroy" : "") + slotSuffix;
    }

    /// <summary>SpawnCard 语义升级：reanimate / gain_crest / discover / chaos_discover / summon / add_to_hand / spawn_copy。</summary>
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
            return "reanimate(" + AstEnglishPrinter.PrintNum(cap) + ")" + dep + slotSuffix;
        }

        // 2. 获得纹章
        if (sp.IsCrest)
            return "gain_crest \"" + sp.CardName + "\""
                 + (sp.CardId > 0 ? " id=" + sp.CardId : "") + ownerSuffix + slotSuffix;

        // 3. 发现 / 混沌发现 / 池随机生成
        if (sp.SourceKind == CardSourceKind.PoolQuery && sp.CopyFrom != null)
        {
            bool chaos = sp.CopyFrom.Zone == ZoneRef.GlobalPool;
            bool choose = sp.CopyFrom.Mode == SelectMode.Choose;
            string verb = choose ? (chaos ? "chaos_discover" : "discover") : "pool_spawn";
            string offer = choose && sp.OfferCount != 3 ? " offer=" + sp.OfferCount : "";
            string toZone = sp.ToZone != ZoneRef.Hand ? " to=" + sp.ToZone : "";
            return verb + " " + AstEnglishPrinter.PrintNum(sp.Count) + " from "
                 + AstEnglishPrinter.PrintTarget(sp.CopyFrom) + offer + toZone + ownerSuffix + slotSuffix;
        }

        // 4. 复制体
        if (sp.SourceKind == CardSourceKind.CopyOfTarget)
        {
            string exact = sp.IsExactCopy ? "exact_copy" : "copy";
            string dep = sp.AddDepartedTrait ? " +departed" : "";
            return "spawn_" + exact + " " + AstEnglishPrinter.PrintNum(sp.Count) + " of "
                 + AstEnglishPrinter.PrintTarget(sp.CopyFrom) + " to " + sp.ToZone
                 + ownerSuffix + dep + slotSuffix;
        }

        // 5. 延续上一次生成
        if (sp.SourceKind == CardSourceKind.CopyOfPrevious)
            return "spawn_previous " + AstEnglishPrinter.PrintNum(sp.Count) + " to " + sp.ToZone
                 + ownerSuffix + slotSuffix;

        // 6. 常规具名召唤 / 加手牌
        string idTag = sp.CardId > 0 ? " id=" + sp.CardId : "";
        string depTag = sp.AddDepartedTrait ? " +departed" : "";
        if (sp.ToZone == ZoneRef.Board)
            return "summon " + AstEnglishPrinter.PrintNum(sp.Count) + " \"" + sp.CardName + "\"" + idTag + ownerSuffix + depTag + slotSuffix;
        if (sp.ToZone == ZoneRef.Hand)
            return "add_to_hand " + AstEnglishPrinter.PrintNum(sp.Count) + " \"" + sp.CardName + "\"" + idTag + ownerSuffix + depTag + slotSuffix;

        return "spawn " + AstEnglishPrinter.PrintNum(sp.Count) + " \"" + sp.CardName + "\"" + idTag
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
        string delta = AstEnglishPrinter.PrintNum(ctr.Delta);

        // 实体级
        if (ctr.Target != null)
        {
            string tgt = AstEnglishPrinter.PrintTarget(ctr.Target);
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
        return "counter " + name + opWord + delta + owner;
    }

    private static string FormatModifyCost(ModifyCostNode mc)
    {
        string tgt = AstEnglishPrinter.PrintTarget(mc.Target);
        string val = AstEnglishPrinter.PrintNum(mc.Value);
        string cmd = mc.Op switch
        {
            CostModOp.Set => "set_cost " + tgt + " to " + val,
            CostModOp.Halve => "halve_cost " + tgt,
            _ => "modify_cost " + tgt + " by " + val,
        };
        return cmd + DurSuffix(mc.Duration);
    }
}

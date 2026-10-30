using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 受限自然语言渲染器（NaturalPrinter）
//
// 与 AstEnglishPrinter 的区别：
//   AstEnglishPrinter 输出的是**结构规范串**（"fanfare: deal 2 damage to slot:$selected"），
//     用来做机器比对，不追求可读。
//   NaturalPrinter 输出的是**受限自然语言**（"Fanfare: Deal 2 damage to an enemy follower."），
//     追求两点：人读得懂，且与卡面原文的措辞同构。
//
// 为什么要「受限」而不是「自由」：
//   自由渲染会引入解析器没有的词汇，双向比对立刻失效。
//   受限渲染只使用解析器认识的词形（术语表里的关键词 + 固定句式），
//   于是「渲染出来的句子」重新喂回编译器必须得到同一个 AST。
//   这条闭环才是设计 3 §六 想要的 Round-Trip。
//
// 输出三种形态：
//   PrintEnglish  → 规范英文（与卡面同构）
//   PrintChinese  → 简体中文（术语走 Glossary）
//   PrintBoth     → 两者并列，便于人工核对
// ============================================================================

public enum RenderLang { English, Chinese }

public sealed class NaturalPrinter
{
    private readonly Glossary _g;
    private readonly RenderLang _lang;

    public NaturalPrinter(Glossary glossary, RenderLang lang)
    {
        _g = glossary ?? Glossary.CreateDefault();
        _lang = lang;
    }

    // ---------- 入口 ----------

    public string PrintCard(IReadOnlyList<AbilityDef> abilities)
    {
        if (abilities == null || abilities.Count == 0) return "";
        var parts = abilities.Select(PrintAbility).Where(s => s.Length > 0);
        return string.Join(Sep(), parts);
    }

    public string PrintAbility(AbilityDef a)
    {
        if (a == null) return "";
        var sb = new StringBuilder();

        // PassiveAura + 授予自身关键词 = 裸关键词（"Ward" / "Storm"）。
        // 官方卡面就是这么写的，给它加上 "Fanfare:" 会把常驻光环变成入场曲。
        if (IsBareKeywordAbility(a, out var bareKw))
            return Word(bareKw) + Period();

        var head = Head(a.Trigger);
        var costs = PrintCosts(a.Costs);

        // 代价与条件本是同一件事的两种表达：Enhance (4) ↔ "if you have at least 4 play points"。
        // 两个都写，重新解析时会叠成两份代价 + 两份条件。
        // 只保留代价头，条件里同源的比较不再渲染。
        var costConds = new HashSet<string>(StringComparer.Ordinal);
        if (a.Costs != null)
            foreach (var cs in a.Costs)
            {
                var counter = CostCounterName(cs.Kind);
                if (counter != null) costConds.Add("cmp:ctr:" + counter + "cmp:Ge");
            }

        // 头与代价的拼接顺序必须符合编译器的语法：
        //   同时存在时写成 "Fanfare: Enhance (4): ..."，
        //   代价自带冒号，不能用 " - " —— 那是条件谓词的连接符。
        var headTrim = head.TrimEnd();
        bool headIsEngage = headTrim.Equals("Engage", StringComparison.OrdinalIgnoreCase);
        bool headIsClause = headTrim.EndsWith(",") || headTrim.EndsWith("，");

        if (headIsClause)
        {
            // 事件从句已经以逗号结尾，不能再加冒号。
            sb.Append(head);
        }
        else if (headIsEngage)
        {
            // Engage 的数值要拼进头里（"Engage (1):"），先不写冒号。
            sb.Append(headTrim);
        }
        else if (head.Length > 0)
        {
            sb.Append(head).Append(Colon());
        }
        // 能力头已含代价语义时（"Engage:"），不能再写 "Engage (N):"，
        // 否则重解析会得到两份代价。
        // 能力头是 "Engage" 时，代价数值仍必须保留：写成 "Engage (1):"
        // 并不会重解析成两份代价（实测确认），丢掉数值反而会把 1 变成 0。
        if (costs.Length > 0)
        {
            if (headIsEngage)
                sb.Append(Space()).Append("(" + PrintCostAmount(a.Costs) + ")").Append(Colon());
            else
                sb.Append(costs).Append(Colon());
        }

        // 条件：恒真不渲染；与代价同源的比较也不渲染（否则重解析时叠成两份）
        if (a.Condition != null && a.Condition is not ConstBoolExpr)
        {
            var cond = Dedupe(a.Condition);
            cond = DropCostDuplicates(cond, costConds);
            if (cond != null)
            {
                var condText = PrintBool(cond);
                if (!string.IsNullOrWhiteSpace(condText))
                    sb.Append(If()).Append(Space()).Append(condText).Append(Comma());
            }
        }

        // 动作序列：同一能力内的多个动作用 "and" 连接。
        // 官方写法："Draw all copies of X and give them Storm."
        // 若用句号分开，第二句不带能力头，重解析会归到 OnPlay，语义就变了。
        if (a.Body != null && a.Body.Count > 0)
        {
            // 剔除衍生的主战者节点：当同一 Body 里存在
            // 「同金额 + IncludeLeader + 非主战者区」的动作时，
            // 打主战者那一条是编译器自动追加的，重写就会重复。
            var bodyNodes = new List<EffectNode>();
            foreach (var node in a.Body)
            {
                if (node is DealDamageNode dmg
                    && dmg.Target != null
                    && (dmg.Target.Zone == ZoneRef.ExArea || dmg.Target.Zone == ZoneRef.Leader)
                    && dmg.Target.IncludeLeader
                    && a.Body.Any(o => o is DealDamageNode od
                                       && od.Target != null
                                       && od.Target.Zone != ZoneRef.ExArea
                                       && od.Target.Zone != ZoneRef.Leader
                                       && od.Target.IncludeLeader
                                       && od.Amount?.Value == dmg.Amount?.Value))
                    continue;
                bodyNodes.Add(node);
            }
            var parts = bodyNodes.Select(PrintNode).Where(s => s.Length > 0).ToList();
            if (parts.Count == 0) return sb.ToString().Trim();

            var sbBody = new System.Text.StringBuilder();
            for (int i = 0; i < parts.Count; i++)
            {
                var piece = parts[i];
                if (i > 0)
                {
                    // 前一句去掉句号，用 " and " 连上后一句（后一句首字母转小写）。
                    var prev = sbBody.ToString().TrimEnd();
                    if (prev.EndsWith(".")) prev = prev.Substring(0, prev.Length - 1);
                    sbBody.Clear();
                    sbBody.Append(prev);
                    piece = char.ToLowerInvariant(piece[0]) + piece.Substring(1);
                    sbBody.Append(Space()).Append(Word("and")).Append(Space()).Append(piece);
                    continue;
                }
                sbBody.Append(piece);
            }
            sb.Append(sbBody.ToString());
        }

        return sb.ToString().Trim();
    }

    // ---------- 触发头 ----------

    private BoolExpr SimplifyCondition(BoolExpr e)
    {
        if (e == null) return null;
        if (e is ConstBoolExpr cb) return cb.Value ? null : cb;
        if (e is AndExpr and)
        {
            var kept = and.Items.Select(SimplifyCondition).Where(x => x != null).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return BoolExpr.And(kept.ToArray());
        }
        return e;
    }

    /// <summary>
    /// 剔除与触发头重复的状态谓词。
    /// "Evolve:" 已经表达了「已进化」，再写 "if it is Evolved" 既冗余又不合语法。
    /// </summary>
    /// <summary>
    /// 去重（**不能**剔除）。
    ///
    /// 旧实现把与触发头重复的 HasStateExpr 直接扔掉，
    /// 但原 AST 里这个条件是真实存在的，打印出来就少了一个语义成分。
    /// 现在只做「幂等去重」：同一个谓词出现多次才合并，不同的一律保留。
    /// </summary>
    private static BoolExpr DropCostDuplicates(BoolExpr e, HashSet<string> costConds)
    {
        if (e == null || costConds.Count == 0) return e;
        if (e is CompareExpr cmp && cmp.Left != null && cmp.Left.Op == NumOp.Counter && cmp.Op == CompareOp.Ge)
        {
            if (costConds.Contains("cmp:ctr:" + cmp.Left.Counter.Name + "cmp:Ge")) return null;
            return e;
        }
        if (e is AndExpr and)
        {
            var kept = and.Items.Select(x => DropCostDuplicates(x, costConds)).Where(x => x != null).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return BoolExpr.And(kept.ToArray());
        }
        return e;
    }

    private static BoolExpr Dedupe(BoolExpr e)
    {
        if (e == null) return null;
        if (e is ConstBoolExpr cb) return cb.Value ? null : cb;
        if (e is AndExpr and)
        {
            var kept = new List<BoolExpr>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in and.Items)
            {
                var s = Dedupe(it);
                if (s == null) continue;
                var key = Key(s);
                if (!seen.Add(key)) continue;
                kept.Add(s);
            }
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return BoolExpr.And(kept.ToArray());
        }
        if (e is OrExpr orr)
        {
            var kept = new List<BoolExpr>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var it in orr.Items)
            {
                var s = Dedupe(it);
                if (s == null) continue;
                var key = Key(s);
                if (!seen.Add(key)) continue;
                kept.Add(s);
            }
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return BoolExpr.Or(kept.ToArray());
        }
        return e;
    }

    /// <summary>谓词的结构指纹，用于幂等去重。</summary>
    private static string Key(BoolExpr e)
    {
        switch (e)
        {
            case CompareExpr c:
                return "cmp:" + NumKey(c.Left) + c.Op + NumKey(c.Right);
            case HasStateExpr hs:
                return "state:" + hs.State;
            case HasKindExpr hk:
                return "kind:" + hk.Kind;
            case HasKeywordExpr hkw:
                return "kw:" + hkw.Keyword.Name;
            case HasTraitExpr ht:
                return "trait:" + ht.Trait.Name;
            case HasClanExpr hc:
                return "clan:" + hc.Clan.Name;
            case NotSameEntityExpr ns:
                return "notsame:" + ns.ExcludeSlot;
            case IsPreviousExpr ip:
                return "issel:" + ip.Slot;
            case DeckRuleExpr dr:
                return "deck:" + dr.Rule;
            default:
                return e.GetType().Name;
        }
    }

    private static string NumKey(NumExpr n)
    {
        if (n == null) return "0";
        if (n.Op == NumOp.Const) return n.Value.ToString();
        if (n.Op == NumOp.Counter) return "ctr:" + n.Counter.Name;
        if (n.Op == NumOp.Property) return "prop:" + n.Stat;
        return n.Op + "(" + string.Join(",", (n.Args ?? Array.Empty<NumExpr>()).Select(NumKey)) + ")";
    }

    // ========================================================================
    // 触发头分两类，这是官方卡面的固定写法：
    //
    //   能力头（大写 + 冒号）：Fanfare: / Evolve: / Last Words: / Strike: / Clash:
    //                On Spellboost: / Engage: / Countdown: ...
    //   事件从句（小写动词）：When/Whenever ... enters the field, ...
    //                ... leaves the field, ... / ... is damaged, ... / ... is banished, ...
    //
    // 早期实现把全部事件一律写成大写冒号形式，于是生造出
    // "Enter the Field:" / "Damaged:" 这种官方从不使用的写法，编译器自然不认。
    // ========================================================================
    private static bool IsBareKeywordAbility(AbilityDef a, out string keyword)
    {
        keyword = null;
        if (a == null) return false;
        if ((a.Trigger?.Event ?? CoreEvent.OnPlay) != CoreEvent.PassiveAura) return false;
        if (a.Body == null || a.Body.Count != 1) return false;
        if (a.Body[0] is not ModifyKeywordNode mk) return false;
        if (mk.Op != KeywordOp.Grant) return false;
        if (mk.Target == null || mk.Target.SourceKind != TargetSourceKind.Slot) return false;
        if (mk.Target.Slot != TargetSpec.SlotSelf) return false;
        keyword = mk.Keyword.Name;
        return true;
    }

    private string Head(TriggerSpec t)
    {
        var ev = t?.Event ?? CoreEvent.OnPlay;

        // ---------- 能力头：固定短语 + 冒号 ----------
        var abilityHead = ev switch
        {
            CoreEvent.OnPlay => "Fanfare",
            CoreEvent.OnEvolve => "Evolve",
            CoreEvent.OnSuperEvolve => "Super-Evolve",
            CoreEvent.OnDestroy => "Last Words",
            CoreEvent.OnAttack => "Strike",
            CoreEvent.OnClash => "Clash",
            CoreEvent.OnSpellboost => "On Spellboost",
            CoreEvent.OnEngage => "Engage",
            CoreEvent.OnFuse => "Fuse",
            CoreEvent.OnCountdownExpire => "Countdown",
            _ => "",
        };
        if (abilityHead.Length > 0) return Word(abilityHead);

        // ---------- 事件从句：作为条件被渲染到能力体前 ----------
        // 这类事件在卡面上写成 "... enters the field, <动作>"，
        // 主语与动词由 PrintEventClause 组装。
        var clause = EventClause(ev, t);
        if (clause != null) return clause;

        // 官方卡面不存在对应写法的事件（Healed / Summon / Transform / CounterChange / DeckOut）
        // 渲染成空，避免造出编译器不认的写法。
        return "";
    }

    /// <summary>
    /// 事件从句。返回 null 表示该事件在官方卡面里没有对应写法。
    /// </summary>
    private string EventClause(CoreEvent ev, TriggerSpec t)
    {
        var zh = _lang == RenderLang.Chinese;
        var subject = zh ? "此随从" : "this follower";

        return ev switch
        {
            CoreEvent.OnEnterField => (zh ? "当" : "When ") + subject
                                      + (zh ? "进入战场时" : " enters the field") + Comma(),
            CoreEvent.OnLeaveField => (zh ? "当" : "When ") + subject
                                      + (zh ? "离场时" : " leaves the field") + Comma(),
            CoreEvent.OnDamaged => (zh ? "当" : "When ") + subject
                                   + (zh ? "受到伤害时" : " is damaged") + Comma(),
            CoreEvent.OnBanish => (zh ? "当" : "When ") + subject
                                  + (zh ? "被消失时" : " is banished") + Comma(),
            CoreEvent.OnDiscard => (zh ? "当" : "When ") + subject
                                   + (zh ? "被舍弃时" : " is discarded") + Comma(),
            CoreEvent.OnDraw => (zh ? "当你抽牌时" : "Whenever you draw a card,") + Comma(),
            CoreEvent.TurnStart => zh ? "你的回合开始时" : "At the start of your turn,",
            CoreEvent.TurnEnd => zh ? "你的回合结束时" : "At the end of your turn,",
            CoreEvent.BattleStart => zh ? "对战开始时" : "At the start of the match,",
            CoreEvent.PassiveAura => "",
            _ => null,
        };
    }

    private string PrintCostAmount(IReadOnlyList<CostSpec> costs)
    {
        if (costs == null || costs.Count == 0) return "0";
        return PrintNum(costs[0].Amount);
    }

    private static string CostCounterName(CostKind k) => k switch
    {
        CostKind.UsePp => "pp",
        CostKind.Necromance => "shadow",
        CostKind.DestroyAlly => "earth_sigil",
        CostKind.TurnPlayCount => "combo",
        CostKind.MatchPlayCount => "match_play",
        _ => null,
    };

    private string PrintCosts(IReadOnlyList<CostSpec> costs)
    {
        if (costs == null || costs.Count == 0) return "";
        var parts = new List<string>();
        foreach (var c in costs)
        {
            var n = PrintNum(c.Amount);
            var s = c.Kind switch
            {
                CostKind.UsePp => c.Label != null && c.Label.Equals("enhance", StringComparison.OrdinalIgnoreCase)
                    ? Word("Enhance") + " (" + n + ")"
                    : c.Label != null && c.Label.Equals("engage", StringComparison.OrdinalIgnoreCase)
                        ? Word("Engage") + " (" + n + ")"
                        : Word("Enhance") + " (" + n + ")",
                CostKind.Necromance => Word("Necromancy") + " (" + n + ")",
                CostKind.DestroyAlly => Word("Earth Rite") + " (" + n + ")",
                CostKind.TurnPlayCount => Word("Combo") + " (" + n + ")",
                CostKind.MatchPlayCount => Word("Rally") + " (" + n + ")",
                CostKind.Countdown => Word("Countdown") + " (" + n + ")",
                _ => "",
            };
            if (s.Length > 0) parts.Add(s);
        }
        return string.Join(Space(), parts);
    }

    // ---------- 动作节点 ----------

    public string PrintNode(EffectNode n)
    {
        if (n == null) return "";
        return n switch
        {
            SelectTargetNode s => Select(s.Target),
            DealDamageNode d => Damage(d),
            RestoreLifeNode r => Restore(r),
            ModifyStatsNode m => ModStats(m),
            ModifyCostNode mc => ModCost(mc),
            ModifyKeywordNode k => ModKeyword(k),
            GrantAbilityNode g => Grant(g),
            SpawnCardNode sp => Spawn(sp),
            MoveZoneNode mv => Move(mv),
            TransformNode t => Transform(t),
            EvolveNode e => Evolve(e),
            ModifyCounterNode c => ModCounter(c),
            InvokeAbilityNode i => Invoke(i),
            ConditionalNode c => Conditional(c),
            LoopNode l => Loop(l),
            BranchModeNode b => Branch(b),
            ReplaceDeckNode r => ReplaceDeck(r),
            GrantCounterAbilityNode gc => GrantCounter(gc),
            MacroNode mac => Word(mac.Macro.Name),
            _ => "",
        };
    }

    private string Select(TargetSpec t)
        => Word("Select") + Space() + PrintTarget(t);

    private string Damage(DealDamageNode d)
    {
        // 主战者衍生节点的判定交给 PrintAbility 处理（它能看到整个 Body）。
        // 这里不能直接跳过，否则真正只有主战者目标的卡会渲染成空。
        var amt = PrintNum(d.Amount);
        var tgt = PrintTarget(d.Target);
        var splitWord = d.IsSplit ? Space() + Word("split between") : Space() + To();
        // IncludeLeader 不再在此追加 "and the enemy leader"：
        // 编译器会自动生成打主战者的动作，渲染器再写就重复。
        var tail = "";
        return Word("Deal") + Space() + amt + Space() + Word("Damage") + splitWord + Space() + tgt + tail + Period();
    }

    private string Restore(RestoreLifeNode r)
    {
        if (r.FullRestore)
            return Word("Fully restore") + Space() + Word("Defense") + Space() + To() + Space()
                   + PrintTarget(r.Target) + Period();
        return Word("Restore") + Space() + PrintNum(r.Amount) + Space() + Word("Defense") + Space()
               + To() + Space() + PrintTarget(r.Target) + Period();
    }

    private string ModStats(ModifyStatsNode m)
    {
        var sign = m.Op == StatModOp.Add ? "" : m.Op == StatModOp.Set ? "= " : "";
        var val = (m.Attack?.Op == NumOp.Const && m.Attack.Value >= 0 ? "+" : "")
                  + PrintNum(m.Attack) + "/"
                  + (m.Life?.Op == NumOp.Const && m.Life.Value >= 0 ? "+" : "")
                  + PrintNum(m.Life);
        return Word("Give") + Space() + PrintTarget(m.Target) + Space() + sign + val + Duration(m.Duration) + Period();
    }

    private string ModCost(ModifyCostNode mc)
    {
        var verb = mc.Op switch
        {
            CostModOp.Halve => Word("halve"),
            _ => Word("reduce"),
        };
        // 官方写法："reduce the cost of this card by 1"（cost 小写，带 the）
        var costWord = _lang == RenderLang.Chinese ? Word("Cost") : "the cost";
        if (mc.Op == CostModOp.Halve)
            return verb + Space() + costWord + Space() + Of() + Space() + PrintTarget(mc.Target) + Period();
        // 官方写法："reduce the cost of this card by 1"
        // 方向已由 reduce 表达，后面必须是正数。
        // 注意：Value 可能是 Arith(0, N) 而不是裸 Const，
        // 只看 Op == Const 会把整个表达式当成 0。
        var amtText = Math.Abs(ConstValue(mc.Value)).ToString();
        return verb + Space() + costWord + Space() + Of() + Space() + PrintTarget(mc.Target)
               + Space() + Word("by") + Space() + amtText + Duration(mc.Duration) + Period();
    }

    private string ModKeyword(ModifyKeywordNode k)
    {
        var kw = Word(k.Keyword.Name);
        var val = k.Value != null ? Space() + PrintNum(k.Value) : "";
        return k.Op switch
        {
            KeywordOp.Grant => Word("Give") + Space() + PrintTarget(k.Target) + Space() + kw + val + Duration(k.Duration) + Period(),
            KeywordOp.Remove => Word("Remove") + Space() + kw + Space() + From() + Space() + PrintTarget(k.Target) + Period(),
            _ => Word("Remove all abilities from") + Space() + PrintTarget(k.Target) + Period(),
        };
    }

    private string Grant(GrantAbilityNode g)
    {
        var inner = PrintAbility(g.Granted);
        return Word("Give") + Space() + PrintTarget(g.Target) + Space()
               + Quote(inner) + Duration(g.Duration) + Period();
    }

    private string GrantCounter(GrantCounterAbilityNode gc)
    {
        var inner = PrintAbility(gc.Granted);
        return Word("Give your") + Space() + Word(gc.Counter.Name) + Space() + Quote(inner) + Period();
    }

    private string Spawn(SpawnCardNode sp)
    {
        // 目的地已由动词本身表达：
        //   Summon   -&gt; 进战场（默认，不写目的地）
        //   Add ...  -&gt; 加手牌（只写 "to your hand"）
        // 写成 "Summon a Knight to Field" 会让编译器在目的地上卡住。
        var what = sp.SourceKind == CardSourceKind.CopyOfTarget
            ? Word("Exact Copy") + Space() + Of() + Space() + PrintTarget(sp.CopyFrom)
            : sp.SourceKind == CardSourceKind.CopyOfPrevious
                ? null   // 在下面单独处理："Summon 2 instead"
                : sp.CardName;

        // CopyOfPrevious 对应卡面 "Super-Evolve: Summon 2 instead."
        if (sp.SourceKind == CardSourceKind.CopyOfPrevious)
        {
            var n = PrintNum(sp.Count);
            return Word("summon") + Space() + n + Space() + Word("instead") + Period();
        }

        var count = (sp.Count != null && !(sp.Count.Op == NumOp.Const && sp.Count.Value == 1))
            ? PrintNum(sp.Count) + Space() + Word("copies of") + Space()
            : ArticleBefore(what) + Space();

        if (sp.ToZone == ZoneRef.Hand)
            return Word("add") + Space() + count + what + Space() + Word("to your hand") + Period();

        if (sp.ToZone == ZoneRef.CrestArea)
            return Word("gain") + Space() + Word("Crest") + Space() + what + Period();

        return Word("summon") + Space() + count + what + Period();
    }

    private string Move(MoveZoneNode mv)
    {
        // 抽牌：牌组 -> 手牌。
        // 官方写法："Draw 2 cards" / "Draw a follower" / "Draw all copies of Rusty"。
        // 目标上带的 CardId / CardName / Mode 必须全部体现到句子里，
        // 否则重解析会退化成普通抽牌。
        if (mv.ToZone == ZoneRef.Hand && mv.Target != null && mv.Target.Zone == ZoneRef.Deck)
            return DrawPhrase(mv.Target);

        // 其余移区：动词必须是单词且在前，宾语在后。
        var verb = mv.ToZone switch
        {
            ZoneRef.Cemetery => Word("destroy"),
            ZoneRef.Banished => Word("banish"),
            ZoneRef.Hand => Word("return"),
            ZoneRef.Deck => Word("return"),
            ZoneRef.Board => Word("summon"),
            _ => Word("return"),
        };
        return verb + Space() + PrintTarget(mv.Target) + DestinationPhrase(mv.ToZone) + Period();
    }

    /// <summary>
    /// 抽牌句。三种形态必须区分：
    ///   "Draw 2 cards"                     — 普通抽牌（Mode=First）
    ///   "Draw a follower"                  — 带卡种过滤（Mode=Random）
    ///   "Draw all copies of Rusty, ..."    — 具体卡 + All
    /// </summary>
    private string DrawPhrase(TargetSpec t)
    {
        var zh = _lang == RenderLang.Chinese;
        var cnt = PrintTargetCount(t);
        var plural = IsPlural(cnt);

        // a) 具体卡名（CardId 或 CardName）
        if (t.SourceKind == TargetSourceKind.CardId || !string.IsNullOrEmpty(t.CardName))
        {
            var name = string.IsNullOrEmpty(t.CardName) ? "#" + t.CardId : t.CardName;
            if (t.Mode == SelectMode.All)
                return Word("Draw") + Space() + Word("all copies of") + Space() + name + Period();
            return Word("Draw") + Space() + ArticleBefore(name) + Space() + name + Period();
        }

        // b) 带卡种 / 属性过滤的抽牌
        //    官方写法："Draw 2 Swordcraft followers"（注意这里是 All + n=2）
        var where = StripKindPredicates(t.Where);
        var kind = KindleOf(t);
        if ((t.Mode == SelectMode.Random || t.Mode == SelectMode.All) && (where != null || kind != null))
        {
            var noun = kind != null ? Word(kind) : Word("card");
            // 官方写法："Draw a follower"（小写）
            if (!zh && kind != null) noun = noun.ToLowerInvariant();
            if (plural && !zh) noun += "s";
            var q = TraitQualifier(t.Where);
            var qual = q != null ? Word(q) + Space() : "";
            var wpart = where != null ? Space() + With() + Space() + PrintBool(where) : "";
            var prefix = cnt == ArticleToken ? (zh ? "" : ArticleBefore(noun)) : cnt;
            var joiner = prefix.Length == 0 ? "" : Space();
            return Word("Draw") + Space() + prefix + joiner + qual + noun + wpart + Period();
        }

        // c) 普通抽牌
        var cw = Word("card");
        if (plural && !zh) cw += "s";
        var pfx = cnt == ArticleToken ? (zh ? "" : ArticleBefore(cw)) : cnt;
        var j = pfx.Length == 0 ? "" : Space();
        return Word("Draw") + Space() + pfx + j + cw + Period();
    }

    /// <summary>从目标的过滤条件里取出卡种名。</summary>
    private static string KindleOf(TargetSpec t)
    {
        if (t?.Where is HasKindExpr hk) return hk.Kind switch
        {
            CardKind.Follower => "Follower",
            CardKind.Spell => "Spell",
            CardKind.Amulet => "Amulet",
            _ => null,
        };
        if (t?.Where is AndExpr and)
            foreach (var it in and.Items)
            {
                var k = KindleOf(new TargetSpec { Where = it });
                if (k != null) return k;
            }
        return null;
    }

    private string DestinationPhrase(ZoneRef z)
    {
        if (_lang == RenderLang.Chinese)
        {
            return z switch
            {
                ZoneRef.Hand => "加入手牌",
                ZoneRef.Deck => "返回牌组",
                _ => "",
            };
        }
        return z switch
        {
            ZoneRef.Hand => " to your hand",
            ZoneRef.Deck => " to your deck",
            _ => "",
        };
    }

    private string Transform(TransformNode t)
    {
        var into = t.CopySource != null
            ? Word("Exact Copy") + Space() + Of() + Space() + PrintTarget(t.CopySource)
            : t.IntoCardName;
        return Word("Transform") + Space() + PrintTarget(t.Target) + Space() + Word("into") + Space() + into + Period();
    }

    private string Evolve(EvolveNode e)
        => (e.IsSuperEvolve ? Word("Super-evolve") : Word("Evolve")) + Space() + PrintTarget(e.Target) + Period();

    private string ModCounter(ModifyCounterNode c)
    {
        // 编译器词表里没有 Change；只有 gain / set / increase / reduce。
        // 渲染器只能用这些词，否则双向闭环断在动词上。
        // 负数增量官方写成 "reduce ... by N"，不写 "gain -N"。
        var isNeg = c.Delta != null && c.Delta.Op == NumOp.Const && c.Delta.Value < 0;
        var verb = c.Op switch
        {
            CounterOp.Set => Word("set"),
            CounterOp.Fill => Word("set"),
            _ => isNeg ? Word("reduce") : Word("gain"),
        };
        var who = c.Target != null ? Space() + Of() + Space() + PrintTarget(c.Target) : "";
        var cname = Word(ResourcePhrase(c.Counter.Name) ?? c.Counter.Name);
        var amt = c.Delta != null && c.Delta.Op == NumOp.Const ? Math.Abs(c.Delta.Value).ToString() : PrintNum(c.Delta);
        if (isNeg)
            return verb + Space() + cname + Space() + Word("by") + Space() + amt + who + Period();
        return verb + Space() + amt + Space() + cname + who + Period();
    }

    private string Invoke(InvokeAbilityNode i)
        => Word("invoke") + Space() + Word(EventName(i.Event)) + Space() + Of() + Space() + PrintTarget(i.Target) + Period();

    private string Conditional(ConditionalNode c)
    {
        var thenPart = string.Join(Space(), c.Then.Select(PrintNode).Where(s => s.Length > 0));
        var sb = new StringBuilder();
        sb.Append(If()).Append(Space()).Append(PrintBool(c.Condition)).Append(Comma()).Append(thenPart);
        if (c.Else != null && c.Else.Count > 0)
        {
            var elsePart = string.Join(Space(), c.Else.Select(PrintNode).Where(s => s.Length > 0));
            sb.Append(Space()).Append(Word("instead")).Append(Comma()).Append(elsePart);
        }
        return sb.ToString();
    }

    private string Loop(LoopNode l)
    {
        var body = string.Join(Space(), l.Body.Select(PrintNode).Where(s => s.Length > 0));
        return l.Kind switch
        {
            LoopKind.Times => Word("Do this") + Space() + PrintNum(l.Count) + Space()
                              + Word("time") + (l.Count?.Op == NumOp.Const && l.Count.Value == 1 ? "" : "s")
                              + Colon() + Quote(body) + Period(),
            LoopKind.ForEach => Word("For each") + Space() + PrintTarget(l.Collection) + Comma() + body,
            LoopKind.RepeatUntil => Word("Repeat") + Space() + Word("until") + Space()
                                    + PrintBool(l.Until) + Colon() + Quote(body) + Period(),
            _ => body,
        };
    }

    private string Branch(BranchModeNode b)
    {
        var kind = b.Kind switch
        {
            ModeDispatchKind.Choose => Word("Select a Mode to activate"),
            ModeDispatchKind.Random => Word("Activate") + Space() + PrintNum(b.PickCount) + Space() + Word("random abilities from the following"),
            ModeDispatchKind.Sequence => Word("Activate an ability in sequence from the following"),
            _ => Word("Select a Mode to activate"),
        };
        var opts = new List<string>();
        for (int i = 0; i < b.Options.Count; i++)
        {
            var body = string.Join(Space(), b.Options[i].Select(PrintNode).Where(s => s.Length > 0));
            opts.Add((i + 1) + Period() + Space() + body);
        }
        return kind + Period() + Space() + string.Join(Space(), opts);
    }

    private string ReplaceDeck(ReplaceDeckNode r)
        => Word("replace") + Space() + Word("Deck") + Space() + Word("with") + Space()
           + Word(r.DeckName) + Period();

    // ---------- 目标 ----------

    public string PrintTarget(TargetSpec t)
    {
        if (t == null) return "";

        // 主战者目标必须写成 "your leader" / "the enemy leader"，
        // 否则重解析时会退化成一个普通的场上目标。
        if (t.Zone == ZoneRef.ExArea || t.Zone == ZoneRef.Leader)
        {
            if (_lang == RenderLang.Chinese)
                return t.Owner == ZoneScope.Opponent ? "对手的主战者" : "你的主战者";
            return t.Owner == ZoneScope.Opponent ? "the enemy leader" : "your leader";
        }

        // 槽位来源：it / them / this follower / the created card
        if (t.SourceKind == TargetSourceKind.Slot)
        {
            var slotWord = t.Slot switch
            {
                TargetSpec.SlotSelf => Word("this follower"),
                TargetSpec.SlotSelected => Word("it"),
                TargetSpec.SlotCreated => Word("it"),
                TargetSpec.SlotTrigger => Word("the triggering card"),
                TargetSpec.SlotItem => Word("it"),
                TargetSpec.SlotOpposing => Word("the opposing follower"),
                _ => Word(t.Slot),
            };
            return slotWord;
        }

        if (t.SourceKind == TargetSourceKind.CardId)
        {
            // 官方写法：直接写卡名（"Draw all copies of Rusty, Luxcard Trickster"）
            if (!string.IsNullOrEmpty(t.CardName)) return t.CardName;
            return "#" + t.CardId;
        }

        // 区域来源：量词 + 所有者 + 区域 + 过滤
        var sb = new StringBuilder();
        var count = PrintTargetCount(t);
        var plural = IsPlural(count);

        var owner = t.Owner switch
        {
            ZoneScope.Self => Word("allied"),
            ZoneScope.Opponent => Word("enemy"),
            _ => "",
        };

        // 单数用冠词 a/an（按后一个词的首字母选），复数/全量用数字或 all
        if (count == ArticleToken)
        {
            var next = owner.Length > 0 ? owner : "";
            sb.Append(ArticleBefore(next)).Append(Space());
        }
        else
        {
            sb.Append(count).Append(Space());
        }

        // 修饰词（官方语序：量词 -> random -> owner -> 名词 -> 后置限定）
        if (t.Mode == SelectMode.Random) sb.Append(Word("random")).Append(Space());
        if (t.DistinctByName) sb.Append(Word("differently named")).Append(Space());

        // 所有者 + 形容词性限定（兵种/职业）+ 名词
        // 官方写法："an allied Golem follower" / "2 Artifact followers"
        // 不写 "an allied follower with Golem" —— 那种写法官方一次都没有，编译器也不收。
        sb.Append(owner).Append(Space());
        var qualifier = TraitQualifier(t.Where);
        if (qualifier != null) sb.Append(Word(qualifier)).Append(Space());
        var noun = ZoneNoun(t);
        sb.Append(noun);
        if (plural && _lang == RenderLang.English) sb.Append("s");



        // 区域短语必须在排序定语之前：
        // 官方写 "a random enemy follower on the field with the highest attack" 否则不合法。
        var zone = ZonePhrase(t);
        if (zone.Length > 0) sb.Append(Space()).Append(zone);

        // 排序定语放在区域之后：
        // "a random enemy follower on the field with the highest base cost"
        if (t.Sort != SortKey.None)
        {
            sb.Append(Space()).Append(Word("with the highest")).Append(Space()).Append(Word(PropertyName(t.Sort)));
        }

        // 过滤：卡种谓词已在名词里体现，不重复渲染
        var where = StripKindPredicates(t.Where);
        if (where != null)
        {
            // HasKeyword/HasTrait 类谓词自带 "with"，不能再加一层。
            var rendered = PrintBool(where);
            var already = rendered.StartsWith("with ", StringComparison.OrdinalIgnoreCase)
                          || rendered.StartsWith("，具有 ", StringComparison.Ordinal);
            sb.Append(Space()).Append(already ? rendered : With() + Space() + rendered);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 剔除卡种谓词（HasKindExpr）：它作为名词已经渲染过了，
    /// 再跟一个 "with it is a Follower" 是冗余。
    /// 剔完没有剩余时返回 null。
    /// </summary>
    private BoolExpr StripKindPredicates(BoolExpr e)
    {
        if (e == null) return null;
        if (e is ConstBoolExpr cb) return cb.Value ? null : cb;
        // 卡种、兵种、职业都已作为名词或定语渲染过，
        // 不能再写一遍 "with Golem"。
        if (e is HasKindExpr) return null;
        if (e is HasTraitExpr) return null;
        if (e is HasClanExpr) return null;
        if (e is AndExpr and)
        {
            var kept = and.Items.Select(StripKindPredicates).Where(x => x != null).ToList();
            if (kept.Count == 0) return null;
            if (kept.Count == 1) return kept[0];
            return BoolExpr.And(kept.ToArray());
        }
        return e;
    }

    private static string TraitQualifier(BoolExpr where)
    {
        if (where is HasTraitExpr ht) return ht.Trait.Name;
        if (where is HasClanExpr hc) return hc.Clan.Name;
        if (where is AndExpr and)
            foreach (var it in and.Items)
            {
                var q = TraitQualifier(it);
                if (q != null) return q;
            }
        return null;
    }

    private string ZoneNoun(TargetSpec t)
    {
        // 从卡种谓词里取名词；取不到时按区域给默认名词
        if (t.Where is HasKindExpr hk)
            return hk.Kind switch
            {
                CardKind.Follower => Word("Follower"),
                CardKind.Spell => Word("Spell"),
                CardKind.Amulet => Word("Amulet"),
                CardKind.Crest => Word("Crest"),
                CardKind.Leader => Word("Leader"),
                _ => Word("Card"),
            };
        // AndExpr 里可能有 HasKindExpr
        if (t.Where is AndExpr and)
        {
            foreach (var item in and.Items)
                if (item is HasKindExpr h2) return ZoneNoun(new TargetSpec { Where = h2 });
        }
        return t.Zone switch
        {
            ZoneRef.Hand => Word("Card"),
            ZoneRef.Deck => Word("Card"),
            ZoneRef.Cemetery => Word("Card"),
            ZoneRef.Leader or ZoneRef.ExArea => Word("Leader"),
            _ => Word("Follower"),
        };
    }

    private string ZonePhrase(TargetSpec t)
    {
        // 主战者不写区域
        if (t.Zone == ZoneRef.ExArea || t.Zone == ZoneRef.Leader)
            return "";
        return t.Zone switch
        {
            ZoneRef.Board => Word("on the field"),
            ZoneRef.Hand => Word("in your hand"),
            ZoneRef.Deck => Word("from your deck"),
            // 官方卡面从不写 "cemetery"（出现 0 次），统一写成 "destroyed this match"
            ZoneRef.Cemetery => Word("destroyed this match"),
            _ => "",
        };
    }

    private string PrintTargetCount(TargetSpec t)
    {
        // Mode.All = 全体，官方写 "all"。
        // 这个判断必须在 Count 之前，否则 All 会被渲染成单数冠词 "a"。
        if (t.Mode == SelectMode.All) return Word("all");
        var n = t.Count;
        if (n == null) return Word("all");
        if (n.Op == NumOp.Const)
        {
            if (n.Value == 1) return ArticleToken;   // 哨兵：由调用方换成 a/an 或省略
            return n.Value.ToString();
        }
        // 非恒定量词（X / Y / Z）
        return PrintNum(n);
    }

    private bool IsPlural(string count)
    {
        // 单数冠词 = 单数；其余（数字、all、X）= 复数
        return count != ArticleToken;
    }

    // ---------- 数值 ----------

    public string PrintNum(NumExpr e)
    {
        if (e == null) return "0";
        return e.Op switch
        {
            NumOp.Const => e.Value.ToString(),
            // 计数器必须用官方卡面的说法，不能直出内部名（pp / sep / ep / shadow）。
            // 官方写法：play points / super-evolution points / evolution point / shadows / defense
            NumOp.Counter => Word(ResourcePhrase(e.Counter.Name) ?? e.Counter.Name),
            NumOp.Property => Word(PropertyName(e.Stat)),
            NumOp.Count => Word("the number of") + Space() + PrintTarget(e.Of),
            NumOp.Sum => Word("the total") + Space() + Word(PropertyName(e.Stat)) + Space() + Of() + Space() + PrintTarget(e.Of),
            // 常见形态是 Sub(Const 0, X)，官方写成 "-X"，不写 "(0 minus X)"。
            NumOp.Arith => SimplifyArith(e),
            NumOp.Cond => "(" + If() + Space() + PrintBool(e.Cond) + Comma() + PrintNum(e.Then) + Space() + Word("instead") + Space() + PrintNum(e.Else) + ")",
            NumOp.Clamp => Word("clamp") + "(" + string.Join(", ", e.Args.Select(PrintNum)) + ")",
            NumOp.Extreme => (e.TakeMax ? Word("highest") : Word("lowest")) + "(" + string.Join(", ", e.Args.Select(PrintNum)) + ")",
            // SplitOf 表示 X / Y / Z 三分量。旧写法把索引拼在后面，产生 "X2"/"X3" 这种不存在的词。
            NumOp.SplitOf => Word(e.Value switch { 0 => "X", 1 => "Y", 2 => "Z", _ => "X" }),
            NumOp.DistinctCount => Word("the number of differently named") + Space() + PrintTarget(e.Of),
            NumOp.MaxGroupSize => Word("the maximum number of") + Space() + PrintTarget(e.Of),
            _ => e.Op.ToString(),
        };
    }

    private static int ConstValue(NumExpr e)
    {
        if (e == null) return 0;
        if (e.Op == NumOp.Const) return e.Value;
        if (e.Op == NumOp.Arith && e.Args != null && e.Args.Count == 2
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
            return e.Args[1].Op == NumOp.Const ? e.Args[1].Value : 1;
        return 1;
    }

    private string SimplifyArith(NumExpr e)
    {
        if (e.Arith == ArithOp.Sub && e.Args != null && e.Args.Count == 2
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
        {
            var rhs = PrintNum(e.Args[1]);
            return _lang == RenderLang.Chinese ? ("负" + rhs) : ("-" + rhs);
        }
        var sep = " " + ArithOpWord(e.Arith) + " ";
        return string.Join(sep, e.Args.Select(PrintNum));
    }

    private string ArithOpWord(ArithOp op)
    {
        if (_lang == RenderLang.Chinese)
        {
            return op switch
            {
                ArithOp.Add => "加",
                ArithOp.Sub => "减",
                ArithOp.Mul => "乘",
                ArithOp.Div => "除以",
                ArithOp.Mod => "模",
                _ => "加",
            };
        }
        return op switch
        {
            ArithOp.Add => "plus",
            ArithOp.Sub => "minus",
            ArithOp.Mul => "times",
            ArithOp.Div => "divided by",
            ArithOp.Mod => "mod",
            _ => "plus",
        };
    }

    // ---------- 布尔 ----------

    public string PrintBool(BoolExpr e)
    {
        if (e == null) return "";
        switch (e)
        {
            case ConstBoolExpr c:
                // 恒真不应出现在渲染结果里（SimplifyCondition 已在上层剔除），
                // 这里返回空串作为最后一道保险。
                return c.Value ? "" : (_lang == RenderLang.Chinese ? "不成立" : "false");
            case CompareExpr cmp:
                // 左侧是计数器时，用编译器认识的资源句式："you have at least N play points"
                if (cmp.Left != null && cmp.Left.Op == NumOp.Counter)
                {
                    var res = ResourcePhrase(cmp.Left.Counter.Name);
                    if (res != null && cmp.Right != null && cmp.Right.Op == NumOp.Const)
                    {
                        var n = cmp.Right.Value.ToString();
                        if (_lang == RenderLang.Chinese)
                            return "你拥有至少 " + n + " 点" + Word(res);
                        return "you have at least " + n + " " + res;
                    }
                }
                if (cmp.Left == null || cmp.Right == null)
                    return PrintNum(cmp.Left) + Space() + CompareWord(cmp.Op) + Space() + PrintNum(cmp.Right);

                // 官方写法（逐一对应）：
                //   本卡属性："this card's cost isn't 2" / "if this card's cost is 5"
                //   存在量：  "if there are at least 2 allied followers on the field"
                //   变量 X：  "if X is at least 5"
                //   计数器：  "if you have at least 3 combo"
                var rn = PrintNum(cmp.Right);
                var zh = _lang == RenderLang.Chinese;

                // a) 本卡自身属性（Cost / Attack / Defense）
                bool selfProp = cmp.Left.Op == NumOp.Property
                                && (cmp.Left.Of == null
                                    || cmp.Left.Of.SourceKind == TargetSourceKind.Slot && cmp.Left.Of.Slot == TargetSpec.SlotSelf);
                if (selfProp)
                {
                    var prop = zh ? Word(PropertyName(cmp.Left.Stat))
                                  : "this card's " + Word(PropertyName(cmp.Left.Stat));
                    var cop = cmp.Op switch
                    {
                        CompareOp.Eq => zh ? "为" : " is ",
                        CompareOp.Ne => zh ? "不为" : " isn't ",
                        CompareOp.Ge => zh ? "至少为" : " is at least ",
                        CompareOp.Le => zh ? "至多为" : " is at most ",
                        CompareOp.Gt => zh ? "大于" : " is greater than ",
                        CompareOp.Lt => zh ? "小于" : " is less than ",
                        _ => zh ? "为" : " is ",
                    };
                    return prop + cop + rn;
                }

                // b) 变量 X / Y / Z
                if (cmp.Left.Op == NumOp.SplitOf || (cmp.Left.Op == NumOp.Counter && cmp.Left.Counter.Name.Length <= 1))
                    return PrintNum(cmp.Left) + (cmp.Op == CompareOp.Ge ? (zh ? " 至少为 " : " is at least ") : (zh ? " 为 " : " is ")) + rn;

                // c) 资源计数器
                if (cmp.Left.Op == NumOp.Counter)
                {
                    var res = ResourcePhrase(cmp.Left.Counter.Name);
                    if (res != null)
                    {
                        if (cmp.Op == CompareOp.Ge)
                            return zh ? "你拥有至少 " + rn + " 点" + Word(res)
                                      : "if you have at least " + rn + " " + res;
                        return zh ? "你的" + Word(res) + "至少为" + rn
                                  : "if you have at least " + rn + " " + res;
                    }
                }

                // d) 存在量 / 其他属性
                if (cmp.Left.Op == NumOp.Count || cmp.Left.Op == NumOp.Property)
                {
                    var lname = cmp.Left.Op == NumOp.Count
                        ? (zh ? "" : "there are at least ") + PrintTarget(cmp.Left.Of)
                        : Word(PropertyName(cmp.Left.Stat));
                    if (cmp.Op == CompareOp.Ge && cmp.Left.Op == NumOp.Count)
                    {
                        // "there are at least N <target>"
                        if (zh) return lname + rn;
                        return "there are at least " + rn + " " + PrintTarget(cmp.Left.Of);
                    }
                    var cmpw = cmp.Op switch
                    {
                        CompareOp.Eq => zh ? " 为 " : " is ",
                        CompareOp.Ne => zh ? " 不为 " : " isn't ",
                        CompareOp.Ge => zh ? " 至少为 " : " is at least ",
                        CompareOp.Le => zh ? " 至多为 " : " is at most ",
                        CompareOp.Gt => zh ? " 大于 " : " is greater than ",
                        CompareOp.Lt => zh ? " 小于 " : " is less than ",
                        _ => zh ? " 为 " : " is ",
                    };
                    return lname + cmpw + rn;
                }

                return PrintNum(cmp.Left) + Space() + CompareWord(cmp.Op) + Space() + rn;
            case HasStateExpr hs:
                // UnitState.None 不是一个状态，渲染成空而不是 "it is None"
                if (hs.State == Core.UnitState.None) return "";
                if (_lang == RenderLang.Chinese) return "处于" + Word(hs.State.ToString()) + "状态";
                // 官方写法："if this follower is super-evolved" / "if it is evolved"
                return "this follower is " + Word(hs.State.ToString());
            case IsPreviousExpr:
                return Word("it is the selected card");
            case AndExpr and:
                return string.Join(AndWord(), and.Items.Select(PrintBool));
            case OrExpr or:
                return string.Join(OrWord(), or.Items.Select(PrintBool));
            case NotExpr not:
                return NotWord() + (_lang == RenderLang.Chinese ? "" : " ") + PrintBool(not.Inner);
            case DeckRuleExpr dr:
                return dr.Rule switch
                {
                    Core.DeckRule.NoDuplicates => _lang == RenderLang.Chinese ? "牌组没有重复卡" : "there are no duplicates in your deck",
                    Core.DeckRule.EvenCostOnly => _lang == RenderLang.Chinese ? "全部是偶数费用" : "all cards have even cost",
                    Core.DeckRule.OddCostOnly => _lang == RenderLang.Chinese ? "全部是奇数费用" : "all cards have odd cost",
                    _ => "",
                };
            case HasKindExpr hk:
                // 卡种谓词通常已经在名词里体现，这里只做保底
                if (_lang == RenderLang.Chinese) return "是" + Word(hk.Kind.ToString());
                return "it is " + ArticleBefore(Word(hk.Kind.ToString())) + " " + Word(hk.Kind.ToString());
            case HasKeywordExpr hkw:
                // 官方写法："with Ward" / "with Last Words"
                return With() + Space() + Word(hkw.Keyword.Name);
            case HasTraitExpr ht:
                // 官方写法：直接写兵种名（"Puppetry followers"）
                return With() + Space() + Word(ht.Trait.Name);
            case HasClanExpr hc:
                return With() + Space() + Word(hc.Clan.Name);
            case NotSameEntityExpr nse:
                // 官方写法："other than the attack target" / "another"
                return _lang == RenderLang.Chinese ? "且不是另一个" : "other than it";
            case ExistsExpr ex:
                // "there are at least N <target>"
                if (_lang == RenderLang.Chinese)
                    return "存在" + PrintNum(ex.Count) + "个" + PrintTarget(ex.Target);
                return "there are at least " + PrintNum(ex.Count) + " " + PrintTarget(ex.Target);
            default:
                // 不再把类名直出（那会产生 "NotSameEntity" 这种非英文）。
                // 未覆盖的谓词渲染成中性占位，并在注释里留下类名供排查。
                return _lang == RenderLang.Chinese ? "（条件）" : "under some condition";
        }
    }

    /// <summary>
    /// 计数器内部名 -&gt; 编译器能识别的英文资源名。
    /// 这两边必须对齐，否则渲染出的句子编译器读不懂，双向闭环断在这里。
    /// </summary>
    /// <summary>
    /// StatKey -&gt; 官方卡面的属性名。
    /// 官方用 "defense" 而不是 "life"，用 "attack" 而不是 "Attack"。
    /// </summary>
    /// <summary>排序依据 -&gt; 官方卡面的属性名。</summary>
    private static string PropertyName(Core.SortKey k) => k switch
    {
        Core.SortKey.Attack => "attack",
        Core.SortKey.Life => "defense",
        Core.SortKey.Cost => "cost",
        Core.SortKey.BaseCost => "base cost",
        Core.SortKey.AttackDiff => "attack",
        _ => "",
    };

    private static string PropertyName(Core.StatKey k) => k switch
    {
        Core.StatKey.Attack => "attack",
        Core.StatKey.Life or Core.StatKey.MaxLife => "defense",
        Core.StatKey.Cost => "cost",
        Core.StatKey.BaseAttack => "base attack",
        Core.StatKey.BaseLife => "base defense",
        Core.StatKey.BaseCost => "base cost",
        Core.StatKey.Countdown => "countdown",
        Core.StatKey.HandCount => "cards in your hand",
        Core.StatKey.DeckCount => "cards in your deck",
        Core.StatKey.BoardCount => "followers on the field",
        Core.StatKey.CemeteryCount => "cards in the cemetery",
        Core.StatKey.PlayPoint => "play points",
        Core.StatKey.MaxPlayPoint => "max play points",
        Core.StatKey.EvolvePoint => "evolution points",
        _ => k.ToString().ToLowerInvariant(),
    };

    private static string ResourcePhrase(string counter) => counter switch
    {
        "pp" => "play points",
        "max_pp" => "max play points",
        "combo" => "combo",
        "shadow" => "shadows",
        "earth_sigil" => "earth sigils",
        "ep" => "evolution points",
        "sep" => "super-evolution points",
        "crest" => "crests",
        "faith" => "faith",
        // 卡面里直接出现的其他计数器
        "mode_count" => "modes",
        "fuse_count" => "cards fused",
        _ => null,
    };

    private string CompareWord(CompareOp op)
    {
        if (_lang == RenderLang.Chinese)
        {
            return op switch
            {
                CompareOp.Eq => "为",
                CompareOp.Ne => "不为",
                CompareOp.Gt => "大于",
                CompareOp.Ge => "至少为",
                CompareOp.Lt => "小于",
                CompareOp.Le => "至多为",
                _ => "为",
            };
        }
        // Ge / Le 直接写成 "is at least" / "is at most"，
        // 因为调用方的语序是 "主语 + 连接词 + 数值"，
        // 写 "or more" 会变成 "countdown or more 4" 这种不合法的语序。
        return op switch
        {
            CompareOp.Eq => "is",
            CompareOp.Ne => "is not",
            CompareOp.Gt => "is greater than",
            CompareOp.Ge => "is at least",
            CompareOp.Lt => "is less than",
            CompareOp.Le => "is at most",
            _ => "is",
        };
    }

    private string AndWord() => _lang == RenderLang.Chinese ? "且" : " and ";
    private string OrWord() => _lang == RenderLang.Chinese ? "或" : " or ";
    private string NotWord() => _lang == RenderLang.Chinese ? "不满足" : "not ";

    private string EventName(CoreEvent ev) => ev switch
    {
        CoreEvent.OnPlay => "Fanfare",
        CoreEvent.OnEvolve => "Evolve",
        CoreEvent.OnSuperEvolve => "Super-Evolve",
        CoreEvent.OnDestroy => "Last Words",
        CoreEvent.OnEngage => "Engage",
        CoreEvent.OnAttack => "Strike",
        CoreEvent.OnClash => "Clash",
        _ => ev.ToString(),
    };

    private string Duration(DurationKind d) => d switch
    {
        DurationKind.ThisTurn => Space() + Word("this turn"),
        DurationKind.ThisMatch => Space() + Word("this match"),
        DurationKind.UntilEndOfOpponentTurn => Space() + Word("until the end of your opponent's turn"),
        _ => "",
    };

    private string ZoneName(ZoneRef z) => Word(z switch
    {
        ZoneRef.Board => "Field",
        ZoneRef.Hand => "Hand",
        ZoneRef.Deck => "Deck",
        ZoneRef.Cemetery => "Cemetery",
        ZoneRef.Banished => "Banished",
        ZoneRef.ExArea => "Ex Area",
        ZoneRef.CrestArea => "Crest Area",
        ZoneRef.Leader => "Leader",
        _ => "Field",
    });

    // ---------- 词形 ----------

    private string Word(string en)
    {
        if (string.IsNullOrEmpty(en)) return "";

        // 少数几个词的官方写法与 AST 枚举名不一致，在这里归一。
        var canon = en switch
        {
            "SuperEvolved" => "super-evolved",
            "Evolved" => "evolved",
            "AttackedThisTurn" => "attacked this turn",
            "AttackedLeaderLastTurn" => "attacked the enemy leader last turn",
            "HasLastWords" => "Last Words",
            "HasOnSpellboost" => "On Spellboost",
            "cannot_attack" => "Can't attack followers or leaders",
            "BaseCost" => "base cost",
            "BaseAttack" => "base attack",
            "BaseLife" => "base defense",
            "Attack" => "attack",
            "Life" => "defense",
            "Cost" => "cost",
            _ => en,
        };
        return _lang == RenderLang.Chinese ? _g.ToChinese(canon) : _g.ToEnglish(canon);
    }

    private const string ArticleToken = "a";
    private string Article() => _lang == RenderLang.Chinese ? "" : ArticleToken;
    private string ArticleBefore(string word) => _lang == RenderLang.Chinese ? "" : ("aeiouAEIOU".IndexOf(word.Length>0?word[0]:'x') >= 0 ? "an" : "a");
    private string Space() => " ";
    private string Colon() => _lang == RenderLang.Chinese ? "：" : ": ";
    private string Comma() => _lang == RenderLang.Chinese ? "，" : ", ";
    private string Period() => _lang == RenderLang.Chinese ? "。" : ".";
    private string Dash() => _lang == RenderLang.Chinese ? " - " : " - ";
    private string Quote(string s) => _lang == RenderLang.Chinese ? "\u300c" + s + "\u300d" : "\"" + s + "\"";
    private string If() => _lang == RenderLang.Chinese ? "若" : "If";
    private string To() => _lang == RenderLang.Chinese ? "给" : "to";
    private string Of() => _lang == RenderLang.Chinese ? "的" : "of";
    private string From() => _lang == RenderLang.Chinese ? "从" : "from";
    private string With() => _lang == RenderLang.Chinese ? "，具有" : "with";
    private string Sep() => _lang == RenderLang.Chinese ? "" : " ";
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 官方级简中卡面渲染器
//
// 与 NaturalPrinter 的分工：
//   NaturalPrinter(RenderLang.English)  英文 Round-Trip 自检专用，必须与卡面原文同构。
//   ChineseCardPrinter                  给人看的中文卡面，追求「读起来像官方译文」。
//
// 为什么不能拿英文渲染器套 Glossary 逐词替换：
//   英文是 SVO + 介词短语后置（Deal 2 damage to an enemy follower），
//   中文的定语必须在核心名词**前面**（对1个敌方随从造成2点伤害）。
//   逐词替换会产出「造成 2 伤害 给 敌方 随从」这种洋泾浜，
//   而洋泾浜的危害不只是难读 —— 它掩盖了 AST 是否真的对齐。
//
// 本渲染器只读 AST，不读原文，所以「渲染出来像人话」本身就说明
// AST 的结构是完整的：缺了某个字段，句子必然读不通。
// ============================================================================
public sealed class ChineseCardPrinter
{
    private readonly Glossary _glossary;

    public ChineseCardPrinter(Glossary glossary = null)
        => _glossary = glossary ?? Glossary.CreateDefault();

    // ---------------------------------------------------------------- 卡面

    /// <summary>渲染一张完整卡面：融合头 → 激奏/结晶 → 本体 → 关联纹章/信仰。</summary>
    public string PrintCard(CardDef card)
    {
        if (card == null) return "";
        var sb = new StringBuilder();

        // ① 融合声明
        if (card.HasFuse)
        {
            sb.Append("【融合】");
            sb.Append(FuseMaterialText(card.FuseMaterialFilter) ?? "卡牌");
            sb.Append('\n');
        }

        // ② 激奏 / 结晶分栏
        if (card.AccelerateDef != null)
        {
            sb.Append("【激奏】").Append(card.AccelerateDef.BaseCost).Append('；');
            sb.Append(PrintAbilities(card.AccelerateDef.Abilities));
            sb.Append("\n----------\n");
        }
        if (card.CrystallizeDef != null)
        {
            sb.Append("【结晶】").Append(card.CrystallizeDef.BaseCost).Append('；');
            sb.Append(PrintAbilities(card.CrystallizeDef.Abilities));
            sb.Append("\n----------\n");
        }

        // ③ 本体：先合并裸关键词与常驻项，再逐条能力
        var plain = card.Abilities?.Where(a => a?.Trigger?.Event == CoreEvent.PassiveAura).ToList()
                    ?? new List<AbilityDef>();
        var actives = card.Abilities?.Where(a => a?.Trigger?.Event != CoreEvent.PassiveAura).ToList()
                      ?? new List<AbilityDef>();

        var keywords = new List<string>();
        foreach (var ab in plain)
        {
            // 常驻的「初始吟唱」与「计数器初值」也住在这里。
            // 只提关键词会让护符变成一张永远不会自己消失的牌 ——
            // 卡面上根本没有【吟唱】，玩家看不出异常，但规则已经错了。
            string passive = PrintPassiveLine(ab);
            if (passive.Length > 0) keywords.Add(passive);
        }

        var innate = (card.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
            .Select(k => "【" + KeywordZh(k.Name) + "】");
        var allKw = innate.Concat(keywords).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        if (allKw.Count > 0) sb.Append(string.Join("", allKw)).Append('\n');

        foreach (var ab in actives)
        {
            var line = PrintAbility(ab);
            if (!string.IsNullOrEmpty(line)) sb.Append(line).Append('\n');
        }

        // ④ 关联纹章 / 信仰
        if (card.CrestDef != null)
        {
            sb.Append('\n').Append("【关联纹章：").Append(card.CrestDef.Name).Append("】\n");
            sb.Append(PrintAbilities(card.CrestDef.Abilities)).Append('\n');
        }
        if (card.FaithDef != null)
        {
            sb.Append('\n').Append("【关联信仰：").Append(card.FaithDef.Name).Append("】\n");
            sb.Append(PrintAbilities(card.FaithDef.Abilities)).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    public string PrintAbilities(IReadOnlyList<AbilityDef> abilities)
    {
        if (abilities == null || abilities.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var ab in abilities)
        {
            // 常驻项（吟唱 / 计数器初值 / 关键词）写在子卡里时也要输出。
            if (ab?.Trigger?.Event == CoreEvent.PassiveAura)
            {
                var pl = PrintPassiveLine(ab);
                if (pl.Length > 0) sb.Append(pl).Append('\n');
                continue;
            }
            var line = PrintAbility(ab);
            if (!string.IsNullOrEmpty(line)) sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>一条能力：[触发头] 可选代价 条件，动作串。</summary>
    /// <summary>
    /// 渲染一条常驻（PassiveAura）项：初始吟唱 / 计数器初值 / 裸关键词。
    ///
    /// 这三类都写在 PassiveAura 上。过去只提关键词，于是
    /// 「【吟唱】3」「初始【信仰】值为 0」在卡面上人间蒸发 ——
    /// 护符变成一张永远不会自己消失的牌，而玩家看不出任何异常。
    /// </summary>
    private string PrintPassiveLine(AbilityDef ab)
    {
        if (ab == null) return "";
        var parts = new List<string>();

        foreach (var c in ab.Costs ?? Array.Empty<CostSpec>())
        {
            if (c.Kind != CostKind.Countdown) continue;
            string amt = c.Amount != null ? PrintNum(c.Amount) : "1";
            parts.Add("【吟唱" + amt + "】");
        }

        foreach (var n in ab.Body ?? Array.Empty<EffectNode>())
        {
            if (n is ModifyKeywordNode mk && mk.Op == KeywordOp.Grant)
                parts.Add(PrintKeyword(mk));
            else if (n is ModifyCounterNode mc && mc.Op == CounterOp.Set)
                parts.Add("初始【" + CounterZh(mc.Counter.Name) + "】值为" + PrintNum(mc.Delta) + "。");
        }

        return string.Join("", parts);
    }

    public string PrintAbility(AbilityDef ab)
    {
        if (ab == null) return "";
        var sb = new StringBuilder();

        // 进化域条件剥离：<ev>/<sev> 段编译时会附 HasStateExpr(Evolved, Self)，
        // 而触发头本身已经写了【进化时】—— 再打一遍就是「进化时：若已进化，…」这种蠢话。
        var cond = StripEvolveDomain(ab.Condition, ab.Trigger, out string domainPrefix);
        if (!string.IsNullOrEmpty(domainPrefix)) sb.Append(domainPrefix);

        sb.Append(TriggerHeader(ab.Trigger));

        if (ab.Costs != null && ab.Costs.Count > 0)
            sb.Append(CostsText(ab.Costs));

        if (!IsTrue(cond))
            sb.Append("若").Append(PrintBool(cond)).Append('，');

        var body = PrintNodes(ab.Body);
        if (string.IsNullOrEmpty(body)) return sb.ToString().TrimEnd();
        sb.Append(body);
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- 触发头

    private string TriggerHeader(TriggerSpec trig)
    {
        if (trig == null) return "";

        // 旁观型触发必须写出「谁」触发的。
        // 只写事件会让卡面变成「【进入战场时】给予该卡牌【疾驰】效果」这种无头苍蝇 ——
        // 读者完全不知道是哪张卡进入了战场。
        if (trig.SubjectFilter != null && trig.SubjectFilter.SourceKind != TargetSourceKind.Slot)
        {
            string who = PrintTarget(trig.SubjectFilter);
            string verb = EventVerbZh(trig.Event, trig.SecondaryEvent);

            // 及物动词（打出 / 融合 / 舍弃 / 抽牌…）必须放在宾语**前面**：
            // 「每当我方打出或融合战利品兵种的卡牌时」
            // 而不是「每当我方战场的1个战利品兵种的打出或融合时」。
            // 不及物动词（进入战场 / 离场 / 被破坏）则跟在主语后面。
            if (IsTransitiveVerb(trig.Event))
            {
                string subject = trig.SubjectFilter.Owner == ZoneScope.Opponent ? "敌方" : "我方";
                string obj = TargetNounOnly(trig.SubjectFilter);
                return "每当" + subject + verb + obj + "时，";
            }

            return "每当" + who + verb + "时，";
        }

        // 「你做了某件事」类（融合 / 魔力增幅 / 选模式）：主体是玩家，不是某张卡。
        if (trig.SubjectFilter == null && trig.Event is CoreEvent.OnFuse or CoreEvent.OnSpellboost
            or CoreEvent.OnSelectMode or CoreEvent.OnEarthRite)
        {
            string verb = EventVerbZh(trig.Event, trig.SecondaryEvent);
            return "每当我方进行" + verb + "时，";
        }
        string head = trig.Event switch
        {
            CoreEvent.OnPlay => "【入场曲】",
            CoreEvent.OnEnterField => "【进入战场时】",
            CoreEvent.OnLeaveField => "【离场时】",
            CoreEvent.OnDestroy => "【谢幕曲】",
            CoreEvent.OnBanish => "【消失时】",
            CoreEvent.OnDiscard => "【被舍弃时】",
            CoreEvent.OnSummon => "【召唤时】",
            CoreEvent.OnEvolve => "【进化时】",
            CoreEvent.OnSuperEvolve => "【超进化时】",
            CoreEvent.OnAttack => "【攻击时】",
            CoreEvent.OnClash => "【交战时】",
            CoreEvent.OnDamaged => "【受到伤害时】",
            CoreEvent.OnHealed => "【回复时】",
            CoreEvent.OnEngage => "【启动】",
            CoreEvent.OnFuse => "【融合时】",
            CoreEvent.OnSpellboost => "【魔力增幅时】",
            CoreEvent.OnDraw => "【抽牌时】",
            CoreEvent.OnReturnToHand => "【返回手牌时】",
            CoreEvent.OnAddToHand => "【加入手牌时】",
            CoreEvent.OnTransform => "【变身时】",
            CoreEvent.OnEarthRite => "【土之秘术时】",
            CoreEvent.OnSelectMode => "【选择模式时】",
            CoreEvent.OnCountdownExpire => "【吟唱归零时】",
            CoreEvent.OnDeckOut => "【牌组抽空时】",
            CoreEvent.OnCounterChange => "【计数器变化时】",
            CoreEvent.BattleStart => "【对战开始时】",
            CoreEvent.TurnStart => "【回合开始时】",
            CoreEvent.TurnEnd => "【回合结束时】",
            _ => "",
        };

        // 区域限定：只有非默认区才需要标出来
        if (trig.ActiveZone == ActiveZoneScope.Hand) head = "【在手牌中】" + head;
        else if (trig.ActiveZone == ActiveZoneScope.Deck) head = "【在牌组中】" + head;

        // 回合归属
        if (trig.DuringTurn == TurnOwnerScope.Self && head.StartsWith("【回合")) head = "【我方" + head.Substring(1);
        else if (trig.DuringTurn == TurnOwnerScope.Opponent && head.StartsWith("【回合")) head = "【敌方" + head.Substring(1);

        // 并列事件："play or Fuse" 两个都要写
        if (trig.SecondaryEvent != null)
            head = head.Replace("】", "或" + EventNameZh(trig.SecondaryEvent.Value) + "】");

        return head;
    }

    /// <summary>及物动词：它们要放在宾语前面。</summary>
    private static bool IsTransitiveVerb(CoreEvent ev) => ev is
        CoreEvent.OnPlay or CoreEvent.OnFuse or CoreEvent.OnDiscard
        or CoreEvent.OnDraw or CoreEvent.OnSummon or CoreEvent.OnAddToHand
        or CoreEvent.OnSpellboost;

    /// <summary>只取「定语 + 核心名词」，不含数量与区域（放在动词后面用）。</summary>
    private string TargetNounOnly(TargetSpec t)
    {
        var parts = new List<string>();
        CollectWhere(t?.Where, parts);
        string noun = CoreNoun(t, parts.Count > 0);
        // 只有兵种/属性定语、没有卡种时，补一个「卡牌」收尾：
        // 「战利品兵种的时」这种半截句子比不写还难读。
        if (string.IsNullOrEmpty(noun)) noun = "卡牌";
        return string.Concat(parts) + noun;
    }

    /// <summary>事件动词短语（含并列事件）。</summary>
    private static string EventVerbZh(CoreEvent ev, CoreEvent? secondary)
    {
        string a = ev switch
        {
            CoreEvent.OnPlay => "打出",
            CoreEvent.OnEnterField => "进入战场",
            CoreEvent.OnLeaveField => "离场",
            CoreEvent.OnDestroy => "被破坏",
            CoreEvent.OnBanish => "被放逐",
            CoreEvent.OnDiscard => "被舍弃",
            CoreEvent.OnSummon => "被召唤",
            CoreEvent.OnEvolve => "进化",
            CoreEvent.OnSuperEvolve => "超进化",
            CoreEvent.OnAttack => "攻击",
            CoreEvent.OnClash => "交战",
            CoreEvent.OnDamaged => "受到伤害",
            CoreEvent.OnHealed => "回复",
            CoreEvent.OnFuse => "融合",
            CoreEvent.OnSpellboost => "魔力增幅",
            CoreEvent.OnDraw => "抽牌",
            CoreEvent.OnSelectMode => "选择模式",
            CoreEvent.OnEarthRite => "发动土之秘术",
            CoreEvent.OnAddToHand => "加入手牌",
            CoreEvent.OnReturnToHand => "返回手牌",
            _ => "触发",
        };
        if (secondary == null) return a;
        string b = secondary.Value switch
        {
            CoreEvent.OnFuse => "融合",
            CoreEvent.OnPlay => "打出",
            CoreEvent.OnDestroy => "被破坏",
            _ => secondary.Value.ToString(),
        };
        return a + "或" + b;
    }

    private static string EventNameZh(CoreEvent ev) => ev switch
    {
        CoreEvent.OnFuse => "融合",
        CoreEvent.OnPlay => "打出",
        CoreEvent.OnDestroy => "被破坏",
        _ => ev.ToString(),
    };

    private string CostsText(IReadOnlyList<CostSpec> costs)
    {
        var parts = new List<string>();
        foreach (var c in costs)
        {
            string amt = c.Amount != null ? PrintNum(c.Amount) : "1";
            switch (c.Kind)
            {
                case CostKind.UsePp: parts.Add(amt + "点能量点"); break;
                case CostKind.Necromance: parts.Add("唤灵" + amt); break;
                case CostKind.DestroyAlly: parts.Add("土之秘术" + amt); break;
                case CostKind.TurnPlayCount: parts.Add("连击" + amt); break;
                case CostKind.MatchPlayCount: parts.Add("协作" + amt); break;
                case CostKind.Countdown: parts.Add("吟唱" + amt); break;
                default: break;
            }
        }
        return parts.Count == 0 ? "" : "【" + string.Join("】【", parts) + "】";
    }

    // ---------------------------------------------------------------- 节点

    public string PrintNodes(IReadOnlyList<EffectNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) return "";
        // 「选择 X，然后对它做 Y」要连成一句：中文里「选择…。对…」读起来像两件事。
        var parts = new List<string>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var s = PrintNode(nodes[i]);
            if (string.IsNullOrEmpty(s)) continue;
            parts.Add(s);
        }
        return string.Join("", parts);
    }

    public string PrintNode(EffectNode n)
    {
        switch (n)
        {
            case SelectTargetNode st:
                return "选择" + PrintTarget(st.Target) + "，";

            case DealDamageNode d:
            {
                string amt = PrintNum(d.Amount);
                if (d.IsSplit)
                    return "将" + amt + "点伤害依次分配给" + PrintTarget(d.Target) + "。";
                return "对" + PrintTarget(d.Target) + "造成" + amt + "点伤害。";
            }

            case RestoreLifeNode r:
                return r.FullRestore
                    ? "完全回复" + PrintTarget(r.Target) + "的生命值。"
                    : "为" + PrintTarget(r.Target) + "回复" + PrintNum(r.Amount) + "点生命值。";

            case ModifyStatsNode m:
                return PrintModifyStats(m);

            case ModifyCostNode mc:
            {
                string dur = Duration(mc.Duration);
                return mc.Op switch
                {
                    CostModOp.Halve => "使" + PrintTarget(mc.Target) + "的费用减半" + dur + "。",
                    CostModOp.Set => "使" + PrintTarget(mc.Target) + "的费用变为" + PrintNum(mc.Value) + dur + "。",
                    _ => "使" + PrintTarget(mc.Target) + "的费用" + Signed(mc.Value) + dur + "。",
                };
            }

            case ModifyKeywordNode k:
                return k.Op switch
                {
                    KeywordOp.Remove => "移除" + PrintTarget(k.Target) + "的" + PrintKeyword(k) + "。",
                    KeywordOp.SilenceAll => "移除" + PrintTarget(k.Target) + "的所有能力。",
                    _ => "给予" + PrintTarget(k.Target) + PrintKeyword(k) + "效果" + Duration(k.Duration) + "。",
                };

            case GrantAbilityNode ga:
                return "给予" + PrintTarget(ga.Target) + "「" + PrintGrantedAbility(ga.Granted) + "」效果"
                       + Duration(ga.Duration) + "。";

            case GrantCounterAbilityNode gc:
            {
                string inner = PrintGrantedAbility(gc.Granted);
                // 空能力渲染成「「」效果」是垃圾输出；宁可明说「一项常驻能力」。
                if (string.IsNullOrEmpty(inner)) inner = "一项常驻能力";
                return "给予我方的【" + CounterZh(gc.Counter.Name) + "】「" + inner + "」效果。";
            }

            case SpawnCardNode sp:
                return PrintSpawn(sp);

            case MoveZoneNode mv:
                return PrintMove(mv);

            case TransformNode t:
                return "使" + PrintTarget(t.Target) + "【变身】为《" + CardZh(t.IntoCardName) + "》。";

            case EvolveNode e:
                return "使" + PrintTarget(e.Target) + (e.IsSuperEvolve ? "【超进化】" : "【进化】") + "。";

            case ModifyCounterNode c:
                return PrintCounter(c);

            case InvokeAbilityNode i:
                return "发动本卡牌的【" + EventNameZh(i.Event) + "】能力。";

            case ConditionalNode cd:
                return PrintConditional(cd);

            case LoopNode l:
            {
                string times = l.Kind == LoopKind.ForEach
                    ? "对" + PrintTarget(l.Collection)
                    : PrintNum(l.Count) + "次";
                return "重复以下效果" + times + "：「" + PrintNodes(l.Body) + "」";
            }

            case BranchModeNode b:
                return PrintBranch(b);

            case ReplaceDeckNode rd:
                return "将我方的牌组替换为《" + DeckNameZh(rd.DeckName) + "》。";

            default:
                return "";
        }
    }

    private string PrintModifyStats(ModifyStatsNode m)
    {
        string tgt = PrintTarget(m.Target);
        string dur = Duration(m.Duration);

        if (m.Op == StatModOp.Swap)
            return "交换" + tgt + "的攻击力与生命值" + dur + "。";

        if (m.Op == StatModOp.Set)
        {
            // 辨识复合形态：翻倍 / 减半。渲染成「= 攻击力 乘 2/生命值 乘 2」没人读得懂。
            if (IsDouble(m.Attack) && IsDouble(m.Life))
                return "使" + tgt + "的攻击力与生命值翻倍" + dur + "。";
            if (IsHalve(m.Life))
                return "使" + tgt + "的生命值上限减半" + dur + "。";
            if (m.Attack != null && m.Life == null)
                return "使" + tgt + "的攻击力变为" + PrintNum(m.Attack) + dur + "。";
            if (m.Attack == null && m.Life != null)
                return "使" + tgt + "的生命值上限变为" + PrintNum(m.Life) + dur + "。";
            if (m.Attack == null && m.Life == null)
                return "";
            return "使" + tgt + "的攻击力/生命值变为" + PrintNum(m.Attack) + "/" + PrintNum(m.Life) + dur + "。";
        }

        // Add：null 表示「这一项不动」，绝不能渲染成 +0
        string a = m.Attack != null ? Signed(m.Attack) : null;
        string l = m.Life != null ? Signed(m.Life) : null;
        if (a == null && l == null) return "";
        if (a != null && l == null) return "给予" + tgt + a + "攻击力效果" + dur + "。";
        if (a == null && l != null) return "给予" + tgt + l + "生命值效果" + dur + "。";
        return "给予" + tgt + a + "/" + l + "效果" + dur + "。";
    }

    /// <summary>prop:X * 2 —— 用于把「翻倍」渲染成一个词，而不是「X 乘 2」。</summary>
    private static bool IsDouble(NumExpr e)
    {
        if (e == null || e.Op != NumOp.Arith || e.Args.Count < 2) return false;
        if (e.Args[1].Op != NumOp.Const || e.Args[1].Value != 2) return false;
        return e.Args[0].Op == NumOp.Property;
    }

    private static bool IsHalve(NumExpr e)
    {
        if (e == null || e.Op != NumOp.Arith || e.Args.Count < 2) return false;
        return e.Args[1].Op == NumOp.Const && e.Args[1].Value == 2;
    }

    private string PrintSpawn(SpawnCardNode sp)
    {
        if (sp.IsCrest)
            return "使" + OwnerZh(sp.ToOwner) + "获得【纹章：「" + CardZh(sp.CardName) + "」】。";

        if (sp.AddDepartedTrait)
            return "【亡者召还】" + DepartedCostCap(sp) + "。";

        string count = PrintNum(sp.Count);

        if (sp.SourceKind == CardSourceKind.CopyOfTarget)
        {
            string kind = sp.IsExactCopy ? "完整复制" : "复制";
            return sp.ToZone == ZoneRef.Hand
                ? "将" + count + "张" + PrintTarget(sp.CopyFrom) + "的" + kind + "加入我方手牌。"
                : "召唤" + count + "个" + PrintTarget(sp.CopyFrom) + "的" + kind + "到战场上。";
        }

        return sp.ToZone == ZoneRef.Hand
            ? "将" + count + "张《" + CardZh(sp.CardName) + "》加入我方手牌。"
            : "召唤" + count + "个《" + CardZh(sp.CardName) + "》到战场上。";
    }

    /// <summary>
    /// 取亡者召还的费用上限。
    ///
    /// 不能直接看 CopyFrom.Where 是不是 CompareExpr ——
    /// 标准形状是 AndExpr(CompareExpr, HasKindExpr)，裸 CompareExpr 判据恒为 false，
    /// 于是所有 Reanimate(4) 都会被印成字母 N。
    /// </summary>
    private string DepartedCostCap(SpawnCardNode sp)
        => Diy.ReanimateHelper.TryExtractCostCap(sp, out var cap) ? PrintNum(cap) : "N";

    private string PrintMove(MoveZoneNode mv)
    {
        string tgt = PrintTarget(mv.Target);

        // 抽牌：Deck -> Hand，取前 N 张
        if (mv.ToZone == ZoneRef.Hand
            && mv.Target?.Zone == ZoneRef.Deck
            && mv.Target.Mode == SelectMode.First)
            return "抽取" + PrintNum(mv.Target.Count) + "张卡牌。";

        // 定向检索：Deck -> Hand 带筛选条件
        if (mv.ToZone == ZoneRef.Hand && mv.Target?.Zone == ZoneRef.Deck)
            return "从我方牌组中检索" + tgt + "并加入手牌。";

        // 瞬念召唤
        if (mv.ToZone == ZoneRef.Board
            && mv.Target?.SourceKind == TargetSourceKind.Slot
            && mv.Target.Slot == TargetSpec.SlotSelf)
            return "将本卡牌【瞬念召唤】到战场上。";

        if (mv.ToZone == ZoneRef.Cemetery)
            return (mv.IsDestroy ? "破坏" : "舍弃") + tgt + "。";

        if (mv.ToZone == ZoneRef.Banished) return "使" + tgt + "【消失】。";
        if (mv.ToZone == ZoneRef.Hand) return "将" + tgt + "返回手牌。";
        if (mv.ToZone == ZoneRef.Deck) return "将" + tgt + "洗回牌组。";
        if (mv.ToZone == ZoneRef.Board) return "将" + tgt + "移动到战场上。";
        if (mv.ToZone == ZoneRef.ExArea) return "将" + tgt + "置于额外区。";
        return "将" + tgt + "移动到" + ZoneZh(mv.ToZone) + "。";
    }

    private string PrintCounter(ModifyCounterNode c)
    {
        string name = c.Counter.Name;
        string tgt = c.Target != null ? PrintTarget(c.Target) : "";

        switch (name)
        {
            case "countdown":
                if (TryGetNegativeAmount(c.Delta, out var adv))
                    return "使" + tgt + "的【吟唱】推进" + PrintNum(adv) + "回合。";
                return "使" + tgt + "的【吟唱】延长" + PrintNum(c.Delta) + "回合。";

            case "spellboost":
                return "使" + tgt + "【魔力增幅】" + PrintNum(c.Delta) + "次。";

            case "pp":
                if (c.Delta != null && c.Delta.Op == NumOp.Const && c.Delta.Value < 0)
                    return "消耗" + (-c.Delta.Value) + "点能量点。";
                return "回复" + PrintNum(c.Delta) + "点能量点。";

            case "earth_sigil":
                return "获得" + PrintNum(c.Delta) + "层【土之印】。";

            case "faith":
                if (TryGetNegativeAmount(c.Delta, out var use))
                    return "消费" + PrintNum(use) + "点【信仰】值。";
                if (c.Delta != null && c.Delta.Op == NumOp.Const)
                    return "使【信仰】值+" + c.Delta.Value + "。";
                return "使【信仰】值" + Signed(c.Delta) + "。";

            case "mode_count":
                return "使可选的【模式】数量+" + PrintNum(c.Delta) + "。";

            default:
                string n2 = CounterZh(name);
                return "使" + (tgt.Length > 0 ? tgt + "的" : "") + "【" + n2 + "】" + Signed(c.Delta) + "。";
        }
    }

    private string PrintConditional(ConditionalNode c)
    {
        // PayThenDo：「扣 N 资源 → 做某事」编译成 if(ctr>=N) then 扣减 + 动作。
        // 渲染成「若信仰>=10，则信仰-10，给予……」是机翻；官方写法是「消费10点信仰值，……」。
        if (c.Condition is CompareExpr cmp
            && cmp.Left?.Op == NumOp.Counter
            && cmp.Op == CompareOp.Ge
            && cmp.Right?.Op == NumOp.Const
            && c.Then != null && c.Then.Count >= 1
            && c.Then[0] is ModifyCounterNode pay
            && pay.Counter.Id == cmp.Left.Counter.Id
            && TryGetNegativeAmount(pay.Delta, out var payMag)
            && payMag.Op == NumOp.Const && payMag.Value == cmp.Right.Value)
        {
            int amt = cmp.Right.Value;
            string zh = CounterZh(cmp.Left.Counter.Name);
            string unit = cmp.Left.Counter.Name == "faith" ? "点【信仰】值" : "点【" + zh + "】";
            var rest = c.Then.Skip(1).ToList();
            return "消费" + amt + unit + "，" + PrintNodes(rest);
        }

        string then = PrintNodes(c.Then);
        string els = PrintNodes(c.Else);
        string head = "若" + PrintBool(c.Condition) + "，";
        if (string.IsNullOrEmpty(els)) return head + then;
        return head + then + "否则，" + els;
    }

    private string PrintBranch(BranchModeNode b)
    {
        int pick = b.PickCount != null ? EvalConst(b.PickCount, 1) : 1;
        string head = b.Kind switch
        {
            ModeDispatchKind.Choose => "选择" + pick + "个【模式】发动：",
            ModeDispatchKind.Random => "从以下能力中随机发动" + pick + "个：",
            ModeDispatchKind.Sequence => "按顺序发动以下全部能力：",
            ModeDispatchKind.RandomUnused => "从以下尚未发动过的能力中随机发动" + pick + "个：",
            ModeDispatchKind.SequenceUnused => "从以下尚未发动过的能力中按顺序发动：",
            _ => "发动以下能力：",
        };
        var sb = new StringBuilder(head);
        for (int i = 0; i < b.Options.Count; i++)
        {
            sb.Append('\n').Append(CircledNumber(i + 1)).Append(' ')
              .Append(PrintNodes(b.Options[i]));
        }
        return sb.ToString();
    }

    private static string CircledNumber(int n) => n switch
    {
        1 => "①", 2 => "②", 3 => "③", 4 => "④", 5 => "⑤",
        6 => "⑥", 7 => "⑦", 8 => "⑧", 9 => "⑨", _ => "(" + n + ")",
    };

    private static int EvalConst(NumExpr e, int fallback)
        => e != null && e.Op == NumOp.Const ? e.Value : fallback;

    // ---------------------------------------------------------------- 目标短语

    /// <summary>
    /// 中文定语链：**[排除词] [归属与区域] [随机/不同名] [属性条件] [兵种/职业] [核心名词]**。
    /// 英文把定语堆在名词后面，中文必须全部前置 —— 这是最大的差别。
    /// </summary>
    public string PrintTarget(TargetSpec t)
    {
        if (t == null) return "目标";

        // 槽位代词
        if (t.SourceKind == TargetSourceKind.Slot)
        {
            switch (t.Slot)
            {
                case TargetSpec.SlotSelf: return "本卡牌";
                case TargetSpec.SlotSelected: return "该目标";
                case TargetSpec.SlotCreated: return "该卡牌";
                case TargetSpec.SlotTrigger: return "该卡牌";
                case TargetSpec.SlotOpposing: return "交战的敌方随从";
                case TargetSpec.SlotItem: return "该卡牌";
                case TargetSpec.SlotCounter: return "该计数器";
                default: return "该目标";
            }
        }

        // 具名卡池不能提前返回：它还带着 History（本局已进入战场的）与 Where（其他的）。
        // 提前返回会把这些定语全部丢掉，
        // 于是「本局对战中已进入战场的其他《痴迷的试验体》」退化成「《痴迷的试验体》」。
        if (t.SourceKind == TargetSourceKind.CardId)
        {
            var pre = new List<string>();
            if (t.History == HistoryScope.EnteredThisMatch) pre.Add("（本局对战中已进入战场的）");
            else if (t.History == HistoryScope.DestroyedThisMatch) pre.Add("（本局对战中已被破坏的）");
            else if (t.History == HistoryScope.DestroyedThisTurn) pre.Add("（本回合已被破坏的）");

            string ownerZh = t.Owner switch
            {
                ZoneScope.Self => "我方的",
                ZoneScope.Opponent => "敌方的",
                _ => "",
            };

            var w = new List<string>();
            CollectWhere(t.Where, w);
            if (t.DuplicatesOnly) w.Insert(0, "重复的");

            string noun = string.IsNullOrEmpty(t.CardName) ? "该卡牌" : "《" + CardZh(t.CardName) + "》";
            string qty = t.Mode == SelectMode.All ? "所有" : (t.Count != null ? PrintNum(t.Count) + "个" : "1个");
            return string.Concat(pre) + ownerZh + qty + string.Concat(w) + noun;
        }

        // 主战者特例
        if (t.Zone == ZoneRef.Leader)
        {
            if (t.Owner == ZoneScope.Self) return "我方主战者";
            if (t.Owner == ZoneScope.Opponent) return "敌方主战者";
            return "双方主战者";
        }

        var sb = new StringBuilder();

        // ① 归属 + 区域
        string owner = t.Owner switch
        {
            ZoneScope.Self => "我方",
            ZoneScope.Opponent => "敌方",
            _ => "",
        };
        string zone = ZoneZh(t.Zone);
        if (t.Owner == ZoneScope.Any && t.IncludeLeader)
            sb.Append("敌方的主战者与全体敌方随从");
        else
        {
            if (owner.Length > 0) sb.Append(owner);
            if (zone.Length > 0) sb.Append(zone).Append('的');
        }

        // ② 排除词。只在这里打一次 —— CollectWhere 里遇到 NotSameEntityExpr
        // 也会打，两者叠加就成了「其他的1个其他的」。
        bool excluded = t.Where is NotSameEntityExpr;
        if (excluded) sb.Append("其他的");

        // ③ 选法 + 数量
        int cnt = t.Count != null ? EvalConst(t.Count, 1) : 1;
        switch (t.Mode)
        {
            case SelectMode.All: sb.Append("所有"); break;
            case SelectMode.Random: sb.Append("随机" + cnt + "个"); break;
            case SelectMode.First: sb.Append("前" + cnt + "个"); break;
            case SelectMode.Choose: sb.Append(cnt + "个"); break;
        }

        // ④ 排序与去重
        if (t.DistinctByName || t.History == HistoryScope.FusedToSelf) sb.Append("不同名的");
        if (t.DuplicatesOnly) sb.Append("重复的");
        if (t.Sort != SortKey.None)
        {
            string key = t.Sort switch
            {
                SortKey.Attack => "攻击力",
                SortKey.Life => "生命值",
                SortKey.Cost => "费用",
                SortKey.BaseCost => "基础费用",
                _ => "",
            };
            // 先拼好再 Append：对 StringBuilder 调 Replace 会把它前面已写的
            // 「攻击力」也一起换掉，把整句话改花。
            if (key.Length > 0)
                sb.Append(key + (t.SortDescending ? "最高的" : "最低的"));
        }

        // ⑤ 属性条件（把后置定语提到名词前）
        string where = PrintTargetWhere(t.Where, t, skipExclusion: excluded);
        if (where.Length > 0) sb.Append(where);

        // ⑥ 核心名词由 Where 的卡种决定（随从 / 法术 / 护符），最后由它收尾。
        sb.Append(CoreNoun(t, where.Length > 0));

        // ⑦ 历史池限定
        string hist = t.History switch
        {
            HistoryScope.DestroyedThisMatch => "（本局对战中已被破坏的）",
            HistoryScope.DestroyedThisTurn => "（本回合已被破坏的）",
            HistoryScope.EnteredThisMatch => "（本局对战中已进入战场的）",
            _ => "",
        };
        if (hist.Length > 0) sb.Insert(0, hist);

        return sb.ToString();
    }

    private string CoreNoun(TargetSpec t, bool whereAlreadySaysNoun)
    {
        if (t?.Zone == ZoneRef.CrestArea) return "纹章";

        // 卡种来自 Where 里的 HasKindExpr：它是核心名词，必须放在最后。
        var items = t?.Where is AndExpr a ? a.Items
                    : t?.Where == null ? Array.Empty<BoolExpr>()
                    : new[] { t.Where };
        foreach (var x in items)
        {
            if (x is HasKindExpr hk)
                return hk.Kind switch
                {
                    CardKind.Follower => "随从",
                    CardKind.Spell => "法术",
                    CardKind.Amulet => "护符",
                    _ => "卡牌",
                };
        }

        if (whereAlreadySaysNoun) return "";
        return "卡牌";
    }

    /// <summary>把 Where 里的「是随从 / 有兵种 / 攻击力≤N」等前置化为形容词。</summary>
    private string PrintTargetWhere(BoolExpr where, TargetSpec t, bool skipExclusion = false)
    {
        if (where == null || IsTrue(where)) return "";
        var parts = new List<string>();
        CollectWhere(where, parts, skipExclusion);
        return parts.Count == 0 ? "" : string.Concat(parts);
    }

    private void CollectWhere(BoolExpr e, List<string> parts, bool skipExclusion = false)
    {
        if (e == null) return;
        switch (e)
        {
            case AndExpr a:
                foreach (var x in a.Items) CollectWhere(x, parts, skipExclusion);
                break;
            case HasKindExpr:
                // 卡种是**核心名词**，不是定语。
                // 放进定语列表会产出「所有随从攻击力至多3的」这种病句；
                // 正确做法是让 CoreNoun 最后统一输出「随从」。
                break;
            case HasTraitExpr ht:
                parts.Add(TraitZh(ht.Trait.Name) + "兵种的");
                break;
            case HasClanExpr hc:
                parts.Add(ClanZh(hc.Clan.Name) + "职业的");
                break;
            case HasKeywordExpr hkw:
                parts.Add("拥有【" + KeywordZh(hkw.Keyword.Name) + "】的");
                break;
            case HasStateExpr hs:
                parts.Add(hs.State switch
                {
                    UnitState.Damaged => "已受伤的",
                    UnitState.Evolved => "已进化的",
                    UnitState.SuperEvolved => "已超进化的",
                    UnitState.HasFuse => "拥有【融合】的",
                    UnitState.HasLastWords => "拥有【谢幕曲】的",
                    UnitState.HasOnSpellboost => "拥有【魔力增幅】的",
                    UnitState.CostModified => "费用已变更的",
                    _ => "",
                });
                break;
            case CompareExpr cmp:
            {
                if (cmp.Left?.Op == NumOp.Property)
                {
                    string prop = StatZh(cmp.Left.Stat);
                    string op = cmp.Op switch
                    {
                        CompareOp.Ge => "至少", CompareOp.Le => "至多",
                        CompareOp.Eq => "等于", CompareOp.Gt => "大于", CompareOp.Lt => "小于",
                        _ => "",
                    };
                    parts.Add(prop + op + PrintNum(cmp.Right) + "的");
                }
                break;
            }
            case NotSameEntityExpr: if (!skipExclusion) parts.Add("其他的"); break;
            case NotExpr nt: break;
        }
    }

    // ---------------------------------------------------------------- 布尔 / 数值

    public string PrintBool(BoolExpr e)
    {
        if (e == null) return "";
        switch (e)
        {
            case ConstBoolExpr cb: return cb.Value ? "成立" : "不成立";
            case AndExpr a:
            {
                // 恒真项（`true and ctr:faith >= 10` 里的 true）不是条件，
                // 打出来会变成「若成立且信仰≥10」这种机器味。
                var parts = a.Items?
                    .Where(x => !(x is ConstBoolExpr cb0 && cb0.Value))
                    .Select(PrintBool)
                    .Where(x => x.Length > 0).ToList()
                            ?? new List<string>();
                return parts.Count == 0 ? "" : string.Join("且", parts);
            }
            case OrExpr o:
            {
                var parts = o.Items?.Select(PrintBool).Where(x => x.Length > 0).ToList()
                            ?? new List<string>();
                return parts.Count == 0 ? "" : string.Join("或", parts);
            }
            case NotExpr n: return "非（" + PrintBool(n.Inner) + "）";

            case CompareExpr c:
            {
                // 状态比较（is Evolved 之类）已在 StripEvolveDomain 处理，
                // 这里处理数值比较。
                string l = PrintNum(c.Left);
                string r = PrintNum(c.Right);
                string op = c.Op switch
                {
                    CompareOp.Ge => "≥", CompareOp.Le => "≤",
                    CompareOp.Eq => "=", CompareOp.Gt => ">", CompareOp.Lt => "<",
                    _ => "=",
                };
                return l + op + r;
            }

            case ExistsExpr ex:
                return PrintTarget(ex.Target) + "数量" + (ex.Op == CompareOp.Ge ? "≥" : "≤") + PrintNum(ex.Count);

            case HasKindExpr hk:
                return "是" + (hk.Kind == CardKind.Follower ? "随从" : hk.Kind == CardKind.Spell ? "法术" : "护符");
            case HasTraitExpr ht: return "具有" + TraitZh(ht.Trait.Name) + "兵种";
            case HasKeywordExpr hkw: return "拥有【" + KeywordZh(hkw.Keyword.Name) + "】";
            case HasStateExpr hs: return hs.State switch
            {
                UnitState.Damaged => "已受伤",
                UnitState.Evolved => "已进化",
                UnitState.SuperEvolved => "已超进化",
                _ => hs.State.ToString(),
            };
            case NotSameEntityExpr: return "不是同一个目标";
            case DeckRuleExpr dr: return "牌组满足规则";
            default: return "";
        }
    }

    public string PrintNum(NumExpr e)
    {
        if (e == null) return "0";
        switch (e.Op)
        {
            case NumOp.Const: return e.Value.ToString();
            // 计数器当数值用时不能带「点」这类量词：「造成连击点伤害」读不通。
            case NumOp.Counter: return CounterZh(e.Counter.Name) + "值";
            case NumOp.Property: return StatZh(e.Stat);
            case NumOp.Count:
                return "「" + PrintTarget(e.Of ?? TargetSpec.Self) + "」的数量";
            case NumOp.DistinctCount:
                return "「" + PrintTarget(e.Of ?? TargetSpec.Self) + "」的不同名数量";
            case NumOp.Sum:
                return "「" + PrintTarget(e.Of ?? TargetSpec.Self) + "」的" + StatZh(e.Stat) + "总和";
            case NumOp.MaxGroupSize:
                return "手牌中同费卡的数量";
            case NumOp.Arith:
            {
                if (e.Args.Count == 0) return "0";
                // 「0 - N」是取负运算的编码方式，直接打 -N，不要打成 (0-N)。
                if (e.Arith == ArithOp.Sub && e.Args.Count > 1
                    && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
                    return "-" + PrintNum(e.Args[1]);
                string l = PrintNum(e.Args[0]);
                string r = e.Args.Count > 1 ? PrintNum(e.Args[1]) : "0";
                string op = e.Arith switch
                {
                    ArithOp.Add => "+", ArithOp.Sub => "-",
                    ArithOp.Mul => "×", ArithOp.Div => "÷",
                    _ => "+",
                };
                return "(" + l + op + r + ")";
            }
            case NumOp.Cond:
                return "（若" + PrintBool(e.Cond) + "则" + PrintNum(e.Then) + "，否则" + PrintNum(e.Else) + "）";
            default: return "0";
        }
    }

    /// <summary>
    /// 带符号的数值。
    ///
    /// 常量直接写 +2 / -1；复合表达式改写成「（X 的数量）」的形式，
    /// 而不是「+「…」的数量」—— 后者读起来像「加一个引号」。
    /// </summary>
    private string Signed(NumExpr e)
    {
        if (e == null) return "";
        if (e.Op == NumOp.Const) return e.Value >= 0 ? "+" + e.Value : e.Value.ToString();
        if (e.Op == NumOp.Arith && e.Arith == ArithOp.Sub && e.Args.Count > 1
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
            return "-" + PrintNum(e.Args[1]);
        // 计数器自带单位（信仰、连击…），再加括号就成了「+（信仰值）」。
        if (e.Op == NumOp.Counter) return "+" + CounterZh(e.Counter.Name) + "值";
        if (e.Op == NumOp.Const) return "+" + e.Value;
        return "+（" + PrintNum(e) + "）";
    }

    // ---------------------------------------------------------------- 进化域

    /// <summary>剥离 &lt;ev&gt;/&lt;sev&gt; 段自动附带的「已进化 / 已超进化」状态条件。</summary>
    private BoolExpr StripEvolveDomain(BoolExpr cond, TriggerSpec trig, out string prefix)
    {
        prefix = "";
        if (cond == null) return null;
        var items = cond is AndExpr a ? a.Items.ToList() : new List<BoolExpr> { cond };
        var kept = new List<BoolExpr>();
        bool sawEvolved = false, sawSuper = false;

        foreach (var x in items)
        {
            if (x is HasStateExpr hs && hs.State == UnitState.Evolved) { sawEvolved = true; continue; }
            if (x is HasStateExpr hs2 && hs2.State == UnitState.SuperEvolved) { sawSuper = true; continue; }
            kept.Add(x);
        }

        // 触发头已经是【进化时】/【超进化时】：条件完全冗余，直接丢掉（不打印前缀）。
        bool trigIsEvolve = trig != null
            && (trig.Event == CoreEvent.OnEvolve || trig.Event == CoreEvent.OnSuperEvolve);
        if (!trigIsEvolve && (sawEvolved || sawSuper))
            prefix = sawSuper ? "【超进化后】" : "【进化后】";

        if (kept.Count == 0) return null;
        if (kept.Count == 1) return kept[0];
        return new AndExpr(kept);
    }

    // ---------------------------------------------------------------- 词表

    private static bool IsTrue(BoolExpr e) => e == null || (e is ConstBoolExpr cb && cb.Value);

    /// <summary>
    /// 识别「负增量」的两种编码：
    ///   · Const(-N)
    ///   · Arith.Sub(Const(0), N)  ← 英文编译器实际产出的形态
    ///
    /// 只认第一种会让所有「减少 N」打印成「增加 -N」，
    /// 于是一张「推进 1 回合」的吟唱卡被渲染成「延长 -1 回合」。
    /// </summary>
    private static bool TryGetNegativeAmount(NumExpr e, out NumExpr positiveMagnitude)
    {
        positiveMagnitude = null;
        if (e == null) return false;

        if (e.Op == NumOp.Const)
        {
            if (e.Value >= 0) return false;
            positiveMagnitude = NumExpr.Const(-e.Value);
            return true;
        }

        if (e.Op == NumOp.Arith && e.Arith == ArithOp.Sub && e.Args.Count > 1
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
        {
            positiveMagnitude = e.Args[1];
            return true;
        }
        return false;
    }

    private string Duration(DurationKind d) => d switch
    {
        DurationKind.ThisTurn => "直到本回合结束",
        DurationKind.UntilEndOfOpponentTurn => "直到敌方回合结束",
        DurationKind.ThisMatch => "",
        _ => "",
    };

    private string PrintKeyword(ModifyKeywordNode k)
    {
        string name = KeywordZh(k.Keyword.Name);
        // 带参关键词必须把参数说出来，否则「可攻击 N 次」变成「可攻击」。
        if (k.Value != null && k.Value.Op == NumOp.Const)
        {
            int v = k.Value.Value;
            return k.Keyword.Name switch
            {
                "max_attacks_per_turn" => "【1回合可攻击" + v + "次】",
                "damage_taken_bonus" => "【受到伤害+" + v + "】",
                "damage_cap" => "【受到伤害上限" + v + "】",
                "attack_limit" => "【攻击力上限" + v + "】",
                _ => "【" + name + v + "】",
            };
        }
        return "【" + name + "】";
    }

    private string PrintGrantedAbility(AbilityDef ab)
    {
        if (ab == null) return "";
        if (ab.Trigger?.Event == CoreEvent.PassiveAura)
            return string.Join("", (ab.Body ?? Array.Empty<EffectNode>())
                .OfType<ModifyKeywordNode>().Select(PrintKeyword));
        return PrintAbility(ab);
    }

    private string FuseMaterialText(TargetSpec filter)
    {
        if (filter?.Where == null || IsTrue(filter.Where)) return null;
        // 必须带上核心名词：只拼定语会把「神器护符」印成「神器」，
        // 而「融合：神器」与「融合：神器护符」是两种不同的材料范围。
        string noun = TargetNounOnly(filter);
        return string.IsNullOrEmpty(noun) ? null : noun;
    }

    public string KeywordZh(string en) => _glossary.ToChinese(en);

    public string CounterZh(string name) => name switch
    {
        "pp" => "能量点",
        "max_pp" => "最大能量点",
        "ep" => "进化点",
        "sep" => "超进化点",
        "shadow" => "墓场",
        "combo" => "连击",
        "rally" => "协作",
        "earth_sigil" => "土之印",
        "countdown" => "吟唱",
        "clues" => "线索",
        "faith" => "信仰",
        "crest" => "纹章",
        "mode_count" => "模式",
        "spellboost" => "魔力增幅",
        "fuse_count" => "融合次数",
        "fuse_cost" => "融合材料总费用",
        "fuse_kinds" => "融合材料种类数",
        "cards_returned_to_hand" => "返回手牌的张数",
        "evolve_count" => "进化次数",
        "wrath" => "血怒",
        "damage_dealt_this_turn" => "本回合造成的伤害",
        _ => _glossary.ToChinese(name),
    };

    public string StatZh(StatKey k) => k switch
    {
        StatKey.Attack => "攻击力",
        StatKey.Life => "生命值",
        StatKey.MaxLife => "生命值上限",
        StatKey.Cost => "费用",
        StatKey.BaseAttack => "基础攻击力",
        StatKey.BaseLife => "基础生命值",
        StatKey.BaseCost => "基础费用",
        StatKey.Countdown => "吟唱",
        StatKey.ChantCount => "吟唱",
        _ => k.ToString(),
    };

    public string ZoneZh(ZoneRef z) => z switch
    {
        ZoneRef.Board => "战场",
        ZoneRef.Hand => "手牌",
        ZoneRef.Deck => "牌组",
        ZoneRef.Cemetery => "墓场",
        ZoneRef.Banished => "消失区",
        ZoneRef.ExArea => "额外区",
        ZoneRef.CrestArea => "纹章区",
        ZoneRef.Leader => "主战者",
        ZoneRef.FuseMaterial => "融合素材区",
        _ => "",
    };

    public string OwnerZh(ZoneScope o) => o switch
    {
        ZoneScope.Self => "我方",
        ZoneScope.Opponent => "敌方",
        _ => "双方",
    };

    private string ClanZh(string en) => en switch
    {
        "Forestcraft" => "精灵",
        "Swordcraft" => "皇家",
        "Runecraft" => "巫师",
        "Dragoncraft" => "龙族",
        "Shadowcraft" => "死灵",
        "Bloodcraft" => "吸血鬼",
        "Havencraft" => "主教",
        "Portalcraft" => "复仇者",
        "Neutral" => "中立",
        _ => en,
    };

    /// <summary>
    /// 卡名中文化。
    ///
    /// 查不到时**保留原名**，不音译、不硬造 —— DIY 卡本来就没有中文名，
    /// 机器硬编一个反而让人认不出这就是他自己那张卡。
    /// </summary>
    private static readonly Dictionary<string, string> CardNameZh = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Fairy"] = "妖精",
        ["Bat"] = "蝙蝠",
        ["Ghost"] = "幽灵",
        ["Knight"] = "骑士",
        ["Guardian Golem"] = "守护魔像",
        ["Magic Sediment"] = "魔力残渣",
        ["Divine Evidence"] = "神启",
        ["Remnant of Hollowness"] = "虚无残渣",
        ["Kindled Blade"] = "燃刃",
        ["Ersatz Elimination"] = "替代排除",
        ["Striker Artifact"] = "突击神器",
        ["Fortifier Artifact"] = "强化神器",
        ["Gear of Ambition"] = "野心之齿轮",
        ["Ominous Artifact \u03b1"] = "不祥神器 α",
        ["Ominous Artifact \u03b2"] = "不祥神器 β",
        ["Ominous Artifact \u03b3"] = "不祥神器 γ",
        ["Silent Rider"] = "寂静的骑士",
        ["Servant of Cocytus"] = "科奇托斯的侍从",
        ["Demon of Purgatory"] = "炼狱恶魔",
        ["Astaroth's Reckoning"] = "阿斯塔罗特的结算",
        ["Prostrating Coward"] = "匍匐的懦夫",
        ["Shoddy Plaything"] = "粗制的玩具",
        ["Indomitable Fighter"] = "不屈的战士",
        ["Detective's Lens"] = "侦探的镜片",
        ["Bug Alert"] = "漏洞警报",
        ["Amataz, Origin Blader"] = "始祖剑士 阿玛兹",
        ["Aria, Lady of the Woods"] = "森林淑女 艾莉雅",
        ["Sham-Nacha, Heir to Entwining"] = "缠绕的继承者 杉那查",
        ["Witch's New Brew"] = "魔女的新酿",
        ["Runeblade Conductor"] = "符文刃指挥者",
        ["Blaze Destroyer"] = "烈焰破坏者",
        ["Godwood Staff"] = "神木之杖",
        ["Edelweiss, Sagelight Ward"] = "贤光守护 艾德薇丝",
        ["Reno, Luxwing Featherfolk"] = "光翼羽民 雷诺",
        ["Sinciro, Heir to Usurpation"] = "篡夺的继承者 辛西罗",
        ["Slaus, Revolving Wheel of Fortune"] = "旋转的命运之轮 斯劳斯",
        ["Sandalphon, Primarch Successor"] = "原初继承者 桑达尔丰",
        ["Obsessed Test Subject"] = "痴迷的试验体",
        ["Pascale's Dance"] = "帕斯卡尔的舞蹈",
        ["Mjerrabaine, Great Manifest"] = "大显身 姆耶拉班",
        ["Octrice, Hollowness Manifest"] = "虚无之显 奥克特莉丝",
        ["Glittering Gold"] = "闪耀的黄金",
        ["City of Babelon"] = "巴别伦之城",
        ["Shakdoh, Nightblossom"] = "夜花 沙克多",
        ["Adventurers' Guild"] = "冒险者公会",
        ["Ancient Cannon"] = "古代加农炮",
        ["Yuna, Occult Hunter"] = "神秘猎人 尤娜",
    };

    /// <summary>兵种中文化。查不到保留原名（DIY 兵种本来就没有官方译名）。</summary>
    public string TraitZh(string en) => (en ?? "").ToLowerInvariant() switch
    {
        "pixie" => "妖精",
        "artifact" => "神器",
        "loot" => "战利品",
        "clue" => "线索",
        "golem" => "魔像",
        "officer" => "指挥官",
        "departed" => "死者",
        "commander" => "司令官",
        "machina" => "机械",
        "levin" => "雷维翁",
        _ => en ?? "",
    };

    public string CardZh(string en)
        => string.IsNullOrWhiteSpace(en) ? ""
           : CardNameZh.TryGetValue(en.Trim(), out var zh) ? zh : en.Trim();

    private string DeckNameZh(string en) => en switch
    {
        "Apocalypse Deck" => "启示录牌组",
        "a deck of 40 cards" => "40张牌的牌组",
        "deck of 40 cards" => "40张牌的牌组",
        _ => en,
    };
}

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

        // ⓪ 构筑期修正：放在最前面，用【构筑规则】前缀与战斗效果区分开。
        // 这类词条在对局中完全不生效，混在能力里会让人以为它能打出来。
        var deckLines = DeckModifierLines(card.DeckModifiers).ToList();
        if (deckLines.Count > 0)
        {
            foreach (var line in deckLines) sb.Append(line).Append('\n');
            sb.Append('\n');
        }

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
            // 激奏本质是「作为法术打出」，不能印成【入场曲】。
            sb.Append(PrintAbilities(card.AccelerateDef.Abilities, CardKind.Spell));
            sb.Append("\n----------\n");
        }
        if (card.CrystallizeDef != null)
        {
            sb.Append("【结晶】").Append(card.CrystallizeDef.BaseCost).Append('；');
            sb.Append(PrintAbilities(card.CrystallizeDef.Abilities, CardKind.Amulet));
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
            // 必须把卡种传下去：法术的 OnPlay 不是【入场曲】。
            var line = PrintAbility(ab, card.Kind);
            if (!string.IsNullOrEmpty(line)) sb.Append(line).Append('\n');
        }

        // ④ 关联纹章 / 信仰
        if (card.CrestDef != null)
        {
            sb.Append('\n').Append("【关联纹章：").Append(CardZh(card.CrestDef.Name)).Append("】\n");
            // 纹章是常驻的护符式实体，不继承本卡的卡种。
            sb.Append(PrintAbilities(card.CrestDef.Abilities, CardKind.Amulet)).Append('\n');
        }
        if (card.FaithDef != null)
        {
            sb.Append('\n').Append("【关联信仰：").Append(CardZh(card.FaithDef.Name)).Append("】\n");
            sb.Append(PrintAbilities(card.FaithDef.Abilities, CardKind.Amulet)).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }

    public string PrintAbilities(IReadOnlyList<AbilityDef> abilities, CardKind kind = CardKind.Follower)
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
            var line = PrintAbility(ab, kind);
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
            else if (n is DamageModifierNode dm)
                // 写在 PassiveAura 里的常驻减伤 / 伤害上限，
                // 必须出现在卡面顶行的常驻段，否则读者看到的是一张白卡。
                parts.Add(PrintNode(dm));
            else if (n is ModifyCounterNode mc && mc.Op == CounterOp.Set)
                parts.Add("初始【" + CounterZh(mc.Counter.Name) + "】值为" + PrintNum(mc.Delta) + "。");
        }

        return string.Join("", parts);
    }

    /// <summary>
    /// 渲染来源卡的完整中文卡面，并把 targetAbility 对应的那一段用金底高亮标出。
    ///
    /// 左上角弹窗点开一条「受能力影响」记录时用：未命中的段落压成柔和灰蓝，
    /// 命中的那一条亮起，玩家视线 0 秒锁定生效的那句话。
    /// </summary>
    public string PrintCardWithHighlight(CardDef card, AbilityDef targetAbility)
    {
        if (card == null) return "";
        if (targetAbility == null) return PrintCard(card);

        // 同一张卡上可能有两条内容完全相同的能力（同名纹章重复授予），
        // 引用比不中时退到「打印结果相同」来匹配。
        // 常驻光环由 PrintPassiveLine 输出而不是 PrintAbility，
        // 用 PrintAbility 算签名会让 PassiveAura 的减伤光环永远匹配不上。
        string LineOf(AbilityDef a)
            => a?.Trigger?.Event == Core.CoreEvent.PassiveAura ? PrintPassiveLine(a) : PrintAbility(a);

        string targetSig = LineOf(targetAbility);
        bool IsMatch(AbilityDef ab)
            => ReferenceEquals(ab, targetAbility)
               || (ab != null && !string.IsNullOrEmpty(targetSig) && LineOf(ab) == targetSig);

        var sb = new StringBuilder();

        void AppendAbilities(IReadOnlyList<AbilityDef> list)
        {
            if (list == null) return;
            foreach (var ab in list)
            {
                string line = LineOf(ab);
                if (string.IsNullOrEmpty(line)) continue;

                if (IsMatch(ab))
                    sb.Append("▶ ").Append(line).Append('\n');
                else
                    sb.Append(line).Append('\n');
            }
        }

        AppendAbilities(card.Abilities);
        if (card.CrestDef != null)
        {
            sb.Append("【关联纹章：").Append(CardZh(card.CrestDef.Name)).Append("】").Append('\n');
            AppendAbilities(card.CrestDef.Abilities);
        }
        if (card.FaithDef != null)
        {
            sb.Append("【关联信仰：").Append(CardZh(card.FaithDef.Name)).Append("】").Append('\n');
            AppendAbilities(card.FaithDef.Abilities);
        }
        return sb.ToString().TrimEnd();
    }

    public string PrintAbility(AbilityDef ab, CardKind kind = CardKind.Follower)
    {
        if (ab == null) return "";
        var sb = new StringBuilder();

        // 进化域条件剥离：<ev>/<sev> 段编译时会附 HasStateExpr(Evolved, Self)，
        // 而触发头本身已经写了【进化时】—— 再打一遍就是「进化时：若已进化，…」这种蠢话。
        var cond = StripEvolveDomain(ab.Condition, ab.Trigger, out string domainPrefix);
        if (!string.IsNullOrEmpty(domainPrefix)) sb.Append(domainPrefix);

        // 注：OnAttack 条件里的「非（攻击目标为主战者）」即「攻击随从」，
        // 卡面写作【攻击时】若攻击随从，…。它作为普通条件印出来即可，
        // 不另立触发头、也不剥掉 —— 剥掉玩家就不知道这个效果有前提。

        // 与 Costs 同源的条件必须剥离：编译器把「连击3」「唤灵6」「爆能5」
        // 同时写进 Costs 与 Condition，两边都印就成了
        // 「【连击3】若连击值≥3，」这种同一件事说两遍。
        cond = StripRedundantCostConditions(cond, ab.Costs);

        sb.Append(TriggerHeader(ab.Trigger, kind));

        if (ab.Costs != null && ab.Costs.Count > 0)
            sb.Append(CostsText(ab.Costs));

        if (!IsTrue(cond))
            sb.Append("若").Append(PrintBool(cond)).Append('，');

        var body = PrintNodes(ab.Body);
        int cap = ab.Trigger?.MaxTriggersPerTurn ?? 0;
        string capTxt = cap > 0
            ? (cap == 1 ? "（1回合中限1次）" : "（1回合中可触发" + cap + "次）")
            : "";
        if (string.IsNullOrEmpty(body)) return (sb.ToString() + capTxt).TrimEnd();
        sb.Append(body).Append(capTxt);
        return sb.ToString().TrimEnd();
    }

    // ---------------------------------------------------------------- 触发头



    /// <summary>
    /// 剥离与 Costs 同源的比较条件。
    ///
    /// 只认「计数器比较 + 阈值与某个代价完全相同」这一种精确形态，
    /// 任何不确定的情况都保留原条件 —— 剥离是美化，不能改变语义。
    /// </summary>
    private static BoolExpr StripRedundantCostConditions(BoolExpr cond, IReadOnlyList<CostSpec> costs)
    {
        if (cond == null || costs == null || costs.Count == 0) return cond;

        bool Redundant(BoolExpr e)
        {
            if (e is not CompareExpr c || c.Op != CompareOp.Ge) return false;
            if (c.Left is not NumExpr { Op: NumOp.Counter } l) return false;
            if (c.Right is not NumExpr { Op: NumOp.Const } r) return false;
            string ctr = l.Counter.Name ?? "";
            foreach (var cs in costs)
            {
                if (cs?.Amount is not NumExpr { Op: NumOp.Const } amt || amt.Value != r.Value) continue;
                string lbl = (cs.Label ?? "").ToLowerInvariant().Replace("_", "").Replace(" ", "");
                bool hit =
                    (lbl == "combo" && ctr == "combo") ||
                    (lbl == "rally" && (ctr == "match_play" || ctr == "rally")) ||
                    (lbl is "necromancy" or "necromance" && (ctr == "shadow" || ctr == "necromancy")) ||
                    (lbl == "earthrite" && (ctr == "earth_sigil" || ctr == "earth_rite")) ||
                    // engage 与 enhance 都是 pp_at_play 比较，但语义不同：
                    //   engage  = 主动启动的付费，触发头里要印【消耗N点能量点】，条件该剥；
                    //   enhance = 爆能档位，档位树已在正文展开，条件该剥。
                    // 两者都只在阈值与代价完全相等时才剥，避免误删真实的独立条件。
                    (lbl == "enhance" && ctr == "pp_at_play") ||
                    (lbl == "engage" && ctr == "pp_at_play");
                if (hit) return true;
            }
            return false;
        }

        if (cond is AndExpr a)
        {
            var kept = a.Items.Where(x => !Redundant(x)).ToList();
            if (kept.Count == a.Items.Count) return cond;
            return kept.Count == 0 ? BoolExpr.True
                 : kept.Count == 1 ? kept[0]
                 : new AndExpr(kept);
        }
        return Redundant(cond) ? BoolExpr.True : cond;
    }
    private string TriggerHeader(TriggerSpec trig, CardKind kind = CardKind.Follower)
    {
        if (trig == null) return "";
        return DecorateTrigger(trig, TriggerHeaderCore(trig, kind));
    }

    /// <summary>
    /// 触发头装饰：回合限定与每回合触发上限。
    ///
    /// 必须独立于 TriggerHeaderCore —— 后者有多条提前 return 的分支
    /// （旁观型、玩家行为型），在末尾追加逻辑会漏掉它们。
    /// </summary>
    private static string DecorateTrigger(TriggerSpec trig, string head)
    {
        if (string.IsNullOrEmpty(head)) return head;
        if (trig.DuringTurn != TurnOwnerScope.Any
            && trig.Event is not (CoreEvent.TurnStart or CoreEvent.TurnEnd)
            && !head.Contains("回合中"))
        {
            head = (trig.DuringTurn == TurnOwnerScope.Self ? "我方回合中，" : "敌方回合中，") + head;
        }
        return head;
    }
    private string TriggerHeaderCore(TriggerSpec trig, CardKind kind)
    {
        if (trig == null) return "";

        // 法术从手牌施放触发的也是 OnPlay，但它**不是【入场曲】**：
        // 卡面上法术从不印【入场曲】，只印效果正文。
        // 以前不分卡种，所有法术都被印成了入场曲。
        bool isSpellCast = kind == CardKind.Spell
                           && trig.Event == CoreEvent.OnPlay
                           && trig.SubjectFilter == null;

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
        // 法术的打出效果没有触发头，直接印正文。
        if (isSpellCast) return "";

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
            // 随从攻击时是专用触发头，与普通「攻击时」不是一回事。
            // 卡面统一写作【攻击时】。「攻击随从」由条件子句表达，
            // 不另立触发头 —— 官方简中卡面没有【随从攻击时】这种写法。
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

        // 【进化时】与「当本随从进化时」是两种东西：
        //   ManualEvolveOnly=true  -> 只有真花 EP 进化才触发，写作【进化时】
        //   ManualEvolveOnly=false -> 能力效果自动进化也触发，写作「当…进化时，」
        if (trig.Event is CoreEvent.OnEvolve or CoreEvent.OnSuperEvolve && !trig.ManualEvolveOnly)
        {
            string evVerb = trig.Event == CoreEvent.OnEvolve ? "进化" : "超进化";
            return "当本随从" + evVerb + "时，";
        }


        return head;
    }

    /// <summary>及物动词：它们要放在宾语前面。</summary>
    /// <summary>
    /// 构筑期修正的地道简中描述。
    ///
    /// 这类能力在对局中不生效，卡面上必须放在最上方单独一行，
    /// 而且措辞要用官方的「【构筑规则】」前缀 —— 否则玩家会以为它是战斗效果。
    /// </summary>
    private IEnumerable<string> DeckModifierLines(IReadOnlyList<DeckBuildModifier> mods)
    {
        if (mods == null) yield break;
        foreach (var m in mods)
        {
            if (m == null) continue;
            int v = m.Value;
            string sign = v >= 0 ? "+" : "";
            switch (m.Kind)
            {
                case DeckModKind.ModifyDeckSize:
                    yield return "【构筑规则】本牌组的构筑卡牌张数上限与下限" + sign + v + "。";
                    break;

                case DeckModKind.AllowSpecificClan:
                    yield return "【构筑规则】本牌组在构筑时可以放入【"
                                 + ClanZh(m.TargetClan) + "】职业的卡牌。";
                    break;

                case DeckModKind.AllowChosenClans:
                    yield return "【构筑规则】本牌组在构筑时可以任选" + v + "个其他职业，"
                                 + "并放入该职业的卡牌。";
                    break;

                case DeckModKind.OverrideSelfCopyLimit:
                    yield return "【构筑规则】本卡牌在牌组中最多可以放入" + v + "张。";
                    break;

                case DeckModKind.ModifyLeaderStartLife:
                    yield return "【构筑规则】对战开始时，你的主战者最大生命值与初始生命值"
                                 + sign + v + "。";
                    break;

                case DeckModKind.ModifyStartingHandSize:
                    yield return "【构筑规则】对战开始时，你的起手抽牌数" + sign + v + "。";
                    break;

                case DeckModKind.ModifyStartEvolvePoints:
                    if (m.Value != 0)
                        yield return "【构筑规则】对战开始时，你的进化点" + (m.Value >= 0 ? "+" : "") + m.Value + "。";
                    if (m.Value2 != 0)
                        yield return "【构筑规则】对战开始时，你的超进化点" + (m.Value2 >= 0 ? "+" : "") + m.Value2 + "。";
                    break;
            }
        }
    }

    /// <summary>能力持有者（复刻类效果的目标）。</summary>
    private string AbilityOwnerZh(TargetSpec t)
    {
        if (t == null) return "本卡牌";
        if (t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelf) return "本卡牌";
        return PrintTarget(t);
    }

    /// <summary>能力名（触发头的叫法，不是动词）。</summary>
    private static string AbilityLabelZh(CoreEvent ev) => ev switch
    {
        CoreEvent.OnPlay => "入场曲",
        CoreEvent.OnDestroy => "谢幕曲",
        CoreEvent.OnEvolve => "进化时",
        CoreEvent.OnSuperEvolve => "超进化时",
        CoreEvent.OnEngage => "启动",
        CoreEvent.OnAttack => "攻击时",
        CoreEvent.OnClash => "交战时",
        CoreEvent.OnFuse => "融合",
        CoreEvent.OnSpellboost => "魔力增幅",
        CoreEvent.PassiveAura => "常驻",
        _ => ev.ToString(),
    };

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
                case CostKind.UsePp:
                    // 爆能强化已在正文以【爆能强化】N；独立成句，触发头里再印一遍
                    // 「【5点能量点】」就是同一件事写两遍。
                    if ((c.Label ?? "").ToLowerInvariant() == "enhance") break;
                    // 启动能力：卡面写作【消耗N点能量点】。0 费启动不印多余的代价栏。
                    if ((c.Label ?? "").ToLowerInvariant() == "engage")
                    {
                        if (amt != "0") parts.Add("消耗" + amt + "点能量点");
                        break;
                    }
                    parts.Add(amt + "点能量点");
                    break;
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


    /// <summary>调试追踪。RESHADOW_TRACE=1 时输出到 stderr。</summary>
    private static void Trace(string msg)
    {
        if (Environment.GetEnvironmentVariable("RESHADOW_TRACE") == "1")
            Console.Error.WriteLine("[CP] " + msg);
    }
    public string PrintNodes(IReadOnlyList<EffectNode> nodes)
    {
        if (nodes == null || nodes.Count == 0) return "";
        // 「选择 X，然后对它做 Y」要连成一句：中文里「选择…。对…」读起来像两件事。
        var parts = new List<string>();
        // 爆能分档树前面可能有公共前置节点（如 select），
        // 它属于「基础效果」，要并入前缀比较，否则基础档会整段消失。
        var prefixNodes = new List<EffectNode>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var cur = nodes[i];
            if (cur is ConditionalNode cn0x && IsEnhanceTree(cn0x))
            {
                // 前置节点已经进 parts 了，爆能树自己不再重复印它们，
                // 但要用它们参与「基础档」的构成与前缀比较。
                parts.Add(PrintEnhanceWithPrefix(cn0x, prefixNodes, emitPrefix: false));
                prefixNodes.Clear();
                continue;
            }
            var s = PrintNode(cur);
            if (string.IsNullOrEmpty(s)) continue;
            parts.Add(s);
            if (cur is SelectTargetNode)
            {
                prefixNodes.Add(cur);
            }
        }
        return string.Join("", parts);
    }

    public string PrintNode(EffectNode n)
    {
        if (n == null) return "";
        // 节点级前置条件（@if）必须印出来。漏了它就是静默吞条件 ——
        // AST 里有、卡面看不到，玩家完全不知道这个效果是有前提的。
        string prefix = "";
        if (n.If != null && n.If is not ConstBoolExpr { Value: true })
            prefix = "若" + PrintBool(n.If) + "，";
        return prefix + PrintNodeCore(n);
    }

    private string PrintNodeCore(EffectNode n)
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

            case RevealCardNode rv:
                return "公开" + PrintTarget(rv.Target) + "。";

            case DamageModifierNode dm:
            {
                string who = PrintTarget(dm.Target);
                string amt = PrintNum(dm.Amount);
                return dm.Kind switch
                {
                    DamageAuraKind.TakenDelta => "使" + who
                        + (dm.IsReduction ? "受到的伤害-" : "受到的伤害+") + amt + "。",
                    DamageAuraKind.TakenCap => "使" + who + "受到的伤害不能超过" + amt + "。",
                    DamageAuraKind.DealtDelta => "使" + who
                        + (dm.IsReduction ? "造成的伤害-" : "造成的伤害+") + amt + "。",
                    DamageAuraKind.DealtCap => "使" + who + "造成的伤害不能超过" + amt + "。",
                    _ => "",
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
                // CopySource 非空时是「变身成某物的复制」，CardId/CardName 都是空的，
                // 按卡名印会印出空的《》。
                if (t.CopySource != null)
                    return "使" + PrintTarget(t.Target) + "【变身】为" + PrintTarget(t.CopySource) + "的复制。";
                return "使" + PrintTarget(t.Target) + "【变身】为《" + CardZh(t.IntoCardName) + "》。";

            case EvolveNode e:
                return "使" + PrintTarget(e.Target) + (e.IsSuperEvolve ? "【超进化】" : "【进化】") + "。";

            case ModifyCounterNode c:
                return PrintCounter(c);

            case InvokeAbilityNode i:
                // 能力名要用**触发头**的叫法（入场曲/谢幕曲/进化时），
                // 不是动词（打出/被破坏）。打错会写成「发动本卡牌的【打出】能力」。
                return "发动" + AbilityOwnerZh(i.Target) + "的【" + AbilityLabelZh(i.Event) + "】能力。";

            case ConditionalNode cd:
                return PrintConditional(cd);

            case LoopNode l:
            {
                // RepeatUntil 没有 Count（是 null），按次数印会得到「重复0次」。
                if (l.Kind == LoopKind.RepeatUntil)
                    return "重复以下效果直到" + PrintBool(l.Until) + "为止：「" + PrintNodes(l.Body) + "」";
                string times = l.Kind == LoopKind.ForEach
                    ? "对" + PrintTarget(l.Collection)
                    : PrintNum(l.Count) + "次";
                return "重复以下效果" + times + "：「" + PrintNodes(l.Body) + "」";
            }

            case BranchModeNode b:
                return PrintBranch(b);

            case MarkVictoryCardNode mvc:
                return "将" + OwnerZh(mvc.Owner) + "牌组底部的死神卡变为【胜利卡】。";

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

        // 卡池检索：标准发现 / 混沌发现 / 随机生成。
        // 三者措辞不同，玩家要能一眼分清「从本环境找」还是「从全宇宙找」。
        if (sp.SourceKind == CardSourceKind.PoolQuery)
        {
            // 范围三态：显式卡包 > 全卡包 > 当前卡池。
            // 有 HasPackExpr 时不再重复印「从当前卡池中」——那会自相矛盾。
            string packId = FindPackFilter(sp.CopyFrom?.Where);
            bool chaos = sp.CopyFrom != null && sp.CopyFrom.Zone == ZoneRef.GlobalPool;
            bool choose = sp.CopyFrom != null && sp.CopyFrom.Mode == SelectMode.Choose;

            string from = !string.IsNullOrEmpty(packId)
                ? "从【" + packId.Replace("|", "、") + "】卡包中"
                : (chaos ? "从全部卡包中" : "从当前卡池中");

            string verb = choose ? (chaos ? "【混沌发现】" : "【发现】") : "随机选择";
            string what = PrintPoolFilter(sp.CopyFrom);

            // X 选 Y：默认三选一时不啰嗦，自定义时才写出来。
            int offerX = sp.OfferCount > 0 ? sp.OfferCount : 3;
            int pickY = sp.Count is NumExpr cnt && cnt.Op == NumOp.Const ? cnt.Value : 1;
            string label = (choose && (offerX != 3 || pickY != 1))
                ? verb + "（" + offerX + "选" + pickY + "）"
                : verb;

            string countText = pickY > 1 ? pickY.ToString() : count;
            return sp.ToZone == ZoneRef.Hand
                ? from + label + countText + "张" + what + "加入我方手牌。"
                : from + label + countText + "个" + what + "召唤到战场上。";
        }

        if (sp.SourceKind == CardSourceKind.CopyOfTarget)
        {
            string kind = sp.IsExactCopy ? "完整复制" : "复制";
            return sp.ToZone == ZoneRef.Hand
                ? "将" + count + "张" + PrintTarget(sp.CopyFrom) + "的" + kind + "加入我方手牌。"
                : "召唤" + count + "个" + PrintTarget(sp.CopyFrom) + "的" + kind + "到战场上。";
        }

        // CopyOfPrevious（spawn_previous）等形态的 CardName 是空串，
        // 直接拼会印出空书名号《》。
        string cardTitle = string.IsNullOrEmpty(sp.CardName)
            ? "同名卡牌"
            : "《" + CardZh(sp.CardName) + "》";
        return sp.ToZone == ZoneRef.Hand
            ? "将" + count + "张" + cardTitle + "加入我方手牌。"
            : "召唤" + count + "个" + cardTitle + "到战场上。";
    }

    /// <summary>
    /// 卡池检索的筛选条件描述（「费用≤3的随从」这类）。
    /// 条件本身是通用 BoolExpr，这里只挑最常见的几类译出来；
    /// 译不出的部分退回「卡牌」，宁可笼统也不要印出英文。
    /// </summary>
    /// <summary>从 Where 里找 PackId（含 And/Or/Not 嵌套）。</summary>
    private static string FindPackFilter(BoolExpr e) => e switch
    {
        HasPackExpr hp => hp.PackId,
        AndExpr a => a.Items.Select(FindPackFilter).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
        OrExpr o => o.Items.Select(FindPackFilter).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
        NotExpr nt => FindPackFilter(nt.Inner),
        _ => null,
    };

    private string PrintPoolFilter(TargetSpec spec)
    {
        if (spec == null) return "卡牌";

        // 筛选条件住在通用的 Where 布尔表达式里（与目标筛选共用同一套求值器），
        // 所以这里只从 Where 里挑最常见的几类译出来，
        // 译不出的部分退回「卡牌」—— 宁可笼统，也不要印出英文。
        string kind = "卡牌";
        int? costMin = null;
        int? costMax = null;
        string trait = "";
        string pack = "";
        string clan = "";

        void Walk(BoolExpr e)
        {
            switch (e)
            {
                case AndExpr a: foreach (var x in a.Items) Walk(x); break;
                case HasKindExpr hk:
                    kind = hk.Kind switch
                    {
                        CardKind.Follower => "随从",
                        CardKind.Spell => "法术",
                        CardKind.Amulet => "护符",
                        _ => "卡牌",
                    };
                    break;
                // 费用上下限要分别记下来再合并渲染。
                // 早先用一个 string 变量，后写的分支覆盖先写的 ——
                // 于是「2~4 费」被印成「不超过 4 费」，下限整个消失。
                case CompareExpr cmp when cmp.Left?.Op == NumOp.Property:
                    if (cmp.Left.Stat == StatKey.Cost && cmp.Right is NumExpr r && r.Op == NumOp.Const)
                    {
                        switch (cmp.Op)
                        {
                            case CompareOp.Le:
                            case CompareOp.Lt: costMax = r.Value; break;
                            case CompareOp.Ge:
                            case CompareOp.Gt: costMin = r.Value; break;
                            case CompareOp.Eq: costMin = costMax = r.Value; break;
                        }
                    }
                    break;
                case HasTraitExpr ht:
                {
                    // 多兵种：卡面可能同时要求两个兵种，累加而非覆盖。
                    var tz = TraitZh(ht.Trait.Name) ?? "";
                    if (!string.IsNullOrEmpty(tz) && !trait.Contains(tz))
                        trait = string.IsNullOrEmpty(trait) ? tz : trait + "·" + tz;
                    break;
                }
                // 卡包与职业筛选也必须渲染出来：
                // 少了它们，卡面会印成「发现1张随从」，
                // 玩家完全看不出它限定在某个卡包或某个职业里。
                // 卡包范围由 PrintSpawn 的 from 前缀统一表达，
                // 这里再印一次会变成「从【A】卡包中…来自【A】卡包的」。
                case HasPackExpr: break;
                case HasClanExpr hcl:
                    if (hcl.IsSelfClan) clan = "本职业的";
                    else if (hcl.Clans != null && hcl.Clans.Count > 0)
                        clan = string.Join("或", hcl.Clans.Select(x => ClanZh(x.Name))) + "职业的";
                    else clan = ClanZh(hcl.Clan.Name) + "职业的";
                    break;
            }
        }
        if (spec.Where != null) Walk(spec.Where);

        string cost;
        if (costMin.HasValue && costMax.HasValue)
            cost = costMin == costMax ? "费用为" + costMin.Value + "的"
                                      : "费用为" + costMin.Value + "~" + costMax.Value + "的";
        else if (costMax.HasValue) cost = "费用不超过" + costMax.Value + "的";
        else if (costMin.HasValue) cost = "费用不低于" + costMin.Value + "的";
        else cost = "";

        return pack + clan + cost + trait + kind;
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

        // 「counter pp = ctr:max_pp」就是完全回复能量点，卡面有专门写法。
        if (c.Op == CounterOp.Set && name == "pp"
            && c.Delta is NumExpr { Op: NumOp.Counter } dctr
            && (dctr.Counter.Name ?? "") == "max_pp")
            return "完全回复" + OwnerZh(c.Owner) + "的能量点。";

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


    /// <summary>该条件节点是否是爆能强化分档树的顶层（ctr:pp_at_play >= N）。</summary>
    private static bool IsEnhanceTree(ConditionalNode c)
        => c.Condition is CompareExpr { Op: CompareOp.Ge, Left: NumExpr { Op: NumOp.Counter } l }
           && (l.Counter.Name ?? "") == "pp_at_play";

    /// <summary>
    /// 带前置节点的爆能树渲染。
    ///
    /// 影之诗的爆能卡常见布局是「select X / if pp>=N: <用X做事甲> else: <用X做事乙>」，
    /// select 在树外、两档共用。基准档必须把 select 也算进去，
    /// 否则「基础效果」为空，替换型会丢掉默认分支。
    /// </summary>
    private string PrintEnhanceWithPrefix(ConditionalNode c, List<EffectNode> prefix, bool emitPrefix = true)
    {
        var tiers = new List<(int n, IReadOnlyList<EffectNode> body)>();
        var baseBody = new List<EffectNode>(prefix);
        var cur = c;
        while (true)
        {
            if (!IsEnhanceTree(cur as ConditionalNode)) return PrintConditional(c);
            var cmp = (CompareExpr)cur.Condition;
            if (cmp.Right is not NumExpr { Op: NumOp.Const } rr) return PrintConditional(c);
            tiers.Add((rr.Value, cur.Then));
            if (cur.Else is { Count: 1 } && cur.Else[0] is ConditionalNode next) { cur = next; continue; }
            if (cur.Else != null) baseBody.AddRange(cur.Else);
            break;
        }
        if (tiers.Count == 0) return PrintConditional(c);

        var sb = new StringBuilder();
        // emitPrefix=false：prefix 的文本已由调用方印过，这里只补「基础档独有的部分」，
        // 但仍参与后面的前缀比较，否则替换型会丢掉默认分支。
        int pre = emitPrefix ? 0 : prefix.Count;
        string baseTail = pre > 0 ? PrintNodes(baseBody.Skip(pre).ToList()) : PrintNodes(baseBody);
        if (!string.IsNullOrEmpty(baseTail)) sb.Append(baseTail);

        var prevNodes = baseBody.ToList();
        foreach (var (n, body) in tiers.OrderBy(t => t.n))
        {
            var tierNodes = body?.ToList() ?? new List<EffectNode>();
            string tierText = PrintNodes(tierNodes);
            if (string.IsNullOrEmpty(tierText)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("【爆能强化】").Append(n).Append("；");

            int skip = 0;
            for (int i = 0; i < prevNodes.Count && i < tierNodes.Count; i++)
            {
                if (SameNode(tierNodes[i], prevNodes[i])) { skip++; continue; }
                break;
            }
            bool isPrefix = prevNodes.Count > 0 && skip == prevNodes.Count && tierNodes.Count > skip;

            if (isPrefix)
                sb.Append("额外").Append(PrintNodes(tierNodes.Skip(skip).ToList()));
            else if (prevNodes.Count > 0)
                sb.Append("改为").Append(tierText);
            else sb.Append(tierText);

            prevNodes = tierNodes;
        }
        return sb.ToString();
    }


    /// <summary>
    /// 判定两个节点是否等价。
    ///
    /// 不能只用 ReferenceEquals：从 py_ast / ast_json 加载的卡，
    /// Then 与 Else 里的同形节点是分别反序列化出来的不同实例，
    /// 引用比较永远为 false，累加前缀就识别不出来。
    /// 退回用规范打印文本比对。
    /// </summary>
    private static bool SameNode(EffectNode a, EffectNode b)
        => ReferenceEquals(a, b)
           || (a != null && b != null && AstEnglishPrinter.PrintNode(a) == AstEnglishPrinter.PrintNode(b));
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
        string cond = PrintBool(c.Condition);

        if (string.IsNullOrEmpty(els)) return "若" + cond + "，" + then;

        // 影之诗的行文习惯是「先写默认情况，再写特殊条件下的变化」，
        // 而不是「若 X 则 A，否则 B」。按累加 / 替代两种关系分别措辞：
        //   累加型：Then 以 Else 为前缀 -> <Else>若<cond>，则额外<Then 新增部分>
        //   替代型：两者互斥           -> <Else>若<cond>，则改为<Then>
        // elif 多分支链（融合分档表 1/2/3+ 之类）：Else 里本身又是 ConditionalNode，
        // 没有无条件兜底动作。倒过来印会把最后一档的条件吞掉，必须保持正序。
        if (c.Else.Any(n => n is ConditionalNode))
            return "若" + cond + "，" + then + "否则，" + els;

        int skip = 0;
        for (int i = 0; i < c.Else.Count && i < c.Then.Count; i++)
        {
            if (SameNode(c.Then[i], c.Else[i])) { skip++; continue; }
            break;
        }
        bool isPrefix = skip == c.Else.Count && c.Then.Count > skip;

        if (isPrefix)
            return els + "若" + cond + "，则额外" + PrintNodes(c.Then.Skip(skip).ToList());
        return els + "若" + cond + "，则改为" + then;
    }

    private string PrintBranch(BranchModeNode b)
    {
        // 必须按表达式打印：PickCount 可能是动态的
        // （「发动 Y 个，Y 为此卡费用」），写死 EvalConst 会印成 1。
        string pick = b.PickCount != null ? PrintNum(b.PickCount) : "1";
        // GrantTarget 非空表示「把这几条能力授予某个目标」，而不是自己发动。
        if (b.GrantTarget != null)
        {
            string g = PrintTarget(b.GrantTarget);
            string intro = b.Kind == ModeDispatchKind.Random
                ? "给予" + g + "以下能力中随机" + pick + "个："
                : "给予" + g + "以下能力：";
            return intro + PrintBranchOptions(b);
        }

        string head = b.Kind switch
        {
            ModeDispatchKind.Choose => "选择" + pick + "个【模式】发动：",
            ModeDispatchKind.Random => "从以下能力中随机发动" + pick + "个：",
            ModeDispatchKind.Sequence => "按顺序发动以下全部能力：",
            ModeDispatchKind.RandomUnused => "从以下尚未发动过的能力中随机发动" + pick + "个：",
            ModeDispatchKind.SequenceUnused => "从以下尚未发动过的能力中按顺序发动：",
            _ => "发动以下能力：",
        };
        return head + PrintBranchOptions(b);
    }

    /// <summary>逐条打印模式选项（①②③…），由 PrintBranch 与 GrantTarget 分支共用。</summary>
    private string PrintBranchOptions(BranchModeNode b)
    {
        var sb = new StringBuilder();
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

    /// <summary>在 Where 里找排除词，含 AndExpr 嵌套的情况。</summary>
    private static NotSameEntityExpr FindExclusion(BoolExpr where)
    {
        if (where == null) return null;
        if (where is NotSameEntityExpr nse) return nse;
        if (where is AndExpr a)
            foreach (var it in a.Items)
            {
                var f = FindExclusion(it);
                if (f != null) return f;
            }
        return null;
    }
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
                // 序数槽位："the second selected follower" -> "第2个所选目标"。
                // 不认它会落到 default，把「第 2 个」渲染成「该目标」，与第 1 个完全同形。
                case var s when TargetSpec.TryParseChooseSlotIndex(s, out int ci):
                    return "第" + ci + "个所选目标";
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
        // +leader 表示「连同主战者一起」。原实现写死成「敌方的主战者与全体敌方随从」，
        // 且前置条件是 Owner == Any —— 这让 self:board +leader 也被印成敌方。
        // 口径：只看 IncludeLeader，主语跟随 t.Owner。
        if (t.IncludeLeader)
        {
            string who = t.Owner switch
            {
                ZoneScope.Self => "我方的主战者与全体我方随从",
                ZoneScope.Opponent => "敌方的主战者与全体敌方随从",
                _ => "双方的主战者与全体随从",
            };
            // 必须直接 return！否则会继续往下走「量词」与「名词」两步，
            // 印成「敌方的主战者与全体敌方随从所有卡牌造成2点伤害」。
            if (t.Mode == SelectMode.All)
                return who;
            string cntL = t.Count != null ? PrintNum(t.Count) : "1";
            string oneWho = t.Owner switch
            {
                ZoneScope.Self => "我方主战者或我方随从",
                ZoneScope.Opponent => "敌方主战者或敌方随从",
                _ => "主战者或随从",
            };
            return (t.Mode == SelectMode.Random ? "随机" : "") + cntL + "个" + oneWho;
        }
        else
        {
            if (owner.Length > 0) sb.Append(owner);
            if (zone.Length > 0) sb.Append(zone).Append('的');
        }

        // ② 排除词。只在这里打一次 —— CollectWhere 里遇到 NotSameEntityExpr
        // 也会打，两者叠加就成了「其他的1个其他的」。
        // Where 常是 AndExpr(排除, 其他条件)，只判顶层会漏掉排除词。
        var exclusion = FindExclusion(t.Where);
        if (exclusion != null)
        {
            if (exclusion.ExcludeSlot == TargetSpec.SlotOpposing) sb.Append("除交战目标以外的");
            else if (TargetSpec.TryParseChooseSlotIndex(exclusion.ExcludeSlot, out var cni))
                sb.Append("除第").Append(cni).Append("个所选目标以外的");
            else sb.Append("其他的");
        }

        // ③ 选法 + 数量
        // 必须用 PrintNum：Count 可能是动态表达式（如「破坏 X 个」），
        // EvalConst 会把动态变量一律压成 1。
        string cnt = t.Count != null ? PrintNum(t.Count) : "1";
        switch (t.Mode)
        {
            case SelectMode.All: sb.Append("所有"); break;
            case SelectMode.Random: sb.Append("随机" + cnt + "个"); break;
            case SelectMode.First: sb.Append("前" + cnt + "个"); break;
            case SelectMode.Choose: sb.Append(cnt + "个"); break;
        }

        // ④ 排序与去重
        // TopN：排序后只取前 N 个。不印的话「前3张」会退化成「所有」。
        if (t.TopN > 0) sb.Append("前").Append(t.TopN).Append("个");
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
        string where = PrintTargetWhere(t.Where, t, skipExclusion: exclusion != null);
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
            case HasPackExpr hp:
                parts.Add("来自【" + hp.PackId + "】卡包的");
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
                    // 字段名有误导性：CombatResolver 里写的是
                    //   attacker.CurrentAttackHitLeader = defender.Zone == ZoneRef.Leader;
                    // 即「这一击打的是主战者」，不是「上回合攻击过」。
                    UnitState.CurrentAttackHitLeader => "攻击目标为主战者",
                    UnitState.Evolved => "已进化的",
                    UnitState.SuperEvolved => "已超进化的",
                    // 缺这一条时，《古旧天剑》的「爆能强化的卡牌」会印成光秃秃的「卡牌」。
                    UnitState.Enhanced => "【爆能强化】的",
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
            // 具名卡与「或」列表：融合材料「Ominous Artifact β or γ」的 Where
            // 就是 OrExpr(HasCardNameExpr, HasCardNameExpr)，漏了会印成空白「卡牌」。
            case HasCardNameExpr hcnW:
                parts.Add(hcnW.CardId == 0 && string.IsNullOrEmpty(hcnW.CardName) && hcnW.Of != null
                    ? "与" + PrintTarget(hcnW.Of) + "同名的"
                    : "《" + CardZh(hcnW.CardName) + "》");
                break;

            case OrExpr oW:
            {
                var branchTxt = new List<string>();
                foreach (var sub in oW.Items)
                {
                    var tmp = new List<string>();
                    CollectWhere(sub, tmp, skipExclusion);
                    if (tmp.Count > 0) branchTxt.Add(string.Concat(tmp).TrimEnd('的'));
                }
                if (branchTxt.Count > 0) parts.Add(string.Join("或", branchTxt) + "的");
                break;
            }

            case NotSameEntityExpr: if (!skipExclusion) parts.Add("其他的"); break;
            // 否定修饰词绝不能直接 break —— "an unevolved follower"、
            // "a non-Encroacher follower"、"a card not named X" 全挂在 NotExpr 上，
            // 吞掉它们等于把这些关键限定词从卡面抹掉。
            case NotExpr nt:
                switch (nt.Inner)
                {
                    case HasStateExpr { State: UnitState.Evolved }: parts.Add("未进化的"); break;
                    case HasStateExpr { State: UnitState.SuperEvolved }: parts.Add("未超进化的"); break;
                    case HasStateExpr { State: UnitState.AttackedThisTurn }: parts.Add("本回合尚未攻击的"); break;
                    case IsPreviousExpr: parts.Add("未被选择的"); break;
                    case HasTraitExpr htNot: parts.Add("非" + TraitZh(htNot.Trait.Name) + "兵种的"); break;
                    case HasKeywordExpr hkNot: parts.Add("未拥有【" + KeywordZh(hkNot.Keyword.Name) + "】的"); break;
                    case HasCardNameExpr hcnNot:
                        parts.Add(string.IsNullOrEmpty(hcnNot.CardName)
                            ? "卡名不同的"
                            : "卡名不为《" + CardZh(hcnNot.CardName) + "》的");
                        break;
                    default:
                    {
                        string inner = PrintBool(nt.Inner);
                        if (!string.IsNullOrEmpty(inner)) parts.Add("不满足「" + inner + "」的");
                        break;
                    }
                }
                break;
        }
    }

    // ---------------------------------------------------------------- 布尔 / 数值


    /// <summary>
    /// 固定阈值计数器比较 -> 官方术语。
    ///
    /// 只在阈值精确命中时翻译，其余原样返回 null（交给普通比较渲染）——
    /// 名词化不能改变语义，max_pp>=10 硬翻成【觉醒】会让玩家误判条件。
    /// </summary>
    private static string TermOfCompare(CompareExpr c)
    {
        if (c.Left is not NumExpr { Op: NumOp.Counter } l0) return null;
        if (c.Right is not NumExpr { Op: NumOp.Const } r0) return null;

        // 「本回合我方随从尚未攻击」是 == 0，不是 >= 0。
        // 放在 Ge 检查之前，否则这条永远进不去，卡面会印出英文内码。
        if (c.Op == CompareOp.Eq && (l0.Counter.Name ?? "") == "turn_ally_attacks" && r0.Value == 0)
            return "本回合中我方随从尚未进行攻击";

        if (c.Op != CompareOp.Ge) return null;
        if (c.Left is not NumExpr { Op: NumOp.Counter } l) return null;
        if (c.Right is not NumExpr { Op: NumOp.Const } r) return null;
        string n = l.Counter.Name ?? "";
        return (n, r.Value) switch
        {
            ("max_pp", 7) => "【觉醒】状态已发动",
            ("wrath", 7) => "【狂乱】状态已发动",
            ("skybound_gauge", 10) => "满足【奥义】10条件",
            ("skybound_gauge", 15) => "满足【解放奥义】15条件",
            ("turn_ally_attacks", 0) => "本回合中我方随从尚未进行攻击",
            ("ally_attacked_leader_last_turn", 1) => "上回合中有我方随从攻击过敌方主战者",
            _ => null,
        };
    }
    public string PrintBool(BoolExpr e)
    {
        if (e == null) return "";
        switch (e)
        {
            case ConstBoolExpr cb: return cb.Value ? "成立" : "不成立";
            case AndExpr a:
            {
                // 融合分档表生成的 [X >= N and X <= N] 是精确档位，简化为「X为N」。
                // 原样印会变成拗口的「融合次数≥1且融合次数≤1」。
                if (a.Items is { Count: 2 }
                    && a.Items[0] is CompareExpr { Op: CompareOp.Ge, Right: NumExpr { Op: NumOp.Const } rf1 } cf1
                    && a.Items[1] is CompareExpr { Op: CompareOp.Le, Right: NumExpr { Op: NumOp.Const } rf2 } cf2
                    && rf1.Value == rf2.Value
                    && cf1.Left != null && cf2.Left != null
                    && AstEnglishPrinter.PrintNum(cf1.Left) == AstEnglishPrinter.PrintNum(cf2.Left))
                    return PrintNum(cf1.Left) + "为" + rf1.Value;

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
            case NotExpr n:
                // 「非（攻击目标为主战者）」→「攻击随从」，这是卡面写法。
                if (n.Inner is HasStateExpr { State: UnitState.CurrentAttackHitLeader })
                    return "攻击随从";
                return "非（" + PrintBool(n.Inner) + "）";

            case CompareExpr c:
            {
                // 固定阈值计数器比较先翻成官方术语 —— 直接印
                // 「若最大能量点值≥7」是机翻味，卡面写的是「若【觉醒】状态已发动」。
                string term = TermOfCompare(c);
                if (term != null) return term;

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
            case HasStateExpr hs: return StateZh(hs.State);
            case NotSameEntityExpr: return "不是同一个目标";

            case DeckRuleExpr dr:
            {
                string who = OwnerZh(dr.Owner);
                return dr.Rule switch
                {
                    DeckRule.NoDuplicates => who + "牌组中没有重复的卡牌",
                    DeckRule.EvenCostOnly => who + "牌组中只有偶数费用的卡牌",
                    DeckRule.OddCostOnly => who + "牌组中只有奇数费用的卡牌",
                    _ => who + "牌组满足构筑规则",
                };
            }

            case HasCardNameExpr hcn:
                // CardId 与 CardName 都空时 Of 是参照槽位，表示「与它同名」。
                if (hcn.CardId == 0 && string.IsNullOrEmpty(hcn.CardName) && hcn.Of != null)
                    return "与" + PrintTarget(hcn.Of) + "同名";
                return "卡名为《" + CardZh(hcn.CardName) + "》";

            case HasClanExpr hcB:
                if (hcB.IsSelfClan) return "属于本职业";
                if (hcB.Clans != null && hcB.Clans.Count > 0)
                    return "属于" + string.Join("或", hcB.Clans.Select(x => ClanZh(x.Name))) + "职业";
                return "属于" + ClanZh(hcB.Clan.Name) + "职业";

            case HasPackExpr hpB: return "来自【" + hpB.PackId + "】卡包";

            case IsPreviousExpr: return "已选择目标";

            default: return "";
        }
    }

    public string PrintNum(NumExpr e)
    {
        if (e == null) return "0";
        switch (e.Op)
        {
            case NumOp.Const: return e.Value.ToString();
            case NumOp.RandomRange:
                return "（" + e.Value + "至"
                    + (e.Args != null && e.Args.Count > 0 ? PrintNum(e.Args[0]) : e.Value.ToString())
                    + "的随机数）";
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

            case NumOp.SplitOf:
                // 把一个计数器随机拆成 N 份后取第 idx 份。
                return "（将【" + CounterZh(e.Counter.Name ?? "") + "】随机拆分后的第" + (e.Value + 1) + "份数值）";

            case NumOp.Extreme:
            {
                string tag = e.TakeMax ? "较大值" : "较小值";
                var list = (e.Args ?? Array.Empty<NumExpr>()).Select(PrintNum).ToList();
                return "（" + tag + "（" + string.Join("、", list) + "））";
            }

            case NumOp.Clamp:
            {
                var a = e.Args ?? Array.Empty<NumExpr>();
                if (a.Count < 3) return a.Count > 0 ? PrintNum(a[0]) : "0";
                return PrintNum(a[0]) + "（范围" + PrintNum(a[1]) + "~" + PrintNum(a[2]) + "）";
            }

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
        if (k.Value != null)
        {
            // 用 PrintNum 而不是取常量：动态 X（rand 或计数器）也要印出来，
            // 写死 e.Value.Value 会让「可攻击 X 次」印成「可攻击 0 次」。
            string v = PrintNum(k.Value);
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
        // 以下内码以前直接漏到卡面上（玩家看到的是 pp_at_play 这种变量名）。
        "pp_at_play" => "打出时能量点",
        "turn_ally_attacks" => "本回合我方随从攻击次数",
        "ally_attacked_leader_last_turn" => "上回合我方随从攻击主战者次数",
        "ally_attacked_leader_this_turn" => "本回合我方随从攻击主战者次数",
        "cards_destroyed_by_ability" => "本次能力破坏的卡牌数",
        "cards_banished_by_ability" => "本次能力消失的卡牌数",
        "cards_discarded_by_ability" => "本次能力舍弃的卡牌数",
        "last_moved_count" => "本次能力移动的卡牌数",
        "match_play" => "本局打出次数",
        "turn_play" => "本回合打出次数",
        "skybound_gauge" => "奥义值",
        "attacked_leader_last_turn" => "上回合攻击过主战者",
        "attacked_follower_last_turn" => "上回合攻击过随从",
        "s_max_defense" => "主战者生命上限",
        "enemy_leader" => "敌方主战者",
        "distinct_base_costs_played" => "本局打出过的不同费用数",
        "cards_played_this_turn" => "本回合打出的张数",
        "destroyed_followers" => "已破坏的随从数",
        "fused_cards" => "已融合的张数",
        "ced_engage" => "本局启动次数",
        // 兜底：解析器曾把「the enemy leader's max defense」整句当成计数器名。
        // 那不是计数器，这里给一个可读名，真正的修复在 ConditionCompiler/ActionParser。
        "enemy_leader's_max_defense" => "敌方主战者生命上限",
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
        // Shadowverse: Worlds Beyond 把死灵与吸血鬼合并为 Abysscraft（深渊）。
        "Abysscraft" => "深渊",
        "Neutral" => "中立",
        _ => _glossary.ToChinese(en),
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
        ["Testset Ghost"] = "幽灵",
        ["Testset Striker Artifact"] = "突击神器",
        ["Testset Gear of Ambition"] = "野心之齿轮",
        ["Testset Prostrating Coward"] = "匍匐的懦夫",
        ["Testset Shoddy Plaything"] = "粗制的玩具",
        ["Testset Indomitable Fighter"] = "不屈的战士",
        ["Testset Detective's Lens"] = "侦探的镜片",
        ["Testset Bug Alert"] = "漏洞警报",
        ["Testset Amataz, Origin Blader"] = "始祖剑士 阿玛兹",
        ["Testset Aria, Lady of the Woods"] = "森林淑女 艾莉雅",
        ["Testset Sham-Nacha, Heir to Entwining"] = "缠绕的继承者 杉那查",
        ["Testset Witch's New Brew"] = "魔女的新酿",
        ["Testset Runeblade Conductor"] = "符文刃指挥者",
        ["Testset Blaze Destroyer"] = "烈焰破坏者",
        ["Testset Godwood Staff"] = "神木之杖",
        ["Testset Edelweiss, Sagelight Ward"] = "贤光守护 艾德薇丝",
        ["Testset Reno, Luxwing Featherfolk"] = "光翼羽民 雷诺",
        ["Testset Sinciro, Heir to Usurpation"] = "篡夺的继承者 辛西罗",
        ["Testset Slaus, Revolving Wheel of Fortune"] = "旋转的命运之轮 斯劳斯",
        ["Testset Sandalphon, Primarch Successor"] = "原初继承者 桑达尔丰",
        ["Testset Obsessed Test Subject"] = "痴迷的试验体",
        ["Testset Pascale's Dance"] = "帕斯卡尔的舞蹈",
        ["Testset Mjerrabaine, Great Manifest"] = "大显身 姆耶拉班",
        ["Testset Octrice, Hollowness Manifest"] = "虚无之显 奥克特莉丝",
        ["Testset Glittering Gold"] = "闪耀的黄金",
        ["Testset City of Babelon"] = "巴别伦之城",
        ["Testset Shakdoh, Nightblossom"] = "夜花 沙克多",
        ["Testset Adventurers' Guild"] = "冒险者公会",
        ["Testset Ancient Cannon"] = "古代加农炮",
        ["Testset Yuna, Occult Hunter"] = "神秘猎人 尤娜",
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
    /// <summary>
    /// 兵种中文名。
    ///
    /// 官方 16 个兵种全部收录（用户提供的官方对照）。
    /// 之前只列了 10 个，且 artifact 译成「神器」、officer 译成「指挥官」、
    /// loot 译成「战利品」—— 与官方中文卡面不一致，玩家认不出。
    /// 未收录的走 Glossary（DIY 自定义兵种走 DefineTrait 注册）。
    /// </summary>
    public string TraitZh(string en) => (en ?? "").ToLowerInvariant() switch
    {
        "officer" => "士兵",
        "luminous" => "鲁米那斯",
        "levin" => "雷维翁",
        "pixie" => "妖精",
        "departed" => "亡者",
        "earth sigil" => "土之印",
        "mysteria" => "玛纳利亚",
        "golem" => "巨像",
        "shikigami" => "式神",
        "artifact" => "创造物",
        "puppetry" => "人偶",
        "marine" => "海洋",
        "loot" => "财宝",
        "encroacher" => "侵蚀者",
        "anathema" => "安纳提玛",
        "clue" => "证据",
        // 非官方兵种（DIY / 旧版本遗留）走 Glossary。
        "machina" => "机械",
        "commander" => "司令官",
        _ => _glossary.ToChinese(en),
    };

    /// <summary>
    /// 卡名转中文。
    ///
    /// 查找顺序：小写内建表 → Glossary（CardPool 从全卡包 name_eng→name_chs 注入）
    /// → 原名兜底。
    ///
    /// 以前只查那张小内建表，于是纹章名、衍生 Token 名在卡面上全是英文 ——
    /// Glossary 里明明已经装好全部 982 张卡的对照，却从没被查过。
    /// </summary>
    /// <summary>
    /// 单位状态的中文名。
    ///
    /// 缺映射时以前走 `hs.State.ToString()`，直接把枚举名印到卡面上
    /// （玩家看到的是 CurrentAttackHitLeader 这种内部标识）。
    /// 未收录的一律给「未知状态」而不是原样输出。
    /// </summary>
    public string StateZh(UnitState st) => st switch
    {
        UnitState.None => "",
        UnitState.Damaged => "已受伤",
        UnitState.Evolved => "已进化",
        UnitState.SuperEvolved => "已超进化",
        UnitState.Silenced => "已被沉默",
        UnitState.AttackedThisTurn => "本回合已攻击",
        UnitState.EnteredThisTurn => "本回合登场",
        UnitState.CostModified => "费用已变更",
        UnitState.HasFuse => "带有融合",
        UnitState.HasLastWords => "带有谢幕曲",
        UnitState.HasOnSpellboost => "带有魔力增幅",
        UnitState.Destroyed => "已破坏",
        UnitState.CurrentAttackHitLeader => "攻击目标为主战者",
        UnitState.Enhanced => "已【爆能强化】",
        _ => "未知状态",
    };

    public string CardZh(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";
        string key = en.Trim();
        if (CardNameZh.TryGetValue(key, out var zh)) return zh;

        string fromGlossary = _glossary.ToChinese(key);
        if (!string.IsNullOrEmpty(fromGlossary) && fromGlossary != key) return fromGlossary;

        return key;
    }

    private string DeckNameZh(string en) => en switch
    {
        "Apocalypse Deck" => "启示录牌组",
        "a deck of 40 cards" => "40张牌的牌组",
        "deck of 40 cards" => "40张牌的牌组",
        _ => _glossary.ToChinese(en),
    };
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// CardDef ⇄ AST JSON 的**完整双向**编解码器。
///
/// 为什么要有它：ReshadowScript 是手写的文本版 AST，
/// 每加一个 EffectNode 字段都要同时改解析与打印两端，漏一个逗号就报错。
/// AST JSON 由这一个文件跟随，加字段只在两处各补一行。
///
/// 编码只输出非默认字段，导出才可读；解码缺省即默认值，
/// 所以旧存档在新增字段后依然读得回来。
/// </summary>
public static class AstJsonCodec
{
    // ---------------------------------------------------------------- 编码

    public static string Encode(CardDef card) => Encode(card, indent: true);

    public static string Encode(CardDef card, bool indent)
    {
        if (card == null) return "";
        var o = EncodeCard(card, isSub: false);
        return indent ? Json.WritePretty(o) : Json.Write(o);
    }

    private static Dictionary<string, object> EncodeCard(CardDef card, bool isSub)
    {
        var o = new Dictionary<string, object>();
        if (!isSub)
        {
            o["id"] = card.CardId;
            o["name"] = card.Name ?? "";
            if (!string.IsNullOrEmpty(card.NameChs)) o["name_chs"] = card.NameChs;
        }
        o["kind"] = card.Kind.ToString();
        if (!isSub && card.Clan.IsValid) o["clan"] = card.Clan.Name;
        if (!isSub && card.Traits != null && card.Traits.Count > 0)
            o["traits"] = card.Traits.Select(t => (object)t.Name).ToList();
        o["cost"] = card.BaseCost;
        o["atk"] = card.BaseAttack;
        o["life"] = card.BaseLife;

        if (card.HasFuse)
        {
            o["has_fuse"] = true;
            if (card.FuseMaterialFilter != null) o["fuse_filter"] = EncodeTarget(card.FuseMaterialFilter);
        }

        var abs = new List<object>();
        foreach (var ab in card.Abilities ?? Array.Empty<AbilityDef>()) abs.Add(EncodeAbility(ab));
        if (abs.Count > 0) o["abilities"] = abs;

        if (!isSub)
        {
            if (card.AccelerateDef != null) o["accelerate"] = EncodeCard(card.AccelerateDef, true);
            if (card.CrystallizeDef != null) o["crystallize"] = EncodeCard(card.CrystallizeDef, true);
            if (card.CrestDef != null) o["crest"] = EncodeCard(card.CrestDef, true);
            if (card.FaithDef != null) o["faith"] = EncodeCard(card.FaithDef, true);
        }
        return o;
    }

    private static Dictionary<string, object> EncodeAbility(AbilityDef ab)
    {
        var d = new Dictionary<string, object>();
        if (ab == null) return d;

        if (ab.Trigger != null)
        {
            var t = new Dictionary<string, object> { ["event"] = ab.Trigger.Event.ToString() };
            if (ab.Trigger.SecondaryEvent.HasValue) t["secondary_event"] = ab.Trigger.SecondaryEvent.Value.ToString();
            if (ab.Trigger.ActiveZone != ActiveZoneScope.Board) t["zone"] = ab.Trigger.ActiveZone.ToString();
            if (ab.Trigger.DuringTurn != TurnOwnerScope.Any) t["turn"] = ab.Trigger.DuringTurn.ToString();
            if (ab.Trigger.MaxTriggersPerTurn > 0) t["max_per_turn"] = ab.Trigger.MaxTriggersPerTurn;
            if (ab.Trigger.ManualEvolveOnly) t["manual_evolve_only"] = true;
            if (ab.Trigger.SubjectFilter != null) t["subject"] = EncodeTarget(ab.Trigger.SubjectFilter);
            d["trigger"] = t;
        }

        if (ab.Condition != null) d["condition"] = EncodeBool(ab.Condition);

        if (ab.Costs != null && ab.Costs.Count > 0)
        {
            d["costs"] = ab.Costs.Select(c => (object)new Dictionary<string, object>
            {
                ["kind"] = c.Kind.ToString(),
                ["amount"] = EncodeNum(c.Amount),
                ["label"] = c.Label ?? "",
            }).ToList();
        }

        if (ab.Body != null && ab.Body.Count > 0)
            d["body"] = ab.Body.Select(n => (object)EncodeNode(n)).ToList();

        return d;
    }

    /// <summary>把一个效果节点写成 {"$op": ...} 形式。22 种节点全部覆盖。</summary>
    private static Dictionary<string, object> EncodeNode(EffectNode n)
    {
        var d = new Dictionary<string, object>();
        if (n == null) { d["$op"] = "null"; return d; }

        switch (n)
        {
            case SelectTargetNode x:
                d["$op"] = "SelectTarget";
                d["target"] = EncodeTarget(x.Target);
                if (x.OutSlot != TargetSpec.SlotSelected) d["out_slot"] = x.OutSlot;
                break;

            case DealDamageNode x:
                d["$op"] = "DealDamage";
                d["target"] = EncodeTarget(x.Target);
                d["amount"] = EncodeNum(x.Amount);
                if (x.IsSplit) d["split"] = true;
                if (!x.IsAbility) d["ability"] = false;
                break;

            case RestoreLifeNode x:
                d["$op"] = "RestoreLife";
                d["target"] = EncodeTarget(x.Target);
                d["amount"] = EncodeNum(x.Amount);
                if (x.FullRestore) d["full"] = true;
                break;

            case ModifyStatsNode x:
                d["$op"] = "ModifyStats";
                d["target"] = EncodeTarget(x.Target);
                d["op"] = x.Op.ToString();
                d["atk"] = EncodeNum(x.Attack);
                d["life"] = EncodeNum(x.Life);
                if (x.Duration != DurationKind.Permanent) d["duration"] = x.Duration.ToString();
                break;

            case ModifyCostNode x:
                d["$op"] = "ModifyCost";
                d["target"] = EncodeTarget(x.Target);
                d["op"] = x.Op.ToString();
                d["value"] = EncodeNum(x.Value);
                if (x.Duration != DurationKind.Permanent) d["duration"] = x.Duration.ToString();
                break;

            case ModifyKeywordNode x:
                d["$op"] = "ModifyKeyword";
                d["target"] = EncodeTarget(x.Target);
                d["op"] = x.Op.ToString();
                d["keyword"] = x.Keyword.Name ?? "";
                if (x.Value != null) d["value"] = EncodeNum(x.Value);
                if (x.Duration != DurationKind.Permanent) d["duration"] = x.Duration.ToString();
                break;

            case GrantAbilityNode x:
                d["$op"] = "GrantAbility";
                d["target"] = EncodeTarget(x.Target);
                if (x.Granted != null) d["granted"] = EncodeAbility(x.Granted);
                if (x.Duration != DurationKind.Permanent) d["duration"] = x.Duration.ToString();
                break;

            case GrantCounterAbilityNode x:
                d["$op"] = "GrantCounterAbility";
                d["counter"] = x.Counter.Name ?? "";
                if (x.Owner != ZoneScope.Self) d["owner"] = x.Owner.ToString();
                if (x.Granted != null) d["granted"] = EncodeAbility(x.Granted);
                break;

            case SpawnCardNode x:
                d["$op"] = "SpawnCard";
                d["source"] = x.SourceKind.ToString();
                if (x.CardId > 0) d["card_id"] = x.CardId;
                if (!string.IsNullOrEmpty(x.CardName)) d["card_name"] = x.CardName;
                if (x.IsCrest) d["is_crest"] = true;
                if (x.IsExactCopy) d["exact"] = true;
                if (x.CopyFrom != null) d["copy_from"] = EncodeTarget(x.CopyFrom);
                d["to_zone"] = x.ToZone.ToString();
                if (x.ToOwner != ZoneScope.Self) d["to_owner"] = x.ToOwner.ToString();
                d["count"] = EncodeNum(x.Count);
                if (x.OutSlot != TargetSpec.SlotCreated) d["out_slot"] = x.OutSlot;
                if (x.OfferCount != 3) d["offer"] = x.OfferCount;
                break;

            case MoveZoneNode x:
                d["$op"] = "MoveZone";
                d["target"] = EncodeTarget(x.Target);
                d["to_zone"] = x.ToZone.ToString();
                if (x.IsDestroy) d["destroy"] = true;
                if (x.OutSlot != TargetSpec.SlotCreated) d["out_slot"] = x.OutSlot;
                break;

            case TransformNode x:
                d["$op"] = "Transform";
                d["target"] = EncodeTarget(x.Target);
                if (x.IntoCardId > 0) d["into_card_id"] = x.IntoCardId;
                if (!string.IsNullOrEmpty(x.IntoCardName)) d["into_card_name"] = x.IntoCardName;
                if (x.CopySource != null) d["copy_source"] = EncodeTarget(x.CopySource);
                break;

            case EvolveNode x:
                d["$op"] = "Evolve";
                d["target"] = EncodeTarget(x.Target);
                if (x.IsSuperEvolve) d["super"] = true;
                break;

            case ModifyCounterNode x:
                d["$op"] = "ModifyCounter";
                d["counter"] = x.Counter.Name ?? "";
                d["op"] = x.Op.ToString();
                d["delta"] = EncodeNum(x.Delta);
                if (x.Target != null) d["target"] = EncodeTarget(x.Target);
                if (x.Owner != ZoneScope.Self) d["owner"] = x.Owner.ToString();
                break;

            case InvokeAbilityNode x:
                d["$op"] = "InvokeAbility";
                d["target"] = EncodeTarget(x.Target);
                d["event"] = x.Event.ToString();
                break;

            case ConditionalNode x:
                d["$op"] = "Conditional";
                d["condition"] = EncodeBool(x.Condition);
                d["then"] = (x.Then ?? Array.Empty<EffectNode>()).Select(k => (object)EncodeNode(k)).ToList();
                if (x.Else != null && x.Else.Count > 0)
                    d["else"] = x.Else.Select(k => (object)EncodeNode(k)).ToList();
                break;

            case LoopNode x:
                d["$op"] = "Loop";
                d["loop_kind"] = x.Kind.ToString();
                if (x.Count != null) d["count"] = EncodeNum(x.Count);
                if (x.Collection != null) d["collection"] = EncodeTarget(x.Collection);
                if (x.Until != null) d["until"] = EncodeBool(x.Until);
                d["body"] = (x.Body ?? Array.Empty<EffectNode>()).Select(k => (object)EncodeNode(k)).ToList();
                if (x.MaxIterations != 30) d["max_iterations"] = x.MaxIterations;
                break;

            case DamageModifierNode x:
                d["$op"] = "DamageModifier";
                d["target"] = EncodeTarget(x.Target);
                d["kind"] = x.Kind.ToString();
                d["source_mask"] = x.SourceMask.ToString();
                d["amount"] = EncodeNum(x.Amount);
                d["is_reduction"] = x.IsReduction;
                if (x.Duration != DurationKind.Permanent) d["duration"] = x.Duration.ToString();
                break;

            case RevealCardNode x:
                d["$op"] = "RevealCard";
                d["target"] = EncodeTarget(x.Target);
                break;

            case BranchModeNode x:
                d["$op"] = "BranchMode";
                d["mode"] = x.Kind.ToString();
                d["pick_count"] = EncodeNum(x.PickCount);
                d["options"] = (x.Options ?? Array.Empty<IReadOnlyList<EffectNode>>())
                    .Select(o => (object)o.Select(k => (object)EncodeNode(k)).ToList()).ToList();
                break;

            case ReplaceDeckNode x:
                d["$op"] = "ReplaceDeck";
                d["deck_name"] = x.DeckName ?? "";
                break;

            case MacroNode x:
                d["$op"] = "Macro";
                d["macro"] = x.Macro.Name ?? "";
                break;

            default:
                d["$op"] = n.GetType().Name.Replace("Node", "");
                break;
        }
        return d;
    }

private static object EncodeNum(NumExpr e)
    {
        if (e == null) return null;
        var d = new Dictionary<string, object> { ["$num"] = e.Op.ToString() };
        switch (e.Op)
        {
            case NumOp.Const:
                d["v"] = e.Value;
                break;
            case NumOp.RandomRange:
                d["min"] = e.Value;
                d["max"] = e.Args != null && e.Args.Count > 0 ? e.Args[0].Value : e.Value;
                d["roll_id"] = e.RollId;
                break;
            case NumOp.Property:
                d["stat"] = e.Stat.ToString();
                if (e.Of != null) d["of"] = EncodeTarget(e.Of);
                break;
            case NumOp.Count:
                if (e.Of != null) d["of"] = EncodeTarget(e.Of);
                break;
            case NumOp.Sum:
                if (e.Of != null) d["of"] = EncodeTarget(e.Of);
                d["stat"] = e.Stat.ToString();
                break;
            case NumOp.Counter:
                d["counter"] = e.Counter.Name ?? "";
                break;
            case NumOp.Arith:
                d["arith"] = e.Arith.ToString();
                d["args"] = (e.Args ?? Array.Empty<NumExpr>()).Select(a => EncodeNum(a)).ToList();
                break;
            case NumOp.Cond:
                d["cond"] = EncodeBool(e.Cond);
                d["then"] = EncodeNum(e.Then);
                d["else"] = EncodeNum(e.Else);
                break;
            case NumOp.Extreme:
                d["take_max"] = e.TakeMax;
                d["args"] = (e.Args ?? Array.Empty<NumExpr>()).Select(a => EncodeNum(a)).ToList();
                break;
            default:
                if (e.Args != null && e.Args.Count > 0)
                    d["args"] = e.Args.Select(a => EncodeNum(a)).ToList();
                break;
        }
        return d;
    }

    private static object EncodeTarget(TargetSpec t)
    {
        if (t == null) return null;

        // 槽位型目标压成短字符串："$self" / "$selected" —— 玩家一眼看得懂，
        // 也避免每个常见目标都写成六行对象。
        if (t.SourceKind == TargetSourceKind.Slot && !string.IsNullOrEmpty(t.Slot))
            return t.Slot;

        var d = new Dictionary<string, object>();
        d["owner"] = t.Owner.ToString();
        d["zone"] = t.Zone.ToString();
        if (t.Mode != SelectMode.All) d["mode"] = t.Mode.ToString();
        if (t.Count != null) d["count"] = EncodeNum(t.Count);
        if (!string.IsNullOrEmpty(t.Slot)) d["slot"] = t.Slot;
        if (t.CardId > 0) d["card_id"] = t.CardId;
        if (!string.IsNullOrEmpty(t.CardName)) d["card_name"] = t.CardName;
        if (t.Where != null && t.Where is not ConstBoolExpr) d["where"] = EncodeBool(t.Where);
        if (t.Sort != SortKey.None) d["sort"] = t.Sort.ToString();
        if (t.SortDescending) d["sort_desc"] = true;
        if (t.TopN > 0) d["top_n"] = t.TopN;
        if (t.DistinctByName) d["distinct"] = true;
        if (t.DuplicatesOnly) d["duplicates"] = true;
        if (t.IncludeLeader) d["include_leader"] = true;
        if (t.History != HistoryScope.None) d["history"] = t.History.ToString();
        return d;
    }

    /// <summary>BoolExpr 是继承体系，不是带 Op 字段的单类 —— 按子类逐个编码。</summary>
    private static object EncodeBool(BoolExpr e)
    {
        if (e == null) return null;
        var d = new Dictionary<string, object>();

        switch (e)
        {
            case ConstBoolExpr c:
                d["$bool"] = c.Value ? "True" : "False";
                break;
            case AndExpr a:
                d["$bool"] = "And";
                d["items"] = (a.Items ?? Array.Empty<BoolExpr>()).Select(x => EncodeBool(x)).ToList();
                break;
            case OrExpr o:
                d["$bool"] = "Or";
                d["items"] = (o.Items ?? Array.Empty<BoolExpr>()).Select(x => EncodeBool(x)).ToList();
                break;
            case NotExpr n:
                d["$bool"] = "Not";
                d["inner"] = EncodeBool(n.Inner);
                break;
            case CompareExpr c2:
                d["$bool"] = "Compare";
                d["left"] = EncodeNum(c2.Left);
                d["cmp"] = c2.Op.ToString();
                d["right"] = EncodeNum(c2.Right);
                break;
            case ExistsExpr ex:
                d["$bool"] = "Exists";
                d["target"] = EncodeTarget(ex.Target);
                d["cmp"] = ex.Op.ToString();
                d["count"] = EncodeNum(ex.Count);
                break;
            case HasKeywordExpr hk:
                d["$bool"] = "HasKeyword";
                d["keyword"] = hk.Keyword.Name ?? "";
                if (hk.Of != null) d["of"] = EncodeTarget(hk.Of);
                break;
            case HasTraitExpr ht:
                d["$bool"] = "HasTrait";
                d["trait"] = ht.Trait.Name ?? "";
                if (ht.Of != null) d["of"] = EncodeTarget(ht.Of);
                break;
            case HasClanExpr hc:
                d["$bool"] = "HasClan";
                d["clan"] = hc.Clan.Name ?? "";
                if (hc.Clans != null && hc.Clans.Count > 0)
                    d["clans"] = hc.Clans.Select(x => (object)(x.Name ?? "")).ToList();
                if (hc.IsSelfClan) d["self_clan"] = true;
                break;
            case HasStateExpr hs:
                d["$bool"] = "HasState";
                d["state"] = hs.State.ToString();
                if (hs.Of != null) d["of"] = EncodeTarget(hs.Of);
                break;
            case HasKindExpr hki:
                d["$bool"] = "HasKind";
                d["kind"] = hki.Kind.ToString();
                if (hki.Of != null) d["of"] = EncodeTarget(hki.Of);
                break;
            case HasPackExpr hp:
                d["$bool"] = "HasPack";
                d["pack_id"] = hp.PackId ?? "";
                d["of"] = EncodeTarget(hp.Of);
                break;
            case NotSameEntityExpr ns:
                d["$bool"] = "NotSameEntity";
                d["exclude_slot"] = ns.ExcludeSlot ?? TargetSpec.SlotSelf;
                if (ns.Of != null) d["of"] = EncodeTarget(ns.Of);
                break;
            case IsPreviousExpr ip:
                d["$bool"] = "IsPrevious";
                d["slot"] = ip.Slot ?? "$selected";
                break;
            case DeckRuleExpr dr:
                d["$bool"] = "DeckRule";
                d["rule"] = dr.Rule.ToString();
                d["owner"] = dr.Owner.ToString();
                break;
            default:
                d["$bool"] = e.GetType().Name.Replace("Expr", "");
                break;
        }
        return d;
    }

    // ---------------------------------------------------------------- 解码

    /// <summary>把 JSON 文本读回 CardDef。容忍 // 注释、尾随逗号、单引号。</summary>
    public static CardDef Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var root = JsonReader.AsMap(JsonReader.Parse(json));
        if (root == null) return null;
        return DecodeCard(root, isSub: false);
    }

    private static CardDef DecodeCard(Dictionary<string, object> m, bool isSub)
    {
        if (m == null) return null;

        // CardDef 的属性全是 init：先算好全部字段再一次性构造，
        // 不能 new 出来再逐个赋值。
        var clan = JsonReader.Str(m, "clan");
        var traits = new List<SymbolId<TraitTag>>();
        if (m.TryGetValue("traits", out var tr) && JsonReader.AsList(tr) is { } tl)
            foreach (var t in tl)
            {
                var nm = t?.ToString();
                if (!string.IsNullOrEmpty(nm)) traits.Add(new SymbolId<TraitTag>(0, nm));
            }

        var abilities = new List<AbilityDef>();
        if (m.TryGetValue("abilities", out var ab) && JsonReader.AsList(ab) is { } al)
            foreach (var a in al)
            {
                var d = DecodeAbility(JsonReader.AsMap(a));
                if (d != null) abilities.Add(d);
            }

        return new CardDef
        {
            CardId = JsonReader.Int(m, "id"),
            Name = JsonReader.Str(m, "name", ""),
            NameChs = JsonReader.Str(m, "name_chs", ""),
            Kind = JsonReader.Enum(m, "kind", CardKind.Follower),
            BaseCost = JsonReader.Int(m, "cost"),
            BaseAttack = JsonReader.Int(m, "atk"),
            BaseLife = JsonReader.Int(m, "life"),
            Clan = string.IsNullOrEmpty(clan)
                ? SymbolId<ClanTag>.Invalid
                : new SymbolId<ClanTag>(0, clan),
            Traits = traits,
            Abilities = abilities,
            HasFuse = JsonReader.Bool(m, "has_fuse"),
            FuseMaterialFilter = m.TryGetValue("fuse_filter", out var ff) ? DecodeTarget(ff) : null,
            AccelerateDef = !isSub && m.TryGetValue("accelerate", out var ac)
                ? DecodeCard(JsonReader.AsMap(ac), true) : null,
            CrystallizeDef = !isSub && m.TryGetValue("crystallize", out var cr)
                ? DecodeCard(JsonReader.AsMap(cr), true) : null,
            CrestDef = !isSub && m.TryGetValue("crest", out var ce)
                ? DecodeCard(JsonReader.AsMap(ce), true) : null,
            FaithDef = !isSub && m.TryGetValue("faith", out var fa)
                ? DecodeCard(JsonReader.AsMap(fa), true) : null,
        };
    }

    private static AbilityDef DecodeAbility(Dictionary<string, object> m)
    {
        if (m == null) return null;

        TriggerSpec trig = null;
        if (m.TryGetValue("trigger", out var tg) && JsonReader.AsMap(tg) is { } t)
        {
            CoreEvent? sec = null;
            var se = JsonReader.Str(t, "secondary_event");
            if (!string.IsNullOrEmpty(se) && System.Enum.TryParse<CoreEvent>(se, true, out var sev)) sec = sev;

            trig = new TriggerSpec
            {
                Event = JsonReader.Enum(t, "event", CoreEvent.OnPlay),
                SecondaryEvent = sec,
                ActiveZone = JsonReader.Enum(t, "zone", ActiveZoneScope.Board),
                DuringTurn = JsonReader.Enum(t, "turn", TurnOwnerScope.Any),
                MaxTriggersPerTurn = JsonReader.Int(t, "max_per_turn"),
                ManualEvolveOnly = JsonReader.Bool(t, "manual_evolve_only"),
                SubjectFilter = t.TryGetValue("subject", out var sj) ? DecodeTarget(sj) : null,
            };
        }

        var costs = new List<CostSpec>();
        if (m.TryGetValue("costs", out var cs) && JsonReader.AsList(cs) is { } csl)
            foreach (var c0 in csl)
            {
                var cm = JsonReader.AsMap(c0);
                if (cm == null) continue;
                costs.Add(new CostSpec
                {
                    Kind = JsonReader.Enum(cm, "kind", CostKind.Countdown),
                    Amount = DecodeNum(cm.TryGetValue("amount", out var am) ? am : null) ?? NumExpr.Const(1),
                    Label = JsonReader.Str(cm, "label", ""),
                });
            }

        var body = new List<EffectNode>();
        if (m.TryGetValue("body", out var bd) && JsonReader.AsList(bd) is { } bl)
            foreach (var b in bl)
            {
                var n = DecodeNode(JsonReader.AsMap(b));
                if (n != null) body.Add(n);
            }

        return new AbilityDef
        {
            Trigger = trig,
            Condition = m.TryGetValue("condition", out var cd) ? DecodeBool(cd) : null,
            Costs = costs,
            Body = body,
        };
    }

    private static EffectNode DecodeNode(Dictionary<string, object> m)
    {
        if (m == null) return null;
        string op = JsonReader.Str(m, "$op", "");
        switch (op)
        {
            case "SelectTarget":
                return new SelectTargetNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t1) ? t1 : null),
                    OutSlot = JsonReader.Str(m, "out_slot", TargetSpec.SlotSelected),
                };
            case "DealDamage":
                return new DealDamageNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t2) ? t2 : null),
                    Amount = DecodeNum(m.TryGetValue("amount", out var a2) ? a2 : null) ?? NumExpr.Const(1),
                    IsSplit = JsonReader.Bool(m, "split"),
                    IsAbility = !m.ContainsKey("ability") || JsonReader.Bool(m, "ability", true),
                };
            case "RestoreLife":
                return new RestoreLifeNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t3) ? t3 : null),
                    Amount = DecodeNum(m.TryGetValue("amount", out var a3) ? a3 : null) ?? NumExpr.Const(1),
                    FullRestore = JsonReader.Bool(m, "full"),
                };
            case "ModifyStats":
                return new ModifyStatsNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t4) ? t4 : null),
                    Op = JsonReader.Enum(m, "op", StatModOp.Add),
                    Attack = DecodeNum(m.TryGetValue("atk", out var a4) ? a4 : null),
                    Life = DecodeNum(m.TryGetValue("life", out var l4) ? l4 : null),
                    Duration = JsonReader.Enum(m, "duration", DurationKind.Permanent),
                };
            case "ModifyCost":
                return new ModifyCostNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t5) ? t5 : null),
                    Op = JsonReader.Enum(m, "op", CostModOp.Add),
                    Value = DecodeNum(m.TryGetValue("value", out var a5) ? a5 : null) ?? NumExpr.Const(0),
                    Duration = JsonReader.Enum(m, "duration", DurationKind.Permanent),
                };
            case "ModifyKeyword":
            {
                var kw = JsonReader.Str(m, "keyword", "");
                return new ModifyKeywordNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t6) ? t6 : null),
                    Op = JsonReader.Enum(m, "op", KeywordOp.Grant),
                    Keyword = string.IsNullOrEmpty(kw) ? SymbolId<KeywordTag>.Invalid : new SymbolId<KeywordTag>(0, kw),
                    Value = m.TryGetValue("value", out var a6) ? DecodeNum(a6) : null,
                    Duration = JsonReader.Enum(m, "duration", DurationKind.Permanent),
                };
            }
            case "GrantAbility":
                return new GrantAbilityNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t7) ? t7 : null),
                    Granted = m.TryGetValue("granted", out var g7) ? DecodeAbility(JsonReader.AsMap(g7)) : null,
                    Duration = JsonReader.Enum(m, "duration", DurationKind.Permanent),
                };
            case "GrantCounterAbility":
            {
                var cn = JsonReader.Str(m, "counter", "");
                return new GrantCounterAbilityNode
                {
                    Counter = string.IsNullOrEmpty(cn) ? SymbolId<CounterTag>.Invalid : new SymbolId<CounterTag>(0, cn),
                    Owner = JsonReader.Enum(m, "owner", ZoneScope.Self),
                    Granted = m.TryGetValue("granted", out var g8) ? DecodeAbility(JsonReader.AsMap(g8)) : null,
                };
            }
            case "SpawnCard":
                return new SpawnCardNode
                {
                    SourceKind = JsonReader.Enum(m, "source", CardSourceKind.CardId),
                    CardId = JsonReader.Int(m, "card_id"),
                    CardName = JsonReader.Str(m, "card_name", ""),
                    IsCrest = JsonReader.Bool(m, "is_crest"),
                    IsExactCopy = JsonReader.Bool(m, "exact"),
                    CopyFrom = m.TryGetValue("copy_from", out var cf) ? DecodeTarget(cf) : null,
                    ToZone = JsonReader.Enum(m, "to_zone", ZoneRef.Hand),
                    ToOwner = JsonReader.Enum(m, "to_owner", ZoneScope.Self),
                    Count = DecodeNum(m.TryGetValue("count", out var a9) ? a9 : null) ?? NumExpr.Const(1),
                    OutSlot = JsonReader.Str(m, "out_slot", TargetSpec.SlotCreated),
                    OfferCount = JsonReader.Int(m, "offer", 3),
                };
            case "MoveZone":
                return new MoveZoneNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t10) ? t10 : null),
                    ToZone = JsonReader.Enum(m, "to_zone", ZoneRef.Hand),
                    IsDestroy = JsonReader.Bool(m, "destroy"),
                    OutSlot = JsonReader.Str(m, "out_slot", TargetSpec.SlotCreated),
                };
            case "Transform":
                return new TransformNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t11) ? t11 : null),
                    IntoCardId = JsonReader.Int(m, "into_card_id"),
                    IntoCardName = JsonReader.Str(m, "into_card_name", ""),
                    CopySource = m.TryGetValue("copy_source", out var cs2) ? DecodeTarget(cs2) : null,
                };
            case "Evolve":
                return new EvolveNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t12) ? t12 : null),
                    IsSuperEvolve = JsonReader.Bool(m, "super"),
                };
            case "ModifyCounter":
            {
                var cn2 = JsonReader.Str(m, "counter", "");
                return new ModifyCounterNode
                {
                    Counter = string.IsNullOrEmpty(cn2) ? SymbolId<CounterTag>.Invalid : new SymbolId<CounterTag>(0, cn2),
                    Op = JsonReader.Enum(m, "op", CounterOp.Add),
                    Delta = DecodeNum(m.TryGetValue("delta", out var a13) ? a13 : null) ?? NumExpr.Const(1),
                    Target = m.TryGetValue("target", out var t13) ? DecodeTarget(t13) : null,
                    Owner = JsonReader.Enum(m, "owner", ZoneScope.Self),
                };
            }
            case "InvokeAbility":
                return new InvokeAbilityNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t14) ? t14 : null),
                    Event = JsonReader.Enum(m, "event", CoreEvent.OnPlay),
                };
            case "Conditional":
                return new ConditionalNode
                {
                    Condition = m.TryGetValue("condition", out var c15) ? DecodeBool(c15) : null,
                    Then = DecodeNodeList(m.TryGetValue("then", out var t15) ? t15 : null),
                    Else = DecodeNodeList(m.TryGetValue("else", out var e15) ? e15 : null),
                };
            case "Loop":
                return new LoopNode
                {
                    Kind = JsonReader.Enum(m, "loop_kind", LoopKind.Times),
                    Count = m.TryGetValue("count", out var a16) ? DecodeNum(a16) : null,
                    Collection = m.TryGetValue("collection", out var t16) ? DecodeTarget(t16) : null,
                    Until = m.TryGetValue("until", out var u16) ? DecodeBool(u16) : null,
                    Body = DecodeNodeList(m.TryGetValue("body", out var b16) ? b16 : null),
                    MaxIterations = JsonReader.Int(m, "max_iterations", 30),
                };
            case "DamageModifier":
                return new DamageModifierNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t17) ? t17 : null) ?? TargetSpec.Self,
                    Kind = JsonReader.Enum(m, "kind", DamageAuraKind.TakenDelta),
                    SourceMask = JsonReader.Enum(m, "source_mask", DamageSourceMask.All),
                    Amount = DecodeNum(m.TryGetValue("amount", out var a17) ? a17 : null) ?? NumExpr.Const(0),
                    IsReduction = JsonReader.Bool(m, "is_reduction", true),
                    Duration = JsonReader.Enum(m, "duration", DurationKind.Permanent),
                };
            case "RevealCard":
                return new RevealCardNode
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var t18) ? t18 : null),
                };
            case "BranchMode":
            {
                var opts = new List<IReadOnlyList<EffectNode>>();
                if (m.TryGetValue("options", out var op19) && JsonReader.AsList(op19) is { } ol)
                    foreach (var one in ol)
                        opts.Add(DecodeNodeList(one));
                return new BranchModeNode
                {
                    Kind = JsonReader.Enum(m, "mode", ModeDispatchKind.Choose),
                    PickCount = DecodeNum(m.TryGetValue("pick_count", out var a19) ? a19 : null) ?? NumExpr.Const(1),
                    Options = opts,
                };
            }
            case "ReplaceDeck":
                return new ReplaceDeckNode { DeckName = JsonReader.Str(m, "deck_name", "") };
            default:
                return null;
        }
    }

    private static List<EffectNode> DecodeNodeList(object o)
    {
        var list = new List<EffectNode>();
        if (JsonReader.AsList(o) is not { } arr) return list;
        foreach (var item in arr)
        {
            var n = DecodeNode(JsonReader.AsMap(item));
            if (n != null) list.Add(n);
        }
        return list;
    }

    private static TargetSpec DecodeTarget(object o)
    {
        if (o == null) return null;

        // 短字符串形式 = 槽位目标
        if (o is string slotStr)
            return slotStr.StartsWith("$")
                ? new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = slotStr }
                : new TargetSpec { SourceKind = TargetSourceKind.Slot, Slot = slotStr };

        var m = JsonReader.AsMap(o);
        if (m == null) return null;

        return new TargetSpec
        {
            Owner = JsonReader.Enum(m, "owner", ZoneScope.Any),
            Zone = JsonReader.Enum(m, "zone", ZoneRef.Board),
            Mode = JsonReader.Enum(m, "mode", SelectMode.All),
            Count = DecodeNum(m.TryGetValue("count", out var ct) ? ct : null) ?? NumExpr.Const(1),
            Slot = JsonReader.Str(m, "slot", ""),
            CardId = JsonReader.Int(m, "card_id"),
            CardName = JsonReader.Str(m, "card_name", ""),
            Where = m.TryGetValue("where", out var wh) ? DecodeBool(wh) : BoolExpr.True,
            Sort = JsonReader.Enum(m, "sort", SortKey.None),
            SortDescending = JsonReader.Bool(m, "sort_desc"),
            TopN = JsonReader.Int(m, "top_n"),
            DistinctByName = JsonReader.Bool(m, "distinct"),
            DuplicatesOnly = JsonReader.Bool(m, "duplicates"),
            IncludeLeader = JsonReader.Bool(m, "include_leader"),
            History = JsonReader.Enum(m, "history", HistoryScope.None),
        };
    }

    private static BoolExpr DecodeBool(object o)
    {
        if (o == null) return null;
        if (o is bool bv) return bv ? BoolExpr.True : BoolExpr.False;
        if (o is string bare)
        {
            if (bare == "true") return BoolExpr.True;
            if (bare == "false") return BoolExpr.False;
            return null;
        }

        var m = JsonReader.AsMap(o);
        if (m == null) return null;
        string op = JsonReader.Str(m, "$bool", "");

        switch (op)
        {
            case "True": return BoolExpr.True;
            case "False": return BoolExpr.False;
            case "And":
            {
                var items = new List<BoolExpr>();
                if (m.TryGetValue("items", out var it) && JsonReader.AsList(it) is { } il)
                    foreach (var x in il) { var d = DecodeBool(x); if (d != null) items.Add(d); }
                return items.Count == 0 ? BoolExpr.True : new AndExpr(items);
            }
            case "Or":
            {
                var items = new List<BoolExpr>();
                if (m.TryGetValue("items", out var it2) && JsonReader.AsList(it2) is { } il2)
                    foreach (var x in il2) { var d = DecodeBool(x); if (d != null) items.Add(d); }
                return items.Count == 0 ? BoolExpr.True : new OrExpr(items);
            }
            case "Not":
                return new NotExpr(DecodeBool(m.TryGetValue("inner", out var inn) ? inn : null) ?? BoolExpr.True);
            case "Compare":
                return new CompareExpr
                {
                    Left = DecodeNum(m.TryGetValue("left", out var lf) ? lf : null) ?? NumExpr.Const(0),
                    Op = JsonReader.Enum(m, "cmp", CompareOp.Ge),
                    Right = DecodeNum(m.TryGetValue("right", out var rt) ? rt : null) ?? NumExpr.Const(0),
                };
            case "Exists":
                return new ExistsExpr
                {
                    Target = DecodeTarget(m.TryGetValue("target", out var tg) ? tg : null),
                    Op = JsonReader.Enum(m, "cmp", CompareOp.Ge),
                    Count = DecodeNum(m.TryGetValue("count", out var ct2) ? ct2 : null) ?? NumExpr.Const(1),
                };
            case "HasKeyword":
            {
                var kw = JsonReader.Str(m, "keyword", "");
                return new HasKeywordExpr
                {
                    Keyword = string.IsNullOrEmpty(kw) ? SymbolId<KeywordTag>.Invalid : new SymbolId<KeywordTag>(0, kw),
                    Of = m.TryGetValue("of", out var of1) ? DecodeTarget(of1) : null,
                };
            }
            case "HasTrait":
            {
                var tr = JsonReader.Str(m, "trait", "");
                return new HasTraitExpr
                {
                    Trait = string.IsNullOrEmpty(tr) ? SymbolId<TraitTag>.Invalid : new SymbolId<TraitTag>(0, tr),
                    Of = m.TryGetValue("of", out var of2) ? DecodeTarget(of2) : null,
                };
            }
            case "HasClan":
            {
                var cl = JsonReader.Str(m, "clan", "");
                var clans = new List<SymbolId<ClanTag>>();
                if (m.TryGetValue("clans", out var cls) && JsonReader.AsList(cls) is { } cll)
                    foreach (var x in cll)
                    {
                        var nm = x?.ToString();
                        if (!string.IsNullOrEmpty(nm)) clans.Add(new SymbolId<ClanTag>(0, nm));
                    }
                return new HasClanExpr
                {
                    Clan = string.IsNullOrEmpty(cl) ? SymbolId<ClanTag>.Invalid : new SymbolId<ClanTag>(0, cl),
                    Clans = clans,
                    IsSelfClan = JsonReader.Bool(m, "self_clan"),
                };
            }
            case "HasState":
                return new HasStateExpr
                {
                    State = JsonReader.Enum(m, "state", UnitState.None),
                    Of = m.TryGetValue("of", out var of3) ? DecodeTarget(of3) : null,
                };
            case "HasKind":
                return new HasKindExpr
                {
                    Kind = JsonReader.Enum(m, "kind", CardKind.Follower),
                    Of = m.TryGetValue("of", out var of4) ? DecodeTarget(of4) : null,
                };
            case "HasPack":
                return new HasPackExpr
                {
                    PackId = JsonReader.Str(m, "pack_id", ""),
                    Of = m.TryGetValue("of", out var of5) ? DecodeTarget(of5) : null,
                };
            case "NotSameEntity":
                return new NotSameEntityExpr
                {
                    ExcludeSlot = JsonReader.Str(m, "exclude_slot", TargetSpec.SlotSelf),
                    Of = m.TryGetValue("of", out var of6) ? DecodeTarget(of6) : null,
                };
            case "IsPrevious":
                return new IsPreviousExpr { Slot = JsonReader.Str(m, "slot", "$selected") };
            case "DeckRule":
                return new DeckRuleExpr
                {
                    Rule = JsonReader.Enum(m, "rule", DeckRule.None),
                    Owner = JsonReader.Enum(m, "owner", ZoneScope.Self),
                };
            default:
                return null;
        }
    }

    private static NumExpr DecodeNum(object o)
    {
        if (o == null) return null;
        if (o is long l) return NumExpr.Const((int)l);
        if (o is double dd) return NumExpr.Const((int)dd);

        var m = JsonReader.AsMap(o);
        if (m == null) return null;
        string op = JsonReader.Str(m, "$num", "");

        switch (op)
        {
            case "Const": return NumExpr.Const(JsonReader.Int(m, "v"));
            case "RandomRange":
                return NumExpr.RandRange(JsonReader.Int(m, "min"),
                                         JsonReader.Int(m, "max", 1),
                                         JsonReader.Int(m, "roll_id"));
            case "Property":
                return NumExpr.Prop(JsonReader.Enum(m, "stat", StatKey.None),
                                    m.TryGetValue("of", out var p1) ? DecodeTarget(p1) : null);
            case "Count":
                return NumExpr.CountOf(DecodeTarget(m.TryGetValue("of", out var p2) ? p2 : null));
            case "Sum":
                return NumExpr.SumOf(DecodeTarget(m.TryGetValue("of", out var p3) ? p3 : null),
                                     JsonReader.Enum(m, "stat", StatKey.Attack));
            case "Counter":
            {
                var cn = JsonReader.Str(m, "counter", "");
                return NumExpr.Ctr(string.IsNullOrEmpty(cn)
                    ? SymbolId<CounterTag>.Invalid : new SymbolId<CounterTag>(0, cn));
            }
            case "Arith":
            {
                var args = new List<NumExpr>();
                if (m.TryGetValue("args", out var ar) && JsonReader.AsList(ar) is { } al)
                    foreach (var x in al) { var d = DecodeNum(x); if (d != null) args.Add(d); }
                return new NumExpr
                {
                    Op = NumOp.Arith,
                    Arith = JsonReader.Enum(m, "arith", ArithOp.Add),
                    Args = args,
                };
            }
            case "Cond":
                return new NumExpr
                {
                    Op = NumOp.Cond,
                    Cond = m.TryGetValue("cond", out var cd) ? DecodeBool(cd) : null,
                    Then = DecodeNum(m.TryGetValue("then", out var th) ? th : null),
                    Else = DecodeNum(m.TryGetValue("else", out var el) ? el : null),
                };
            case "Extreme":
            {
                var args = new List<NumExpr>();
                if (m.TryGetValue("args", out var ar2) && JsonReader.AsList(ar2) is { } al2)
                    foreach (var x in al2) { var d = DecodeNum(x); if (d != null) args.Add(d); }
                return new NumExpr
                {
                    Op = NumOp.Extreme,
                    TakeMax = JsonReader.Bool(m, "take_max", true),
                    Args = args,
                };
            }
            default:
                return null;
        }
    }
}
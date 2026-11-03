using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// CardDef ⇄ 紧凑 AST JSON 的编解码器。
///
/// 为什么要有它：ReshadowScript 那套自定义语法是**手写的文本版 AST**，
/// 每加一个 EffectNode 字段就要手改解析与打印两端，漏一个逗号四端同构门禁就报错。
/// AST JSON 由序列化器自动跟随类型变化，加字段不需要动这里。
///
/// 只输出非默认字段，导出的 JSON 才有可读性；读回时缺省即默认值。
/// </summary>
public static class AstJsonCodec
{
    /// <summary>把一张卡写成 JSON 文本。</summary>
    public static string Encode(CardDef card)
    {
        if (card == null) return "";
        var o = new Dictionary<string, object>
        {
            ["id"] = card.CardId,
            ["name"] = card.Name ?? "",
        };
        if (!string.IsNullOrEmpty(card.NameChs)) o["name_chs"] = card.NameChs;
        o["kind"] = card.Kind.ToString();
        o["cost"] = card.BaseCost;
        o["atk"] = card.BaseAttack;
        o["life"] = card.BaseLife;
        if (card.Clan.IsValid) o["clan"] = card.Clan.Name;

        var abs = new List<object>();
        foreach (var ab in card.Abilities ?? new List<AbilityDef>())
            abs.Add(EncodeAbility(ab));
        if (abs.Count > 0) o["abilities"] = abs;

        return Json.Write(o);
    }

    private static Dictionary<string, object> EncodeAbility(AbilityDef ab)
    {
        var d = new Dictionary<string, object>();
        if (ab?.Trigger != null)
        {
            var t = new Dictionary<string, object> { ["event"] = ab.Trigger.Event.ToString() };
            if (ab.Trigger.SecondaryEvent.HasValue)
                t["secondary"] = ab.Trigger.SecondaryEvent.Value.ToString();
            d["trigger"] = t;
        }
        if (ab?.Body != null && ab.Body.Count > 0)
        {
            var body = new List<object>();
            foreach (var n in ab.Body) body.Add(EncodeNode(n));
            d["body"] = body;
        }
        return d;
    }

    /// <summary>把一个效果节点写成 {"$op":...} 形式。未登记的节点写成类型名，至少不丢信息。</summary>
    private static Dictionary<string, object> EncodeNode(EffectNode n)
    {
        var d = new Dictionary<string, object>();
        if (n == null) { d["$op"] = "null"; return d; }
        d["$op"] = n.GetType().Name.Replace("Node", "");
        switch (n)
        {
            case DealDamageNode dd:
                d["amount"] = EncodeNum(dd.Amount);
                if (dd.IsAbility) d["ability"] = true;
                break;
            case RestoreLifeNode rl:
                d["amount"] = EncodeNum(rl.Amount);
                break;
            case ModifyKeywordNode mk:
                d["op"] = mk.Op.ToString();
                d["keyword"] = mk.Keyword.Name ?? "";
                if (mk.Value != null) d["value"] = EncodeNum(mk.Value);
                break;
            case DamageModifierNode dm:
                d["kind"] = dm.Kind.ToString();
                d["mask"] = dm.SourceMask.ToString();
                d["amount"] = EncodeNum(dm.Amount);
                d["reduce"] = dm.IsReduction;
                break;
            case BranchModeNode bm:
                d["mode"] = bm.Kind.ToString();
                if (bm.PickCount != null) d["pick"] = EncodeNum(bm.PickCount);
                d["options"] = (bm.Options ?? new List<IReadOnlyList<EffectNode>>())
                    .Select(o2 => (object)o2.Select(EncodeNode).ToList()).ToList();
                break;
        }
        return d;
    }

    private static Dictionary<string, object> EncodeNum(NumExpr e)
    {
        var d = new Dictionary<string, object>();
        if (e == null) { d["$num"] = "Const"; d["v"] = 0; return d; }
        d["$num"] = e.Op.ToString();
        switch (e.Op)
        {
            case NumOp.Const:
                d["v"] = e.Value;
                break;
            case NumOp.RandomRange:
                d["min"] = e.Value;
                d["max"] = e.Args != null && e.Args.Count > 0 ? e.Args[0].Value : e.Value;
                d["roll"] = e.RollId;
                break;
            case NumOp.Property:
                d["stat"] = e.Stat.ToString();
                break;
        }
        return d;
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// 极简 JSON 写入器（只做序列化，不做解析）。
///
/// 不引 System.Text.Json 的原因：AST 节点是多态树，用 STJ 需要给每个节点
/// 挂 [JsonDerivedType]，等于把序列化耦合进 AST 定义。
/// 这里按 Dictionary/List/基础类型直接写，AST 加字段只需在 Codec 里补一行。
/// </summary>
public static class Json
{
    public static string Write(object o)
    {
        var sb = new StringBuilder();
        Emit(sb, o);
        return sb.ToString();
    }


    /// <summary>缩进版输出：给玩家看的 AST JSON 必须是能读的，一行流没法改。</summary>
    public static string WritePretty(object o, int indentWidth = 2)
    {
        var sb = new StringBuilder();
        EmitPretty(sb, o, 0, indentWidth);
        return sb.ToString();
    }

    private static void EmitPretty(StringBuilder sb, object o, int depth, int iw)
    {
        string pad = new string(' ', depth * iw);
        string padIn = new string(' ', (depth + 1) * iw);

        switch (o)
        {
            case null: sb.Append("null"); return;
            case string s: Escape(sb, s); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
            case IDictionary<string, object> map:
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{').Append('\n');
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',').Append('\n');
                    first = false;
                    sb.Append(padIn);
                    Escape(sb, kv.Key);
                    sb.Append(": ");
                    EmitPretty(sb, kv.Value, depth + 1, iw);
                }
                sb.Append('\n').Append(pad).Append('}');
                return;
            }
            case IEnumerable seq:
            {
                var items = new List<object>();
                foreach (var x in seq) items.Add(x);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[').Append('\n');
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) sb.Append(',').Append('\n');
                    sb.Append(padIn);
                    EmitPretty(sb, items[i], depth + 1, iw);
                }
                sb.Append('\n').Append(pad).Append(']');
                return;
            }
            default: Escape(sb, o.ToString()); return;
        }
    }
    private static void Emit(StringBuilder sb, object o)
    {
        switch (o)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                Escape(sb, s);
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                return;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                return;
            case IDictionary<string, object> map:
                EmitMap(sb, map);
                return;
            case IEnumerable seq:
                EmitSeq(sb, seq);
                return;
            default:
                Escape(sb, o.ToString());
                return;
        }
    }

    private static void EmitMap(StringBuilder sb, IDictionary<string, object> m)
    {
        sb.Append('{');
        bool first = true;
        foreach (var kv in m)
        {
            if (!first) sb.Append(", ");
            first = false;
            Escape(sb, kv.Key);
            sb.Append(": ");
            Emit(sb, kv.Value);
        }
        sb.Append('}');
    }

    private static void EmitSeq(StringBuilder sb, IEnumerable seq)
    {
        sb.Append('[');
        bool first = true;
        foreach (var it in seq)
        {
            if (!first) sb.Append(", ");
            first = false;
            Emit(sb, it);
        }
        sb.Append(']');
    }

    private static void Escape(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
    }
}

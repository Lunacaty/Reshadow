using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// 极简 JSON 解析器（与 Json 写入器配套）。
///
/// 为什么不用 System.Text.Json：AST JSON 是玩家手写的，需要容忍
/// 注释、尾随逗号、单引号这些「人类写法」。STJ 默认全部拒绝，
/// 玩家改一行就得对着报错猜。这里自己实现，规则放宽但结果一致。
///
/// 支持：// 行注释、/* 块注释 */、尾随逗号、单/双引号字符串。
/// </summary>
public static class JsonReader
{
    public static object Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int i = 0;
        var v = ParseValue(text, ref i);
        return v;
    }

    private static object ParseValue(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) return null;

        char c = s[i];
        if (c == '{') return ParseObject(s, ref i);
        if (c == '[') return ParseArray(s, ref i);
        if (c == '"' || c == '\'') return ParseString(s, ref i);
        return ParseScalar(s, ref i);
    }

    private static Dictionary<string, object> ParseObject(string s, ref int i)
    {
        var d = new Dictionary<string, object>();
        i++;   // {
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == '}') { i++; break; }
            if (s[i] == ',') { i++; continue; }

            string key = ParseString(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ':') i++;
            var val = ParseValue(s, ref i);
            if (key != null) d[key] = val;
        }
        return d;
    }

    private static List<object> ParseArray(string s, ref int i)
    {
        var a = new List<object>();
        i++;   // [
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ']') { i++; break; }
            if (s[i] == ',') { i++; continue; }
            a.Add(ParseValue(s, ref i));
        }
        return a;
    }

    private static string ParseString(string s, ref int i)
    {
        if (i >= s.Length) return null;
        char quote = s[i];
        if (quote != '"' && quote != '\'') return null;
        i++;
        var sb = new StringBuilder();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == quote) { i++; break; }
            if (c == '\\' && i + 1 < s.Length)
            {
                i++;
                char e = s[i];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(e); break;
                }
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static object ParseScalar(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && !IsDelim(s[i])) i++;
        string tok = s.Substring(start, i - start).Trim();
        if (tok.Length == 0) { i = Math.Max(i, start + 1); return null; }

        if (tok == "true") return true;
        if (tok == "false") return false;
        if (tok == "null") return null;
        if (tok == "undefined") return null;

        if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return l;
        if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;

        // 未加引号的裸词（玩家图省事写的 OnPlay / ward）按字符串收下。
        return tok;
    }

    private static bool IsDelim(char c)
        => c == ',' || c == '}' || c == ']' || c == ':' | c == ' ' || c == '\n' || c == '\r' || c == '\t';

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\n' || c == '\r' || c == '\t') { i++; continue; }
            if (c == '/' && i + 1 < s.Length)
            {
                if (s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    i = Math.Min(s.Length, i + 2);
                    continue;
                }
            }
            break;
        }
    }

    // ---------- 取值辅助 ----------

    public static Dictionary<string, object> AsMap(object o) => o as Dictionary<string, object>;

    public static List<object> AsList(object o) => o as List<object>;

    public static string Str(Dictionary<string, object> m, string key, string def = null)
    {
        if (m == null || !m.TryGetValue(key, out var v) || v == null) return def;
        return v as string ?? v.ToString();
    }

    public static int Int(Dictionary<string, object> m, string key, int def = 0)
    {
        if (m == null || !m.TryGetValue(key, out var v) || v == null) return def;
        if (v is long l) return (int)l;
        if (v is double d) return (int)d;
        if (v is bool b) return b ? 1 : 0;
        return int.TryParse(v.ToString(), out var r) ? r : def;
    }

    public static bool Bool(Dictionary<string, object> m, string key, bool def = false)
    {
        if (m == null || !m.TryGetValue(key, out var v) || v == null) return def;
        if (v is bool b) return b;
        if (v is long l) return l != 0;
        var str = v.ToString();
        return str == "true" || str == "1";
    }

    public static TEnum Enum<TEnum>(Dictionary<string, object> m, string key, TEnum def) where TEnum : struct
    {
        var str = Str(m, key);
        return System.Enum.TryParse<TEnum>(str, true, out var r) ? r : def;
    }
}

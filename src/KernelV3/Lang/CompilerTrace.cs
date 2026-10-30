using System;
using System.Collections.Generic;
using System.Text;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 编译器诊断
//
// 之前每次报错只有一句「无法解析的动作：X（位置 N）」，看不出上下文，
// 只能反复加临时打印、改一次重编译一次。
//
// 现在统一在这里做：
//   · 报错自动附带完整 token 流，出错位置用 <<< 标出，并回显原文
//   · Trace 开关打开后，每一步切分 / 前缀剥离 / 动作解析都留痕
// ============================================================================

public static class CompilerTrace
{
    /// <summary>打开后输出完整流程；关闭时零开销。</summary>
    public static bool Enabled;

    /// <summary>当前正在编译的原文，用于报错时回显。</summary>
    public static string CurrentText = "";

    [ThreadStatic] private static int _depth;

    public static void Log(string stage, string detail)
    {
        if (!Enabled) return;
        Console.WriteLine(new string(' ', _depth * 2) + "[" + stage + "] " + detail);
    }

    public static void Push() { _depth++; }
    public static void Pop() { if (_depth > 0) _depth--; }

    /// <summary>把 token 流排成一行，出错位置用 &lt;&lt;&lt; 标出。</summary>
    public static string RenderTokens(IReadOnlyList<Token> tokens, int errorPos)
    {
        if (tokens == null || tokens.Count == 0) return "";
        var sb = new StringBuilder();
        sb.Append("    tokens: ");
        bool marked = false;
        foreach (var t in tokens)
        {
            if (t.Kind == TokKind.Eof) continue;
            if (!marked && errorPos >= 0 && t.Pos >= errorPos)
            {
                sb.Append("<<< ");
                marked = true;
            }
            sb.Append(t.ToString()).Append(' ');
        }
        if (!marked && errorPos >= 0) sb.Append("<<<");
        return sb.ToString().TrimEnd();
    }

    /// <summary>构造一条带完整上下文的报错消息。</summary>
    public static string BuildError(string stage, string why, TokenCursor cur, IReadOnlyList<Token> tokens)
    {
        var sb = new StringBuilder();
        sb.Append(stage).Append("：").Append(why);

        if (cur != null && !cur.IsAtEnd)
            sb.Append("\n    停在: [").Append(cur.Current).Append("] 位置 ").Append(cur.Current.Pos);

        if (tokens != null && tokens.Count > 0)
        {
            sb.Append('\n').Append(RenderTokens(tokens, cur != null && !cur.IsAtEnd ? cur.Current.Pos : -1));
        }

        if (!string.IsNullOrEmpty(CurrentText))
        {
            var clipped = CurrentText.Length > 150 ? CurrentText.Substring(0, 150) + "…" : CurrentText;
            sb.Append("\n    原文: ").Append(clipped);
        }

        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// Python 风格缩进词法器（204 号文档第三节思路 2）
//
// 用**缩进栈**表达块结构，绝不用 Split('\n') 硬凑：
//   · 缩进增加 -> 发射 Indent
//   · 缩进减少 -> 循环发射 Dedent，栈顶对不齐就精准报错
//   · 括号内部的换行忽略缩进（与 Python 官方一致，便于长条件跨行）
//   · 文件末尾补齐未闭合的 Dedent
// ============================================================================

public enum PyTokKind
{
    Eof = 0, Newline, Indent, Dedent,
    Ident, Number, String, Slot,
    Colon, Comma, LParen, RParen, LBracket, RBracket, LAngle, RAngle,
    Arrow, Plus, Minus, Star, Slash, Percent, Assign, Pipe, Hash, At, Underscore,
    Eq, Ne, Gt, Ge, Lt, Le,
}

public readonly record struct PyToken(PyTokKind Kind, string Text, int IntVal, int Line)
{
    public override string ToString() => Kind + "[" + Text + "]";
}

public static class PyAstLexer
{
    public static List<PyToken> Tokenize(string source)
    {
        var tokens = new List<PyToken>();
        var indentStack = new Stack<int>();
        indentStack.Push(0);

        int parenDepth = 0;
        var lines = (source ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        for (int lineIdx = 0; lineIdx < lines.Length; lineIdx++)
        {
            string line = lines[lineIdx];
            int lineNo = lineIdx + 1;

            int i = 0;
            if (parenDepth == 0)
            {
                while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;

                // 空行 / 纯注释行不参与缩进
                if (i >= line.Length) { EmitNewline(tokens, lineNo); continue; }
                if (line[i] == '#') { EmitNewline(tokens, lineNo); continue; }
                if (line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') { EmitNewline(tokens, lineNo); continue; }

                int curIndent = i;
                if (curIndent > indentStack.Peek())
                {
                    indentStack.Push(curIndent);
                    tokens.Add(new PyToken(PyTokKind.Indent, "<indent>", curIndent, lineNo));
                }
                else
                {
                    while (curIndent < indentStack.Peek())
                    {
                        indentStack.Pop();
                        tokens.Add(new PyToken(PyTokKind.Dedent, "<dedent>", curIndent, lineNo));
                    }
                    if (curIndent != indentStack.Peek())
                        throw new LangException("第 " + lineNo + " 行缩进未对齐（空格数=" + curIndent + "）");
                }
            }

            while (i < line.Length)
            {
                char c = line[i];
                if (c == ' ' || c == '\t') { i++; continue; }
                if (c == '#') break;
                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;

                if (c == '(' || c == '[') parenDepth++;
                else if (c == ')' || c == ']') parenDepth = Math.Max(0, parenDepth - 1);

                i = ScanOneToken(line, i, lineNo, tokens);
            }

            if (parenDepth == 0) EmitNewline(tokens, lineNo);
        }

        while (indentStack.Count > 1)
        {
            indentStack.Pop();
            tokens.Add(new PyToken(PyTokKind.Dedent, "<dedent>", 0, lines.Length));
        }
        tokens.Add(new PyToken(PyTokKind.Eof, "<eof>", 0, lines.Length));
        return tokens;
    }

    private static void EmitNewline(List<PyToken> tokens, int lineNo)
    {
        if (tokens.Count > 0 && tokens[^1].Kind != PyTokKind.Newline
            && tokens[^1].Kind != PyTokKind.Indent && tokens[^1].Kind != PyTokKind.Dedent)
            tokens.Add(new PyToken(PyTokKind.Newline, "\n", 0, lineNo));
    }

    private static int ScanOneToken(string line, int i, int lineNo, List<PyToken> tokens)
    {
        char c = line[i];

        // 双字符算子
        if (i + 1 < line.Length)
        {
            string two = line.Substring(i, 2);
            PyTokKind? k2 = two switch
            {
                "->" => PyTokKind.Arrow,
                ">=" => PyTokKind.Ge,
                "<=" => PyTokKind.Le,
                "!=" => PyTokKind.Ne,
                "==" => PyTokKind.Eq,
                _ => null,
            };
            if (k2 != null) { tokens.Add(new PyToken(k2.Value, two, 0, lineNo)); return i + 2; }
        }

        PyTokKind? k = c switch
        {
            ':' => PyTokKind.Colon,
            ',' => PyTokKind.Comma,
            '(' => PyTokKind.LParen,
            ')' => PyTokKind.RParen,
            '[' => PyTokKind.LBracket,
            ']' => PyTokKind.RBracket,
            '<' => PyTokKind.Lt,
            '>' => PyTokKind.Gt,
            '+' => PyTokKind.Plus,
            '-' => PyTokKind.Minus,
            '*' => PyTokKind.Star,
            '/' => PyTokKind.Slash,
            '%' => PyTokKind.Percent,
            '=' => PyTokKind.Assign,
            '|' => PyTokKind.Pipe,
            '@' => PyTokKind.At,
            '_' => null,   // _ 可能是单独的「不修改」占位符，也可能是标识符开头
            _ => null,
        };
        if (k != null) { tokens.Add(new PyToken(k.Value, c.ToString(), 0, lineNo)); return i + 1; }

        // 字符串
        if (c == '"')
        {
            int j = i + 1;
            var sb = new System.Text.StringBuilder();
            while (j < line.Length && line[j] != '"')
            {
                if (line[j] == '\\' && j + 1 < line.Length) { sb.Append(line[j + 1]); j += 2; continue; }
                sb.Append(line[j]); j++;
            }
            tokens.Add(new PyToken(PyTokKind.String, sb.ToString(), 0, lineNo));
            return j < line.Length ? j + 1 : j;
        }

        // 槽位 $self / $selected / $choose1
        if (c == '$')
        {
            int j = i + 1;
            while (j < line.Length && (char.IsLetterOrDigit(line[j]) || line[j] == '_')) j++;
            tokens.Add(new PyToken(PyTokKind.Slot, line.Substring(i, j - i), 0, lineNo));
            return j;
        }

        // 数字
        if (char.IsDigit(c))
        {
            int j = i;
            while (j < line.Length && char.IsDigit(line[j])) j++;
            string num = line.Substring(i, j - i);
            int.TryParse(num, out int v);
            tokens.Add(new PyToken(PyTokKind.Number, num, v, lineNo));
            return j;
        }

        // 标识符（含 _ 开头、带点、带冒号的名字如 ctr:pp_at_play）
        if (char.IsLetter(c) || c == '_')
        {
            int j = i;
            while (j < line.Length)
            {
                char d = line[j];
                // 冒号绝不能并进标识符：它是块结构的定界符（`fanfare:` / `option 1:`）。
                // 目标表达式里的 `self:board` 由解析端自己把两段拼回去。
                if (char.IsLetterOrDigit(d) || d == '_' || d == '.') { j++; continue; }
                break;
            }
            string id = line.Substring(i, j - i);
            tokens.Add(new PyToken(PyTokKind.Ident, id, 0, lineNo));
            return j;
        }

        // 其它未知字符：跳过（容错）
        return i + 1;
    }
}

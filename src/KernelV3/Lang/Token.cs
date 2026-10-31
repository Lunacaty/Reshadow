using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 词法单元
//
// V3 的铁律二：双引号在**词法期**就递归产出一个 Quoted(Token[])，
// 而不是留一个字符串让语法层去 Substring。
// 这样 Give it "Last Words: Deal 2 damage" 里的内容天然是 Token 流，
// 语法层可以直接把它当一个完整句子来解析。
// ============================================================================

public enum TokKind
{
    Eof = 0,
    Word,
    Number,
    CardName,
    Keyword,
    Trait,
    Clan,
    Quoted,
    StatMod,
    Comma,
    Period,
    Colon,
    Dash,
    Semicolon,
    OpenParen,
    CloseParen,
    SectionBreak,
    OptionMark,
}

public sealed class Token
{
    public TokKind Kind { get; init; }
    public string Text { get; init; } = "";
    public int Value { get; init; }
    public int CardId { get; init; }
    public int Pos { get; init; }

    /// <summary>
    /// 该 token 所在行的缩进层级（行首空格的列数）。
    /// 模式编号块靠它判层级：缩进相同的 1./2. 是同层兄弟。
    /// XML 注释 / 生成器产出的 token 默认为 0。
    /// </summary>
    public int Indent { get; init; }
    public IReadOnlyList<Token> Inner { get; init; } = Array.Empty<Token>();

    public bool IsWord(string w) =>
        (Kind == TokKind.Word || Kind == TokKind.Keyword || Kind == TokKind.CardName
         || Kind == TokKind.Trait || Kind == TokKind.Clan)
        && string.Equals(Text, w, StringComparison.OrdinalIgnoreCase);

    public bool IsAnyWord(params string[] words)
    {
        foreach (var w in words) if (IsWord(w)) return true;
        return false;
    }

    public override string ToString()
    {
        if (Kind == TokKind.Eof) return "<eof>";
        if (Kind == TokKind.Quoted) return "\"" + string.Join(" ", Inner.Select(t => t.ToString())) + "\"";
        return Text;
    }
}

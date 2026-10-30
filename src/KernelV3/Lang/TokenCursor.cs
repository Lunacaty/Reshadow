using System;
using System.Collections.Generic;
using System.Linq;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// TokenCursor —— 语法层唯一的输入通道
//
// V2 的病根：Stage 3 里到处 Substring / Split，
// 于是「and Ward」被静默截断、「and Reanimate (1)」被吞、
// 「with 3 defense or less」丢掉，而且没有任何地方报错。
//
// V3 的铁律一：语法层只碰 TokenCursor，绝不碰原文字符串。
// 每解析完一个句子，强制 AssertEnd() —— 只要还剩 token 就当场报错，
// 报错信息里直接指出停在哪个词上。
// ============================================================================

public sealed class TokenCursor
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    public TokenCursor(IReadOnlyList<Token> tokens, int start = 0)
    {
        _tokens = tokens ?? Array.Empty<Token>();
        _pos = start;
    }

    public int Position => _pos;

    /// <summary>完整的 token 列表（诊断用）。</summary>
    public IReadOnlyList<Token> All => _tokens;
    public int Length => _tokens.Count;
    public bool IsAtEnd => Current.Kind == TokKind.Eof;

    public Token Current => _pos < _tokens.Count ? _tokens[_pos] : EofToken();

    public Token Peek(int ahead = 1)
    {
        int i = _pos + ahead;
        return i >= 0 && i < _tokens.Count ? _tokens[i] : EofToken();
    }

    public Token Consume()
    {
        var t = Current;
        if (_pos < _tokens.Count) _pos++;
        return t;
    }

    public void Advance() { if (_pos < _tokens.Count) _pos++; }

    public bool TryMatch(TokKind kind)
    {
        if (Current.Kind != kind) return false;
        _pos++;
        return true;
    }

    public bool TryMatchWord(string word)
    {
        if (!Current.IsWord(word)) return false;
        _pos++;
        return true;
    }

    /// <summary>MatchWord 的别名，与 Gemini 方案里的 cur.MatchWord 一致。</summary>
    public bool MatchWord(string word) => TryMatchWord(word);
    public bool Match(TokKind kind) => TryMatch(kind);
    public bool MatchAnyWord(params string[] words) => TryMatchAnyWord(words);

    /// <summary>依次匹配一串词；全部命中才消耗，否则回退并返回 false。</summary>
    public bool MatchPhrase(params string[] words)
    {
        int save = _pos;
        foreach (var w in words)
        {
            if (!Current.IsWord(w)) { _pos = save; return false; }
            _pos++;
        }
        return true;
    }

    /// <summary>上一个被消耗的 token（Gemini 方案里的 cur.Previous）。</summary>
    public Token Previous => _pos > 0 && _pos <= _tokens.Count ? _tokens[_pos - 1] : EofToken();

    public bool TryMatchAnyWord(params string[] words)
    {
        if (!Current.IsAnyWord(words)) return false;
        _pos++;
        return true;
    }

    public Token Expect(TokKind kind, string what)
    {
        if (Current.Kind != kind)
            throw new LangException($"期望{what}，实际遇到 {Current}（位置 {Current.Pos}）");
        return Consume();
    }

    public Token ExpectWord(string word)
    {
        if (!Current.IsWord(word))
            throw new LangException($"期望 \"{word}\"，实际遇到 {Current}（位置 {Current.Pos}）");
        return Consume();
    }

    /// <summary>
    /// 铁律一的执行点：解析完一个句子后必须调用。
    /// 只要句尾还有 token 没被消费，立刻报错 —— 绝不静默吞词。
    /// </summary>
    public void AssertEnd(string context)
    {
        // 句末的句号/分号允许残留
        while (Current.Kind == TokKind.Period || Current.Kind == TokKind.Semicolon) Advance();
        if (!IsAtEnd)
            throw LangException.At(context, "句尾还有未消费的词", this, All);
    }

    /// <summary>不报错的版本：返回剩余 token，供上层决定如何处理。</summary>
    public IReadOnlyList<Token> Rest()
    {
        var rest = new List<Token>();
        for (int i = _pos; i < _tokens.Count; i++)
            if (_tokens[i].Kind != TokKind.Eof) rest.Add(_tokens[i]);
        return rest;
    }

    public TokenCursor Fork() => new(_tokens, _pos);

    /// <summary>回退 n 个 token（不少于 0）。</summary>
    public void Retreat(int n)
    {
        if (n <= 0) return;
        _pos = Math.Max(0, _pos - n);
        if (_pos > 0 && _tokens[_pos - 1].Kind == TokKind.Eof) _pos--;
    }
    public void Seek(int pos) => _pos = Math.Clamp(pos, 0, _tokens.Count);

    private static Token EofToken() => new() { Kind = TokKind.Eof, Text = "", Pos = -1 };
}

/// <summary>语法层异常。</summary>
public sealed class LangException : Exception
{
    public LangException(string message) : base(message) { }

    /// <summary>在 cursor 处报错，自动附带完整 token 流与原文。</summary>
    public static LangException At(string stage, string why, TokenCursor cur, System.Collections.Generic.IReadOnlyList<Token> tokens)
        => new(CompilerTrace.BuildError(stage, why, cur, tokens));
}

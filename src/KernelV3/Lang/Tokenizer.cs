using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 闭环词典 —— 卡名 / 关键词 / 兵种 / 职业
//
// 铁律三：词典从 GameSchema 加载，玩家 DIY 的新卡新兵种即刻生效。
// 分词时用**最长前缀匹配**把卡名冻结成单个 token，
// 于是 "Rusty, Luxcard Trickster" 不会被逗号切成两半。
// ============================================================================

public sealed class Lexicon
{
    /// <summary>符号注册表。Lang 层一切符号必须经由它取得真实 Id。</summary>
    public GameSchema Schema { get; } = GameSchema.CreateDefault();

    private readonly Dictionary<string, int> _cardNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _traits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有卡名按长度降序，用于最长前缀匹配。</summary>
    private readonly List<string> _namesByLength = new();

    public int CardNameCount => _cardNames.Count;

    public SymbolId<CounterTag> Counter(string name) => Schema.Counter(name);
    public SymbolId<KeywordTag> Keyword(string name) => Schema.Keyword(name);
    public SymbolId<TraitTag> Trait(string name) => Schema.Trait(name);
    public SymbolId<ClanTag> Clan(string name) => Schema.Clan(name);
    public SymbolId<MacroTag> Macro(string name) => Schema.Macro(name);

    public void AddCard(string name, int cardId)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var key = name.Trim();
        _cardNames[key] = cardId;
        _cardIds[cardId.ToString()] = cardId;
    }

    public void AddKeyword(string kw) { if (!string.IsNullOrWhiteSpace(kw)) _keywords.Add(kw.Trim()); }
    public void AddTrait(string tr) { if (!string.IsNullOrWhiteSpace(tr)) _traits.Add(tr.Trim()); }
    public void AddClan(string cl) { if (!string.IsNullOrWhiteSpace(cl)) _clans.Add(cl.Trim()); }

    /// <summary>构建最长匹配表（加完所有词后调用一次）。</summary>
    public void Build()
    {
        _namesByLength.Clear();
        _namesByLength.AddRange(_cardNames.Keys);
        _namesByLength.Sort((a, b) => b.Length.CompareTo(a.Length));
    }

    public bool IsKeyword(string w) => w != null && _keywords.Contains(w.Trim());
    public bool IsTrait(string w) => w != null && _traits.Contains(w.Trim());
    public bool IsClan(string w) => w != null && _clans.Contains(w.Trim());

    public bool TryGetCardId(string name, out int id)
    {
        if (name != null && _cardNames.TryGetValue(name.Trim(), out id)) return true;
        id = 0;
        return false;
    }

    /// <summary>从 text[from..] 做最长卡名匹配。返回名字与长度。</summary>
    public (string name, int cardId, int length)? MatchLongestCardName(string text, int from)
    {
        if (from >= text.Length) return null;
        foreach (var name in _namesByLength)
        {
            if (from + name.Length > text.Length) continue;
            if (string.Compare(text, from, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            // 词边界：后面不能紧跟字母
            int end = from + name.Length;
            if (end < text.Length && char.IsLetter(text[end])) continue;
            return (name, _cardNames[name], name.Length);
        }
        return null;
    }
}

// ============================================================================
// 分词器
// ============================================================================

public sealed class Tokenizer
{
    private readonly Lexicon _lex;

    public Tokenizer(Lexicon lex) => _lex = lex ?? throw new ArgumentNullException(nameof(lex));

    public IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(text)) { tokens.Add(Eof()); return tokens; }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            //  是段落分隔符：当空白跳过（行首缩进靠 Pos 回溯原文得到）。
            if (char.IsWhiteSpace(c) || c == '') { i++; continue; }

            // 引号块：递归分词
            if (c == '"')
            {
                int start = i;
                int close = text.IndexOf('"', i + 1);
                if (close < 0)
                    throw new LangException($"引号未闭合（位置 {start}）");
                var inner = text.Substring(i + 1, close - i - 1);
                var innerTokens = Tokenize(inner);
                tokens.Add(new Token
                {
                    Kind = TokKind.Quoted,
                    Text = inner,
                    Pos = start,
                    Inner = innerTokens.Where(t => t.Kind != TokKind.Eof).ToList(),
                });
                i = close + 1;
                continue;
            }

            // 标点
            if (c == ',') { tokens.Add(new Token { Kind = TokKind.Comma, Text = ",", Pos = i }); i++; continue; }
            if (c == ';') { tokens.Add(new Token { Kind = TokKind.Semicolon, Text = ";", Pos = i }); i++; continue; }
            if (c == ':') { tokens.Add(new Token { Kind = TokKind.Colon, Text = ":", Pos = i }); i++; continue; }
            if (c == '(') { tokens.Add(new Token { Kind = TokKind.OpenParen, Text = "(", Pos = i }); i++; continue; }
            if (c == ')') { tokens.Add(new Token { Kind = TokKind.CloseParen, Text = ")", Pos = i }); i++; continue; }
            if (c == '-' && (i + 1 >= text.Length || !char.IsDigit(text[i + 1])))
            {
                tokens.Add(new Token { Kind = TokKind.Dash, Text = "-", Pos = i });
                i++;
                continue;
            }
            // "vs." 不是句末，跳过句号只留 vs
            if (c == '.' && i >= 2 && text.Substring(Math.Max(0, i - 2), 2).Equals("vs", StringComparison.OrdinalIgnoreCase))
            {
                i++;
                continue;
            }
            if (c == '.')
            {
                // "1." 这种序号点算 Period，交给上层判断
                tokens.Add(new Token { Kind = TokKind.Period, Text = ".", Pos = i });
                i++;
                continue;
            }

            // 属性修正 +2 / -1 / +X
            if ((c == '+' || c == '-') && i + 1 < text.Length)
            {
                char n = text[i + 1];
                if (char.IsDigit(n) || n == 'X' || n == 'x' || n == 'Y' || n == 'y' || n == 'Z' || n == 'z')
                {
                    int j = i + 1;
                    while (j < text.Length && (char.IsDigit(text[j]) || char.IsLetter(text[j]))) j++;
                    // +3/+3 形态：把斜杠和第二段一并收进同一个 StatMod
                    if (j < text.Length && text[j] == '/' && j + 1 < text.Length
                        && (text[j + 1] == '+' || text[j + 1] == '-'))
                    {
                        int k = j + 2;
                        while (k < text.Length && (char.IsDigit(text[k]) || char.IsLetter(text[k]))) k++;
                        j = k;
                    }
                    tokens.Add(new Token { Kind = TokKind.StatMod, Text = text.Substring(i, j - i), Pos = i });
                    i = j;
                    continue;
                }
            }

            // 数字（含 "1. " 这类选项序号 → OptionMark）
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < text.Length && char.IsDigit(text[j])) j++;
                // 选项序号：数字 + '.'（可跟空格，也可直接句末）
                // 且必须处在「块起点」——行首或上一 token 是句读。
                // 否则 "X starts at 2." 的句末 2. 会被误判成选项标记，
                // 把 "2. On Spellboost: ..." 整体吞成 "选项 2"，句子就此断裂。
                bool isOption = j < text.Length && text[j] == '.'
                    && (j + 1 >= text.Length || text[j + 1] == ' ')
                    && IsOptionBoundary(tokens);
                if (isOption)
                {
                    tokens.Add(new Token
                    {
                        Kind = TokKind.OptionMark,
                        Text = text.Substring(i, j - i),
                        Value = int.TryParse(text.Substring(i, j - i), out var ov) ? ov : 0,
                        Pos = i,
                    });
                    i = (j + 1 < text.Length && text[j + 1] == ' ') ? j + 2 : j + 1;
                    continue;
                }
                var numText = text.Substring(i, j - i);
                tokens.Add(new Token
                {
                    Kind = TokKind.Number,
                    Text = numText,
                    Value = int.TryParse(numText, out var v) ? v : 0,
                    Pos = i,
                });
                i = j;
                continue;
            }

            // 词：先试最长卡名
            if (char.IsLetter(c))
            {
                var hit = _lex.MatchLongestCardName(text, i);
                if (hit != null && hit.Value.length > 0)
                {
                    tokens.Add(new Token
                    {
                        Kind = TokKind.CardName,
                        Text = hit.Value.name,
                        CardId = hit.Value.cardId,
                        Pos = i,
                    });
                    i += hit.Value.length;
                    continue;
                }

                int j = i;
                while (j < text.Length && (char.IsLetter(text[j]) || text[j] == '\'' || text[j] == '’')) j++;
                var word = text.Substring(i, j - i);

                // Super - Evolve 合成一个词（影之诗原文把连字符当分隔符用）
                if (word.Equals("super", StringComparison.OrdinalIgnoreCase))
                {
                    int k = j;
                    while (k < text.Length && text[k] == ' ') k++;
                    if (k < text.Length && text[k] == '-')
                    {
                        k++;
                        while (k < text.Length && text[k] == ' ') k++;
                        int m = k;
                        while (m < text.Length && char.IsLetter(text[m])) m++;
                        if (m > k && text.Substring(k, m - k).Equals("evolve", StringComparison.OrdinalIgnoreCase))
                        {
                            word = "super-evolve";
                            j = m;
                        }
                    }
                }

                tokens.Add(new Token
                {
                    Kind = Classify(word),
                    Text = word,
                    Pos = i,
                });
                i = j;
                continue;
            }

            // 未知字符：原样保留为 Word，交给上层报错
            tokens.Add(new Token { Kind = TokKind.Word, Text = c.ToString(), Pos = i });
            i++;
        }

        tokens.Add(Eof());

        // 统一回填缩进：按 Pos 往上找到行首，数行首空格。
        // 模式编号块靠它判层级——缩进相同的 1./2. 才是同层兄弟。
        var indents = new Dictionary<int, int>();
        for (int k = 0; k < tokens.Count; k++)
        {
            var tk = tokens[k];
            if (tk.Kind == TokKind.Eof) continue;
            int pos = tk.Pos;
            if (!indents.TryGetValue(pos, out var ind))
            {
                int lineStart = pos;
                while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\u0001') lineStart--;
                int q = lineStart;
                while (q < text.Length && (text[q] == ' ' || text[q] == '	')) q++;
                ind = q - lineStart;
                indents[pos] = ind;
            }
            if (ind != 0) tokens[k] = new Token { Kind = tk.Kind, Text = tk.Text, Value = tk.Value, Pos = tk.Pos, Inner = tk.Inner, Indent = ind };
        }

        return tokens;
    }

    /// <summary>
    /// 当前位置是否允许出现选项序号。
    /// 选项序号只会出现在「块起点」：文本开头，或紧跟句号 / 逗号 / 冒号 /
    /// 分号 / 另一个选项序号 / 引号块之后。
    /// 这样 "X starts at 2." 的句末 2. 就不会被当成下一块的序号。
    /// </summary>
    private static bool IsOptionBoundary(List<Token> tokens)
    {
        if (tokens.Count == 0) return true;
        return tokens[^1].Kind switch
        {
            TokKind.Period or TokKind.Comma or TokKind.Colon
                or TokKind.Semicolon or TokKind.OptionMark or TokKind.Quoted => true,
            _ => false,
        };
    }

    private TokKind Classify(string word)
    {
        if (_lex.IsKeyword(word)) return TokKind.Keyword;
        if (_lex.IsClan(word)) return TokKind.Clan;
        if (_lex.IsTrait(word)) return TokKind.Trait;
        return TokKind.Word;
    }

    private static Token Eof() => new() { Kind = TokKind.Eof, Text = "", Pos = -1 };
}

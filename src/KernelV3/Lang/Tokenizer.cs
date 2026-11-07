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
    /// <summary>
    /// 符号注册表。Lang 层一切符号必须经由它取得真实 Id。
    ///
    /// 必须可注入：SymbolId 的 Id 由 GameSchema 分配，而 BattleState.ReadCounter
    /// 是按整数 Id 索引字典的。若编译器与对局各持一个 CreateDefault() 实例，
    /// 两边对同一个符号名会分配出不同的 Id，动态注册的计数器直接串槽。
    /// </summary>
    public GameSchema Schema { get; }

    public Lexicon(GameSchema schema = null)
    {
        Schema = schema ?? GameSchema.CreateDefault();

        // 必须把 Schema 里**已注册**的关键词 / 兵种 / 职业同步进词法表。
        //
        // 早先这里只写了 Schema = ...，词法表是空的。于是
        // Tokenizer.Classify("Drain") 调 IsKeyword 返回 false，
        // 把 Drain 当成普通 Word 交给 ActionParser，
        // 落到 default 分支：不是 Keyword → 返回 null → 报
        // 「无法解析的动作：停在 [Drain]」。
        //
        // 这类报错看起来像「卡池没定义关键词」，实际上 Schema 里全都有，
        // 只是两个表各管各的、从没同步过。
        foreach (var k in Schema.Keywords.All) _keywords.Add(k.Name);
        foreach (var t in Schema.Traits.All) _traits.Add(t.Name);
        foreach (var c in Schema.Clans.All) _clans.Add(c.Name);
    }

    private readonly Dictionary<string, int> _cardNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _cardIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keywords = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _traits = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _clans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>所有卡名按长度降序，用于最长前缀匹配。</summary>
    private readonly List<string> _namesByLength = new();
    private readonly Dictionary<int, string> _cardNamesById = new();

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
        // 反向表：PyAST 只写卡号，解析时要靠它把名字补回去。
        // CardName 参与运行期按名过滤（CardVm.RemoveByFilter），补不回来会让
        // "deal 2 damage to card:#123@self:board" 变成「对任意卡生效」。
        if (cardId > 0) _cardNamesById[cardId] = key;
    }

    /// <summary>按卡号反查卡名（PyAST 只写卡号时用于补回 CardName）。</summary>
    public bool TryGetCardName(int cardId, out string name)
        => _cardNamesById.TryGetValue(cardId, out name);

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
            // 补齐 -y -> -ies（Fairy -> Fairies，178 号文档 2）
            if (name.EndsWith("y", StringComparison.OrdinalIgnoreCase) && from + name.Length + 2 <= text.Length)
            {
                string stem = name.Substring(0, name.Length - 1);
                if (string.Compare(text, from, stem, 0, stem.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int iesEnd = from + stem.Length + 3;
                    if (iesEnd <= text.Length && string.Compare(text, from + stem.Length, "ies", 0, 3, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        if (iesEnd >= text.Length || !char.IsLetter(text[iesEnd]))
                            return (name, _cardNames[name], iesEnd - from);
                    }
                }
            }

            if (from + name.Length > text.Length) continue;
            if (string.Compare(text, from, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
            // 词边界：后面不能紧跟字母
            int end = from + name.Length;

            // 复数后缀："Summon 2 Ghosts instead" 里的 Ghosts 必须命中卡名 Ghost。
            // 不认复数的话，它掉进普通 Word，召唤句退化成「移动自己」。
            // 只在复数化后仍处在词边界时才接受，避免 "Golds" 误配 "Gold"+s。
            if (end < text.Length && (text[end] == 's' || text[end] == 'S'))
            {
                int endPlural = end + 1;
                if (endPlural >= text.Length || !char.IsLetter(text[endPlural]))
                    return (name, _cardNames[name], endPlural - from);
            }
            // 补齐 -es（Box -> Boxes, Witch -> Witches，178 号文档 2）
            if ((name.EndsWith("x", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("ch", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("sh", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith("s", StringComparison.OrdinalIgnoreCase)) && end + 2 <= text.Length)
            {
                if (string.Compare(text, end, "es", 0, 2, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int endEs = end + 2;
                    if (endEs >= text.Length || !char.IsLetter(text[endEs]))
                        return (name, _cardNames[name], endEs - from);
                }
            }
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
            // 卡号引用：#12345678。
            //
            // 这是 DIY 引用卡牌的**唯一**写法 —— 名字在不同卡池里会重名，
            // 卡号才是稳定的。产出成 CardName token，与按名匹配走同一条下游路径。
            if (c == '#' )
            {
                int j2 = i + 1;
                while (j2 < text.Length && char.IsDigit(text[j2])) j2++;
                if (j2 > i + 1 && int.TryParse(text.Substring(i + 1, j2 - i - 1), out int cid2))
                {
                    _lex.TryGetCardName(cid2, out var refName);
                    tokens.Add(new Token
                    {
                        Kind = TokKind.CardName,
                        Text = refName ?? ("#" + cid2),
                        CardId = cid2,
                        Pos = i,
                    });
                    i = j2;
                    continue;
                }
            }

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

                // 标识符允许下划线：DIY 的自定义计数器/兵种名形如 sword_intent。
                // 只认 IsLetter 会在下划线处截断，把 sword_intent 拆成 sword 与 intent ——
                // 于是玩家注册的计数器名永远匹配不上，而错误信息只说「解析器无法推进」。
                int j = i;
                while (j < text.Length && (char.IsLetter(text[j]) || text[j] == '\'' || text[j] == '’' || text[j] == '_')) j++;
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

                // 英文数词当数字 token：
                //   "at least two 1-base-cost allied cards"
                // 不认的话 ParseNumExpr 读不到数量，整句退化成「1 张」。
                // 只认 1..10，避免把卡名里的 one/two 误当数量
                // （卡名走的是上面的 MatchLongestCardName，到不了这里）。
                int wordNum = EnglishNumber(word);
                if (wordNum > 0)
                {
                    tokens.Add(new Token
                    {
                        Kind = TokKind.Number,
                        Text = word,
                        Value = wordNum,
                        Pos = i,
                    });
                    i = j;
                    continue;
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
            if (ind != 0) tokens[k] = new Token { Kind = tk.Kind, Text = tk.Text, Value = tk.Value, Pos = tk.Pos, Inner = tk.Inner, Indent = ind, CardId = tk.CardId };
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

    /// <summary>英文数词 → 数值（1..10）；非数词返回 0。</summary>
    private static int EnglishNumber(string w) => w.ToLowerInvariant() switch
    {
        // 频率副词当数字："Can attack twice per turn" 里的 twice 必须读成 2，
        // 否则整句退化成「攻击 1 次」，而卡面看起来完全正常。
        "once" => 1,
        "twice" => 2,
        "thrice" => 3,
        "one" => 1,
        "two" => 2,
        "three" => 3,
        "four" => 4,
        "five" => 5,
        "six" => 6,
        "seven" => 7,
        "eight" => 8,
        "nine" => 9,
        "ten" => 10,
        _ => 0,
    };
}

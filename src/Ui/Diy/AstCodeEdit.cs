using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// DIY 工坊的 PyAST / 英文 CNL 代码编辑器。
///
/// 在 Godot 原生 CodeEdit 之上补两件 IDE 能力（179 号文档 C4）：
///   1. 语法高亮：触发头 / 动作动词 / 运算符 / 字符串分别着色；
///   2. 输入 # 时弹出**浮动搜卡候选栏**：可以按卡号、中文名、英文名过滤，
///      回车或 Tab 把 "#妖" 原地替换成 "#90011110"。
///
/// 为什么 # 要单独做浮动栏：玩家记不住 8 位卡号，只会想输入 #妖精，
/// 而 CodeEdit 原生补全只能做前缀匹配，做不到「按卡名搜卡再替换成卡号」。
/// </summary>
public partial class AstCodeEdit : CodeEdit
{
    /// <summary>候选卡来源：(卡号, 中文名, 英文名, 是否衍生)。</summary>
    public Func<IEnumerable<(int Id, string Zh, string En, bool IsToken)>> CardSource { get; set; }

    private PopupPanel _cardPopup;
    private ItemList _cardList;
    private readonly List<(int Id, string Zh, string En, bool IsToken)> _matched = new();
    private int _hashStartCol = -1;

    /// <summary>
    /// 替换 #搜索词 期间抑制 TextChanged 回调。
    /// 不抑制的话：InsertTextAtCaret 写入卡号会同步触发 OnEditorTextChanged，
    /// 正则再次命中刚写好的卡号，刚隐藏的候选窗瞬间又弹出来（180 号文档 D4）。
    /// </summary>
    private bool _suppressPopup;

    /// <summary>一次性接好全部 IDE 特性。构造后必须显式调用。</summary>
    public void SetupIdeFeatures()
    {
        GuttersDrawLineNumbers = true;
        IndentAutomatic = true;
        AutoBraceCompletionEnabled = true;

        // 语法高亮
        // 换成自定义高亮器：内置 CodeHighlighter 只能给整词一个纯色，
        // 无法做「浅主色 -> 主色」的词内渐变（187 号文档 专题三）。
        SyntaxHighlighter = new GradientAstHighlighter();

        // 原生关键字与符号补全（180 号文档 D5）。
        // Godot CodeEdit 自带补全框，开启后敲 $ 或 : 会自动弹出候选。
        CodeCompletionEnabled = true;

        // # 搜卡浮动候选栏
        _cardPopup = new PopupPanel { Unfocusable = true, Size = new Vector2I(340, 190) };
        _cardList = new ItemList { CustomMinimumSize = new Vector2(320, 170) };
        _cardList.ItemActivated += idx => ApplySelectedCard((int)idx);
        _cardPopup.AddChild(_cardList);
        AddChild(_cardPopup);

        TextChanged += OnEditorTextChanged;
    }

    private void OnEditorTextChanged()
    {
        if (_suppressPopup) return;

        int line = GetCaretLine();
        string cur = GetLine(line);
        int col = Math.Min(GetCaretColumn(), cur.Length);
        string prefix = cur.Substring(0, col);

        // 形如光标前是 "...#妖" / "...#Fairy" / "...#900" 时进入搜卡态。
        var m = Regex.Match(prefix, @"#([^\s\n:(),]*)$");
        if (m.Success && CardSource != null)
        {
            _hashStartCol = m.Index;
            string kw = m.Groups[1].Value.Trim();
            _matched.Clear();
            foreach (var c in CardSource())
            {
                bool hit = kw.Length == 0
                    || c.Id.ToString().Contains(kw, StringComparison.Ordinal)
                    || (!string.IsNullOrEmpty(c.Zh) && c.Zh.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrEmpty(c.En) && c.En.Contains(kw, StringComparison.OrdinalIgnoreCase));
                if (!hit) continue;
                _matched.Add(c);
                if (_matched.Count >= 30) break;
            }

            if (_matched.Count > 0)
            {
                _cardList.Clear();
                foreach (var c in _matched)
                {
                    string zh = string.IsNullOrWhiteSpace(c.Zh) ? c.En : c.Zh;
                    _cardList.AddItem("#" + c.Id + " 《" + zh + "》" + (c.IsToken ? " [衍生]" : ""));
                }
                _cardList.Select(0);

                Vector2 caret = GetGlobalTransformWithCanvas().Origin + GetCaretDrawPos();
                _cardPopup.Position = new Vector2I((int)caret.X, (int)caret.Y + 22);
                if (!_cardPopup.Visible) _cardPopup.Popup();
                return;
            }
        }
        if (_cardPopup != null && _cardPopup.Visible) _cardPopup.Hide();

        // 原生关键字补全（180 号文档 D5）：
        // 输入字母 / $ / : 时把 PyAST 指令、槽位与计数器前缀推给 CodeEdit。
        TriggerKeywordCompletion(prefix);
    }

    /// <summary>上一次推入补全队列的「词 + 行」指纹，用于幂等去重。</summary>
    private string _lastCompletionKey;

    /// <summary>
    /// 把 PyAST 关键字推给 CodeEdit 的原生补全框。
    ///
    /// ⚠ AddCodeCompletionOption 是**入队**语义：不清空就直接追加的话，
    /// 每敲一个字符都会重加 40 条，候选列表会越滚越长（内存与体验双输）。
    /// 因此先按当前词干做前缀过滤，并用「词干+行号」做幂等，
    /// 同一位置重复触发时不重复入队。
    /// </summary>
    private void TriggerKeywordCompletion(string prefix)
    {
        if (prefix.Length == 0) { _lastCompletionKey = null; return; }

        char last = prefix[prefix.Length - 1];
        bool trigger = char.IsLetter(last) || last == (char)36 || last == (char)58;
        if (!trigger) { _lastCompletionKey = null; return; }

        // 取光标前最后一个「词」（连续字母 / $ / : / 下划线）。
        int ws = prefix.Length;
        while (ws > 0)
        {
            char ch = prefix[ws - 1];
            if (char.IsLetterOrDigit(ch) || ch == (char)36 || ch == (char)58 || ch == (char)95) ws--;
            else break;
        }
        string stem = prefix.Substring(ws);
        if (stem.Length == 0) { _lastCompletionKey = null; return; }

        // 幂等：同一行、同一词干只入队一次。
        string key = GetCaretLine() + "|" + stem;
        if (key == _lastCompletionKey) return;
        _lastCompletionKey = key;

        int added = 0;
        foreach (var kw in IdeKeywords)
        {
            // 前缀过滤：敲 "fa" 只提示 fanfare:，不是把 40 条全推上去。
            if (kw.IndexOf(stem, StringComparison.OrdinalIgnoreCase) < 0) continue;
            AddCodeCompletionOption(CodeCompletionKind.PlainText, kw, kw);
            added++;
        }
        if (added > 0) UpdateCodeCompletionOptions(false);
    }

    /// <summary>原生补全候选：触发头、动作动词、槽位与计数器前缀。</summary>
    private static readonly string[] IdeKeywords =
    {
        "fanfare:", "last_words:", "evolve:", "super_evolve:", "strike:", "clash:",
        "engage:", "aura:", "turn_start:", "turn_end:", "on_enter", "on_leave",
        "on_destroy", "on_summon", "on_draw", "on_discard", "on_damaged",
        "deal", "draw", "restore", "summon", "add_to_hand", "destroy", "banish",
        "discard", "buff", "set_stats", "grant_keyword", "counter", "select",
        "transform", "move", "where", "modes", "option", "if", "else",
        "$self", "$selected", "$created", "$trigger", "$opposing", "$choose1",
        "ctr:", "prop:", "sum:", "split:", "trait:",
    };

    private void ApplySelectedCard(int idx)
    {
        if (idx < 0 || idx >= _matched.Count) return;
        // 整个替换过程必须抑制 TextChanged，否则写回卡号的瞬间
        // 正则又命中、候选窗立刻重开（180 号文档 D4）。
        _suppressPopup = true;
        try
        {
            if (_cardPopup != null) _cardPopup.Hide();
            int line = GetCaretLine();
            int col = GetCaretColumn();
            // 选中整段 "#搜索词"，再原位替换成 "#卡号 "。
            // 末尾补一个空格：既符合书写习惯，也让光标离开 # 匹配区，
            // 双重保证候选窗不会再被这一下重新唤出。
            // 光标必须落在**插入内容之后**，而不是整行末尾：
            // "#妖" 后面可能还有别的代码，跳到行尾会把光标甩到无关位置。
            Select(line, _hashStartCol, line, col);
            string inserted = "#" + _matched[idx].Id + " ";
            InsertTextAtCaret(inserted);
            SetCaretLine(line);
            SetCaretColumn(_hashStartCol + inserted.Length);
        }
        finally
        {
            _suppressPopup = false;
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        // 候选栏打开时劫持方向键 / 回车 / Tab / Esc
        if (_cardPopup != null && _cardPopup.Visible
            && @event is InputEventKey k && k.Pressed)
        {
            int cur = _cardList.GetSelectedItems().Length > 0
                ? _cardList.GetSelectedItems()[0] : 0;
            switch (k.Keycode)
            {
                case Key.Down:
                    _cardList.Select(Math.Min(_matched.Count - 1, cur + 1));
                    AcceptEvent();
                    return;
                case Key.Up:
                    _cardList.Select(Math.Max(0, cur - 1));
                    AcceptEvent();
                    return;
                case Key.Enter:
                case Key.KpEnter:
                case Key.Tab:
                    ApplySelectedCard(cur);
                    AcceptEvent();
                    return;
                case Key.Escape:
                    _cardPopup.Hide();
                    AcceptEvent();
                    return;
            }
        }
        base._GuiInput(@event);
    }

    private static CodeHighlighter BuildPyAstHighlighter()
    {
        var hl = new CodeHighlighter
        {
            NumberColor = new Color("4ADE80"),
            SymbolColor = new Color("94A3B8"),
            FunctionColor = new Color("38BDF8"),
            MemberVariableColor = new Color("FBBF24"),
        };

        // 触发头
        foreach (var trig in new[]
        {
            "fanfare", "last_words", "evolve", "super_evolve", "strike", "clash",
            "engage", "aura", "turn_start", "turn_end", "crest", "faith",
            "accelerate", "crystallize", "on_enter", "on_leave", "on_destroy",
            "on_summon", "on_draw", "on_discard", "on_damaged", "on_healed",
            "on_banish", "on_fuse", "on_spellboost", "on_earth_rite",
            "on_select_mode", "on_deck_out", "on_countdown_expire", "on_counter_change",
        })
            hl.AddKeywordColor(trig, new Color("FDE047"));

        // 动作动词
        foreach (var act in new[]
        {
            "deal", "draw", "restore", "summon", "add_to_hand", "destroy", "banish",
            "discard", "bounce", "buff", "set_stats", "grant_keyword", "discover",
            "reanimate", "gain_crest", "if", "elif", "else", "repeat", "for_each",
            "modes", "option", "pay", "where", "counter", "select", "transform",
            "evolve", "invoke_ability", "grant_ability", "move",
        })
            hl.AddKeywordColor(act, new Color("C084FC"));

        // 字符串
        hl.AddColorRegion("\"", "\"", new Color("86EFAC"), false);
        return hl;
    }

/// <summary>
/// 支持「浅主色 -> 主色」词内线性渐变的全语法高亮器（187 号文档 专题三）。
///
/// 内置 CodeHighlighter 只能给一个词一个纯色，做不到逐字符插值。
/// 这里重写 SyntaxHighlighter._GetLineSyntaxHighlighting，按**列号**逐字符
/// 写入插值后的颜色 —— 每个关键字、每个槽位、每串字符串都会呈现横向渐变。
///
/// 词表同时覆盖 PyAST 规范语法、官方英文 CNL 与中文关键词三大阵营。
/// </summary>
public sealed partial class GradientAstHighlighter : SyntaxHighlighter
{
    // 七大语法色系（浅主色 -> 主色）
    private static readonly (Color Light, Color Main) PalTrigger = (new Color("FFF9DB"), new Color("F59E0B"));
    private static readonly (Color Light, Color Main) PalAction  = (new Color("F3E8FF"), new Color("C084FC"));
    private static readonly (Color Light, Color Main) PalTarget  = (new Color("E0F7FA"), new Color("06B6D4"));
    private static readonly (Color Light, Color Main) PalSlot    = (new Color("FFE4E6"), new Color("FB7185"));
    private static readonly (Color Light, Color Main) PalKeyword = (new Color("DCFCE7"), new Color("10B981"));
    private static readonly (Color Light, Color Main) PalNumber  = (new Color("FEF08A"), new Color("EAB308"));
    private static readonly (Color Light, Color Main) PalDefault = (new Color("FFF3CF"), new Color("E3BD63"));
    private static readonly (Color Light, Color Main) PalComment = (new Color("94A3B8"), new Color("475569"));

    /// <summary>
    /// 一次扫描匹配全部语法家族。命名组顺序即优先级：
    /// 注释 > 字符串 > 卡号 > 槽位/前缀 > 触发头 > 动作 > 关键词 > 目标 > 数字。
    /// </summary>
    private static readonly Regex TokenRegex = new(
        @"(?<comment>//.*$)" +
        @"|(?<str>""[^""]*""|【[^】]+】|《[^》]+》)" +
        @"|(?<card>#\d+\b)" +
        @"|(?<slot>\$[a-zA-Z0-9_]+|\b(?:ctr|prop|sum|split|trait|clan):[a-zA-Z0-9_\u4e00-\u9fa5]+)" +
        @"|(?<trig>\b(?:fanfare|last_words|evolve|super_evolve|strike|clash|engage|aura|turn_start|turn_end|crest|faith|accelerate|crystallize|on_\w+|Fanfare|Last\s+Words|Evolve|Super-Evolve|Strike|Clash|Engage|Activate|Countdown|Necromancy|Earth\s+Rite|Combo|Rally|Overflow|Wrath|Spellboost|Skybound\s+Art|Super\s+Skybound\s+Art|Enhance|Fuse|Reanimate|Invoke)\b:?)" +
        @"|(?<act>\b(?:card|deck_mod|keywords|deal|draw|restore|summon|add_to_hand|destroy|banish|discard|bounce|buff|set_stats|grant_keyword|remove_keyword|discover|reanimate|gain_crest|if|elif|else|repeat|for_each|modes|option|pay|where|counter|select|transform|invoke_ability|grant_ability|move|give|gain|damage|life|leader|followers?|amulets?|spells?|cards?)\b)" +
        @"|(?<kw>\b(?:ward|rush|storm|bane|drain|ambush|intimidate|barrier|ignores_ward|free_evolve|free_super_evolve|immune_\w+|cannot_\w+|Ward|Rush|Storm|Bane|Drain|Ambush|Aura|Intimidate|Barrier)\b)" +
        @"|(?<tgt>\b(?:self|opponent|any|board|hand|deck|cemetery|banished|crest_area|ex_area|All|Choose|Random|First|Last|Distinct|Follower|Spell|Amulet|Crest|Faith|Leader|ThisTurn|Permanent|UntilEndOfOpponentTurn)\b)" +
        @"|(?<num>[+-]?\d+(?:/[+-]?\d+)?)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    public override Godot.Collections.Dictionary _GetLineSyntaxHighlighting(int line)
    {
        var dict = new Godot.Collections.Dictionary();
        var te = GetTextEdit();
        if (te == null || line < 0 || line >= te.GetLineCount()) return dict;
        string text = te.GetLine(line);
        if (string.IsNullOrEmpty(text)) return dict;

        // 先铺整行默认渐变，保证普通单词也有质感；再按 Token 覆盖专属色系。
        PaintSpan(dict, 0, text.Length, PalDefault.Light, PalDefault.Main);

        foreach (Match m in TokenRegex.Matches(text))
        {
            if (!m.Success || m.Length == 0) continue;
            var pal = m.Groups["comment"].Success ? PalComment
                    : m.Groups["str"].Success     ? PalKeyword
                    : m.Groups["card"].Success    ? PalNumber
                    : m.Groups["slot"].Success    ? PalSlot
                    : m.Groups["trig"].Success    ? PalTrigger
                    : m.Groups["act"].Success     ? PalAction
                    : m.Groups["kw"].Success      ? PalKeyword
                    : m.Groups["tgt"].Success     ? PalTarget
                    : m.Groups["num"].Success     ? PalNumber
                    : PalDefault;
            PaintSpan(dict, m.Index, m.Length, pal.Light, pal.Main);
        }
        return dict;
    }

    /// <summary>给 [start, start+len) 的每一列写入插值色，形成词内渐变。</summary>
    private static void PaintSpan(Godot.Collections.Dictionary dict, int start, int len,
                                  Color light, Color main)
    {
        for (int i = 0; i < len; i++)
        {
            float t = len <= 1 ? 0f : (float)i / (len - 1);
            dict[start + i] = new Godot.Collections.Dictionary
            {
                ["color"] = light.Lerp(main, t),
            };
        }
    }
}
}

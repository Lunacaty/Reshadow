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

    /// <summary>一次性接好全部 IDE 特性。构造后必须显式调用。</summary>
    public void SetupIdeFeatures()
    {
        GuttersDrawLineNumbers = true;
        IndentAutomatic = true;
        AutoBraceCompletionEnabled = true;

        // 语法高亮
        SyntaxHighlighter = BuildPyAstHighlighter();

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
    }

    private void ApplySelectedCard(int idx)
    {
        if (idx < 0 || idx >= _matched.Count) return;
        int line = GetCaretLine();
        int col = GetCaretColumn();
        // 选中整段 "#搜索词"，再原位替换成 "#卡号"。
        Select(line, _hashStartCol, line, col);
        InsertTextAtCaret("#" + _matched[idx].Id);
        if (_cardPopup != null) _cardPopup.Hide();
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
}

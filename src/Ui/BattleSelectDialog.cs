using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui;

/// <summary>
/// 对战选择窗：先挑赛制卡池 → 再挑我方牌组 → 再挑 AI 牌组 → 开打。
///
/// 卡池放最前是因为牌组按 PoolId 过滤：先定赛制，后面的列表才有意义。
/// 三栏都只列**该卡池下可出战**的牌组，避免选到一半被校验拦下来。
/// </summary>
public partial class BattleSelectDialog : Control
{
    public event Action<ConstructedDeck, ConstructedDeck> OnStart;
    public event Action OnCancelled;

    private GameSessionStore _store;
    private string _poolId;
    private ConstructedDeck _myDeck;
    private ConstructedDeck _foeDeck;

    private VBoxContainer _poolCol;
    private VBoxContainer _myCol;
    private VBoxContainer _foeCol;
    private Button _startBtn;
    private Label _status;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 90;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.74f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(1080, 608));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.AccentGold, 14, 2, 20));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 14);
        panel.AddChild(root);

        var title = new Label { Text = "⚔  开 始 对 战" };
        title.AddThemeFontSizeOverride("font_size", 26);
        title.AddThemeColorOverride("font_color", new Color("FDE68A"));
        root.AddChild(title);

        var hint = new Label { Text = "先选赛制卡池，再选我方牌组与对手牌组。" };
        hint.AddThemeFontSizeOverride("font_size", 12);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        root.AddChild(hint);

        var cols = new HBoxContainer();
        cols.AddThemeConstantOverride("separation", 16);
        cols.SizeFlagsVertical = SizeFlags.ExpandFill;
        root.AddChild(cols);

        _poolCol = AddColumn(cols, "① 赛制卡池", 240);
        _myCol = AddColumn(cols, "② 我方牌组", 380);
        _foeCol = AddColumn(cols, "③ 对手（AI）牌组", 380);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 12);
        root.AddChild(bar);

        _status = new Label { Text = "" };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        bar.AddChild(_status);

        bar.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var cancel = new Button { Text = "取消", CustomMinimumSize = new Vector2(120, 44) };
        SvWbTheme.StyleButton(cancel);
        cancel.Pressed += () => OnCancelled?.Invoke();
        bar.AddChild(cancel);

        _startBtn = new Button { Text = "⚔  出 战", CustomMinimumSize = new Vector2(200, 44) };
        SvWbTheme.StyleButton(_startBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        _startBtn.Pressed += () =>
        {
            if (_myDeck != null && _foeDeck != null) OnStart?.Invoke(_myDeck, _foeDeck);
        };
        bar.AddChild(_startBtn);

        // 默认选中第一个卡池
        var first = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();
        SelectPool(first);
    }

    private static VBoxContainer AddColumn(HBoxContainer host, string title, int width)
    {
        var wrap = new VBoxContainer { CustomMinimumSize = new Vector2(width, 0) };
        wrap.AddThemeConstantOverride("separation", 8);
        host.AddChild(wrap);

        var lbl = new Label { Text = title };
        lbl.AddThemeFontSizeOverride("font_size", 14);
        lbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        wrap.AddChild(lbl);

        var scroll = new ScrollContainer();
        scroll.SizeFlagsVertical = SizeFlags.ExpandFill;
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        wrap.AddChild(scroll);
        SvWbTheme.StyleScroll(scroll);

        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(col);
        return col;
    }

    private void Clear(VBoxContainer col)
    {
        foreach (var c in col.GetChildren()) c.QueueFree();
    }

    private void SelectPool(string poolId)
    {
        _poolId = poolId;
        _myDeck = null;
        _foeDeck = null;
        RefreshPools();
        RefreshDeckColumns();
        UpdateStatus();
    }

    private void RefreshPools()
    {
        Clear(_poolCol);
        foreach (var kv in _store.PoolConfigs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            string pid = kv.Key;
            bool on = pid == _poolId;
            _poolCol.AddChild(MakePickButton(
                (kv.Value.Name ?? pid) + "  (" + CountOf(pid) + " 副)",
                on, SvWbTheme.AccentCyan,
                () => SelectPool(pid)));
        }
    }

    private int CountOf(string poolId)
        => _store.SavedDecks.Count(d => string.Equals(d.PoolId, poolId, StringComparison.OrdinalIgnoreCase));

    private void RefreshDeckColumns()
    {
        Clear(_myCol);
        Clear(_foeCol);

        var decks = _store.SavedDecks
            .Where(d => string.Equals(d.PoolId, _poolId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(d => d.Cards.Count).ToList();

        if (decks.Count == 0)
        {
            var none = new Label { Text = "（该卡池下没有牌组）", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            none.AddThemeFontSizeOverride("font_size", 12);
            none.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _myCol.AddChild(none);
            var none2 = new Label { Text = "（该卡池下没有牌组）" };
            none2.AddThemeFontSizeOverride("font_size", 12);
            none2.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _foeCol.AddChild(none2);
            return;
        }

        // 已选的牌组可能不属于当前卡池（刚切过池）—— 那种情况下必须重选，
        // 否则「我方」栏会保留上一次卡池的牌组，而列表里根本没有它，
        // 出战按钮亮着、点下去却拿另一套卡池的牌组开局。
        if (_myDeck != null && !decks.Any(d => ReferenceEquals(d, _myDeck))) _myDeck = null;
        if (_foeDeck != null && !decks.Any(d => ReferenceEquals(d, _foeDeck))) _foeDeck = null;

        // 默认：我方选第一副，AI 选第二副（没有就同一副）
        _myDeck ??= decks[0];
        _foeDeck ??= decks.Count > 1 ? decks[1] : decks[0];

        foreach (var d in decks)
        {
            var capture = d;
            _myCol.AddChild(MakePickButton(DeckLabel(d), ReferenceEquals(d, _myDeck), SvWbTheme.AccentGold,
                () => { _myDeck = capture; RefreshDeckColumns(); UpdateStatus(); }));
            _foeCol.AddChild(MakePickButton(DeckLabel(d), ReferenceEquals(d, _foeDeck), SvWbTheme.StatusDanger,
                () => { _foeDeck = capture; RefreshDeckColumns(); UpdateStatus(); }));
        }
    }

    private static string DeckLabel(ConstructedDeck d)
    {
        int n = d.Cards?.Sum(x => x.count) ?? 0;
        return d.Name + "\n" + n + " 张 · " + (d.LeaderClan ?? "Neutral");
    }

    private static Button MakePickButton(string text, bool active, Color accent, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            Alignment = HorizontalAlignment.Left,
            CustomMinimumSize = new Vector2(0, 52),
        };
        b.AddThemeFontSizeOverride("font_size", 13);
        if (active)
        {
            // 选中态：深底 + 该强调色的边光与辉光，文字同色提亮。
            // 不再用「accent 分通道乘 0.25」那种算法 —— 它会把金色压成污浊橄榄褐。
            var accentLit = SvWbTheme.Brighten(accent, 1.15f);
            var box = SvWbTheme.ButtonBox(new Color("1A2438"), accentLit, 2, 14, accentLit);
            b.AddThemeStyleboxOverride("normal", box);
            b.AddThemeStyleboxOverride("hover", box);
            b.AddThemeStyleboxOverride("pressed", box);
            b.AddThemeStyleboxOverride("focus", box);
            b.AddThemeColorOverride("font_color", SvWbTheme.Brighten(accent, 2.0f));
            b.AddThemeColorOverride("font_hover_color", Colors.White);
            b.MouseDefaultCursorShape = CursorShape.PointingHand;
        }
        else
        {
            SvWbTheme.StyleButton(b);
        }
        b.Pressed += onPressed;
        return b;
    }

    private void UpdateStatus()
    {
        if (_startBtn == null) return;
        bool ready = _myDeck != null && _foeDeck != null;
        _startBtn.Disabled = !ready;

        if (_status != null)
        {
            _status.Text = ready
                ? "我方：" + _myDeck.Name + "　vs　AI：" + _foeDeck.Name
                : "请先选择卡池与双方牌组";
        }
    }
}
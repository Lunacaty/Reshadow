using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Decks;

/// <summary>
/// 卡片一览 / 图鉴：全量浏览卡池，查看大图与完整效果，切换原画⇄异画。
///
/// 右栏的变体切换调 CardArtLoader.SetPreferredVariant ——
/// 一处切换，手牌 / 详情 / 构筑台 / 战场棋子全部同步生效。
/// </summary>
public partial class CardGalleryView : Control
{
    public event Action OnBack;

    private GameSessionStore _store;
    private GridContainer _grid;
    private ScrollContainer _gridScroll;
    private VBoxContainer _detail;
    private LineEdit _search;
    private OptionButton _kindPicker;
    private readonly List<CardDef> _shown = new();
    private CardDef _selected;
    private bool _showEvolved;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 8);

        var back = new Button { Text = "← 返回主菜单" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBack?.Invoke();
        bar.AddChild(back);

        _search = new LineEdit { PlaceholderText = "搜卡名 / 效果…", CustomMinimumSize = new Vector2(220, 30) };
        _search.TextChanged += _ => RefreshGrid();
        bar.AddChild(_search);

        _kindPicker = new OptionButton { CustomMinimumSize = new Vector2(120, 30) };
        SvWbTheme.StyleButton(_kindPicker);
        SvWbTheme.StyleOptionPopup(_kindPicker);
        _kindPicker.AddItem("全部卡种", 0);
        _kindPicker.AddItem("随从", 1);
        _kindPicker.AddItem("法术", 2);
        _kindPicker.AddItem("护符", 3);
        _kindPicker.ItemSelected += _ => RefreshGrid();
        bar.AddChild(_kindPicker);

        AddChild(bar);

        var split = new HBoxContainer();
        split.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        split.OffsetTop = 44f;
        split.AddThemeConstantOverride("separation", 12);
        AddChild(split);

        _gridScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(_gridScroll);
        split.AddChild(_gridScroll);

        _grid = new GridContainer { Columns = 5 };
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        _gridScroll.AddChild(_grid);

        _detail = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        _detail.AddThemeConstantOverride("separation", 6);
        split.AddChild(_detail);

        RefreshGrid();
    }

    private void RefreshGrid()
    {
        foreach (var c in _grid.GetChildren()) { _grid.RemoveChild(c); c.QueueFree(); }
        _shown.Clear();
        if (_store?.Catalog == null) return;

        string q = (_search?.Text ?? "").Trim();
        int kindSel = _kindPicker?.Selected ?? 0;

        var cards = _store.Catalog.AllCards.Values
            .Where(c => !c.IsToken)
            .Where(c => kindSel == 0 || (int)c.Kind == kindSel)
            .Where(c => q.Length == 0
                        || (c.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                        || (c.NameChs ?? "").Contains(q)
                        || _store.CardPrinter.PrintCard(c).Contains(q))
            .OrderBy(c => c.CardId)
            .ToList();

        foreach (var def in cards)
        {
            _shown.Add(def);
            int idx = _shown.Count - 1;
            var w = new Widgets.CardVisualWidget();
            w.Setup(def, _store.CardPrinter, 0, 3, showCopyPips: false);
            w.SetCardSize(150f, 197f);
            w.OnLeftClick += _ => ShowDetail(_shown[idx]);
            _grid.AddChild(w);
        }
    }

    private void ShowDetail(CardDef def)
    {
        _selected = def;
        foreach (var c in _detail.GetChildren()) { _detail.RemoveChild(c); c.QueueFree(); }
        if (def == null) return;

        var title = new Label { Text = def.NameChs ?? def.Name, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        title.AddThemeFontSizeOverride("font_size", 17);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        _detail.AddChild(title);

        var sub = new Label
        {
            Text = $"{def.CardId}　{(def.Name ?? "")}　{(def.Clan.IsValid ? def.Clan.Name : "")}　{(def.IsToken ? "Token" : "")}",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        sub.AddThemeFontSizeOverride("font_size", 11);
        sub.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _detail.AddChild(sub);

        // 形态切换：进化前 / 进化后。
        var formRow = new HBoxContainer();
        formRow.AddThemeConstantOverride("separation", 6);
        var f0 = new Button { Text = "👁 进化前", CustomMinimumSize = new Vector2(0, 30) };
        var f1 = new Button { Text = "🔥 进化后", CustomMinimumSize = new Vector2(0, 30) };
        SvWbTheme.StyleButton(f0, isPrimary: !_showEvolved);
        SvWbTheme.StyleButton(f1, isPrimary: _showEvolved);
        f0.Pressed += () => { _showEvolved = false; ShowDetail(def); };
        f1.Pressed += () => { _showEvolved = true; ShowDetail(def); };
        formRow.AddChild(f0);
        formRow.AddChild(f1);
        _detail.AddChild(formRow);

        // 大图预览（当前形态 + 当前变体）。
        var art = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = new Vector2(0, 320),
        };
        var tex = _showEvolved
            ? CardArtLoader.LoadEvolved(def.CardId) ?? CardArtLoader.Load(def.CardId)
            : CardArtLoader.Load(def.CardId);
        if (tex != null) art.Texture = tex;
        _detail.AddChild(art);

        // 立绘版本切换台（原画 ⇄ 异画）。
        var variants = CardArtLoader.GetAvailableVariants(def.CardId);
        if (variants.Count > 1)
        {
            var vHead = new Label { Text = "立绘版本" };
            vHead.AddThemeFontSizeOverride("font_size", 13);
            vHead.AddThemeColorOverride("font_color", new Color("FDE68A"));
            _detail.AddChild(vHead);

            var vRow = new HFlowContainer();
            vRow.AddThemeConstantOverride("h_separation", 6);
            int cur = CardArtLoader.GetPreferredVariant(def.CardId);
            foreach (var (code, label) in variants)
            {
                var b = new Button { Text = (code == cur ? "● " : "") + label, CustomMinimumSize = new Vector2(0, 28) };
                SvWbTheme.StyleButton(b, isPrimary: code == cur);
                int captured = code;
                b.Pressed += () =>
                {
                    CardArtLoader.SetPreferredVariant(def.CardId, captured);
                    ShowDetail(def);
                    RefreshGrid();
                };
                vRow.AddChild(b);
            }
            _detail.AddChild(vRow);
        }

        // 完整效果文本。
        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = SvWbTheme.FormatRichCardText(_store.CardPrinter.PrintCard(def)),
            FitContent = true,
            ScrollActive = false,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        body.AddThemeFontSizeOverride("normal_font_size", 12);
        _detail.AddChild(body);
    }
}

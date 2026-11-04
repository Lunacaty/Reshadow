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

    // 分页：千卡一次性实例化会卡死 UI。每页只造 15 张（5 列 × 3 行）。
    private const int PageSize = 15;
    private int _currentPage;
    private int _totalPages = 1;
    private Label _pageLabel;
    private Button _prevPageBtn;
    private Button _nextPageBtn;
    private int _lastFilteredCount;
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

        var filterBar = BuildFilterBar();
        filterBar.SetAnchorsPreset(LayoutPreset.TopLeft);
        filterBar.OffsetTop = 36f;
        AddChild(filterBar);

        var split = new HBoxContainer();
        split.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        split.OffsetTop = 76f;
        split.AddThemeConstantOverride("separation", 12);
        AddChild(split);

        // 左半屏：网格 + 底部分页栏
        var gridCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        gridCol.AddThemeConstantOverride("separation", 6);
        split.AddChild(gridCol);

        _gridScroll = new ScrollContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(_gridScroll);
        gridCol.AddChild(_gridScroll);

        _grid = new GridContainer { Columns = 5 };
        _grid.AddThemeConstantOverride("h_separation", 8);
        _grid.AddThemeConstantOverride("v_separation", 8);
        _gridScroll.AddChild(_grid);

        // 分页栏：千卡图鉴只实例化当前页，翻页不重建整表。
        var pageBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 34) };
        pageBar.AddThemeConstantOverride("separation", 8);
        _prevPageBtn = new Button { Text = "◀ 上一页", CustomMinimumSize = new Vector2(96, 30) };
        SvWbTheme.StyleButton(_prevPageBtn);
        _prevPageBtn.Pressed += () => { if (_currentPage > 0) { _currentPage--; RefreshGrid(); } };
        pageBar.AddChild(_prevPageBtn);

        _pageLabel = new Label
        {
            Text = "第 1 / 1 页",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageLabel.AddThemeFontSizeOverride("font_size", 12);
        pageBar.AddChild(_pageLabel);

        _nextPageBtn = new Button { Text = "下一页 ▶", CustomMinimumSize = new Vector2(96, 30) };
        SvWbTheme.StyleButton(_nextPageBtn);
        _nextPageBtn.Pressed += () => { if (_currentPage < _totalPages - 1) { _currentPage++; RefreshGrid(); } };
        pageBar.AddChild(_nextPageBtn);

        gridCol.AddChild(pageBar);

        _detail = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        _detail.AddThemeConstantOverride("separation", 6);
        split.AddChild(_detail);

        RefreshGrid();
    }


    // ---------------- 多维筛选器（126 专题六） ----------------

    private OptionButton _clanPicker;
    private OptionButton _costPicker;
    private OptionButton _rarityPicker;
    private OptionButton _packPicker;
    private CheckBox _altOnlyCheck;
    private CheckBox _tokenCheck;

    private Control BuildFilterBar()
    {
        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 4);

        // 职业
        _clanPicker = new OptionButton { CustomMinimumSize = new Vector2(110, 28) };
        SvWbTheme.StyleButton(_clanPicker); SvWbTheme.StyleOptionPopup(_clanPicker);
        _clanPicker.AddItem("全部职业", 0);
        var clans = (_store.Catalog.AllCards.Values
            .Select(c => c.Clan.IsValid ? c.Clan.Name : "Neutral")
            .Distinct().OrderBy(x => x, StringComparer.Ordinal)).ToList();
        for (int i = 0; i < clans.Count; i++) _clanPicker.AddItem(clans[i], i + 1);
        _clanPicker.ItemSelected += _ => RefreshGrid();
        flow.AddChild(_clanPicker);

        // 费用
        _costPicker = new OptionButton { CustomMinimumSize = new Vector2(96, 28) };
        SvWbTheme.StyleButton(_costPicker); SvWbTheme.StyleOptionPopup(_costPicker);
        _costPicker.AddItem("全部费用", 0);
        for (int i = 0; i <= 8; i++) _costPicker.AddItem(i == 8 ? "8+ 费" : i + " 费", i + 1);
        _costPicker.ItemSelected += _ => RefreshGrid();
        flow.AddChild(_costPicker);

        // 稀有度
        _rarityPicker = new OptionButton { CustomMinimumSize = new Vector2(100, 28) };
        SvWbTheme.StyleButton(_rarityPicker); SvWbTheme.StyleOptionPopup(_rarityPicker);
        _rarityPicker.AddItem("全部稀有度", 0);
        _rarityPicker.AddItem("青铜", 1); _rarityPicker.AddItem("白银", 2);
        _rarityPicker.AddItem("黄金", 3); _rarityPicker.AddItem("虹彩", 4);
        _rarityPicker.ItemSelected += _ => RefreshGrid();
        flow.AddChild(_rarityPicker);

        // 卡包
        _packPicker = new OptionButton { CustomMinimumSize = new Vector2(120, 28) };
        SvWbTheme.StyleButton(_packPicker); SvWbTheme.StyleOptionPopup(_packPicker);
        _packPicker.AddItem("全部卡包", 0);
        var packs = (_store.Catalog.Packs?.Keys ?? Enumerable.Empty<string>())
            .OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < packs.Count; i++) _packPicker.AddItem(packs[i], i + 1);
        _packPicker.ItemSelected += _ => RefreshGrid();
        flow.AddChild(_packPicker);

        // 特殊筛选
        _altOnlyCheck = new CheckBox { Text = "★ 仅看含异画" };
        _altOnlyCheck.Toggled += _ => RefreshGrid();
        flow.AddChild(_altOnlyCheck);

        _tokenCheck = new CheckBox { Text = "含衍生 Token" };
        _tokenCheck.Toggled += _ => RefreshGrid();
        flow.AddChild(_tokenCheck);

        return flow;
    }

    private void RefreshGrid()
    {
        foreach (var c in _grid.GetChildren()) { _grid.RemoveChild(c); c.QueueFree(); }
        _shown.Clear();
        if (_store?.Catalog == null) return;

        string q = (_search?.Text ?? "").Trim();
        int kindSel = _kindPicker?.Selected ?? 0;

        // 逐维叠加：任何一种筛选都是「再加一层 where」，互不覆盖。
        IEnumerable<CardDef> query = _store.Catalog.AllCards.Values;

        if (_tokenCheck?.ButtonPressed != true) query = query.Where(c => !c.IsToken);
        if (kindSel != 0) query = query.Where(c => (int)c.Kind == kindSel);

        // 职业
        if (_clanPicker != null && _clanPicker.Selected > 0)
        {
            string want = _clanPicker.GetItemText(_clanPicker.Selected);
            query = query.Where(c =>
                string.Equals(c.Clan.IsValid ? c.Clan.Name : "Neutral", want, StringComparison.OrdinalIgnoreCase));
        }

        // 费用（下标 1..9 对应 0..8，8 是 8+）
        if (_costPicker != null && _costPicker.Selected > 0)
        {
            int costSel = _costPicker.Selected - 1;
            query = costSel >= 8
                ? query.Where(c => c.BaseCost >= 8)
                : query.Where(c => c.BaseCost == costSel);
        }

        // 稀有度
        if (_rarityPicker != null && _rarityPicker.Selected > 0)
        {
            int rar = _rarityPicker.Selected;
            query = query.Where(c => c.Rarity == rar);
        }

        // 卡包
        if (_packPicker != null && _packPicker.Selected > 0)
        {
            string pack = _packPicker.GetItemText(_packPicker.Selected);
            query = query.Where(c => string.Equals(c.PackId, pack, StringComparison.OrdinalIgnoreCase));
        }

        // 仅看含异画
        if (_altOnlyCheck?.ButtonPressed == true)
            query = query.Where(c => CardArtLoader.GetAvailableVariants(c.CardId).Count > 1);

        // 搜索
        if (q.Length > 0)
            query = query.Where(c =>
                (c.Name ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                || (c.NameChs ?? "").Contains(q)
                || _store.CardPrinter.PrintCard(c).Contains(q));

        var all = query.OrderBy(c => c.CardId).ToList();
        _lastFilteredCount = all.Count;

        _totalPages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        _currentPage = Math.Clamp(_currentPage, 0, _totalPages - 1);
        var cards = all.Skip(_currentPage * PageSize).Take(PageSize).ToList();

        if (_pageLabel != null)
            _pageLabel.Text = $"第 {_currentPage + 1} / {_totalPages} 页　（共 {all.Count} 张）";
        if (_prevPageBtn != null) _prevPageBtn.Disabled = _currentPage <= 0;
        if (_nextPageBtn != null) _nextPageBtn.Disabled = _currentPage >= _totalPages - 1;

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


    /// <summary>
    /// 在图鉴里直接导入/替换某张卡的异画。
    ///
    /// 落盘到该卡所属卡包的 cards/ 目录（自包含），命名 <ArtCardId>_3.png；
    /// 保存后清缓存并把该卡的偏好切到异画，网格与详情立刻刷新。
    /// </summary>
    private void OpenImportAltArt(CardDef def)
    {
        if (def == null) return;

        var dlg = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; 图片文件" },
            Title = "选择异画图片（" + (def.NameChs ?? def.Name) + "）",
            Size = new Vector2I(760, 480),
        };
        dlg.FileSelected += srcPath =>
        {
            try
            {
                var img = Image.LoadFromFile(srcPath);
                if (img == null || img.IsEmpty()) { dlg.QueueFree(); return; }

                // 沿用源素材原号，跨创作者撞号时各读各的目录。
                int artId = def.ArtCardId > 0 ? def.ArtCardId : def.CardId;
                string packId = string.IsNullOrEmpty(def.PackId) ? "testset-v2" : def.PackId;

                string dir = System.IO.Path.Combine(
                    ShadowverseFan.Ui.ContentPaths.Root, "data", "packs", packId, "cards");
                System.IO.Directory.CreateDirectory(dir);
                string dst = System.IO.Path.Combine(dir, artId + "_3.png");
                img.SavePng(dst);

                CardArtLoader.ClearCache();
                CardArtLoader.SetPreferredVariant(def.CardId, 3);

                ShowDetail(def);
                RefreshGrid();
            }
            catch { }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
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

        // 立绘版本切换台（原画 ⇄ 异画）+
        // ★ 无论有没有现成异画文件都要显示 —— 早先整个区块包在
        //   variants.Count > 1 里，没异画的卡连「导入异画」的入口都没有。
        {
            var vHead = new Label { Text = "立绘版本" };
            vHead.AddThemeFontSizeOverride("font_size", 13);
            vHead.AddThemeColorOverride("font_color", new Color("FDE68A"));
            _detail.AddChild(vHead);

            var variants = CardArtLoader.GetAvailableVariants(def.CardId);
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
                    ShowDetail(def);   // 详情大图跟着换
                    RefreshGrid();     // 网格立绘也换（文档 Bug 6 点名的第二点）
                };
                vRow.AddChild(b);
            }

            // 图鉴里直接导入/替换异画：选一张本地图片落盘为 _3，并立刻切过去。
            var importBtn = new Button { Text = "🎨 导入/替换异画", CustomMinimumSize = new Vector2(0, 28) };
            SvWbTheme.StyleButton(importBtn);
            importBtn.Pressed += () => OpenImportAltArt(def);
            vRow.AddChild(importBtn);

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

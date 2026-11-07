using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Diy;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Decks;

/// <summary>
/// 界面 3：卡组构筑工作台。
///
/// 顶部仪表盘（费用曲线 + 动态规则状态栏）/ 左侧 68% 卡池 / 右侧 32% 牌组托盘。
/// 所有规则数字来自 DeckEditorViewModel.CurrentProfile —— UI 只负责渲染。
/// </summary>
public partial class DeckBuilderView : Control
{
    public event Action<ConstructedDeck> OnSaveAndBack;

    private readonly GameSessionStore _store;
    private readonly DeckEditorViewModel _vm;

    private Label _capacityLabel;
    private Label _startupLabel;
    private Label _statusLabel;
    private Button _extraClanBtn;
    private LineEdit _nameEdit;
    private CostCurveWidget _curve;
    private GridContainer _cardGrid;

    // 分页：一次只造当页的卡，避免上千张卡实例化卡死 UI。
    // 每页 2 行。4 行时最后一排会被窗口裁掉，玩家必须滚动才看得到，
    // 而分页存在的意义就是「一屏看全」，要滚动就失去意义了。
    private const int PageSize = 8;    // 4 列 × 2 行
    private int _currentPage;
    private int _totalPages = 1;
    private Label _pageLabel;
    private Button _prevPageBtn;
    private Button _nextPageBtn;

    /// <summary>左侧卡池网格的控件缓存（按卡号）。只在筛选条件变化时重建。</summary>
    private readonly Dictionary<int, CardVisualWidget> _gridWidgets = new();
    private VBoxContainer _deckList;
    private Label _statsLabel;
    private Label _errorLabel;

    private bool _bigView;
    private HBoxContainer _extraClanTabs;
    private CardHoverTooltip _tooltip;

    private static readonly string[] AllClans =
    {
        "精灵", "皇家护卫", "巫师", "龙族",
        "梦魇", "主教", "超越者", "中立",
    };

    public DeckBuilderView(GameSessionStore store, ConstructedDeck deck)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _vm = new DeckEditorViewModel(store, deck);
        _vm.OnStateChanged += RefreshAll;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);

        // 背景由 DeckManagementRoot 的 AstralBackgroundCanvas 提供，这里不铺纯色。

        var root = new VBoxContainer();
        root.SetAnchorsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        root.AddChild(body);

        body.AddChild(BuildLeftPane());
        body.AddChild(BuildRightPane());

        // 悬停浮窗：卡面本体现在不印效果文字，效果统一由它承载。
        _tooltip = new CardHoverTooltip { ZIndex = 90 };
        AddChild(_tooltip);

        RefreshAll();
    }

    // ---------------------------------------------------------------- 顶部
    private Control BuildTopBar()
    {
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 64) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        var back = new Button { Text = "← 保存并返回" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () =>
        {
            // Name 是 init-only：RenameDeck 会**返回一个新实例**。
            // 必须把返回值一路传下去 —— 拿着旧的 WorkingDeck 去 SaveDeck，
            // 写回磁盘的就是改名前的名字，UI 上看着像"改名没生效"。
            var toSave = _nameEdit != null
                ? _store.RenameDeck(_vm.WorkingDeck, _nameEdit.Text)
                : _vm.WorkingDeck;
            _store.SaveDeck(toSave);
            OnSaveAndBack?.Invoke(toSave);
        };
        row.AddChild(back);

        Color clanCol = SvWbTheme.ClanColor(_vm.WorkingDeck.LeaderClan);
        var badge = new PanelContainer { CustomMinimumSize = new Vector2(32, 32) };
        badge.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            clanCol.Darkened(0.55f), clanCol, 16, 2, 3));
        var bt = new Label
        {
            Text = SvWbTheme.ClanDisplayZh(_vm.WorkingDeck.LeaderClan).Substring(0, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bt.AddThemeColorOverride("font_color", Colors.White);
        badge.AddChild(bt);
        row.AddChild(badge);

        _nameEdit = new LineEdit
        {
            Text = _vm.WorkingDeck.Name,
            CustomMinimumSize = new Vector2(160, 32),
        };
        row.AddChild(_nameEdit);

        // 中区：费用曲线
        _curve = new CostCurveWidget { CustomMinimumSize = new Vector2(180, 56) };
        row.AddChild(_curve);

        _statsLabel = new Label();
        _statsLabel.AddThemeFontSizeOverride("font_size", 11);
        _statsLabel.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        row.AddChild(_statsLabel);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 右区：动态规则状态
        _capacityLabel = new Label();
        _capacityLabel.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(_capacityLabel);

        _startupLabel = new Label();
        // 多条构筑规则会把右侧的「构筑合法」顶出屏幕：限宽 + 省略号。
        _startupLabel.ClipText = true;
        _startupLabel.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _startupLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _startupLabel.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_startupLabel);

        _extraClanBtn = new Button { Text = "+ 选择副职业", Visible = false };
        SvWbTheme.StyleButton(_extraClanBtn, customAccent: SvWbTheme.AccentPurple);
        _extraClanBtn.Pressed += ShowExtraClanPicker;
        row.AddChild(_extraClanBtn);

        _statusLabel = new Label();
        _statusLabel.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(_statusLabel);

        return bar;
    }

    // ---------------------------------------------------------------- 左侧
    private Control BuildLeftPane()
    {
        var pane = new VBoxContainer();
        // 去掉原来的硬编码 860：那个宽度在窄窗口下会直接把右栏挤出屏幕。
        // 改成弹性比例（左 2.3 份 / 右 1 份），随窗口自适应。
        pane.CustomMinimumSize = new Vector2(420, 0);
        pane.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pane.SizeFlagsStretchRatio = 2.3f;
        pane.AddThemeConstantOverride("separation", 6);

        // 第一行：职业页签 + 搜索
        var filterRow = new HBoxContainer();
        filterRow.AddThemeConstantOverride("separation", 6);
        pane.AddChild(filterRow);

        AddClanTab(filterRow, "本职业+中立", "SELF_NEUTRAL");
        AddClanTab(filterRow, "本职业", "SELF");
        AddClanTab(filterRow, "中立", "中立");
        AddClanTab(filterRow, "全部", "ALL");

        // 副职业页签：由 profile.AllowedClans 动态追加。
        // 只有 AllowSpecificClan / AllowChosenClans 解锁后才会出现。
        _extraClanTabs = new HBoxContainer();
        _extraClanTabs.AddThemeConstantOverride("separation", 4);
        filterRow.AddChild(_extraClanTabs);

        filterRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 卡种下拉
        var kindOpt = new OptionButton { CustomMinimumSize = new Vector2(140, 32) };
        kindOpt.AddItem("卡种: 全部", 0);
        kindOpt.AddItem("随从", 1);
        kindOpt.AddItem("法术", 2);
        kindOpt.AddItem("护符", 3);
        kindOpt.AddItem("★含构筑规则", 4);
        SvWbTheme.StyleButton(kindOpt);
        SvWbTheme.StyleOptionPopup(kindOpt);
        kindOpt.ItemSelected += idx =>
        {
            switch (idx)
            {
                case 1: _vm.ActiveKindFilter = CardKind.Follower; _vm.OnlyDeckModCards = false; break;
                case 2: _vm.ActiveKindFilter = CardKind.Spell;    _vm.OnlyDeckModCards = false; break;
                case 3: _vm.ActiveKindFilter = CardKind.Amulet;   _vm.OnlyDeckModCards = false; break;
                case 4: _vm.ActiveKindFilter = null; _vm.OnlyDeckModCards = true; break;
                default: _vm.ActiveKindFilter = null; _vm.OnlyDeckModCards = false; break;
            }
            _currentPage = 0;
            RefreshCardGrid();
        };
        filterRow.AddChild(kindOpt);

        var search = new LineEdit
        {
            PlaceholderText = "搜索卡名 / 兵种 / 效果文本",
            CustomMinimumSize = new Vector2(240, 32),
        };
        search.TextChanged += t => { _vm.SearchQuery = t; _currentPage = 0; RefreshCardGrid(); };
        filterRow.AddChild(search);

        // ---- 牌组编辑器完整筛选栏 ----
        // 一行放不下 12 个筛选项，用 HFlowContainer 自动折行。
        var advRow = new HFlowContainer();
        advRow.AddThemeConstantOverride("h_separation", 6);
        advRow.AddThemeConstantOverride("v_separation", 4);
        pane.AddChild(advRow);

        // 兵种
        var traitOpt = new OptionButton { CustomMinimumSize = new Vector2(120, 30) };
        traitOpt.AddItem("兵种: 全部");
        var traits = new List<string>();
        try
        {
            foreach (var t in _store.Catalog.Schema.Traits.All.OrderBy(x => x.Name, StringComparer.Ordinal))
                if (!string.IsNullOrWhiteSpace(t.Name)) { traits.Add(t.Name); traitOpt.AddItem(t.Name); }
        }
        catch (Exception) { }
        SvWbTheme.StyleButton(traitOpt);
        SvWbTheme.StyleOptionPopup(traitOpt);
        traitOpt.ItemSelected += i =>
        {
            int k = (int)i;
            _vm.ActiveTraitFilter = k <= 0 || k - 1 >= traits.Count ? null : traits[k - 1];
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(traitOpt);

        // 关键词
        var kwOpt = new OptionButton { CustomMinimumSize = new Vector2(130, 30) };
        kwOpt.AddItem("关键词: 全部");
        var kws = new List<string>();
        try
        {
            foreach (var kv in DiyZh.Keywords) { kws.Add(kv.Key); kwOpt.AddItem(kv.Zh); }
        }
        catch (Exception) { }
        SvWbTheme.StyleButton(kwOpt);
        SvWbTheme.StyleOptionPopup(kwOpt);
        kwOpt.ItemSelected += i =>
        {
            int k = (int)i;
            _vm.ActiveKeywordFilter = k <= 0 || k - 1 >= kws.Count ? null : kws[k - 1];
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(kwOpt);

        // 稀有度
        var rarOpt = new OptionButton { CustomMinimumSize = new Vector2(110, 30) };
        rarOpt.AddItem("稀有度: 全部");
        for (int r = 1; r <= 4; r++) rarOpt.AddItem("★" + r);
        SvWbTheme.StyleButton(rarOpt);
        SvWbTheme.StyleOptionPopup(rarOpt);
        rarOpt.ItemSelected += i =>
        {
            int k = (int)i;
            _vm.ActiveRarityFilter = k <= 0 ? (int?)null : k;
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(rarOpt);

        // 卡包
        var packOpt = new OptionButton { CustomMinimumSize = new Vector2(130, 30) };
        packOpt.AddItem("卡包: 全部");
        var packs = new List<string>();
        try
        {
            var poolIds = _vm.CurrentPool.EnabledPackIds != null
                ? new List<string>(_vm.CurrentPool.EnabledPackIds)
                : new List<string>();
            if (poolIds.Count == 0) poolIds.AddRange(_store.Catalog.Packs.Keys);
            foreach (var pid in poolIds.OrderBy(x => x, StringComparer.Ordinal))
                { packs.Add(pid); packOpt.AddItem(pid); }
        }
        catch (Exception) { }
        SvWbTheme.StyleButton(packOpt);
        SvWbTheme.StyleOptionPopup(packOpt);
        packOpt.ItemSelected += i =>
        {
            int k = (int)i;
            _vm.ActivePackFilter = k <= 0 || k - 1 >= packs.Count ? null : packs[k - 1];
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(packOpt);

        // 攻击力 / 生命值（8 代表 >=8）
        advRow.AddChild(MakeNumberFilter("攻击力", v => { _vm.ActiveAtkFilter = v; _currentPage = 0; RefreshCardGrid(); }));
        advRow.AddChild(MakeNumberFilter("生命值", v => { _vm.ActiveLifeFilter = v; _currentPage = 0; RefreshCardGrid(); }));

        // 卡牌 ID
        var idEdit = new LineEdit { PlaceholderText = "卡牌 ID", CustomMinimumSize = new Vector2(110, 30) };
        idEdit.TextChanged += t =>
        {
            _vm.ActiveCardIdFilter = long.TryParse(t?.Trim(), out long id) && id > 0 ? (int)id : (int?)null;
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(idEdit);

        // 重置
        var resetBtn = new Button { Text = "重置筛选", CustomMinimumSize = new Vector2(88, 30) };
        SvWbTheme.StyleButton(resetBtn);
        resetBtn.Pressed += () =>
        {
            _vm.ActiveTraitFilter = null; _vm.ActiveKeywordFilter = null;
            _vm.ActiveRarityFilter = null; _vm.ActivePackFilter = null;
            _vm.ActiveAtkFilter = null; _vm.ActiveLifeFilter = null;
            _vm.ActiveCardIdFilter = null; _vm.ActiveCostFilter = null;
            _vm.ActiveKindFilter = null; _vm.SearchQuery = "";
            idEdit.Text = ""; traitOpt.Selected = 0; kwOpt.Selected = 0;
            rarOpt.Selected = 0; packOpt.Selected = 0;
            _currentPage = 0; RefreshCardGrid();
        };
        advRow.AddChild(resetBtn);

        // 第二行：费用圆钮
        var costRow = new HBoxContainer();
        costRow.AddThemeConstantOverride("separation", 4);
        pane.AddChild(costRow);

        var allBtn = new Button { Text = "全部" };
        SvWbTheme.StyleButton(allBtn);
        allBtn.Pressed += () => { _vm.ActiveCostFilter = null; _currentPage = 0; RefreshCardGrid(); };
        costRow.AddChild(allBtn);

        for (int i = 0; i <= 8; i++)
        {
            int cost = i;
            var b = new Button
            {
                Text = i < 8 ? i.ToString() : "8+",
                CustomMinimumSize = new Vector2(34, 30),
            };
            SvWbTheme.StyleButton(b);
            b.Pressed += () =>
            {
                _vm.ActiveCostFilter = _vm.ActiveCostFilter == cost ? (int?)null : cost;
                _currentPage = 0;
                RefreshCardGrid();
            };
            costRow.AddChild(b);
        }

        // 卡牌网格 + 底部分页栏
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddChild(scroll);
        SvWbTheme.StyleScroll(scroll);

        var pageBar = new HBoxContainer { CustomMinimumSize = new Vector2(0, 32) };
        pageBar.AddThemeConstantOverride("separation", 6);
        _prevPageBtn = new Button { Text = "◀ 上一页", CustomMinimumSize = new Vector2(88, 28) };
        SvWbTheme.StyleButton(_prevPageBtn);
        _prevPageBtn.Pressed += () => { if (_currentPage > 0) { _currentPage--; RefreshCardGrid(); } };
        pageBar.AddChild(_prevPageBtn);

        _pageLabel = new Label
        {
            Text = "第 1 / 1 页",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _pageLabel.AddThemeFontSizeOverride("font_size", 11);
        pageBar.AddChild(_pageLabel);

        _nextPageBtn = new Button { Text = "下一页 ▶", CustomMinimumSize = new Vector2(88, 28) };
        SvWbTheme.StyleButton(_nextPageBtn);
        _nextPageBtn.Pressed += () => { if (_currentPage < _totalPages - 1) { _currentPage++; RefreshCardGrid(); } };
        pageBar.AddChild(_nextPageBtn);

        pane.AddChild(pageBar);

        _cardGrid = new GridContainer { Columns = 4 };
        // 列数跟随可用宽度：标准窗口 4 列，宽屏自动扩到 5~6 列。
        scroll.Resized += () =>
        {
            if (_cardGrid == null || !IsInstanceValid(_cardGrid)) return;
            float w = scroll.Size.X;
            if (w <= 0f) return;
            int cols = Mathf.Max(2, (int)((w - 24f) / 196f));
            if (_cardGrid.Columns != cols) _cardGrid.Columns = cols;
        };
        _cardGrid.AddThemeConstantOverride("h_separation", 8);
        _cardGrid.AddThemeConstantOverride("v_separation", 8);
        scroll.AddChild(_cardGrid);

        return pane;
    }

    private void AddClanTab(HBoxContainer row, string caption, string filter)
    {
        var b = new Button { Text = caption };
        SvWbTheme.StyleButton(b);
        b.Pressed += () => { _vm.ActiveClanFilter = filter; _currentPage = 0; RefreshCardGrid(); };
        row.AddChild(b);
    }

    // ---------------------------------------------------------------- 右侧
    private Control BuildRightPane()
    {
        var pane = new VBoxContainer { CustomMinimumSize = new Vector2(390, 0) };
        pane.AddThemeConstantOverride("separation", 6);

        var head = new HBoxContainer();
        pane.AddChild(head);

        var t = new Label { Text = "当前牌组清单" };
        t.AddThemeFontSizeOverride("font_size", 15);
        t.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        head.AddChild(t);
        head.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var toggle = new Button { Text = "切换视图" };
        SvWbTheme.StyleButton(toggle);
        toggle.Pressed += () => { _bigView = !_bigView; RefreshDeckList(); };
        head.AddChild(toggle);

        var clear = new Button { Text = "清空" };
        SvWbTheme.StyleButton(clear, customAccent: SvWbTheme.StatusDanger);
        // 走 ClearAll：内部抑制逐张通知，只在最后刷新一次。
        // 旧写法是「每张卡循环调用 60 次 RemoveCard」，每次都触发
        // RefreshAll 全量重建控件，30 张卡就是 1800 次重建 —— 界面卡死。
        clear.Pressed += () => _vm.ClearAll();
        head.AddChild(clear);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddChild(scroll);

        _deckList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _deckList.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(_deckList);

        _errorLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _errorLabel.AddThemeFontSizeOverride("font_size", 11);
        _errorLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusDanger);
        pane.AddChild(_errorLabel);

        return pane;
    }

    // ---------------------------------------------------------------- 刷新
    private void RefreshAll()
    {
        RefreshExtraClanTabs();
        RefreshTopBar();
        // 投入 / 移除一张卡不该重建整个卡池网格 ——
        // 那是上百个 CardVisualWidget 的销毁重建，连续点选时非常卡。
        // 只有第一次（缓存为空）才真建，其余走原位更新。
        // ⚠ 这里**绝不能**重置 _currentPage：
        // RefreshAll 每次加减投入张数都会跑，重置会让玩家翻到第 2 页点一张卡
        // 就被弹回第 1 页。页码只在筛选条件变更时重置。
        if (_gridWidgets.Count == 0) RefreshCardGrid();
        else UpdateCardGridInPlace();
        RefreshDeckList();
    }

    /// <summary>原作「投入 / 移除」只改的豆子数与锁定态，原位刷新即可。</summary>
    private void UpdateCardGridInPlace()
    {
        var allowed = _vm.CurrentProfile.AllowedClans;
        bool cross = _vm.CurrentProfile.AllowCrossCraft;
        foreach (var (cid, w) in _gridWidgets)
        {
            if (!IsInstanceValid(w) || w.Card == null) continue;
            bool locked = !cross && w.Card.Clan.IsValid && !allowed.Contains(w.Card.Clan.Name);
            w.SetLocked(locked);
            w.UpdateDeckCopyState(_vm.GetCopyCount(cid), _vm.GetMaxLimitForCard(cid));
        }
    }

    /// <summary>
    /// 重建副职业页签。
    ///
    /// profile.AllowedClans 里除「本职业 + 中立」之外的，都是被构筑卡解锁的副职业。
    /// 每次规则变化都要重建 —— 玩家刚投进一张 allow_clan 卡，页签应当立刻出现。
    /// </summary>
    private void RefreshExtraClanTabs()
    {
        if (_extraClanTabs == null) return;
        foreach (var c in _extraClanTabs.GetChildren()) c.QueueFree();

        var allowed = _vm.CurrentProfile.AllowedClans;
        string self = _vm.WorkingDeck.LeaderClan ?? "中立";

        foreach (var clan in allowed)
        {
            if (string.Equals(clan, self, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(clan, "中立", StringComparison.OrdinalIgnoreCase)) continue;

            string c = clan;
            var b = new Button { Text = "★ 副职业: " + SvWbTheme.ClanDisplayZh(clan) };
            SvWbTheme.StyleButton(b, customAccent: SvWbTheme.AccentPurple);
            _currentPage = 0;
            b.Pressed += () => { _vm.ActiveClanFilter = c; RefreshCardGrid(); };
            _extraClanTabs.AddChild(b);
        }
    }

    private void RefreshTopBar()
    {
        var p = _vm.CurrentProfile;
        var baseRules = _vm.CurrentPool.BaseRules;
        int total = _vm.TotalCardCount;

        bool expanded = p.MaxDeckSize != baseRules.MaxDeckSize;
        _capacityLabel.Text = $"卡牌: {total} / {p.MaxDeckSize}" + (expanded ? " ★" : "");
        _capacityLabel.AddThemeColorOverride("font_color",
            expanded ? SvWbTheme.AccentGold
          : total == p.MaxDeckSize ? SvWbTheme.StatusValid
          : SvWbTheme.StatusWarn);

        var extra = new List<string>();
        if (p.LeaderInitialLife != baseRules.LeaderInitialLife)
            extra.Add($"{p.LeaderInitialLife}血");
        if (p.StartingHandSize != baseRules.StartingHandSize)
            extra.Add($"起手{p.StartingHandSize}张");
        if (p.InitialEpBonus != 0) extra.Add($"EP+{p.InitialEpBonus}");
        _startupLabel.Text = extra.Count > 0 ? $"★ 开局: {string.Join(" | ", extra)}" : "";
        _startupLabel.AddThemeColorOverride("font_color", SvWbTheme.AccentGold);

        _extraClanBtn.Visible = p.AllowedChosenClanSlots > 0;
        if (_extraClanBtn.Visible)
        {
            int chosen = _vm.WorkingDeck.ChosenExtraClans?.Count ?? 0;
            _extraClanBtn.Text = $"+ 副职业 ({chosen}/{p.AllowedChosenClanSlots})";
        }

        _statusLabel.Text = p.IsValid ? "✔ 构筑合法" : $"⚠ {p.Errors.Count} 项规则未满足";
        _statusLabel.AddThemeColorOverride("font_color",
            p.IsValid ? SvWbTheme.StatusValid : SvWbTheme.StatusDanger);

        _errorLabel.Text = p.IsValid ? "" : string.Join("\n", p.Errors.Take(4));

        _curve.SetData(_vm.GetCostCurve());
        int followers = 0, spells = 0, amulets = 0;
        foreach (var (def, count, _) in _vm.GetDeckEntries())
        {
            if (def.Kind == CardKind.Follower) followers += count;
            else if (def.Kind == CardKind.Spell) spells += count;
            else amulets += count;
        }
        _statsLabel.Text = $"随从: {followers}  法术: {spells}  护符: {amulets}";
    }


    /// <summary>攻击力 / 生命值筛选下拉：0..8，8 表示 8 以上。</summary>
    private static OptionButton MakeNumberFilter(string label, Action<int?> onChange)
    {
        var opt = new OptionButton { CustomMinimumSize = new Vector2(112, 30) };
        opt.AddItem(label + ": 全部");
        for (int i = 0; i <= 8; i++) opt.AddItem(i < 8 ? (label + " " + i) : (label + " 8+"));
        SvWbTheme.StyleButton(opt);
        SvWbTheme.StyleOptionPopup(opt);
        opt.ItemSelected += i =>
        {
            int k = (int)i;
            onChange(k <= 0 ? (int?)null : k - 1);
        };
        return opt;
    }

    private void RefreshCardGrid()
    {
        _tooltip?.HideTooltip();
        foreach (var c in _cardGrid.GetChildren()) c.QueueFree();
        _gridWidgets.Clear();

        var pool = _vm.CurrentPool;
        var allowed = _vm.CurrentProfile.AllowedClans;
        bool cross = _vm.CurrentProfile.AllowCrossCraft;

        var all = _vm.GetFilteredPoolCards();
        _totalPages = Math.Max(1, (all.Count + PageSize - 1) / PageSize);
        _currentPage = Math.Clamp(_currentPage, 0, _totalPages - 1);
        var page = all.Skip(_currentPage * PageSize).Take(PageSize).ToList();

        if (_pageLabel != null)
            _pageLabel.Text = $"第 {_currentPage + 1} / {_totalPages} 页　（共 {all.Count} 张）";
        if (_prevPageBtn != null) _prevPageBtn.Disabled = _currentPage <= 0;
        if (_nextPageBtn != null) _nextPageBtn.Disabled = _currentPage >= _totalPages - 1;

        foreach (var def in page)
        {
            var w = new CardVisualWidget();
            bool locked = !cross && def.Clan.IsValid && !allowed.Contains(def.Clan.Name);
            w.Setup(def, _store.CardPrinter,
                    _vm.GetCopyCount(def.CardId), _vm.GetMaxLimitForCard(def.CardId));
            w.SetLocked(locked);
            _gridWidgets[def.CardId] = w;

            var d = def;
            w.OnLeftClick += _ =>
            {
                if (!_vm.TryAddCard(d.CardId, out string _why))
                {
                    // 加不进去时轻微抖动，给出可感知的反馈
                    var tw = CreateTween();
                    tw.TweenProperty(w, "position:x", w.Position.X + 4, 0.04f);
                    tw.TweenProperty(w, "position:x", w.Position.X - 4, 0.04f);
                    tw.TweenProperty(w, "position:x", w.Position.X, 0.04f);
                }
            };
            w.OnRightClick += cd => ShowInspector(cd);
            w.OnHoverEntered += (cd, ctrl) => _tooltip?.ShowForCard(cd, _store.CardPrinter, ctrl);
            w.OnHoverExited += _ => _tooltip?.HideTooltip();
            _cardGrid.AddChild(w);
        }
    }

    private void RefreshDeckList()
    {
        _tooltip?.HideTooltip();
        foreach (var c in _deckList.GetChildren()) c.QueueFree();

        foreach (var (def, count, limit) in _vm.GetDeckEntries())
        {
            if (_bigView)
            {
                var w = new CardVisualWidget();
                w.Setup(def, _store.CardPrinter, count, limit);
                var d0 = def;
                w.OnLeftClick += _ => _vm.RemoveCard(d0.CardId);
                w.OnRightClick += cd => ShowInspector(cd);
                w.OnHoverEntered += (cd, ctrl) => _tooltip?.ShowForCard(cd, _store.CardPrinter, ctrl);
                w.OnHoverExited += _ => _tooltip?.HideTooltip();
                _deckList.AddChild(w);
            }
            else
            {
                var strip = new DeckStripWidget();
                strip.Setup(def, count, limit);
                var d = def;
                strip.OnLeftClick += _ => _vm.RemoveCard(d.CardId);
                strip.OnRightClick += cd => ShowInspector(cd);
                strip.OnHoverEntered += (cd, ctrl) => _tooltip?.ShowForCard(cd, _store.CardPrinter, ctrl);
                strip.OnHoverExited += _ => _tooltip?.HideTooltip();
                _deckList.AddChild(strip);
            }
        }
    }

    /// <summary>开发期入口：强制让浮窗显示卡池里第一张卡的效果（截图用）。</summary>
    public void ShowFirstTooltipForShot()
    {
        var first = _vm.GetFilteredPoolCards().FirstOrDefault();
        if (first == null || _tooltip == null) return;

        // 造一个锚点控件包住第一格，让浮窗有位置可依。
        var anchorCtrl = _cardGrid.GetChildren().OfType<CardVisualWidget>().FirstOrDefault();
        if (anchorCtrl != null)
            _tooltip.ShowForCard(first, _store.CardPrinter, anchorCtrl);
    }

    private void ShowInspector(CardDef def)
    {
        var modal = CardInspectorModal.Create(def, _store.CardPrinter);
        AddChild(modal);
        modal.OnClosed += () => { };
    }

    private void ShowExtraClanPicker()
    {
        var dlg = new AcceptDialog { Title = "选择副职业" };
        var list = new VBoxContainer();
        dlg.AddChild(list);

        foreach (var clan in AllClans)
        {
            if (string.Equals(clan, _vm.WorkingDeck.LeaderClan, StringComparison.OrdinalIgnoreCase))
                continue;
            string c = clan;
            var b = new Button { Text = SvWbTheme.ClanDisplayZh(clan) };
            SvWbTheme.StyleButton(b);
            b.Pressed += () => { _vm.ToggleChosenExtraClan(c); dlg.QueueFree(); };
            list.AddChild(b);
        }

        AddChild(dlg);
        dlg.PopupCentered();
    }
}

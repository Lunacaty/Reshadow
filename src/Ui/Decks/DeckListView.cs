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
/// 界面 1：卡组一览（卡组盒陈列架）。
///
/// 第一格固定是「新建牌组」，后续是已有卡组的盒子。
/// 状态徽章的数字全部来自 DeckRuleResolver —— 这里不算任何规则。
/// </summary>
public partial class DeckListView : Control
{
    public event Action<ConstructedDeck> OnEditDeck;
    public event Action OnCreateDeck;
    public event Action<ConstructedDeck> OnBattleDeck;
    public event Action OnOpenDiyWorkshop;
    public event Action OnOpenPackManager;
    /// <summary>顶部「← 返回」：回到主界面，而不是什么都不做。</summary>
    public event Action OnExitToMainMenu;

    private readonly GameSessionStore _store;
    private string _poolFilter;

    private GridContainer _grid;
    private ScrollContainer _scrollHost;
    private HBoxContainer _poolTabs;
    private Label _countLabel;

    public DeckListView(GameSessionStore store, string poolFilter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _poolFilter = poolFilter;
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 背景由 DeckManagementRoot 的 AstralBackgroundCanvas 提供，这里不铺纯色。

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        // 主陈列区
        // MarginContainer / ScrollContainer 都必须**双向** ExpandFill。
        // 只设 Vertical 时它们的宽度会坍缩到内容最小宽，
        // 结果是「顶部栏只占左上角一小块、网格整个看不见」——
        // 编译期完全正常，只有真正跑起来才暴露。
        var margin = new MarginContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        margin.AddThemeConstantOverride("margin_left", 24);
        margin.AddThemeConstantOverride("margin_right", 24);
        margin.AddThemeConstantOverride("margin_top", 20);
        margin.AddThemeConstantOverride("margin_bottom", 20);
        root.AddChild(margin);

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        margin.AddChild(scroll);
        _scrollHost = scroll;
        SvWbTheme.StyleScroll(scroll);

        // 网格宽度跟着滚动容器走。
        // 之前设 ExpandFill 又放在 ScrollContainer 里：ScrollContainer 不约束子项宽度，
        // 网格按内容撑开，最后一列直接顶到窗口外 —— 三列四列都会裁掉一张。
        _grid = new GridContainer
        {
            Columns = 4,
            SizeFlagsHorizontal = SizeFlags.Fill,
        };
        _grid.AddThemeConstantOverride("h_separation", 20);
        _grid.AddThemeConstantOverride("v_separation", 20);
        scroll.AddChild(_grid);

        // 列数按实际可用宽度算。
        // 写死 4 列时：网格被 ScrollContainer 包着、又设了 ExpandFill，
        // 宽度不受窗口约束，1280 宽下第 4 张牌组会被窗口右缘裁掉。
        scroll.Resized += OnViewResized;
        CallDeferred(nameof(OnViewResized));

        BuildPoolTabs();
        Refresh();
    }

    /// <summary>
    /// 顶栏分两行。
    ///
    /// 早先全塞在一行 HBoxContainer 里：返回 + 标题 + 5 个卡池页签 +
    /// 3 个操作按钮 + 计数，总宽远超 1280。HBoxContainer 既不换行也不压缩，
    /// 结果右侧的「卡包/卡池管理」「导入牌组代码」直接被窗口裁掉。
    ///
    /// 行一：返回 / 标题 / ←弹性→ / 操作按钮 / 计数
    /// 行二：赛制卡池页签（数量随 data/pools/ 增长，必须独占一行）
    /// </summary>
    private void OnViewResized()
    {
        RecomputeColumns();
        // 列数变了必须重新排一次，否则 GridContainer 仍按旧列数摆位。
        if (_grid != null) _grid.QueueSort();
    }

    /// <summary>
    /// 按当前可用宽度重算网格列数。
    ///
    /// 每张牌组卡最小 280 宽，列间距 20，左右外边距各 24。
    /// 1280 窗口下只能放 4 列（1920 下 6 列），写死列数必然在不同窗口宽度下出错。
    /// </summary>
    private void RecomputeColumns()
    {
        if (_grid == null) return;

        const float CARD_W = 300f, H_GAP = 20f, MARGIN = 56f;

        // 用滚动容器的实际宽度，不用本视图的 Size ——
        // 视图在 _Ready 阶段 Size 仍是 0，按 0 算会永远得到默认列数。
        float avail = _scrollHost != null && _scrollHost.Size.X > 0
            ? _scrollHost.Size.X - MARGIN
            : 1280f - MARGIN;
        int cols = Math.Max(1, (int)((avail + H_GAP) / (CARD_W + H_GAP)));

        // 实际卡宽 280 + 条纹/描边会略微撑开，按 300 估一个安全档，
        // 宁可少一列也不让最右侧那张被窗口裁掉一半。

        if (_grid.Columns != cols) _grid.Columns = cols;
    }

    private Control BuildTopBar()
    {
        var bar = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 8);
        bar.AddChild(col);

        // ---------------- 行一 ----------------
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        col.AddChild(row);

        var back = new Button { Text = "← 返回" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnExitToMainMenu?.Invoke();
        row.AddChild(back);

        var title = new Label { Text = "牌组一览 / DECKS" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        row.AddChild(title);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var diyBtn = new Button { Text = "🛠 DIY 造卡工坊" };
        SvWbTheme.StyleButton(diyBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        diyBtn.Pressed += () => OnOpenDiyWorkshop?.Invoke();
        row.AddChild(diyBtn);

        var packBtn = new Button { Text = "🌐 卡包/卡池管理" };
        SvWbTheme.StyleButton(packBtn);
        packBtn.Pressed += () => OnOpenPackManager?.Invoke();
        row.AddChild(packBtn);

        var importBtn = new Button { Text = "📥 导入牌组代码" };
        SvWbTheme.StyleButton(importBtn);
        importBtn.Pressed += ShowImportDialog;
        row.AddChild(importBtn);

        _countLabel = new Label();
        _countLabel.AddThemeFontSizeOverride("font_size", 13);
        _countLabel.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        row.AddChild(_countLabel);

        // ---------------- 行二 ----------------
        // 赛制卡池页签：从 data/pools/*.pool.json 动态生成。
        // 写死「全部/标准/高费」会让新增卡池在 UI 上永远看不见；
        // 而数量会随卡池增加而增长，所以必须独占一行并允许横向滚动。
        var tabScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 44),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Auto,
            VerticalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(tabScroll);
        col.AddChild(tabScroll);

        _poolTabs = new HBoxContainer();
        _poolTabs.AddThemeConstantOverride("separation", 6);
        tabScroll.AddChild(_poolTabs);

        return bar;
    }

    /// <summary>重建卡池过滤页签。选中项由 _poolFilter 决定，空串代表「全部卡池」。</summary>
    private void BuildPoolTabs()
    {
        if (_poolTabs == null) return;
        foreach (var c in _poolTabs.GetChildren()) c.QueueFree();

        AddPoolTab("全部卡池", null);
        // Dictionary 遍历顺序不稳定，页签位置每次启动都可能变。
        // 按 PoolId 排序保证顺序固定（standard 会在最后，符合「官方标准」的定位）。
        foreach (var kv in _store.PoolConfigs.OrderBy(k => k.Key, StringComparer.Ordinal))
            AddPoolTab(kv.Value.Name ?? kv.Key, kv.Key);
    }

    private void AddPoolTab(string caption, string poolId)
    {
        bool selected = string.IsNullOrEmpty(poolId)
            ? string.IsNullOrEmpty(_poolFilter)
            : string.Equals(_poolFilter, poolId, StringComparison.OrdinalIgnoreCase);

        var b = new Button { Text = caption };
        SvWbTheme.StyleButton(b, isPrimary: selected);
        string pid = poolId;
        b.Pressed += () =>
        {
            _poolFilter = pid;
            BuildPoolTabs();
            Refresh();
        };
        _poolTabs.AddChild(b);
    }

    public void Refresh()
    {
        if (_grid == null) return;
        foreach (var c in _grid.GetChildren()) c.QueueFree();

        _grid.AddChild(BuildNewDeckTile());

        var decks = _store.SavedDecks
            .Where(d => string.IsNullOrEmpty(_poolFilter) || d.PoolId == _poolFilter)
            .ToList();

        foreach (var d in decks) _grid.AddChild(BuildDeckBox(d));

        if (_countLabel != null) _countLabel.Text = $"已创建: {decks.Count} 副";
    }

    /// <summary>第一格：新建牌组引导卡。</summary>
    private Control BuildNewDeckTile()
    {
        var box = new PanelContainer { CustomMinimumSize = new Vector2(280, 180) };
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.02f, 0.08f, 0.12f, 0.9f), SvWbTheme.AccentCyan, 12, 2, 8));
        box.MouseDefaultCursorShape = CursorShape.PointingHand;
        box.MouseFilter = MouseFilterEnum.Stop;

        var v = new VBoxContainer();
        v.Alignment = BoxContainer.AlignmentMode.Center;
        box.AddChild(v);

        var plus = new Label
        {
            Text = "＋",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        plus.AddThemeFontSizeOverride("font_size", 48);
        plus.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        v.AddChild(plus);

        var cap = new Label
        {
            Text = "新建牌组 / CREATE DECK",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        cap.AddThemeFontSizeOverride("font_size", 14);
        cap.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        v.AddChild(cap);

        box.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                OnCreateDeck?.Invoke();
        };
        return box;
    }

    /// <summary>已有卡组盒。</summary>
    private Control BuildDeckBox(ConstructedDeck deck)
    {
        var pool = _store.GetPoolView(deck.PoolId);
        // 状态数字 100% 来自后端：UI 不自己数牌。
        var profile = DeckRuleResolver.ResolveAndValidate(deck, pool);
        int total = deck.Cards.Sum(c => c.count);

        Color clanCol = SvWbTheme.ClanColor(deck.LeaderClan);
        var box = new PanelContainer { CustomMinimumSize = new Vector2(280, 180) };
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 12, 1, 6));

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 6);
        box.AddChild(outer);

        // 职业代表色发光横条
        var stripe = new ColorRect { Color = clanCol, CustomMinimumSize = new Vector2(0, 6) };
        outer.AddChild(stripe);

        // 上半：职业徽章 + 名称 + 卡池胶囊
        var top = new HBoxContainer();
        outer.AddChild(top);

        var badge = new PanelContainer { CustomMinimumSize = new Vector2(40, 40) };
        badge.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            clanCol.Darkened(0.55f), clanCol, 20, 2, 3));
        var badgeTxt = new Label
        {
            Text = SvWbTheme.ClanDisplayZh(deck.LeaderClan).Substring(0, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        badgeTxt.AddThemeFontSizeOverride("font_size", 18);
        badgeTxt.AddThemeColorOverride("font_color", Colors.White);
        badge.AddChild(badgeTxt);
        top.AddChild(badge);

        var nameCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        top.AddChild(nameCol);

        var nameLbl = new Label { Text = deck.Name, ClipText = true };
        nameLbl.AddThemeFontSizeOverride("font_size", 18);
        nameLbl.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        nameCol.AddChild(nameLbl);

        var poolTag = new Label { Text = $"[{deck.PoolId}]" };
        poolTag.AddThemeFontSizeOverride("font_size", 11);
        poolTag.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        nameCol.AddChild(poolTag);

        // 中部：费用最高的 3 张核心卡（原版卡组盒的招牌展示位）
        var core = deck.Cards
            .Select(c => (def: pool.Catalog.TryGetCard(c.cardId), count: c.count))
            .Where(x => x.def != null)
            .OrderByDescending(x => x.def.BaseCost)
            .ThenBy(x => x.def.CardId)
            .Take(3)
            .ToList();
        if (core.Count > 0)
        {
            var coreRow = new HBoxContainer();
            coreRow.AddThemeConstantOverride("separation", 6);
            outer.AddChild(coreRow);
            foreach (var (d, _) in core)
            {
                var chip = new Label
                {
                    Text = "◆ " + DeckEditorViewModel.DisplayName(d),
                    ClipText = true,
                    // 不能 ExpandFill：三张卡名各自撑一份最小宽，
                    // 会把整个网格单元顶到 430+，右边那张牌组卡被窗口裁掉。
                    SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
                    CustomMinimumSize = new Vector2(72, 0),
                };
                chip.AddThemeFontSizeOverride("font_size", 10);
                chip.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
                coreRow.AddChild(chip);
            }
        }

        // 中部：构筑修正徽章
        var mods = new HBoxContainer();
        mods.AddThemeConstantOverride("separation", 8);
        outer.AddChild(mods);
        if (profile.MaxDeckSize != 40)
            mods.AddChild(Tag($"★ {profile.MaxDeckSize}张构筑", SvWbTheme.AccentGold));
        if (profile.LeaderInitialLife != 20)
            mods.AddChild(Tag($"★ 主战者{profile.LeaderInitialLife}血", SvWbTheme.AccentGold));
        if (profile.AllowedChosenClanSlots > 0)
            mods.AddChild(Tag($"★ 副职业{profile.AllowedChosenClanSlots}", SvWbTheme.AccentPurple));

        var spacer = new Control { SizeFlagsVertical = SizeFlags.ExpandFill };
        outer.AddChild(spacer);

        // 下部：状态徽章 + 操作按钮。
        //
        // 这一行原本是 HBox + 弹性垫片，6 个元素（状态/垫片/出战/编辑/复制/删除）
        // 的最小宽度加起来把整张牌组卡撑到 433 宽 —— 远超设定的 280，
        // 三列就要 1339，右缘被窗口裁掉。
        //
        // 改成两行：状态单独一行，四个按钮一行且尺寸收紧。
        var statusRow = new HBoxContainer();
        outer.AddChild(statusRow);

        bool ok = profile.IsValid;
        var status = Tag(
            ok ? $"✔ {total} / {profile.MaxDeckSize} 可出战"
               : $"⚠ {total} / {profile.MaxDeckSize} 未完成",
            ok ? SvWbTheme.StatusValid : SvWbTheme.StatusWarn);
        statusRow.AddChild(status);
        statusRow.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 4);
        outer.AddChild(bottom);
        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        // 【⚔ 出战】：带这副牌组直接进战斗桌面。
        // 只有构筑合法时才点亮 —— 不合法时点进去只会被 MatchRunner 拒绝开局，
        // 与其弹个错误框，不如直接禁掉。
        var battleBtn = new Button
        {
            Text = "⚔ 出战",
            Disabled = !ok,
            CustomMinimumSize = new Vector2(72, 34),
        };
        battleBtn.AddThemeFontSizeOverride("font_size", 13);
        SvWbTheme.StyleButton(battleBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        battleBtn.Pressed += () => OnBattleDeck?.Invoke(deck);
        bottom.AddChild(battleBtn);

        var editBtn = new Button { Text = "编辑", CustomMinimumSize = new Vector2(52, 34) };
        editBtn.AddThemeFontSizeOverride("font_size", 13);
        SvWbTheme.StyleButton(editBtn, isPrimary: true);
        editBtn.Pressed += () => OnEditDeck?.Invoke(deck);
        bottom.AddChild(editBtn);

        var dupBtn = new Button { Text = "复制", CustomMinimumSize = new Vector2(52, 34) };
        dupBtn.AddThemeFontSizeOverride("font_size", 13);
        SvWbTheme.StyleButton(dupBtn);
        dupBtn.Pressed += () =>
        {
            var copy = _store.DuplicateDeck(deck);
            _store.SaveDeck(copy);
            Refresh();
        };
        bottom.AddChild(dupBtn);

        var delBtn = new Button { Text = "🗑" };
        SvWbTheme.StyleButton(delBtn, customAccent: SvWbTheme.StatusDanger);
        delBtn.Pressed += () => ConfirmDelete(deck);
        bottom.AddChild(delBtn);

        return box;
    }

    private static Label Tag(string text, Color color)
    {
        var l = new Label { Text = text };
        l.AddThemeFontSizeOverride("font_size", 11);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    /// <summary>导入牌组代码：接受一个 .deck.json 的裸 JSON 或完整路径。</summary>
    private void ShowImportDialog()
    {
        var dlg = new AcceptDialog { Title = "导入牌组代码" };
        var v = new VBoxContainer();
        dlg.AddChild(v);

        var input = new TextEdit
        {
            CustomMinimumSize = new Vector2(520, 180),
            PlaceholderText = "粘贴 .deck.json 内容，或填写 data/decks/ 下的文件名",
        };
        v.AddChild(input);

        dlg.Confirmed += () =>
        {
            string raw = (input.Text ?? "").Trim();
            if (raw.Length == 0) { dlg.QueueFree(); return; }
            try
            {
                var d = System.Text.Json.JsonDocument.Parse(raw);
                var r = d.RootElement;
                var deck = new ConstructedDeck
                {
                    DeckId = Guid.NewGuid().ToString("N").Substring(0, 8),
                    Name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "导入牌组") : "导入牌组",
                    PoolId = r.TryGetProperty("pool_id", out var pi) ? (pi.GetString() ?? "standard") : "standard",
                    LeaderClan = r.TryGetProperty("leader_clan", out var lc) ? (lc.GetString() ?? "Neutral") : "Neutral",
                };
                if (r.TryGetProperty("cards", out var cs) && cs.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var c in cs.EnumerateArray())
                    {
                        int id = c.TryGetProperty("card_id", out var idv) ? idv.GetInt32() : 0;
                        int cnt = c.TryGetProperty("count", out var cv) ? cv.GetInt32() : 1;
                        if (id > 0 && cnt > 0) deck.Cards.Add((id, cnt));
                    }
                }
                _store.SaveDeck(deck);
                Refresh();
            }
            catch
            {
                // 解析失败就什么都不做：静默失败比塞进一副坏牌组好
            }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();

        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void ConfirmDelete(ConstructedDeck deck)
    {
        var dlg = new ConfirmationDialog
        {
            Title = "删除牌组",
            DialogText = $"确定删除《{deck.Name}》？此操作不可撤销。",
        };
        AddChild(dlg);
        dlg.Confirmed += () =>
        {
            _store.DeleteDeck(deck.DeckId);
            Refresh();
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        dlg.PopupCentered();
    }
}

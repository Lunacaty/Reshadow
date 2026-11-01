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

    private readonly GameSessionStore _store;
    private string _poolFilter;

    private GridContainer _grid;
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

        _grid = new GridContainer
        {
            Columns = 4,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _grid.AddThemeConstantOverride("h_separation", 20);
        _grid.AddThemeConstantOverride("v_separation", 20);
        scroll.AddChild(_grid);

        BuildPoolTabs();
        Refresh();
    }

    private Control BuildTopBar()
    {
        var bar = new PanelContainer
        {
            CustomMinimumSize = new Vector2(0, 64),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);
        bar.AddChild(row);

        var back = new Button { Text = "← 返回" };
        SvWbTheme.StyleButton(back);
        row.AddChild(back);

        var title = new Label { Text = "牌组一览 / DECKS" };
        title.AddThemeFontSizeOverride("font_size", 24);
        title.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        row.AddChild(title);

        // 赛制卡池过滤页签：从 data/pools/*.pool.json 动态生成。
        // 写死「全部/标准/高费」会让新增卡池在 UI 上永远看不见。
        _poolTabs = new HBoxContainer();
        _poolTabs.AddThemeConstantOverride("separation", 6);
        row.AddChild(_poolTabs);

        var spacer = new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(spacer);

        var importBtn = new Button { Text = "📥 导入牌组代码" };
        SvWbTheme.StyleButton(importBtn);
        importBtn.Pressed += ShowImportDialog;
        row.AddChild(importBtn);

        _countLabel = new Label();
        _countLabel.AddThemeFontSizeOverride("font_size", 13);
        _countLabel.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        row.AddChild(_countLabel);

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
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
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

        // 下部：状态徽章 + 操作按钮
        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 6);
        outer.AddChild(bottom);

        bool ok = profile.IsValid;
        var status = Tag(
            ok ? $"✔ {total} / {profile.MaxDeckSize} 可出战"
               : $"⚠ {total} / {profile.MaxDeckSize} 未完成",
            ok ? SvWbTheme.StatusValid : SvWbTheme.StatusWarn);
        bottom.AddChild(status);

        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var editBtn = new Button { Text = "编辑" };
        SvWbTheme.StyleButton(editBtn, isPrimary: true);
        editBtn.Pressed += () => OnEditDeck?.Invoke(deck);
        bottom.AddChild(editBtn);

        var dupBtn = new Button { Text = "复制" };
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

using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// 卡包浏览器 & 赛制卡池规则配置器。
///
/// 左：卡包列表 + 该包的卡；右：卡池的 MatchRules 全字段可视化。
/// 规则字段是直接按类型反射列出来的 —— 36 项手写一遍必然漏，
/// 而且以后 MatchRules 加字段时这里会自动跟上。
/// </summary>
public partial class PackAndPoolManagerView : Control
{
    public event Action OnBackToDecks;

    /// <summary>请求在 DIY 工坊里编辑这张卡（形如「在本包中造新卡 / 编辑已有卡」）。</summary>
    public event Action<ShadowverseFan.KernelV3.Ast.CardDef, string> OnEditCardInWorkshop;

    private GameSessionStore _store;
    private VBoxContainer _packList;
    private string _selectedPackId;
    private readonly Dictionary<string, CheckBox> _enabledPackChecks = new();
    private VBoxContainer _cardList;
    private VBoxContainer _ruleList;
    private VBoxContainer _namedDeckList;
    private Label _header;
    private readonly Dictionary<string, Control> _ruleEdits = new();
    private Button _saveRulesBtn;
    private Label _ruleStatus;
    private string _selectedPoolId;
    private OptionButton _poolPicker;
    private OptionButton rowPools;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        if (!_store.PoolConfigs.Any()) _store.Initialize();
        BuildUi();
        RefreshPacks();
        RefreshRules();
        RefreshNamedDecks();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 58) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));
        root.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        var back = new Button { Text = "← 返回牌组一览" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBackToDecks?.Invoke();
        row.AddChild(back);

        _header = new Label { Text = "卡包 / 卡池管理" };
        _header.AddThemeFontSizeOverride("font_size", 22);
        _header.AddThemeColorOverride("font_color", new Color("FEF08A"));
        row.AddChild(_header);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        root.AddChild(body);

        // 左：卡包 + 卡
        // 左栏不能写死 560：加上右栏的固定宽度按钮，总最小宽会顶到 1360+，
        // 在 1280 窗口下右侧按钮与状态标签直接被挤出屏幕外。
        // 改为按比例伸缩，窗口窄时两栏一起收。
        var left = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(360, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.9f,
        };
        left.AddThemeConstantOverride("separation", 6);
        body.AddChild(left);

        var packBar = new HBoxContainer();
        packBar.AddThemeConstantOverride("separation", 6);
        var packTitle = new Label { Text = "物理卡包", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        packTitle.AddThemeFontSizeOverride("font_size", 14);
        packTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        packBar.AddChild(packTitle);

        var newPackBtn = new Button { Text = "➕ 新建", CustomMinimumSize = new Vector2(72, 26) };
        SvWbTheme.StyleButton(newPackBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        newPackBtn.Pressed += () => OpenPackMetaDialog(null);
        packBar.AddChild(newPackBtn);

        var editPackBtn = new Button { Text = "✏ 修改", CustomMinimumSize = new Vector2(72, 26) };
        SvWbTheme.StyleButton(editPackBtn);
        editPackBtn.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_selectedPackId)) { _header.Text = "请先选中一个卡包"; return; }
            OpenPackMetaDialog(_selectedPackId);
        };
        packBar.AddChild(editPackBtn);

        var delPackBtn = new Button { Text = "🗑 删除", CustomMinimumSize = new Vector2(72, 26) };
        SvWbTheme.StyleButton(delPackBtn, customAccent: SvWbTheme.StatusDanger);
        delPackBtn.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_selectedPackId)) { _header.Text = "请先选中一个卡包"; return; }
            _store.DeletePack(_selectedPackId, out string msg);
            _header.Text = "卡包 / 卡池管理 —— " + msg;
            _selectedPackId = null;
            RefreshPacks();
        };
        packBar.AddChild(delPackBtn);

        left.AddChild(packBar);

        var packScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 110) };
        left.AddChild(packScroll);
        SvWbTheme.StyleScroll(packScroll);
        _packList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        packScroll.AddChild(_packList);

        var cardTitle = new Label { Text = "包内卡牌" };
        cardTitle.AddThemeFontSizeOverride("font_size", 14);
        cardTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        left.AddChild(cardTitle);

        var cardScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        left.AddChild(cardScroll);
        SvWbTheme.StyleScroll(cardScroll);
        _cardList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cardScroll.AddChild(_cardList);

        // 右：卡池规则
        var right = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(420, 0),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.3f,
        };
        right.AddThemeConstantOverride("separation", 6);
        body.AddChild(right);

        // 用 HFlowContainer 而不是 HBoxContainer：标题 + 卡池下拉 + 三个按钮 +
        // 状态标签的总最小宽度会超过 1360px，HBox 不换行，窗口一窄就溢出。
        // HFlow 会自动折到第二行。
        var ruleBar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ruleBar.AddThemeConstantOverride("h_separation", 8);
        ruleBar.AddThemeConstantOverride("v_separation", 6);
        right.AddChild(ruleBar);

        var ruleTitle = new Label { Text = "赛制卡池规则（MatchRules 全字段）" };
        ruleTitle.AddThemeFontSizeOverride("font_size", 14);
        ruleTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        ruleBar.AddChild(ruleTitle);

        // 卡池切换：允许自由选择要查看/编辑哪个赛制卡池，
        // 而不是写死只让第一个可编辑、其余只读跳过。
        _poolPicker = new OptionButton { CustomMinimumSize = new Vector2(200, 32) };
        SvWbTheme.StyleButton(_poolPicker);
        SvWbTheme.StyleOptionPopup(_poolPicker);
        _poolPicker.ItemSelected += idx =>
        {
            var keys = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
            int k = (int)idx;
            if (k < 0 || k >= keys.Count) return;
            _selectedPoolId = keys[k];
            RefreshRules();
            RefreshNamedDecks();
        };
        ruleBar.AddChild(_poolPicker);

        var newPoolBtn = new Button { Text = "➕ 新建卡池", CustomMinimumSize = new Vector2(96, 32) };
        SvWbTheme.StyleButton(newPoolBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        newPoolBtn.AddThemeFontSizeOverride("font_size", 11);
        newPoolBtn.Pressed += OpenNewPoolDialog;
        ruleBar.AddChild(newPoolBtn);

        var delPoolBtn = new Button { Text = "🗑 删除卡池", CustomMinimumSize = new Vector2(96, 32) };
        SvWbTheme.StyleButton(delPoolBtn, customAccent: SvWbTheme.StatusDanger);
        delPoolBtn.AddThemeFontSizeOverride("font_size", 11);
        delPoolBtn.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_selectedPoolId)) { _ruleStatus.Text = "请先选择一个卡池"; return; }
            _store.DeletePool(_selectedPoolId, out string msg);
            _ruleStatus.Text = msg;
            _selectedPoolId = null;
            BuildPoolPicker();
            RefreshRules();
            RefreshNamedDecks();
        };
        ruleBar.AddChild(delPoolBtn);

        _saveRulesBtn = new Button { Text = "💾 保存卡池规则", CustomMinimumSize = new Vector2(150, 32) };
        SvWbTheme.StyleButton(_saveRulesBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        _saveRulesBtn.Pressed += OnSaveRules;
        ruleBar.AddChild(_saveRulesBtn);

        // 状态标签会写「✔ 已保存：xxx」这类长句，不裁剪就会把整行撑宽，
        // HFlowContainer 折行后仍然溢出。
        _ruleStatus = new Label
        {
            Text = "",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        _ruleStatus.AddThemeFontSizeOverride("font_size", 11);
        _ruleStatus.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
        ruleBar.AddChild(_ruleStatus);

        var ruleScroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        right.AddChild(ruleScroll);
        SvWbTheme.StyleScroll(ruleScroll);
        _ruleList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _ruleList.AddThemeConstantOverride("separation", 3);
        ruleScroll.AddChild(_ruleList);

        // ── 具名牌组（归属卡池）───────────────────────────────────────
        // 130 号文档把 NamedDecks 从卡包迁到了卡池，卡池配置器必须能看到它，
        // 否则一个卡池声明了哪些「整副替换牌库」无从查起。
        var ndBar = new HBoxContainer();
        ndBar.AddThemeConstantOverride("separation", 6);
        var ndTitle = new Label { Text = "具名牌组（本卡池）", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        ndTitle.AddThemeFontSizeOverride("font_size", 13);
        ndTitle.AddThemeColorOverride("font_color", new Color("7DD3FC"));
        ndBar.AddChild(ndTitle);

        var addNdBtn = new Button { Text = "➕ 新增", CustomMinimumSize = new Vector2(76, 26) };
        SvWbTheme.StyleButton(addNdBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        addNdBtn.AddThemeFontSizeOverride("font_size", 11);
        addNdBtn.Pressed += () =>
        {
            if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg0))
            { _ruleStatus.Text = "请先选择一个卡池"; return; }

            // NamedDecks 是 init-only：只能整表重建后写回，不能就地 Add。
            var list0 = new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>(
                cfg0.NamedDecks ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>());
            list0.Add(new ShadowverseFan.KernelV3.Packs.NamedDeckRawDef
            {
                DeckName = "New Deck",
                DeckNameChs = "新牌组",
                Entries = new List<ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw>(),
            });
            cfg0 = new ShadowverseFan.KernelV3.Packs.CardPoolConfig
            {
                PoolId = cfg0.PoolId, Name = cfg0.Name, Rules = cfg0.Rules,
                EnabledPackIds = cfg0.EnabledPackIds, NamedDecks = list0,
            };
            _store.PoolConfigs[cfg0.PoolId] = cfg0;
            _store.SavePoolConfigFull(cfg0.PoolId, cfg0.Name, cfg0.EnabledPackIds,
                                      cfg0.Rules, cfg0.NamedDecks, out string msg);
            _ruleStatus.Text = msg;
            RefreshNamedDecks();
        };
        ndBar.AddChild(addNdBtn);
        right.AddChild(ndBar);

        _namedDeckList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _namedDeckList.AddThemeConstantOverride("separation", 2);
        var ndScroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 110) };
        ndScroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;
        SvWbTheme.StyleScroll(ndScroll);
        ndScroll.AddChild(_namedDeckList);
        right.AddChild(ndScroll);
    }

    /// <summary>刷新具名牌组列表（随卡池下拉切换）。</summary>
    private void RefreshNamedDecks()
    {
        if (_namedDeckList == null) return;
        foreach (var c in _namedDeckList.GetChildren()) { _namedDeckList.RemoveChild(c); c.QueueFree(); }

        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg)
            || cfg.NamedDecks == null || cfg.NamedDecks.Count == 0)
        {
            var empty = new Label { Text = "（本卡池未定义具名牌组）" };
            empty.AddThemeFontSizeOverride("font_size", 11);
            empty.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _namedDeckList.AddChild(empty);
            return;
        }

        foreach (var nd in cfg.NamedDecks)
        {
            int total = 0;
            foreach (var e in nd.Entries ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw>()) total += e.Copies;

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var lbl = new Label
            {
                Text = "《" + (string.IsNullOrEmpty(nd.DeckNameChs) ? nd.DeckName : nd.DeckNameChs)
                       + "》 " + (nd.Entries?.Count ?? 0) + " 种 / " + total + " 张",
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            lbl.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(lbl);

            var editNdBtn = new Button { Text = "✏ 编辑成员", CustomMinimumSize = new Vector2(88, 24) };
            SvWbTheme.StyleButton(editNdBtn);
            editNdBtn.AddThemeFontSizeOverride("font_size", 11);
            var capturedForEdit = nd;
            editNdBtn.Pressed += () => OpenNamedDeckEditor(cfg, capturedForEdit);
            row.AddChild(editNdBtn);

            var delBtn = new Button { Text = "🗑", CustomMinimumSize = new Vector2(32, 24) };
            SvWbTheme.StyleButton(delBtn, customAccent: SvWbTheme.StatusDanger);
            delBtn.AddThemeFontSizeOverride("font_size", 11);
            var capturedNd = nd;
            delBtn.Pressed += () =>
            {
                var rest = new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>();
                foreach (var x in cfg.NamedDecks)
                    if (!ReferenceEquals(x, capturedNd)) rest.Add(x);

                _store.SavePoolConfigFull(cfg.PoolId, cfg.Name, cfg.EnabledPackIds,
                                          cfg.Rules, rest, out string msg);
                _ruleStatus.Text = msg;
                RefreshNamedDecks();
            };
            row.AddChild(delBtn);

            _namedDeckList.AddChild(row);
        }
    }



    /// <summary>重建卡池下拉框（新建/删除卡池后调用）。</summary>
    private void BuildPoolPicker()
    {
        if (_poolPicker == null) return;
        _poolPicker.Clear();
        var keys = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var cfg = _store.PoolConfigs[keys[i]];
            _poolPicker.AddItem((string.IsNullOrEmpty(cfg.Name) ? keys[i] : cfg.Name) + " (" + keys[i] + ")", i);
        }
        int sel = _selectedPoolId != null ? keys.IndexOf(_selectedPoolId) : -1;
        if (sel >= 0) _poolPicker.Select(sel);
        else if (keys.Count > 0 && _selectedPoolId == null) _selectedPoolId = keys[0];
    }



    /// <summary>具名牌组编辑弹窗：改名字 + 增删成员 + 调张数。</summary>
    private void OpenNamedDeckEditor(ShadowverseFan.KernelV3.Packs.CardPoolConfig cfg,
                                     ShadowverseFan.KernelV3.Packs.NamedDeckRawDef target)
    {
        if (cfg == null || target == null) return;

        // 工作副本：所有改动先落在这里，点保存才写回。
        var working = new List<ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw>();
        foreach (var e in target.Entries ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw>())
            working.Add(new ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw
                        { CardId = e.CardId, CardName = e.CardName, Copies = e.Copies });

        var dlg = new AcceptDialog { Title = "编辑具名牌组", Size = new Vector2I(620, 520) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        dlg.AddChild(box);

        var nameRow = new HBoxContainer();
        var nameLb = new Label { Text = "牌组名", CustomMinimumSize = new Vector2(70, 0) };
        nameLb.AddThemeFontSizeOverride("font_size", 12);
        nameRow.AddChild(nameLb);
        var nameEdit = new LineEdit
        {
            Text = target.DeckName ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "英文标识名，供 replace_deck 引用（如 Apocalypse Deck）",
        };
        nameRow.AddChild(nameEdit);
        box.AddChild(nameRow);

        var chsRow = new HBoxContainer();
        var chsLb = new Label { Text = "中文显示名", CustomMinimumSize = new Vector2(70, 0) };
        chsLb.AddThemeFontSizeOverride("font_size", 12);
        chsRow.AddChild(chsLb);
        var chsEdit = new LineEdit
        {
            Text = target.DeckNameChs ?? "",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "卡面上显示的名字（如 启示录牌组）",
        };
        chsRow.AddChild(chsEdit);
        box.AddChild(chsRow);

        var listScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(listScroll);
        var listCol = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        listCol.AddThemeConstantOverride("separation", 3);
        listScroll.AddChild(listCol);
        box.AddChild(listScroll);

        void RebuildList()
        {
            foreach (var c in listCol.GetChildren()) { listCol.RemoveChild(c); c.QueueFree(); }
            for (int i = 0; i < working.Count; i++)
            {
                var e = working[i];
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 6);

                var nm = _store.Catalog.AllCards.Values.FirstOrDefault(x => x.CardId == e.CardId);
                var lb = new Label
                {
                    Text = $"{(nm == null ? e.CardName : nm.NameChs)}（{e.CardId}）",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    ClipText = true,
                };
                lb.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(lb);

                var spin = new SpinBox { MinValue = 1, MaxValue = 40, Value = e.Copies,
                                         CustomMinimumSize = new Vector2(72, 0) };
                var captured = e;
                spin.ValueChanged += v =>
                {
                    // NamedDeckEntryRaw.Copies 是 init-only：重建整条替换。
                    int idx = working.IndexOf(captured);
                    if (idx < 0) return;
                    working[idx] = new ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw
                    { CardId = captured.CardId, CardName = captured.CardName, Copies = (int)v };
                };
                row.AddChild(spin);

                var rm = new Button { Text = "✕", CustomMinimumSize = new Vector2(28, 24) };
                SvWbTheme.StyleButton(rm, customAccent: SvWbTheme.StatusDanger);
                rm.AddThemeFontSizeOverride("font_size", 11);
                rm.Pressed += () => { working.Remove(captured); RebuildList(); };
                row.AddChild(rm);

                listCol.AddChild(row);
            }
            if (working.Count == 0)
            {
                var empty = new Label { Text = "（暂无成员，从下方添加）" };
                empty.AddThemeFontSizeOverride("font_size", 11);
                empty.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
                listCol.AddChild(empty);
            }
        }
        RebuildList();

        // 添加栏：卡池全卡下拉（含衍生 Token —— 启示录牌组装的就是 Token）。
        var addRow = new HBoxContainer();
        addRow.AddThemeConstantOverride("separation", 6);
        var picker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleOptionPopup(picker);
        var poolCards = _store.Catalog.AllCards.Values.OrderBy(x => x.CardId).ToList();
        for (int i = 0; i < poolCards.Count; i++)
            picker.AddItem($"{poolCards[i].NameChs}（{poolCards[i].CardId}）" + (poolCards[i].IsToken ? " [Token]" : ""), i);
        addRow.AddChild(picker);

        var addSpin = new SpinBox { MinValue = 1, MaxValue = 40, Value = 1, CustomMinimumSize = new Vector2(72, 0) };
        addRow.AddChild(addSpin);

        var addBtn = new Button { Text = "➕ 加入", CustomMinimumSize = new Vector2(80, 28) };
        SvWbTheme.StyleButton(addBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        addBtn.Pressed += () =>
        {
            int idx = (int)picker.Selected;
            if (idx < 0 || idx >= poolCards.Count) return;
            var cd = poolCards[idx];
            working.Add(new ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw
            { CardId = cd.CardId, CardName = cd.Name ?? "", Copies = (int)addSpin.Value });
            RebuildList();
        };
        addRow.AddChild(addBtn);
        box.AddChild(addRow);

        dlg.Confirmed += () =>
        {
            var newNd = new ShadowverseFan.KernelV3.Packs.NamedDeckRawDef
            {
                DeckName = nameEdit.Text?.Trim() ?? target.DeckName,
                DeckNameChs = chsEdit.Text?.Trim() ?? target.DeckNameChs,
                Entries = working,
            };

            var list = new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>();
            foreach (var x in cfg.NamedDecks ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>())
                list.Add(ReferenceEquals(x, target) ? newNd : x);

            _store.SavePoolConfigFull(cfg.PoolId, cfg.Name, cfg.EnabledPackIds,
                                      cfg.Rules, list, out string msg);
            _ruleStatus.Text = msg;
            RefreshNamedDecks();
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>「本卡池启用的物理卡包」勾选区。原生包锁死勾选。</summary>
    private void BuildEnabledPacksSection()
    {
        if (_ruleList == null || !_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg)) return;

        var head = new Label { Text = "本卡池启用的物理卡包" };
        head.AddThemeFontSizeOverride("font_size", 13);
        head.AddThemeColorOverride("font_color", new Color("7DD3FC"));
        _ruleList.AddChild(head);

        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 10);
        flow.AddThemeConstantOverride("v_separation", 4);
        _ruleList.AddChild(flow);

        _enabledPackChecks.Clear();
        foreach (var kv in _store.Catalog.Packs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string pid = kv.Key;
            bool isNative = string.Equals(kv.Value.NativePoolId, cfg.PoolId, StringComparison.OrdinalIgnoreCase);
            bool on = cfg.EnabledPackIds.Contains(pid, StringComparer.OrdinalIgnoreCase) || isNative;

            var cb = new CheckBox
            {
                Text = pid + (isNative ? "（原生必选）" : ""),
                ButtonPressed = on,
                Disabled = isNative,
            };
            cb.AddThemeFontSizeOverride("font_size", 11);
            _enabledPackChecks[pid] = cb;
            flow.AddChild(cb);
        }

        var applyBtn = new Button { Text = "✔ 应用启用卡包", CustomMinimumSize = new Vector2(0, 28) };
        SvWbTheme.StyleButton(applyBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        applyBtn.AddThemeFontSizeOverride("font_size", 11);
        applyBtn.Pressed += () =>
        {
            var chosen = new List<string>();
            foreach (var kv2 in _enabledPackChecks)
                if (kv2.Value.ButtonPressed) chosen.Add(kv2.Key);
            if (chosen.Count == 0) { _ruleStatus.Text = "至少要启用一个卡包"; return; }

            bool ok = _store.SavePoolConfigFull(cfg.PoolId, cfg.Name, chosen,
                cfg.Rules, cfg.NamedDecks ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>(),
                out string msg);
            _ruleStatus.Text = msg;
            if (ok) RefreshRules();
        };
        _ruleList.AddChild(applyBtn);

        var sep = new HSeparator();
        _ruleList.AddChild(sep);
    }

    /// <summary>新建卡池弹窗：填 ID 与显示名，其余用标准规则与 official-sv 起步。</summary>
    private void OpenNewPoolDialog()
    {
        var dlg = new AcceptDialog { Title = "新建卡池", Size = new Vector2I(440, 240) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        LineEdit Field(string label, string initial)
        {
            var row = new HBoxContainer();
            var lb = new Label { Text = label, CustomMinimumSize = new Vector2(96, 0) };
            lb.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(lb);
            var ed = new LineEdit { Text = initial ?? "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(ed);
            box.AddChild(row);
            return ed;
        }

        var idEdit = Field("卡池 ID", "my-pool");
        var nameEdit = Field("显示名称", "我的卡池");

        var hint = new Label
        {
            Text = "新卡池默认启用 official-sv，规则用标准赛制。建好后可在右侧勾选更多卡包。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        box.AddChild(hint);

        dlg.Confirmed += () =>
        {
            string pid = idEdit.Text?.Trim();
            if (string.IsNullOrEmpty(pid)) { _ruleStatus.Text = "卡池 ID 不能为空"; dlg.QueueFree(); return; }
            bool ok = _store.SavePoolConfigFull(pid, nameEdit.Text?.Trim(),
                new List<string> { "official-sv" }, ShadowverseFan.KernelV3.Core.MatchRules.CreateStandard(),
                new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>(), out string msg);
            _ruleStatus.Text = msg;
            if (ok)
            {
                _selectedPoolId = pid;
                BuildPoolPicker();
                RefreshRules();
                RefreshNamedDecks();
            }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>新建 / 修改卡包元信息的弹窗。</summary>
    private void OpenPackMetaDialog(string existingPackId)
    {
        var existing = existingPackId != null && _store.Catalog.Packs.TryGetValue(existingPackId, out var pk)
            ? pk : null;

        var dlg = new AcceptDialog
        {
            Title = existing == null ? "新建卡包" : "修改卡包信息",
            Size = new Vector2I(460, 300),
        };

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        dlg.AddChild(box);

        LineEdit Field(string label, string initial)
        {
            var row = new HBoxContainer();
            var lb = new Label { Text = label, CustomMinimumSize = new Vector2(110, 0) };
            lb.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(lb);
            var ed = new LineEdit { Text = initial ?? "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(ed);
            box.AddChild(row);
            return ed;
        }

        var idEdit = Field("卡包 ID", existing?.PackId);
        if (existing != null) idEdit.Editable = false;   // ID 是目录名，改了等于换包
        var nameEdit = Field("卡包名称", existing?.Name);
        var authorEdit = Field("作者", existing?.Author);
        var nativeEdit = Field("原生卡池 ID", existing?.NativePoolId ?? "standard");

        dlg.Confirmed += () =>
        {
            bool ok = _store.CreateOrUpdatePack(
                idEdit.Text?.Trim(), nameEdit.Text?.Trim(), authorEdit.Text?.Trim(),
                nativeEdit.Text?.Trim(), out string msg);
            _header.Text = "卡包 / 卡池管理 —— " + msg;
            if (ok) { _selectedPackId = idEdit.Text?.Trim(); RefreshPacks(); }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void RefreshPacks()
    {
        foreach (var c in _packList.GetChildren()) c.QueueFree();

        foreach (var kv in _store.Catalog.Packs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var pack = kv.Value;
            var btn = new Button
            {
                Text = kv.Key + "   (" + (pack.Cards?.Count ?? 0) + " 张)",
                Alignment = HorizontalAlignment.Left,
            };
            SvWbTheme.StyleButton(btn);
            string pid = kv.Key;
            btn.Pressed += () => { _selectedPackId = pid; ShowPackCards(pid); };
            _packList.AddChild(btn);
        }
    }

    private void ShowPackCards(string packId)
    {
        foreach (var c in _cardList.GetChildren()) c.QueueFree();
        if (!_store.Catalog.Packs.TryGetValue(packId, out var pack)) return;

        _header.Text = "卡包 / 卡池管理 —— " + packId;

        foreach (var c in pack.Cards.OrderBy(x => x.CardId))
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);

            var idLbl = new Label { Text = c.CardId.ToString(), CustomMinimumSize = new Vector2(84, 0) };
            idLbl.AddThemeFontSizeOverride("font_size", 11);
            idLbl.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            row.AddChild(idLbl);

            var nameLbl = new Label
            {
                Text = string.IsNullOrWhiteSpace(c.NameChs) ? (c.NameEng ?? "") : c.NameChs,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 12);
            nameLbl.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
            row.AddChild(nameLbl);

            var clanLbl = new Label { Text = c.Clan ?? "Neutral", CustomMinimumSize = new Vector2(96, 0) };
            clanLbl.AddThemeFontSizeOverride("font_size", 11);
            clanLbl.AddThemeColorOverride("font_color", SvWbTheme.ClanColor(c.Clan));
            row.AddChild(clanLbl);

            var costLbl = new Label { Text = c.Cost + " 费", CustomMinimumSize = new Vector2(52, 0) };
            costLbl.AddThemeFontSizeOverride("font_size", 11);
            costLbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
            row.AddChild(costLbl);

            int capId = c.CardId;
            var editBtn = new Button { Text = "✏", CustomMinimumSize = new Vector2(32, 24) };
            SvWbTheme.StyleButton(editBtn);
            editBtn.AddThemeFontSizeOverride("font_size", 11);
            editBtn.TooltipText = "在 DIY 工坊中编辑这张卡";
            var capRaw = c;
            editBtn.Pressed += () =>
            {
                // 转成运行时 CardDef 再交给工坊：Raw 条目里没有编译好的 Abilities。
                var def = _store.Catalog.AllCards.Values.FirstOrDefault(x => x.CardId == capId);
                if (def == null)
                {
                    _header.Text = "卡包 / 卡池管理 —— 该卡尚未成功编译，无法编辑";
                    return;
                }
                OnEditCardInWorkshop?.Invoke(def, packId);
            };
            row.AddChild(editBtn);

            var delBtn = new Button { Text = "🗑", CustomMinimumSize = new Vector2(32, 24) };
            SvWbTheme.StyleButton(delBtn, customAccent: SvWbTheme.StatusDanger);
            delBtn.AddThemeFontSizeOverride("font_size", 11);
            delBtn.Pressed += () =>
            {
                _store.DeleteCardFromPack(packId, capId, out string msg);
                _header.Text = "卡包 / 卡池管理 —— " + msg;
                ShowPackCards(packId);
            };
            row.AddChild(delBtn);

            _cardList.AddChild(row);
        }
    }

    /// <summary>
    /// 列出 MatchRules 全部字段并**允许编辑**。
    ///
    /// 反射驱动：手写 36 行必然漏，且 MatchRules 加字段时会过期。
    /// 改完点【保存卡池规则】写回 data/pools/*.pool.json 并热重载。
    /// </summary>
    private void RefreshRules()
    {
        foreach (var c in _ruleList.GetChildren()) c.QueueFree();
        _ruleEdits.Clear();

        // 下拉框：列出全部卡池
        if (_poolPicker != null)
        {
            _poolPicker.Clear();
            int pi = 0, selIdx = 0;
            foreach (var k in _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                // Clear + AddItem 会把选中项重置到第 0 项，
                // 不在这里恢复下标，下拉框文字就会永远弹回第一个卡池。
                if (string.Equals(k, _selectedPoolId, StringComparison.OrdinalIgnoreCase))
                    selIdx = pi;
                _poolPicker.AddItem(k, pi++);
            }
            _poolPicker.Selected = selIdx;
        }

        if (string.IsNullOrEmpty(_selectedPoolId)
            || !_store.PoolConfigs.ContainsKey(_selectedPoolId))
            _selectedPoolId = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).FirstOrDefault();

        // 只渲染当前选中的那个卡池（下拉框切换）。
        // 早先写死「只有第一个池可编辑、其余 continue 跳过」，
        // 导致玩家根本没法编辑 standard 或 custom-high-mana。
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var curCfg))
            return;

        {
            var head = new Label { Text = "【" + (curCfg.Name ?? _selectedPoolId) + "】" };
            head.AddThemeFontSizeOverride("font_size", 13);
            head.AddThemeColorOverride("font_color", new Color("FEF08A"));
            _ruleList.AddChild(head);

            var headHint = new Label { Text = "（当前编辑中的卡池）" };
            headHint.AddThemeFontSizeOverride("font_size", 10);
            headHint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _ruleList.AddChild(headHint);

            var rules = curCfg.Rules;
            if (rules == null) return;

            int count = 0;
            foreach (var prop in typeof(MatchRules).GetProperties())
            {
                if (!prop.CanRead) continue;
                if (prop.GetIndexParameters().Length > 0) continue;

                object val;
                try { val = prop.GetValue(rules); }
                catch { continue; }
                if (val == null) continue;

                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 10);

                // 字段名显示简中；未登记时回落到英文原名而不是空白 ——
                // 留空会让人以为「这行没有规则」。
                var a = new Label
                {
                    Text = MatchRulesLabels.Of(prop.Name),
                    CustomMinimumSize = new Vector2(230, 0),
                    TooltipText = prop.Name,
                };
                a.AddThemeFontSizeOverride("font_size", 11);
                a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
                row.AddChild(a);

                if (val is bool bv)
                {
                    var chk = new CheckBox { ButtonPressed = bv };
                    _ruleEdits[prop.Name] = chk;
                    row.AddChild(chk);
                }
                else if (val is int iv)
                {
                    var sp = new SpinBox { MinValue = 0, MaxValue = 999, Value = iv,
                                          CustomMinimumSize = new Vector2(110, 26) };
                    _ruleEdits[prop.Name] = sp;
                    row.AddChild(sp);
                }
                else
                {
                    var lbl = new Label { Text = val.ToString() };
                    lbl.AddThemeFontSizeOverride("font_size", 11);
                    lbl.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
                    row.AddChild(lbl);
                }

                _ruleList.AddChild(row);
                count++;
            }

            var foot = new Label { Text = "—— 共 " + count + " 项规则（可编辑；鼠标悬停看英文字段名） ——" };
            foot.AddThemeFontSizeOverride("font_size", 10);
            foot.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _ruleList.AddChild(foot);
        }

        BuildEnabledPacksSection();
    }

    /// <summary>
    /// 把规则面板上的编辑结果写回 data/pools/<id>.pool.json 并热重载。
    ///
    /// 用反射把控件值转回 MatchRules —— 与读取侧共用同一份字段清单，
    /// 不会出现「读得到但存不回」的半套实现。
    /// </summary>
    private void OnSaveRules()
    {
        if (string.IsNullOrEmpty(_selectedPoolId)) return;
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId, out var cfg)) return;

        var src = cfg.Rules ?? MatchRules.CreateStandard();

        // MatchRules 是 init-only 的 record-like class，只能重建一份
        var clone = new MatchRules();
        int applied = 0;

        foreach (var prop in typeof(MatchRules).GetProperties())
        {
            if (!prop.CanWrite) continue;
            if (prop.GetIndexParameters().Length > 0) continue;

            object cur;
            try { cur = prop.GetValue(src); }
            catch { continue; }

            object newVal = cur;
            if (_ruleEdits.TryGetValue(prop.Name, out var ctl))
            {
                if (ctl is CheckBox cb) newVal = cb.ButtonPressed;
                else if (ctl is SpinBox sp) newVal = (int)sp.Value;
            }

            try
            {
                if (newVal != null && prop.PropertyType.IsInstanceOfType(newVal))
                {
                    prop.SetValue(clone, newVal);
                    applied++;
                }
            }
            catch { }
        }

        bool ok = _store.SavePoolRules(_selectedPoolId, clone, out string msg);
        if (_ruleStatus != null)
        {
            _ruleStatus.Text = (ok ? "✔ " : "✘ ") + msg;
            _ruleStatus.AddThemeColorOverride("font_color",
                ok ? SvWbTheme.StatusValid : SvWbTheme.StatusDanger);
        }

        RefreshRules();
        RefreshNamedDecks();
    }
}

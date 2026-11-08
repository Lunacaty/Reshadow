using System;
using System.Collections.Generic;
using System.IO;
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
    /// <summary>左栏卡池列表容器。</summary>
    private VBoxContainer _poolListBox;
    private VBoxContainer _packList;
    private string _selectedPackId;
    private readonly Dictionary<string, CheckBox> _enabledPackChecks = new();
    private VBoxContainer _cardList;
    /// <summary>右栏当前页签：0=赛制规则 1=具名牌组 2=卡池计数器 3=卡包归属。</summary>
    private int _activeRightTab = 0;
    /// <summary>页签内容宿主；切换页签时只重填它，不重建整页。</summary>
    private VBoxContainer _tabContentHost;
    private Button[] _tabButtons;
    /// <summary>各区域的页签包装容器（用于显示 / 隐藏）。</summary>
    private readonly List<Control> _tabPages = new();
    private VBoxContainer _ruleList;
    private VBoxContainer _namedDeckList;
    private HFlowContainer _poolClanList;
    private VBoxContainer _poolCounterList;
    private Label _header;
    private readonly Dictionary<string, Control> _ruleEdits = new();
    private Button _saveRulesBtn;
    private Label _ruleStatus;
    private string _selectedPoolId;
    private OptionButton rowPools;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        if (!_store.PoolConfigs.Any()) _store.Initialize();
        BuildUi();
        RefreshPacks();
        RefreshRules();
        RefreshNamedDecks();
        RefreshPoolClans();
        RefreshPoolCounters();
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
        // 通道 B 示范点：页面大标题走「奶白 -> 鎏金」渐变（187 号文档 专题二）。
        SvWbTheme.ApplyGradientText(_header, SvWbTheme.AccentGold);
        row.AddChild(_header);

        // 外包一层左右 16 的边距：不加的话右栏紧贴屏幕右缘，
        // 最右侧的删除按钮会被窗口边缘切掉一半。
        var bodyMargin = new MarginContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        bodyMargin.AddThemeConstantOverride("margin_left", 16);
        bodyMargin.AddThemeConstantOverride("margin_right", 16);
        bodyMargin.AddThemeConstantOverride("margin_top", 4);
        bodyMargin.AddThemeConstantOverride("margin_bottom", 4);
        root.AddChild(bodyMargin);

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10);
        bodyMargin.AddChild(body);

        // ── 左栏：赛制卡池列表（185 号文档 专题一 三栏主从）──
        //
        // 早先左栏是「物理卡包」，卡池只是一个下拉框（_poolPicker），
        // 玩家要编辑哪个卡池得先在下拉里翻 —— 心智模型割裂。
        // 现在卡池独立成左栏，卡包归属移进中栏每个卡包行的即时开关。
        var poolCol = new VBoxContainer
        {
            CustomMinimumSize = new Vector2(240, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 0.7f,
        };
        poolCol.AddThemeConstantOverride("separation", 6);
        body.AddChild(poolCol);

        var poolTitle = new Label { Text = "赛制卡池", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        poolTitle.AddThemeFontSizeOverride("font_size", 14);
        poolTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentGold);
        poolCol.AddChild(poolTitle);

        var poolBar = new HFlowContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        poolBar.AddThemeConstantOverride("h_separation", 4);
        poolBar.AddThemeConstantOverride("v_separation", 4);
        poolCol.AddChild(poolBar);

        var newPoolBtn2 = new Button { Text = "➕新建", CustomMinimumSize = new Vector2(70, 26) };
        SvWbTheme.StyleButton(newPoolBtn2, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        newPoolBtn2.AddThemeFontSizeOverride("font_size", 11);
        newPoolBtn2.Pressed += OpenNewPoolDialog;
        poolBar.AddChild(newPoolBtn2);

        var delPoolBtn2 = new Button { Text = "🗑删除", CustomMinimumSize = new Vector2(70, 26) };
        SvWbTheme.StyleButton(delPoolBtn2, customAccent: SvWbTheme.StatusDanger);
        delPoolBtn2.AddThemeFontSizeOverride("font_size", 11);
        delPoolBtn2.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_selectedPoolId)) { _header.Text = "请先选中一个卡池"; return; }
            _store.DeletePool(_selectedPoolId, out string m);
            _header.Text = "卡包 / 卡池管理 —— " + m;
            // 删掉后当前池已不存在，必须走全量刷新：
            // 只调 RefreshRules() 的话，中栏卡包勾选框与右栏具名牌组/计数器
            // 还停在刚被删掉的那个池上（186 号文档复核 遗留项 1）。
            _selectedPoolId = null;
            SelectPoolAndRefreshAll(null);
        };
        poolBar.AddChild(delPoolBtn2);

        var poolScroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(poolScroll);
        poolCol.AddChild(poolScroll);

        _poolListBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _poolListBox.AddThemeConstantOverride("separation", 4);
        poolScroll.AddChild(_poolListBox);

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

        // ── 右栏页签化（185 号文档 专题一）──
        //
        // 早先右栏在一个 720p 高的容器里堆了 4 个独立 ScrollContainer
        // （规则 / 具名牌组 / 职业 / 计数器），每块只有 90~110px，
        // 形同「钥匙孔看世界」。改成页签后每块独占全高。
        var tabBar = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        tabBar.AddThemeConstantOverride("separation", 6);
        string[] tabNames = { "⚙ 赛制规则", "📚 具名牌组", "🔢 卡池计数器", "📦 卡包归属" };
        _tabButtons = new Button[tabNames.Length];
        for (int ti = 0; ti < tabNames.Length; ti++)
        {
            int idx = ti;
            var tb = new Button
            {
                Text = tabNames[ti],
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(0, 32),
            };
            SvWbTheme.StyleButton(tb, isPrimary: _activeRightTab == idx);
            tb.AddThemeFontSizeOverride("font_size", 11);
            tb.Pressed += () => { _activeRightTab = idx; ApplyRightTabVisibility(); };
            _tabButtons[ti] = tb;
            tabBar.AddChild(tb);
        }
        right.AddChild(tabBar);

        // 用 HFlowContainer 而不是 HBoxContainer：标题 + 卡池下拉 + 三个按钮 +
        // 状态标签的总最小宽度会超过 1360px，HBox 不换行，窗口一窄就溢出。
        // HFlow 会自动折到第二行。
        var ruleBar = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ruleBar.AddThemeConstantOverride("h_separation", 8);
        ruleBar.AddThemeConstantOverride("v_separation", 6);
        // ── 页签 0：赛制规则（独占全高）──
        var page0 = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        page0.AddThemeConstantOverride("separation", 6);
        right.AddChild(page0);
        _tabPages.Add(page0);
        page0.AddChild(ruleBar);

        var ruleTitle = new Label { Text = "赛制卡池规则（MatchRules 全字段）" };
        ruleTitle.AddThemeFontSizeOverride("font_size", 14);
        ruleTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        ruleBar.AddChild(ruleTitle);

        // 卡池下拉框已删除：切池统一由左栏卡池列表负责
        // （186 号文档 缺陷 1：右栏留一个重复下拉框会让玩家以为有两处入口，
        //  而且它漏调 RefreshPacks，切池后中栏勾选态不跟着变）。

        // 167 专项二：卡池整合包导出 / 导入。分享自制赛制时，
        // 只发 .pool.json 对方会因缺卡包直接建池失败。
        var exportBtn = new Button { Text = "📦 导出整合包", CustomMinimumSize = new Vector2(112, 32) };
        SvWbTheme.StyleButton(exportBtn);
        exportBtn.AddThemeFontSizeOverride("font_size", 11);
        exportBtn.Pressed += () =>
        {
            if (string.IsNullOrEmpty(_selectedPoolId)) { _ruleStatus.Text = "请先选择一个卡池"; return; }
            // 必须让玩家自己选路径：写死到 UserDataDir 的隐藏文件，
            // 他既不知道文件在哪，也没法直接发给别人。
            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.SaveFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "导出卡池整合包",
                CurrentFile = _selectedPoolId + ".poolbundle.json",
            };
            dlg.AddFilter("*.json", "卡池整合包 JSON");
            dlg.FileSelected += path =>
            {
                bool ok = _store.ExportPoolBundleToFile(_selectedPoolId, path, out string m);
                _ruleStatus.Text = (ok ? "✔ " : "✘ ") + m + (ok ? " -> " + path : "");
                dlg.QueueFree();
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(760, 520));
        };
        ruleBar.AddChild(exportBtn);

        var importBtn = new Button { Text = "📥 导入整合包", CustomMinimumSize = new Vector2(112, 32) };
        SvWbTheme.StyleButton(importBtn);
        importBtn.AddThemeFontSizeOverride("font_size", 11);
        importBtn.Pressed += () =>
        {
            var dlg = new FileDialog
            {
                FileMode = FileDialog.FileModeEnum.OpenFile,
                Access = FileDialog.AccessEnum.Filesystem,
                Title = "导入卡池整合包",
            };
            dlg.AddFilter("*.json", "卡池整合包 JSON");
            dlg.FileSelected += path =>
            {
                bool ok = _store.ImportPoolBundleFromFile(path, out string m);
                _ruleStatus.Text = (ok ? "✔ " : "✘ ") + m;
                if (ok)
                {
                    RefreshPacks(); RefreshRules(); RefreshNamedDecks();
                    RefreshPoolClans(); RefreshPoolCounters();
                }
                dlg.QueueFree();
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered(new Vector2I(760, 520));
        };
        ruleBar.AddChild(importBtn);

        // 新建 / 删除卡池按钮已移至左栏卡池列表顶部。


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
        page0.AddChild(ruleScroll);
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
                DeckName = "新牌组",
                DeckNameChs = "新牌组",
                Entries = new List<ShadowverseFan.KernelV3.Packs.NamedDeckEntryRaw>(),
            });
            cfg0 = new ShadowverseFan.KernelV3.Packs.CardPoolConfig
            {
                PoolId = cfg0.PoolId, Name = cfg0.Name, Rules = cfg0.Rules,
                EnabledPackIds = cfg0.EnabledPackIds, NamedDecks = list0,
                // 职业与主战者计数器必须一起带上：
                // 只重建具名牌组就丢掉这两项，等于点一次「新增具名牌组」
                // 把当前卡池的职业与计数器全部抹空，而且没有任何提示。
                PoolClans = cfg0.PoolClans,
                LeaderCounters = cfg0.LeaderCounters,
            };
            _store.PoolConfigs[cfg0.PoolId] = cfg0;
            _store.SavePoolConfigFull(cfg0.PoolId, cfg0.Name, cfg0.EnabledPackIds,
                                      cfg0.Rules, cfg0.NamedDecks, out string msg,
                                      cfg0.PoolClans, cfg0.LeaderCounters);
            _ruleStatus.Text = msg;
            RefreshNamedDecks();
        };
        ndBar.AddChild(addNdBtn);
        // ── 页签 1：具名牌组 ──
        var page1 = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        page1.AddThemeConstantOverride("separation", 6);
        right.AddChild(page1);
        _tabPages.Add(page1);
        page1.AddChild(ndBar);

        _namedDeckList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _namedDeckList.AddThemeConstantOverride("separation", 2);
        // SizeFlagsVertical 必填：只给 CustomMinimumSize 的话，
        // 页签切过来时列表只有 110px 高，下面一大片全空着（186 号文档 缺陷 3）。
        var ndScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 110),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(ndScroll);
        ndScroll.AddChild(_namedDeckList);
        page1.AddChild(ndScroll);

        // ---- 本卡池职业（164 专题二） ----
        //
        // 职业归属卡池而非卡包：同一批卡在不同赛制里开放的职业可以不同。
        // 这里只做展示与勾选，真正生效在 CardPool 构建时读 PoolClans。
        // ── 页签 2：卡池职业 + 计数器 ──
        var page2 = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        page2.AddThemeConstantOverride("separation", 6);
        right.AddChild(page2);
        _tabPages.Add(page2);

        var clanBar = new HBoxContainer();
        clanBar.AddThemeConstantOverride("separation", 6);
        var clanTitle = new Label { Text = "本卡池职业", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        clanTitle.AddThemeFontSizeOverride("font_size", 13);
        clanTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        clanBar.AddChild(clanTitle);
        var addClanBtn = new Button { Text = "+ 新增职业", CustomMinimumSize = new Vector2(88, 26) };
        SvWbTheme.StyleButton(addClanBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        addClanBtn.AddThemeFontSizeOverride("font_size", 11);
        addClanBtn.Pressed += OpenAddPoolClanDialog;
        clanBar.AddChild(addClanBtn);
        page2.AddChild(clanBar);

        _poolClanList = new HFlowContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _poolClanList.AddThemeConstantOverride("h_separation", 8);
        _poolClanList.AddThemeConstantOverride("v_separation", 4);
        page2.AddChild(_poolClanList);

        // ---- 主战者计数器（164 专题二） ----
        //
        // 赛制级资源，跨卡包共享。内置的那批由引擎维护，UI 只读锁死。
        var ctrBar = new HBoxContainer();
        ctrBar.AddThemeConstantOverride("separation", 6);
        var ctrTitle = new Label { Text = "主战者计数器", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        ctrTitle.AddThemeFontSizeOverride("font_size", 13);
        ctrTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        ctrBar.AddChild(ctrTitle);
        var addCtrBtn = new Button { Text = "+ 新增计数器", CustomMinimumSize = new Vector2(96, 26) };
        SvWbTheme.StyleButton(addCtrBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        addCtrBtn.AddThemeFontSizeOverride("font_size", 11);
        addCtrBtn.Pressed += () => OpenAddPoolCounterDialog();
        ctrBar.AddChild(addCtrBtn);
        page2.AddChild(ctrBar);

        _poolCounterList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _poolCounterList.AddThemeConstantOverride("separation", 2);
        // 同 ndScroll：必须占满全高。
        var ctrScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 90),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(ctrScroll);
        ctrScroll.AddChild(_poolCounterList);
        page2.AddChild(ctrScroll);

        // ── 页签 3：卡包归属 ──
        var page3 = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        page3.AddThemeConstantOverride("separation", 6);
        right.AddChild(page3);
        _tabPages.Add(page3);
        _tabContentHost = page3;

        ApplyRightTabVisibility();
    }


    /// <summary>新增卡池职业：填英文内部 ID 与显示名，注册进 Schema 与本卡池配置。</summary>
    private void OpenAddPoolClanDialog()
    {
        if (string.IsNullOrEmpty(_selectedPoolId)) { _ruleStatus.Text = "请先选择一个卡池"; return; }
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId, out var cfg)) return;

        var dlg = new AcceptDialog { Title = "新增卡池职业" };
        dlg.OkButtonText = "创建";

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        box.AddThemeConstantOverride("separation", 6);

        // 只填一个名字。职业名就是玩家写的那一个 ——
        // 拆成「内部 ID + 显示名」等于存两套真相，改了名还要同步两处，
        // 而卡包的 clan 字段本来存的也是同一个字符串。
        var nameEdit = new LineEdit { PlaceholderText = "职业名（如 机甲）" };
        box.AddChild(new Label { Text = "职业名" });
        box.AddChild(nameEdit);
        WrapDialogContent(dlg, box);

        dlg.Confirmed += () =>
        {
            string cname = nameEdit.Text?.Trim();
            if (string.IsNullOrEmpty(cname)) { _ruleStatus.Text = "职业名不能为空"; return; }

            // 注册进 Schema：不注册的话效果里写 clan=机甲 会拿到 Invalid。
            _store.Catalog.Schema.Clan(cname, cfg.PoolId);
            _store.Catalog.Lexicon.AddClan(cname);

            var clans = new List<string>(cfg.PoolClans ?? new List<string>());
            if (!clans.Contains(cname, StringComparer.OrdinalIgnoreCase)) clans.Add(cname);
            _store.SavePoolConfigFull(cfg.PoolId, cfg.Name, cfg.EnabledPackIds, cfg.Rules, cfg.NamedDecks,
                                     out string msg, clans, cfg.LeaderCounters);
            _ruleStatus.Text = msg;
            RefreshPoolClans();
        };
        AddChild(dlg);
        dlg.Canceled += () => dlg.QueueFree();
        dlg.PopupCentered();
    }

    /// <summary>
    /// 新增 / 编辑卡池主战者计数器。
    ///
    /// existing 非空即为编辑：同一个弹窗回填已有值，
    /// 免得玩家「改一个数值只能删掉重建」，中间那段配置就丢了。
    /// </summary>
    /// <summary>当前卡池的全部卡（供计数器规则的 # 搜卡候选栏使用）。</summary>
    private List<KernelV3.Ast.CardDef> CurrentPoolCardsForCounter()
    {
        try
        {
            if (!string.IsNullOrEmpty(_selectedPoolId))
            {
                var rt = _store.GetPoolRuntime(_selectedPoolId);
                if (rt != null) return rt.AllCards.Values.OrderBy(c => c.CardId).ToList();
            }
            return _store.Catalog.AllCards.Values.OrderBy(c => c.CardId).ToList();
        }
        catch { return new List<KernelV3.Ast.CardDef>(); }
    }

    private void OpenAddPoolCounterDialog(KernelV3.Packs.PoolCounterDef existing = null)
    {
        if (string.IsNullOrEmpty(_selectedPoolId)) { _ruleStatus.Text = "请先选择一个卡池"; return; }
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId, out var cfg)) return;

        bool editing = existing != null;
        var dlg = new AcceptDialog { Title = editing ? "编辑主战者计数器" : "新增主战者计数器" };
        dlg.OkButtonText = editing ? "保存" : "创建";

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        box.AddThemeConstantOverride("separation", 6);

        // 恢复「英文内部 ID + 中文显示名」双输入框（179 号文档 C6 环节 3）。
        // 内部 ID 要写进 AST（ctr:sword_intent），中文名只用于显示（【剑意】）。
        // 合成一个框的话，玩家用中文当 ID 会让英文端与 PyAST 端的脚本全部无法引用。
        var nameEdit = new LineEdit { PlaceholderText = "英文内部 ID（如 sword_intent）" };
        var zhNameEdit = new LineEdit { PlaceholderText = "中文显示名（如 剑意）" };
        var minEdit = new SpinBox { MinValue = -999, MaxValue = 999, Value = 0 };
        var maxEdit = new SpinBox { MinValue = -999, MaxValue = 999, Value = 99 };
        var initEdit = new SpinBox { MinValue = -999, MaxValue = 999, Value = 0 };
        var lifePick = new OptionButton();
        var lifecycles = Enum.GetValues<KernelV3.Packs.CounterLifecycle>();
        // 下拉显示中文说明 + 内码：只显示内码玩家不知道该选哪个。
        for (int li = 0; li < lifecycles.Length; li++)
            lifePick.AddItem(LifecycleZh(lifecycles[li]) + " (" + lifecycles[li] + ")", li);

        // 编辑模式回填。
        // 编辑时 ID 不可改：改了等于换一个新计数器，旧的那个引用它的效果全部失效。
        if (editing)
        {
            nameEdit.Editable = false;
            nameEdit.Text = existing.Name ?? "";
            zhNameEdit.Text = existing.NameChs ?? "";
            minEdit.Value = existing.Min;
            maxEdit.Value = existing.Max;
            initEdit.Value = existing.Initial;
            lifePick.Select((int)existing.Lifecycle);
        }

        box.AddChild(new Label { Text = "英文内部 ID（供 AST 与脚本引用）" });
        box.AddChild(nameEdit);
        box.AddChild(new Label { Text = "中文显示名（供卡面与界面渲染）" });
        box.AddChild(zhNameEdit);
        box.AddChild(new Label { Text = "最小值" });
        box.AddChild(minEdit);
        box.AddChild(new Label { Text = "最大值" });
        box.AddChild(maxEdit);
        box.AddChild(new Label { Text = "初始值" });
        box.AddChild(initEdit);
        box.AddChild(new Label { Text = "回合生命周期" });
        box.AddChild(lifePick);

        // ── 计数器的事件驱动规则（179 号文档 C6 环节 4）──
        //
        // 规则本质就是标准的 AbilityDef，所以直接复用 DIY 的代码编辑器：
        // 玩家写的 PyAST 与积木端 / 表单端编译出的东西完全同构。
        var ruleHeader = new HBoxContainer();
        ruleHeader.AddThemeConstantOverride("separation", 4);
        var ruleTitle = new Label
        {
            Text = "⚡ 触发变化规则（PyAST）",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        ruleTitle.AddThemeFontSizeOverride("font_size", 12);
        ruleTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        ruleHeader.AddChild(ruleTitle);

        var tplEnterBtn = new Button { Text = "+ 兵种进场 +1" };
        SvWbTheme.StyleButton(tplEnterBtn, customAccent: SvWbTheme.AccentPurple);
        ruleHeader.AddChild(tplEnterBtn);

        var tplStrikeBtn = new Button { Text = "+ 随从攻击 +1" };
        SvWbTheme.StyleButton(tplStrikeBtn, customAccent: SvWbTheme.AccentPurple);
        ruleHeader.AddChild(tplStrikeBtn);

        var tplTurnBtn = new Button { Text = "+ 回合开始 +1" };
        SvWbTheme.StyleButton(tplTurnBtn, customAccent: SvWbTheme.AccentPurple);
        ruleHeader.AddChild(tplTurnBtn);
        box.AddChild(ruleHeader);

        var ruleEdit = new AstCodeEdit
        {
            CustomMinimumSize = new Vector2(520, 150),
            Text = editing ? (existing.RulesScript ?? "") : "",
            PlaceholderText =
                "# 当友方【剑仙】随从进入战场时，使该计数器 +1\n" +
                "on_enter on(self:board<Follower> where has trait 剑仙):\n" +
                "    counter sword_intent by 1",
            CardSource = () =>
            {
                try
                {
                    return CurrentPoolCardsForCounter().Select(c => (
                        c.CardId,
                        string.IsNullOrWhiteSpace(c.NameChs) ? (c.Name ?? "") : c.NameChs,
                        c.Name ?? "",
                        c.IsToken));
                }
                catch { return Enumerable.Empty<(int, string, string, bool)>(); }
            },
        };
        ruleEdit.SetupIdeFeatures();
        box.AddChild(ruleEdit);

        // 模板：把当前填的 ID 直接嵌进脚本，玩家点一下就有一条能跑的规则。
        void AppendRule(string snippet)
        {
            string cid = string.IsNullOrWhiteSpace(nameEdit.Text)
                ? "sword_intent" : nameEdit.Text.Trim();
            string body = snippet.Replace("__CTR__", cid);
            string cur2 = ruleEdit.Text ?? "";
            ruleEdit.Text = string.IsNullOrWhiteSpace(cur2)
                ? body : cur2.TrimEnd() + "\n\n" + body;
        }
        tplEnterBtn.Pressed += () => AppendRule(
            "on_enter on(self:board<Follower> where has trait 剑仙):\n    counter __CTR__ by 1");
        tplStrikeBtn.Pressed += () => AppendRule(
            "strike on(self:board<Follower>):\n    counter __CTR__ by 1");
        tplTurnBtn.Pressed += () => AppendRule(
            "turn_start:\n    counter __CTR__ by 1");

        WrapDialogContent(dlg, box);

        dlg.Confirmed += () =>
        {
            string id = nameEdit.Text?.Trim();
            if (string.IsNullOrEmpty(id)) { _ruleStatus.Text = "计数器名称不能为空"; return; }

            string zhName = zhNameEdit.Text?.Trim();
            if (string.IsNullOrEmpty(zhName)) zhName = id;

            var def = new KernelV3.Packs.PoolCounterDef
            {
                // 英文内部 ID + 中文显示名分离（179 号文档 C6 环节 3）：
                // 内部 ID 要能被 AST / 脚本引用，中文名只做渲染。
                Name = id, NameChs = zhName,
                Min = (int)minEdit.Value, Max = (int)maxEdit.Value, Initial = (int)initEdit.Value,
                Lifecycle = (KernelV3.Packs.CounterLifecycle)lifePick.Selected,
                IsBuiltin = false,
                // 规则脚本必须一起存下来，重载后由 CardPoolBuilder 重新编译
                RulesScript = (ruleEdit.Text ?? "").Trim(),
            };

            // 注册进 Schema：不注册的话效果里写 ctr:my_charge 会拿到 Invalid。
            _store.Catalog.Schema.Counter(id, cfg.PoolId);
            _store.Catalog.Schema.CounterLimits[id] = (def.Min, def.Max);
            // 中文名进术语表：卡面上 ctr:sword_intent 才能渲染成【剑意】
            _store.Catalog.Glossary.Add(id, zhName);

            var ctrs = new List<KernelV3.Packs.PoolCounterDef>(cfg.LeaderCounters ?? new List<KernelV3.Packs.PoolCounterDef>());
            ctrs.RemoveAll(x => x != null && string.Equals(x.Name, id, StringComparison.OrdinalIgnoreCase));
            ctrs.Add(def);
            _store.SavePoolConfigFull(cfg.PoolId, cfg.Name, cfg.EnabledPackIds, cfg.Rules, cfg.NamedDecks,
                                     out string msg, cfg.PoolClans, ctrs);
            _ruleStatus.Text = msg;
            RefreshPoolCounters();
        };
        AddChild(dlg);
        dlg.Canceled += () => dlg.QueueFree();
        dlg.PopupCentered();
    }

    /// <summary>刷新「本卡池职业」勾选区（随卡池下拉切换）。</summary>
    private void RefreshPoolClans()
    {
        if (_poolClanList == null) return;
        foreach (var c in _poolClanList.GetChildren()) { _poolClanList.RemoveChild(c); c.QueueFree(); }

        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg)) return;
        var enabled = new HashSet<string>(cfg.PoolClans ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        // 来源合并：全局 Schema 里注册过的 + 本卡池自己配的。
        // 只读 Schema 会在重载后丢掉自定义职业（它们存在 .pool.json，不在 pack.json）；
        // 只读 PoolClans 则会把卡包声明但本池尚未勾选的标准职业藏起来。
        var allClanNames = _store.Catalog.Schema.Clans.All.Select(x => x.Name)
            .Concat(cfg.PoolClans ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Ordinal);

        foreach (var cname in allClanNames)
        {
            // 167 专项一：每个职业一行 —— [勾选] [名称] [✏ 改名] [🗑 删除]。
            // 只给一个 CheckBox 的话，玩家既改不了显示名也删不掉职业。
            var rowBox = new HBoxContainer();
            rowBox.AddThemeConstantOverride("separation", 6);

            var cb = new CheckBox { Text = SvWbTheme.ClanDisplayZh(cname), ButtonPressed = enabled.Contains(cname) };
            cb.AddThemeFontSizeOverride("font_size", 11);
            cb.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            string captured = cname;

            cb.Toggled += on =>
            {
                // 关闭勾选 = 从卡池移除该职业，必须先做依赖检查。
                if (!on) { TryRemoveClan(captured, cb); return; }

                // 必须读**最新**配置：闭包里的 cfg 是建控件那一刻的快照，
                // 连续勾两个职业时第二个会拿旧快照覆盖第一个，前者当场消失。
                if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latest)) return;
                var cur = new HashSet<string>(latest.PoolClans ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
                cur.Add(captured);
                _store.SavePoolConfigFull(latest.PoolId, latest.Name, latest.EnabledPackIds, latest.Rules,
                                         latest.NamedDecks, out string m, cur.ToList(), latest.LeaderCounters);
                _ruleStatus.Text = m;
            };
            rowBox.AddChild(cb);

            var rename = new Button { Text = "✏", CustomMinimumSize = new Vector2(26, 24) };
            SvWbTheme.StyleButton(rename);
            rename.Pressed += () => OpenRenameClanDialog(captured);
            rowBox.AddChild(rename);

            var del = new Button { Text = "🗑", CustomMinimumSize = new Vector2(26, 24) };
            SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
            del.Pressed += () => { cb.ButtonPressed = false; TryRemoveClan(captured, cb); };
            rowBox.AddChild(del);

            _poolClanList.AddChild(rowBox);
        }
    }


    /// <summary>计数器生命周期 → 中文说明。枚举名直接显示玩家看不懂。</summary>
    private static string LifecycleZh(KernelV3.Packs.CounterLifecycle lc) => lc switch
    {
        KernelV3.Packs.CounterLifecycle.ResetToMinOnTurnStart => "回合初清零",
        KernelV3.Packs.CounterLifecycle.ResetToMinOnTurnEnd => "回合末清零",
        KernelV3.Packs.CounterLifecycle.IncrementOnTurnStart => "回合初+1",
        _ => "整局保留",
    };


    /// <summary>
    /// 尝试从卡池移除职业。先做依赖扫描，有卡依赖就拦下并恢复勾选。
    ///
    /// 不拦的话，删完那一刻看不出异常，要等构筑或对局才炸，
    /// 而报错指向「符号无效」，跟这次删除毫无关联。
    /// </summary>
    private void TryRemoveClan(string clanName, CheckBox restoreTo)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return;
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latest)) return;

        var deps = ClanDependencyScanner.FindCardsDependingOnClan(_store.GetPoolRuntime(_selectedPoolId), clanName);
        if (deps.Count > 0)
        {
            if (restoreTo != null) restoreTo.ButtonPressed = true;
            _ruleStatus.Text = "✘ 无法删除职业【" + SvWbTheme.ClanDisplayZh(clanName) + "】：卡池内有 "
                               + deps.Count + " 张卡依赖此职业（" + string.Join("、", deps.Take(3)) + "）";
            return;
        }

        var cur = new HashSet<string>(latest.PoolClans ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        cur.Remove(clanName);
        _store.SavePoolConfigFull(latest.PoolId, latest.Name, latest.EnabledPackIds, latest.Rules,
                                 latest.NamedDecks, out string m, cur.ToList(), latest.LeaderCounters);
        _ruleStatus.Text = m;
        RefreshPoolClans();
    }

    /// <summary>修改职业的显示名。只改 Glossary 里的译名，内部 ID 不动。</summary>
    private void OpenRenameClanDialog(string clanName)
    {
        if (string.IsNullOrWhiteSpace(clanName)) return;
        var dlg = new AcceptDialog { Title = "重命名职业" };
        dlg.OkButtonText = "保存";

        var box = new VBoxContainer { CustomMinimumSize = new Vector2(320, 0) };
        box.AddThemeConstantOverride("separation", 6);
        box.AddChild(new Label { Text = "当前职业名：" + clanName });
        var edit = new LineEdit { Text = clanName };
        box.AddChild(new Label { Text = "新职业名" });
        box.AddChild(edit);
        WrapDialogContent(dlg, box);

        dlg.Confirmed += () =>
        {
            string zh = edit.Text?.Trim();
            if (string.IsNullOrEmpty(zh)) return;

            // 改名 = 直接改 pool_clans 里的那个字符串。
            // 不存在「内部 ID + 显示名」两套：卡包的 clan 字段与本表存的是同一个值，
            // 分两处存的话迟早对不上。
            if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latest)) return;
            if (string.Equals(zh, clanName, StringComparison.Ordinal)) return;

            // 改名等于换一个职业符号：已有卡绑在旧名上，改完它们的 Clan 就失效了。
            // 而卡包里如果声明过旧职业名，RefreshPoolClans 会把它重新扫出来，
            // 界面上变成「旧名（未勾选）+ 新名（已勾选）」两个职业并存。
            var deps = ClanDependencyScanner.FindCardsDependingOnClan(
                _store.GetPoolRuntime(_selectedPoolId), clanName);
            if (deps.Count > 0)
            {
                _ruleStatus.Text = "✘ 无法重命名【" + clanName + "】：卡池内有 " + deps.Count
                                   + " 张卡绑定此职业（" + string.Join("、", deps.Take(3))
                                   + "）。请先改掉这些卡的职业，或新建一个职业。";
                return;
            }

            var clans = new List<string>(latest.PoolClans ?? new List<string>());
            int at = clans.FindIndex(x => string.Equals(x, clanName, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) clans[at] = zh; else clans.Add(zh);
            _store.SavePoolConfigFull(latest.PoolId, latest.Name, latest.EnabledPackIds, latest.Rules,
                                     latest.NamedDecks, out string m, clans, latest.LeaderCounters);
            // 卡包的 clans 声明与卡片 clan= 里还写着旧名，不同步替换的话，
            // 重载后旧名会被重新注册进 Schema，界面上变成两个职业并存。
            // 只改**本卡池启用**的卡包：全盘扫 data/packs 会把别的卡池一起篡改
            // （179 号文档 C5c）。
            _store.RenameClanInPacks(clanName, zh, latest.EnabledPackIds, out string pm);
            _store.Catalog.Schema.Clan(zh, latest.PoolId);
            _store.Catalog.Lexicon.AddClan(zh);
            if (!string.IsNullOrEmpty(pm)) m = m + "；" + pm;
            _ruleStatus.Text = "已把职业「" + clanName + "」改名为「" + zh + "」" + (string.IsNullOrEmpty(m) ? "" : "（" + m + "）");
            RefreshPoolClans();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>刷新「主战者计数器」列表：内置的锁死只读，自定义的可删。</summary>
    private void RefreshPoolCounters()
    {
        if (_poolCounterList == null) return;
        foreach (var c in _poolCounterList.GetChildren()) { _poolCounterList.RemoveChild(c); c.QueueFree(); }

        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg)) return;

        // 先展示**内置**计数器（165 专题二断链 C）。
        // 它们由引擎维护、不进 cfg.LeaderCounters，只遍历配置会让这一栏看起来是空的，
        // 玩家会以为「本卡池没有任何计数器」。内置的锁死只读。
        // 只列**真正的主战者计数器**。
        // GameSchema.BuiltinCounterDefs 里还有 pp_at_play / X / Y / Z / ced_engage
        // 这类内部编译寄存器，把它们当计数器展示会让玩家以为卡池有这么些资源
        // （179 号文档 C6 环节 3）。
        var visibleBuiltin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "pp", "max_pp", "ep", "sep", "combo", "rally", "shadow", "wrath",
            "evolve_count", "faith", "earth_sigil", "clues", "crest",
        };
        foreach (var (bName, bMin, bMax) in KernelV3.Core.GameSchema.BuiltinCounterDefs)
        {
            if (!visibleBuiltin.Contains(bName)) continue;
            var brow = new HBoxContainer();
            brow.AddThemeConstantOverride("separation", 6);
            // 内置计数器也要显示中文名：「pp」这种内码玩家认不出是什么。
            string bZh = _store.CardPrinter != null ? _store.CardPrinter.CounterZh(bName) : bName;
            var bname = new Label
            {
                Text = "🔒 " + bZh + " (" + bName + ")  范围[" + bMin + ".." + bMax + "]  · 内置不可编辑",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
            };
            bname.AddThemeFontSizeOverride("font_size", 11);
            bname.AddThemeColorOverride("font_color", SvWbTheme.BorderSubtle);
            brow.AddChild(bname);
            _poolCounterList.AddChild(brow);
        }

        foreach (var def in cfg.LeaderCounters ?? new List<KernelV3.Packs.PoolCounterDef>())
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);
            // 一行写全：中文名 (英文名) [范围, 初值] · 变化方式。
            // 只显示中文名与内码 Lifecycle，玩家看不出范围与初值是多少。
            string zhName = string.IsNullOrWhiteSpace(def.NameChs) ? def.Name : def.NameChs;
            // 有规则脚本的计数器给一个 ⚡ 标记，玩家一眼看出它是事件驱动的，
            // 而不是只能靠卡面效果手动加减（179 号文档 C6）。
            bool hasRule = !string.IsNullOrWhiteSpace(def.RulesScript);
            string ruleTag = hasRule ? "  ⚡规则" + (def.RulesAbilities?.Count ?? 0) + "条" : "";
            var name = new Label
            {
                Text = (zhName == def.Name ? def.Name : zhName + " (" + def.Name + ")") + "  [" + def.Min + ".." + def.Max
                       + ", 初值:" + def.Initial + "] · " + LifecycleZh(def.Lifecycle) + ruleTag,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                ClipText = true,
            };
            name.AddThemeFontSizeOverride("font_size", 11);
            row.AddChild(name);

            if (def.IsBuiltin)
            {
                // 内置计数器由引擎维护（pp / ep / sep …），改它会直接打断对局。
                var lockLbl = new Label { Text = "🔒" };
                lockLbl.AddThemeFontSizeOverride("font_size", 11);
                row.AddChild(lockLbl);
            }
            else
            {
                // 编辑：打开同一个弹窗，把已有值回填进去。
                // 没有编辑入口时，改一个数值只能删掉重建，中间那段配置就丢了。
                var edit = new Button { Text = "✏", CustomMinimumSize = new Vector2(28, 24) };
                SvWbTheme.StyleButton(edit);
                var ed = def;
                edit.Pressed += () => OpenAddPoolCounterDialog(ed);
                row.AddChild(edit);

                var del = new Button { Text = "🗑", CustomMinimumSize = new Vector2(28, 24) };
                SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
                var d = def;
                del.Pressed += () =>
                {
                    // 与具名牌组删除同源：cfg 是旧快照，必须现取最新配置。
                    if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latestCtr)) return;
                    var rest = (latestCtr.LeaderCounters ?? new List<KernelV3.Packs.PoolCounterDef>())
                        .Where(x => !ReferenceEquals(x, d) && x.Name != d.Name).ToList();
                    bool okCtr = _store.SavePoolConfigFull(latestCtr.PoolId, latestCtr.Name,
                                             latestCtr.EnabledPackIds, latestCtr.Rules,
                                             latestCtr.NamedDecks, out string m,
                                             latestCtr.PoolClans, rest);
                    _ruleStatus.Text = m;
                    if (okCtr) RefreshPoolCounters();
                };
                row.AddChild(del);
            }
            _poolCounterList.AddChild(row);
        }
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
                // 必须现取最新配置：cfg 是外部闭包捕获的旧快照，
                // 每次 SavePoolConfigFull 后 ReloadCatalogAndPools 都会换新实例。
                // 且要显式带上 PoolClans / LeaderCounters —— 漏传会把
                // 玩家自定义的开放职业与主战者计数器整段清空。
                if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latestNd)) return;

                var rest = new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>();
                foreach (var x in latestNd.NamedDecks ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>())
                    if (!ReferenceEquals(x, capturedNd)) rest.Add(x);

                bool okDel = _store.SavePoolConfigFull(latestNd.PoolId, latestNd.Name,
                                          latestNd.EnabledPackIds, latestNd.Rules, rest,
                                          out string msg,
                                          latestNd.PoolClans, latestNd.LeaderCounters);
                _ruleStatus.Text = msg;
                if (okDel) RefreshNamedDecks();
            };
            row.AddChild(delBtn);

            _namedDeckList.AddChild(row);
        }
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
        WrapDialogContent(dlg, box);

        // 牌组名只有一个。分「英文标识名 + 中文显示名」两套的话，
        // replace_deck 引用的是英文名、卡面显示的是中文名，改名要同步两处。
        var nameRow = new HBoxContainer();
        var nameLb = new Label { Text = "牌组名", CustomMinimumSize = new Vector2(70, 0) };
        nameLb.AddThemeFontSizeOverride("font_size", 12);
        nameRow.AddChild(nameLb);
        var nameEdit = new LineEdit
        {
            Text = string.IsNullOrEmpty(target.DeckNameChs) ? (target.DeckName ?? "") : target.DeckNameChs,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            TooltipText = "牌组名称，供 replace_deck 引用（如 启示录牌组）",
        };
        nameRow.AddChild(nameEdit);
        box.AddChild(nameRow);

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
                DeckNameChs = nameEdit.Text?.Trim() ?? target.DeckNameChs,
                Entries = working,
            };

            // 同 delBtn：cfg 是旧快照，必须现取最新配置；
            // 并显式带上 PoolClans / LeaderCounters，否则保存具名牌组会把
            // 自定义职业与主战者计数器一并清空。
            if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var latestEd)) { dlg.QueueFree(); return; }

            var list = new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>();
            foreach (var x in latestEd.NamedDecks ?? new List<ShadowverseFan.KernelV3.Packs.NamedDeckRawDef>())
                list.Add(ReferenceEquals(x, target) ? newNd : x);

            bool okEd = _store.SavePoolConfigFull(latestEd.PoolId, latestEd.Name,
                                      latestEd.EnabledPackIds, latestEd.Rules, list,
                                      out string msg,
                                      latestEd.PoolClans, latestEd.LeaderCounters);
            _ruleStatus.Text = msg;
            if (okEd) RefreshNamedDecks();
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    /// <summary>「本卡池启用的物理卡包」勾选区。原生包锁死勾选。</summary>
    /// <summary>
    /// 按 _activeRightTab 显示对应区域、隐藏其余。
    ///
    /// 用「隐藏」而不是销毁重建：规则行、计数器行都带输入控件，
    /// 重建会丢掉玩家正在编辑、尚未落盘的内容。
    /// </summary>
    /// <summary>
    /// 切换卡池并**全量刷新**所有依赖它的区域（185/186 号文档）。
    ///
    /// 早先左栏点击只调了 RefreshRules()，于是：
    ///   · 中栏卡包行的归属勾选框还停在上一个卡池的状态；
    ///   · 右栏「具名牌组 / 卡池计数器」显示的全是旧池数据。
    /// 凡是依赖 _selectedPoolId 的区域，都必须在这里一次性刷完。
    /// </summary>
    private void SelectPoolAndRefreshAll(string poolId)
    {
        _selectedPoolId = poolId;
        RefreshPacks();
        RefreshRules();
        RefreshNamedDecks();
        RefreshPoolClans();
        RefreshPoolCounters();
    }

    /// <summary>
    /// 把某个卡包在当前卡池中的启用状态设为 enable，并落盘。
    ///
    /// 186 号文档 缺陷 2 的两个致命点：
    ///   1. 早先从 _enabledPackChecks 字典收集勾选项 —— 取消勾选时字典里
    ///      那条仍是 true，chosen 照样包含它，**根本取消不掉**；
    ///   2. 闭包里捕获的 cfg 在每次 SavePoolConfigFull 后因 ReloadCatalogAndPools
    ///      变成野引用，继续用它写盘会丢数据。
    /// 因此这里每次都以 _selectedPoolId 重新取**最新**配置，
    /// 并在落盘时显式带上 PoolClans / LeaderCounters —— 漏传会把
    /// 玩家自定义的开放职业与主战者计数器整段清空。
    /// </summary>
    private void TogglePackInSelectedPool(string packId, bool enable)
    {
        if (string.IsNullOrEmpty(packId)) return;
        if (string.IsNullOrEmpty(_selectedPoolId))
        {
            _ruleStatus.Text = "请先选中一个卡池";
            return;
        }
        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId, out var latestCfg)) return;

        var chosen = new HashSet<string>(latestCfg.EnabledPackIds ?? new List<string>(),
                                         StringComparer.OrdinalIgnoreCase);
        if (enable) chosen.Add(packId);
        else chosen.Remove(packId);

        // 原生绑定包永远保留，否则卡池会失去自己的底座。
        foreach (var kv in _store.Catalog.Packs)
            if (kv.Value != null && curIsNative(kv.Value, latestCfg))
                chosen.Add(kv.Key);

        if (chosen.Count == 0)
        {
            _ruleStatus.Text = "✘ 至少要启用一个卡包";
            RefreshPacks();
            BuildEnabledPacksSection();
            return;
        }

        bool ok = _store.SavePoolConfigFull(
            latestCfg.PoolId, latestCfg.Name, chosen.ToList(),
            latestCfg.Rules, latestCfg.NamedDecks, out string msg,
            latestCfg.PoolClans, latestCfg.LeaderCounters);

        _ruleStatus.Text = msg;
        if (ok) SelectPoolAndRefreshAll(latestCfg.PoolId);
        else { RefreshPacks(); BuildEnabledPacksSection(); }

        static bool curIsNative(ShadowverseFan.KernelV3.Packs.CardPackDef pack,
                                ShadowverseFan.KernelV3.Packs.CardPoolConfig cfg)
            => string.Equals(pack.NativePoolId, cfg.PoolId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 重填左栏卡池列表。点选即切换当前编辑目标（三栏主从的「主」）。
    /// </summary>
    private void RefreshPoolList()
    {
        if (_poolListBox == null || !IsInstanceValid(_poolListBox)) return;
        foreach (var c in _poolListBox.GetChildren()) { _poolListBox.RemoveChild(c); c.QueueFree(); }

        string cur = _selectedPoolId;
        foreach (var poolId in _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            var cfg = _store.PoolConfigs[poolId];
            bool selected = string.Equals(poolId, cur, StringComparison.OrdinalIgnoreCase);

            var btn = new Button
            {
                Text = (cfg?.Name ?? poolId) + "  [" + poolId + "]",
                CustomMinimumSize = new Vector2(0, 32),
                Alignment = HorizontalAlignment.Left,
                TooltipText = "启用卡包：" + string.Join(", ", cfg?.EnabledPackIds ?? new List<string>()),
            };
            string pid = poolId;
            btn.Pressed += () => SelectPoolAndRefreshAll(pid);
            SvWbTheme.StyleButton(btn, isPrimary: selected, customAccent: SvWbTheme.AccentGold);
            btn.AddThemeFontSizeOverride("font_size", 12);
            _poolListBox.AddChild(btn);
        }
    }

    private void ApplyRightTabVisibility()
    {
        for (int i = 0; i < _tabPages.Count; i++)
            if (_tabPages[i] != null && IsInstanceValid(_tabPages[i]))
                _tabPages[i].Visible = i == _activeRightTab;

        if (_tabButtons != null)
            for (int i = 0; i < _tabButtons.Length; i++)
                if (_tabButtons[i] != null && IsInstanceValid(_tabButtons[i]))
                    SvWbTheme.StyleButton(_tabButtons[i], isPrimary: i == _activeRightTab);
    }

    /// <summary>
    /// 页签 3：卡包归属。
    ///
    /// 185 号文档 专题一：这块原先埋在右栏规则列表最底部，改完还要再点一次
    /// 「应用启用卡包」—— 心智模型割裂。现在独立成页签，且**勾选即存盘**。
    /// </summary>
    private void BuildEnabledPacksSection()
    {
        if (_tabContentHost == null || !IsInstanceValid(_tabContentHost)) return;
        foreach (var c in _tabContentHost.GetChildren()) { _tabContentHost.RemoveChild(c); c.QueueFree(); }

        if (!_store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var cfg))
        {
            _tabContentHost.AddChild(new Label { Text = "（未选中卡池）" });
            return;
        }

        var head = new Label { Text = "本卡池启用的物理卡包（勾选即时生效）" };
        head.AddThemeFontSizeOverride("font_size", 13);
        head.AddThemeColorOverride("font_color", new Color("7DD3FC"));
        _tabContentHost.AddChild(head);

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(scroll);
        _tabContentHost.AddChild(scroll);

        var list = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(list);

        _enabledPackChecks.Clear();
        foreach (var kv in _store.Catalog.Packs.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            string pid = kv.Key;
            bool isNative = string.Equals(kv.Value.NativePoolId, cfg.PoolId, StringComparison.OrdinalIgnoreCase);
            bool on = cfg.EnabledPackIds.Contains(pid, StringComparer.OrdinalIgnoreCase) || isNative;

            var cb = new CheckBox
            {
                Text = kv.Value.Name + "  [" + pid + "]  " + kv.Value.Cards.Count + " 张"
                       + (isNative ? "  🔒原生绑定" : ""),
                ButtonPressed = on,
                Disabled = isNative,
                TooltipText = isNative ? "原生绑定卡包，不可从本卡池移除" : "勾选即把此卡包纳入当前卡池",
            };
            cb.AddThemeFontSizeOverride("font_size", 12);

            // 勾选即时存盘，不再需要「应用」按钮。
            // 与中栏共用同一套逻辑（186 号文档 缺陷 2）。
            cb.Toggled += pressed => { if (!isNative) TogglePackInSelectedPool(pid, pressed); };

            _enabledPackChecks[pid] = cb;
            list.AddChild(cb);
        }
    }


    /// <summary>新建卡池弹窗：填 ID 与显示名，其余用标准规则与 official-sv 起步。</summary>
    /// <summary>
    /// 给 AcceptDialog 套一层受控尺寸，防止内容把窗口撑出屏幕。
    ///
    /// Godot 的 AcceptDialog 会按子节点自动撑大：内部一旦有高组件
    /// （AstCodeEdit、长提示文本），高度就突破 720p，底部 OK 按钮掉出屏幕外。
    /// 这里把内容包进固定高度的 ScrollContainer，并钳死窗口上限。
    /// </summary>
    private static ScrollContainer WrapDialogContent(AcceptDialog dlg, Control content,
                                                     int maxW = 560, int maxH = 260)
    {
        // 186 号文档 缺陷 4：
        // 早先 maxH=320 且 MaxSize=maxH+72=392，而 AcceptDialog 自己的
        // 标题栏 + 内容边距 + OK 按钮通常要 80px 以上，高内容（计数器规则弹窗
        // 里有 6 个输入框 + 150px 的 AstCodeEdit）会把原生 OK 按钮挤出底边。
        //
        // 现在：内容区限高 260、滚到底即可；窗口上限 640 保证不超 720p，
        // 同时留足按钮空间。scroll 必须 ExpandFill，否则会被内容顶开。
        dlg.Size = new Vector2I(maxW, Math.Min(maxH + 90, 620));
        dlg.MaxSize = new Vector2I(maxW, 640);
        var scroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(maxW - 32, Math.Min(maxH, 260)),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(scroll);
        if (content != null)
        {
            content.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            scroll.AddChild(content);
        }
        dlg.AddChild(scroll);
        return scroll;
    }

    private void OpenNewPoolDialog()
    {
        var dlg = new AcceptDialog { Title = "新建卡池", Size = new Vector2I(440, 240) };
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 8);
        WrapDialogContent(dlg, box);

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
                // 统一走 SelectPoolAndRefreshAll：新建卡池后所有依赖 _selectedPoolId
                // 的区域（卡包归属、具名牌组、职业、计数器）必须一起刷新。
                SelectPoolAndRefreshAll(pid);
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
        WrapDialogContent(dlg, box);

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
        // 所属卡池必须是**下拉选择**、没有默认值：
        // 卡包不能脱离卡池存在，随便填一个不存在的 ID 会让那座池当场构建失败。
        var nativePick = new OptionButton { CustomMinimumSize = new Vector2(260, 32) };
        var poolIds = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < poolIds.Count; i++) nativePick.AddItem(poolIds[i], i);
        int curPoolIdx = existing != null ? poolIds.IndexOf(existing.NativePoolId) : poolIds.IndexOf(_selectedPoolId ?? "");
        nativePick.Selected = curPoolIdx >= 0 ? curPoolIdx : 0;
        SvWbTheme.StyleButton(nativePick);
        SvWbTheme.StyleOptionPopup(nativePick);
        var nativeRow = new HBoxContainer();
        nativeRow.AddThemeConstantOverride("separation", 8);
        var nativeLbl = new Label { Text = "所属卡池", CustomMinimumSize = new Vector2(80, 0) };
        nativeLbl.AddThemeFontSizeOverride("font_size", 12);
        nativeRow.AddChild(nativeLbl);
        nativeRow.AddChild(nativePick);
        box.AddChild(nativeRow);

        dlg.Confirmed += () =>
        {
            bool ok = _store.CreateOrUpdatePack(
                idEdit.Text?.Trim(), nameEdit.Text?.Trim(), authorEdit.Text?.Trim(),
                nativePick.Selected >= 0 && nativePick.Selected < poolIds.Count
                    ? poolIds[nativePick.Selected] : "", out string msg);
            _header.Text = "卡包 / 卡池管理 —— " + msg;
            // 新建卡包会改卡池的 enabled_packs，右栏勾选区必须跟着刷，
            // 否则新包已经进了卡池，玩家在界面上却看不到它被勾上。
            if (ok)
            {
                _selectedPackId = idEdit.Text?.Trim();
                RefreshPacks();
                RefreshRules();
                RefreshPoolClans();
                RefreshPoolCounters();
            }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private void RefreshPacks()
    {
        foreach (var c in _packList.GetChildren()) c.QueueFree();

        // 首次构建时 _selectedPoolId 还是 null（RefreshRules 尚未跑），
        // 这里补一次兜底，否则卡包行的归属勾选框会全部处于 Disabled 状态。
        if (string.IsNullOrEmpty(_selectedPoolId) && _store.PoolConfigs.Count > 0)
            _selectedPoolId = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).First();

        _store.PoolConfigs.TryGetValue(_selectedPoolId ?? "", out var curPool);

        foreach (var kv in _store.Catalog.Packs.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var pack = kv.Value;
            string pid = kv.Key;

            // 卡包行 = 归属开关 + 包名。开关**勾选即存盘**（185 号文档 专题一）。
            // 早先归属勾选藏在右栏规则列表最底部，改完还要再点一次「应用」；
            // 现在直接长在卡包行上，三栏主从的心智模型才对得上。
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            bool isNative = curPool != null
                && string.Equals(pack.NativePoolId, curPool.PoolId, StringComparison.OrdinalIgnoreCase);
            bool on = curPool != null
                && (curPool.EnabledPackIds.Contains(pid, StringComparer.OrdinalIgnoreCase) || isNative);

            var toggle = new CheckBox
            {
                ButtonPressed = on,
                Disabled = curPool == null || isNative,
                TooltipText = curPool == null
                    ? "未选中卡池"
                    : (isNative ? "原生绑定卡包，不可从本卡池移除" : "勾选以将此卡包纳入当前卡池（即时生效）"),
            };
            // 统一走 TogglePackInSelectedPool：
            // 它以 _selectedPoolId 现取最新配置，支持**取消勾选**，
            // 并显式带上 PoolClans / LeaderCounters（否则会抹空职业与计数器）。
            toggle.Toggled += pressed => { if (!isNative) TogglePackInSelectedPool(pid, pressed); };
            row.AddChild(toggle);

            var btn = new Button
            {
                Text = (pack.Name ?? pid) + "  [" + pid + "]   " + (pack.Cards?.Count ?? 0) + " 张"
                       + (isNative ? "  🔒" : ""),
                Alignment = HorizontalAlignment.Left,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            SvWbTheme.StyleButton(btn, isPrimary: _selectedPackId == pid);
            btn.AddThemeFontSizeOverride("font_size", 12);
            btn.Pressed += () => { _selectedPackId = pid; ShowPackCards(pid); RefreshPacks(); };
            row.AddChild(btn);

            _packList.AddChild(row);
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

            var clanLbl = new Label { Text = c.Clan ?? "中立", CustomMinimumSize = new Vector2(96, 0) };
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
                // 优先匹配**本卡包**内的卡：两个卡包都可能有 CardId == 950001，
                // 只按卡号查会拿到另一个卡包的卡，点「编辑」打开的是别人的卡。
                // 同时兼容被自动改号后原号存进 ArtCardId 的情况（180 号文档 D12）。
                var def = _store.Catalog.AllCards.Values.FirstOrDefault(x =>
                              string.Equals(x.PackId, packId, StringComparison.OrdinalIgnoreCase)
                              && (x.CardId == capId || x.ArtCardId == capId))
                          ?? _store.Catalog.AllCards.Values.FirstOrDefault(x => x.CardId == capId);
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

        // 左栏卡池列表（三栏主从的「主」）。
        RefreshPoolList();

        // 卡包归属页签跟随卡池切换重建（勾选状态按当前池重算）。
        BuildEnabledPacksSection();


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
        RefreshPoolClans();
        RefreshPoolCounters();
    }
}

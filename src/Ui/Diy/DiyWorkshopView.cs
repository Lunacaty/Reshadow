using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// DIY 四端造卡工坊主界面。
///
/// 左：实时卡面 + 简中预览 + AST；右：四端页签（表单 / 积木 / 脚本 / 英文）。
/// 任一端的编译成功都会把结果投影到 CurrentDef，再反向刷新其余三端 ——
/// 所以切页签时看到的永远是同一张卡的四种表达。
/// </summary>
public partial class DiyWorkshopView : Control
{
    public event Action OnBackToDecks;

    private GameSessionStore _store;
    private DiyWorkshopController _ctrl;

    private DiyEndpointKind _active = DiyEndpointKind.Script;
    private CardVisualWidget _cardPreview;
    private RichTextLabel _chinesePreview;
    private RichTextLabel _astLabel;
    private Label _statusLabel;
    private VBoxContainer _endpointHost;
    /// <summary>积木工作台实例。复用而非每次重建：它内部保存着当前能力域与选中积木。</summary>
    private BlockTreeEditorWidget _blockEditor;
    private AstCodeEdit _astJsonEdit;
    private OptionButton _packPicker;
    private OptionButton _loadPicker;
    private List<ShadowverseFan.KernelV3.Ast.CardDef> _activePoolCardList = new();
    private Control _hover;

    // 表单端可编辑控件（每次切到表单页签重建）
    private LineEdit _formNameEdit;
    private LineEdit _formNameEngEdit;
    private SpinBox _formCostSpin;
    private SpinBox _formAtkSpin;
    private SpinBox _formLifeSpin;
    private CheckBox _formTokenCheck;
    private OptionButton _formKindPicker;
    private OptionButton _formClanPicker;
    private LineEdit _formTraitsEdit;
    private LineEdit _formKeywordsEdit;

    // 英文端可编辑控件
    private LineEdit _engNameEdit;
    private LineEdit _engTraitsEdit;
    private AstCodeEdit _engTextEdit;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        _ctrl = new DiyWorkshopController(store);
        _ctrl.OnCardUpdated += RefreshAll;
        BuildUi();
        RefreshAll();
    }

    /// <summary>
    /// 直接打开某张卡进行编辑（供「卡包 / 卡池管理」跳转过来）。
    /// 先正常 Boot 建好界面，再把这张卡装进控制器。
    /// </summary>
    public void BootForCard(GameSessionStore store, ShadowverseFan.KernelV3.Ast.CardDef card)
    {
        Boot(store);
        if (card == null) return;
        _ctrl.LoadExistingCard(card);
        RefreshAll();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var root = new VBoxContainer();
        root.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        root.AddThemeConstantOverride("separation", 0);
        AddChild(root);

        root.AddChild(BuildTopBar());

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 8);
        root.AddChild(body);

        body.AddChild(BuildLeftPane());
        body.AddChild(BuildRightPane());
    }

    private Control BuildTopBar()
    {
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 52) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));

        // 外层 MarginContainer 保证最右侧按钮离窗口右缘始终有 16px 安全距离，
        // 否则「保存进卡包」会被前面几个控件的固有宽度顶出屏幕。
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 16);
        margin.AddThemeConstantOverride("margin_right", 16);
        margin.AddThemeConstantOverride("margin_top", 4);
        margin.AddThemeConstantOverride("margin_bottom", 4);
        bar.AddChild(margin);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        margin.AddChild(row);

        var back = new Button { Text = "← 返回牌组一览" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBackToDecks?.Invoke();
        row.AddChild(back);

        var title = new Label { Text = "🛠 DIY 造卡工坊", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 18);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        row.AddChild(title);

        // 当前卡池选择器（165 专题二断链 D）。
        // 工坊不是无环境的造卡器：卡池里定义的自定义职业、主战者计数器、
        // 卡名对照都必须在造卡时拿得到，否则造出的卡在自己的池子里编译不过。
        // 没有这个下拉框，BindPool 会永远停在默认卡池。
        var poolPicker = new OptionButton { CustomMinimumSize = new Vector2(150, 32) };
        SvWbTheme.StyleButton(poolPicker);
        SvWbTheme.StyleOptionPopup(poolPicker);
        var poolIds = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int pi = 0; pi < poolIds.Count; pi++)
        {
            var pcfg = _store.PoolConfigs[poolIds[pi]];
            poolPicker.AddItem("卡池: " + (string.IsNullOrWhiteSpace(pcfg.Name) ? poolIds[pi] : pcfg.Name), pi);
        }
        int activeIdx = poolIds.IndexOf(_ctrl.ActivePoolId);
        if (activeIdx >= 0) poolPicker.Select(activeIdx);
        poolPicker.ItemSelected += idx =>
        {
            int k = (int)idx;
            if (k < 0 || k >= poolIds.Count) return;
            _ctrl.BindPool(poolIds[k]);
            // 卡池换了，目标卡包与可加载卡牌都必须跟着换 ——
            // 留着旧列表会让玩家往新池子里存一个它根本没有的卡包。
            // 断链 4：切换卡池后积木端必须重建。
            // RefreshEndpoint 开头有「已有实例就 return」的复用逻辑（为了保住选中态），
            // 但换卡池时那棵旧树的词条是旧卡池的，必须主动弃掉。
            if (_blockEditor != null && IsInstanceValid(_blockEditor))
            {
                _blockEditor.QueueFree();
                _blockEditor = null;
            }
            FillPackPicker();
            FillLoadPicker();
            RefreshAll();
        };
        row.AddChild(poolPicker);

        // 从当前卡池导入一张真卡，看它的四端投影。
        // 提升为成员：切换卡池后要能重填，否则列表还停在旧池子的卡上。
        _loadPicker = new OptionButton { CustomMinimumSize = new Vector2(180, 32) };
        SvWbTheme.StyleButton(_loadPicker);
        SvWbTheme.StyleOptionPopup(_loadPicker);
        FillLoadPicker();
        _loadPicker.ItemSelected += idx =>
        {
            int k = (int)idx;
            if (k <= 0 || k - 1 >= _activePoolCardList.Count) return;
            _ctrl.LoadExistingCard(_activePoolCardList[k - 1]);
            // 换了一张卡：工作台必须按新模型重建。
            // RefreshEndpoint 现在会复用实例（保住选中态），所以这里先主动弃掉它。
            if (_blockEditor != null && IsInstanceValid(_blockEditor))
            {
                _blockEditor.QueueFree();
                _blockEditor = null;
            }
            RefreshAll();
            if (_statusLabel != null) _statusLabel.Text = _ctrl.LastStatusMessage;
        };
        row.AddChild(_loadPicker);

        var defineBtn = new Button { Text = "➕ 注册词条", CustomMinimumSize = new Vector2(120, 32) };
        SvWbTheme.StyleButton(defineBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        defineBtn.Pressed += ShowDefineDialog;
        row.AddChild(defineBtn);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var packLbl = new Label { Text = "目标卡包:" };
        packLbl.AddThemeFontSizeOverride("font_size", 12);
        packLbl.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(packLbl);

        _packPicker = new OptionButton { CustomMinimumSize = new Vector2(130, 32) };
        SvWbTheme.StyleButton(_packPicker);
        SvWbTheme.StyleOptionPopup(_packPicker);
        FillPackPicker();
        row.AddChild(_packPicker);

        var saveBtn = new Button { Text = "💾 保存进卡包", CustomMinimumSize = new Vector2(130, 32) };
        SvWbTheme.StyleButton(saveBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        saveBtn.Pressed += OnSave;
        row.AddChild(saveBtn);

        return bar;
    }


    // ---------------------------------------------------------------- 立绘上传（126 专题三）

    /// <summary>
    /// 立绘上传按钮组：进化前 / 进化后 / 异画 I / 异画 I 进化后。
    ///
    /// 落盘目标固定为**目标卡包自己的目录** data/packs/&lt;packId&gt;/cards/，
    /// 这样创作者把整个卡包文件夹打包发出去，素材就跟着走，绝不污染全局图库。
    /// </summary>
    private Control BuildArtUploadBar()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);

        var head = new Label { Text = "卡面立绘" };
        head.AddThemeFontSizeOverride("font_size", 12);
        head.AddThemeColorOverride("font_color", new Color("FDE68A"));
        box.AddChild(head);

        var flow = new HFlowContainer();
        flow.AddThemeConstantOverride("h_separation", 6);
        flow.AddThemeConstantOverride("v_separation", 4);
        AddArtUploadBtn(flow, "🖼 上传进化前", "");
        AddArtUploadBtn(flow, "🔥 上传进化后", "_e");
        AddArtUploadBtn(flow, "🎨 上传异画", "_3");
        AddArtUploadBtn(flow, "✨ 上传异画进化", "_4");
        box.AddChild(flow);

        return box;
    }

    private void AddArtUploadBtn(Control host, string label, string suffix)
    {
        var btn = new Button { Text = label, CustomMinimumSize = new Vector2(0, 28) };
        SvWbTheme.StyleButton(btn);
        btn.Pressed += () => OpenImageFileDialog(suffix);
        host.AddChild(btn);
    }

    private void OpenImageFileDialog(string suffix)
    {
        if (_ctrl?.CurrentDef == null)
        {
            if (_statusLabel != null) _statusLabel.Text = "✘ 请先加载或新建一张卡";
            return;
        }

        var dlg = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.png, *.jpg, *.jpeg, *.webp ; 图片文件" },
            Title = "选择卡牌立绘图片",
            Size = new Vector2I(760, 480),
        };
        dlg.FileSelected += srcPath =>
        {
            try
            {
                var img = Image.LoadFromFile(srcPath);
                if (img == null || img.IsEmpty())
                {
                    if (_statusLabel != null) _statusLabel.Text = "✘ 图片加载失败";
                    dlg.QueueFree();
                    return;
                }

                string packId = (_packPicker != null && _packPicker.ItemCount > 0)
                    ? _packPicker.GetItemText(_packPicker.Selected) : "testset-v2";

                // 用 ArtCardId（源素材原始卡号）命名，跨创作者撞号时各读各的目录。
                int cid = _ctrl.CurrentDef.ArtCardId > 0
                    ? _ctrl.CurrentDef.ArtCardId : _ctrl.CurrentDef.CardId;

                string packCardsDir = System.IO.Path.Combine(
                    ShadowverseFan.Ui.ContentPaths.Root, "data", "packs", packId, "cards");
                System.IO.Directory.CreateDirectory(packCardsDir);
                string dstPath = System.IO.Path.Combine(packCardsDir, cid + suffix + ".png");
                img.SavePng(dstPath);

                // 清缓存让新图立刻生效；异画顺带把该卡偏好切到这一版。
                ShadowverseFan.Ui.CardArtLoader.ClearCache();
                if (suffix == "_3") ShadowverseFan.Ui.CardArtLoader.SetPreferredVariant(cid, 3);
                if (suffix == "_4") ShadowverseFan.Ui.CardArtLoader.SetPreferredVariant(cid, 3);

                RefreshAll();
                if (_statusLabel != null) _statusLabel.Text = "✔ 已保存立绘至 " + dstPath;
            }
            catch (Exception ex)
            {
                if (_statusLabel != null) _statusLabel.Text = "✘ 保存立绘出错：" + ex.Message;
            }
            dlg.QueueFree();
        };
        dlg.Canceled += () => dlg.QueueFree();
        AddChild(dlg);
        dlg.PopupCentered();
    }

    private Control BuildLeftPane()
    {
        var pane = new VBoxContainer { CustomMinimumSize = new Vector2(350, 0) };
        pane.AddThemeConstantOverride("separation", 6);

        var holder = new CenterContainer { CustomMinimumSize = new Vector2(0, 280) };
        pane.AddChild(holder);

        _cardPreview = new CardVisualWidget();
        holder.AddChild(_cardPreview);

        var zhTitle = new Label { Text = "官方级简中卡面预览" };
        zhTitle.AddThemeFontSizeOverride("font_size", 13);
        zhTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        pane.AddChild(zhTitle);

        _chinesePreview = new RichTextLabel
        {
            BbcodeEnabled = true,
            CustomMinimumSize = new Vector2(0, 120),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _chinesePreview.AddThemeFontSizeOverride("normal_font_size", 12);
        pane.AddChild(_chinesePreview);

        // 立绘上传（126 专题三）：落盘到目标卡包自己的 cards/ 目录。
        pane.AddChild(BuildArtUploadBar());

        var astTitle = new Label { Text = "AST 规范形" };
        astTitle.AddThemeFontSizeOverride("font_size", 13);
        astTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
        pane.AddChild(astTitle);

        // RichTextLabel 才能把 #卡号 渲染成高亮卡名胶囊（179 号文档 C3）。
        // 裸 Label 只会原样打印 #90011110，玩家完全看不出那是什么卡。
        _astLabel = new RichTextLabel
        {
            BbcodeEnabled = true,
            // 必须开滚动：带纹章 / 模式分支的卡，AST 有 15~25 行，
            // FitContent=true 会把整个左栏顶出 720p 屏幕下沿（180 号文档 D10）。
            FitContent = false,
            ScrollActive = true,
            CustomMinimumSize = new Vector2(0, 110),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _astLabel.AddThemeFontSizeOverride("normal_font_size", 11);
        _astLabel.AddThemeColorOverride("default_color", SvWbTheme.TextMuted);
        pane.AddChild(_astLabel);

        return pane;
    }

    private Control BuildRightPane()
    {
        var pane = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        pane.AddThemeConstantOverride("separation", 6);

        var tabs = new HBoxContainer();
        tabs.AddThemeConstantOverride("separation", 6);
        pane.AddChild(tabs);

        AddEndpointTab(tabs, "① 表单向导端", DiyEndpointKind.Form);
        AddEndpointTab(tabs, "② Scratch 积木", DiyEndpointKind.Block);
        // ③ 端在 204/206 号文档落地后换成了 PyAST DSL（规范 AST 语法），
        // 与卡包 source_format: "py_ast" 共用同一份打印器与解析器。
        AddEndpointTab(tabs, "③ AST 规范语法", DiyEndpointKind.Script);
        AddEndpointTab(tabs, "④ 官方英文", DiyEndpointKind.EnglishCnl);

        // 不再给 _endpointHost 套外层 ScrollContainer：
        // 三栏（调色盘 / 画布 / 属性检视器）在 BlockTreeEditorWidget 内部各自独立滚动，
        // 右栏必须钉死在视口右侧。再套一层总滚动，画布滚到底部时
        // 属性面板会跟着滚出视野，玩家改属性还得拉回顶部。
        var hostBox = new VBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        pane.AddChild(hostBox);

        _endpointHost = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _endpointHost.AddThemeConstantOverride("separation", 6);
        hostBox.AddChild(_endpointHost);

        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 46) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.BorderSubtle, 6, 1, 0));
        pane.AddChild(bar);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        var compileBtn = new Button { Text = "🔄 立即编译并同步四端" };
        SvWbTheme.StyleButton(compileBtn, isPrimary: true);
        compileBtn.Pressed += OnCompileCurrent;
        row.AddChild(compileBtn);

        var isoBtn = new Button { Text = "🧪 四端同构自检" };
        SvWbTheme.StyleButton(isoBtn, customAccent: SvWbTheme.AccentPurple);
        isoBtn.Pressed += OnVerify;
        row.AddChild(isoBtn);

        _statusLabel = new Label { Text = "就绪" };
        _statusLabel.AddThemeFontSizeOverride("font_size", 12);
        _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(_statusLabel);

        return pane;
    }

    /// <summary>四端页签按钮。切换时必须刷新全部按钮的高亮，
    /// 否则会出现「点在积木端、亮着的却是 AST 端」的错位。</summary>
    private readonly List<(Button Btn, DiyEndpointKind Kind)> _endpointTabBtns = new();

    private void AddEndpointTab(HBoxContainer host, string caption, DiyEndpointKind kind)
    {
        var b = new Button { Text = caption };
        SvWbTheme.StyleButton(b, isPrimary: _active == kind);
        b.Pressed += () =>
        {
            _active = kind;
            foreach (var (btn, k) in _endpointTabBtns)
                if (IsInstanceValid(btn)) SvWbTheme.StyleButton(btn, isPrimary: _active == k);
            RefreshEndpoint();
        };
        host.AddChild(b);
        _endpointTabBtns.Add((b, kind));
    }

    // ---------------------------------------------------------------- 刷新

    /// <summary>
    /// 当前卡池可见的卡。
    ///
    /// 从全局总表列卡会让玩家把别的卡池里的卡拉进当前池子，
    /// 造出的卡引用了一个本池子根本没有的卡号，还不报错。
    /// </summary>
    private List<ShadowverseFan.KernelV3.Ast.CardDef> CurrentPoolCards()
    {
        try
        {
            var rt = _store.GetPoolRuntime(_ctrl.ActivePoolId);
            if (rt != null)
                return rt.AllCards.Values.OrderBy(c => c.CardId).ToList();
        }
        catch (Exception) { }
        return _store.Catalog.AllCards.Values.OrderBy(c => c.CardId).ToList();
    }


    /// <summary>
    /// 重填「目标卡包」下拉框，只列当前卡池启用的卡包。
    ///
    /// 写死成全表会让玩家把卡存进一个当前池子没启用的卡包 ——
    /// 存进去了，但立刻从池子里消失，而且不报错。
    /// </summary>

    /// <summary>
    /// 重填「加载现有卡牌」下拉框，只列当前卡池的卡。
    ///
    /// 切池后不重填的话，列表还停在旧池子的卡上：点进去会把一个
    /// 当前池子根本没有的卡拉进工坊，之后怎么都编译不过。
    /// </summary>
    private void FillLoadPicker()
    {
        if (_loadPicker == null || !IsInstanceValid(_loadPicker)) return;
        _loadPicker.Clear();
        _loadPicker.AddItem("📂 加载现有卡牌…", 0);

        _activePoolCardList = CurrentPoolCards();
        for (int ci = 0; ci < _activePoolCardList.Count; ci++)
        {
            var cd = _activePoolCardList[ci];
            string nm = string.IsNullOrWhiteSpace(cd.NameChs) ? (cd.Name ?? "") : cd.NameChs;
            _loadPicker.AddItem(cd.CardId + " " + nm, ci + 1);
        }
    }

    private void FillPackPicker()
    {
        if (_packPicker == null || !IsInstanceValid(_packPicker)) return;
        _packPicker.Clear();
        _packPicker.AddItem("（选择卡包）", 0);

        var ids = new List<string>();
        try
        {
            var rt = _store.GetPoolRuntime(_ctrl.ActivePoolId);
            if (rt != null) ids.AddRange(rt.EnabledPackIds);
        }
        catch (Exception) { }
        if (ids.Count == 0) ids.AddRange(_store.Catalog.Packs.Keys);

        int i = 1;
        foreach (var pid in ids.Where(x => !string.IsNullOrWhiteSpace(x))
                               .Distinct(StringComparer.OrdinalIgnoreCase)
                               .OrderBy(x => x, StringComparer.Ordinal))
            _packPicker.AddItem(pid, i++);
    }

    private void RefreshAll()
    {
        if (_ctrl?.CurrentDef == null) return;

        var def = _ctrl.CurrentDef;
        _cardPreview.Setup(def, _store.CardPrinter, 0, 3, showCopyPips: false);
        _cardPreview.SetCardSize(192, 254);

        _chinesePreview.Text = SvWbTheme.FormatRichCardText(_ctrl.GetChineseCardText());
        // 走显示层转换：数据仍是 #卡号，只有渲染成卡名。
        var cardLookup = new Func<int, ShadowverseFan.KernelV3.Ast.CardDef>(cid =>
        {
            try { return _store?.Catalog?.TryGetCard(cid); }
            catch { return null; }
        });
        _astLabel.Text = SvWbTheme.FormatAstWithCardNames(
            _ctrl.GetCanonicalAstText(), cardLookup);
        _statusLabel.Text = _ctrl.LastStatusMessage;

        RefreshEndpoint();
    }

    private void RefreshEndpoint()
    {
        // 若正停在积木端且工作台还在树里，绝不销毁重建 ——
        // 它内部保存着当前能力域页签与选中积木，重建一次全丢。
        // 而镶嵌器每改一个孔位都会触发 OnCardUpdated -> RefreshAll -> 这里。
        if (_active == DiyEndpointKind.Block
            && _blockEditor != null && IsInstanceValid(_blockEditor)
            && _blockEditor.GetParent() == _endpointHost)
        {
            return;
        }

        foreach (var c in _endpointHost.GetChildren()) c.QueueFree();
        _blockEditor = null;

        switch (_active)
        {
            case DiyEndpointKind.Script: BuildScriptEndpoint(); break;
            case DiyEndpointKind.Form: BuildFormEndpoint(); break;
            case DiyEndpointKind.Block: BuildBlockEndpoint(); break;
            case DiyEndpointKind.EnglishCnl: BuildEnglishEndpoint(); break;
        }
    }

    /// <summary>
    /// ③ AST 规范语法端：直接编写 PyAST DSL 的**效果块**（纯能力列表，无卡头）。
    ///
    /// 204/206 号文档落地后，这一端从旧的 ReshadowScript 效果块换成了规范 AST 语法：
    /// 打印用 AstCanonicalPrinter、解析用 AstCanonicalParser.ParseAbilities，
    /// 与卡包 source_format: "py_ast" 共用同一份语法定义。
    /// 模板按钮里的每一段都能被真实解析 —— 点进去直接编译即可通过。
    /// </summary>
    private void BuildScriptEndpoint()
    {
        var hint = new Label
        {
            Text = "直接编写卡片的规范 AST 效果块（Python 风格缩进，游戏术语）。" +
                   "卡名/费用/身材沿用上方卡头，这里只写能力。点下方模板可一键插入：",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _endpointHost.AddChild(hint);

        var tplBar = new HFlowContainer();
        tplBar.AddThemeConstantOverride("h_separation", 6);
        tplBar.AddThemeConstantOverride("v_separation", 4);
        _endpointHost.AddChild(tplBar);

        AddAstSnippetBtn(tplBar, "入场曲",
            "fanfare:\n    deal 3 damage to opponent:board Choose(1) where is Follower\n");

        AddAstSnippetBtn(tplBar, "谢幕曲",
            "last_words:\n    draw 1\n");

        AddAstSnippetBtn(tplBar, "进化",
            "evolve:\n    buff $self +2/+2\n");

        AddAstSnippetBtn(tplBar, "超进化",
            "super_evolve:\n    destroy opponent:board All where is Follower\n");

        AddAstSnippetBtn(tplBar, "常驻光环",
            "aura:\n    grant_keyword ward to $self\n");

        AddAstSnippetBtn(tplBar, "抽牌",
            "draw 2");

        AddAstSnippetBtn(tplBar, "加身材",
            "buff $self +2/+2");

        AddAstSnippetBtn(tplBar, "授予关键词",
            "grant_keyword ward to $self");

        AddAstSnippetBtn(tplBar, "选择目标",
            "select opponent:board Choose(1) where is Follower");

        AddAstSnippetBtn(tplBar, "条件分支",
            "if ctr:pp_at_play >= 7:\n    draw 2\nelse:\n    draw 1");

        AddAstSnippetBtn(tplBar, "模式选择",
            "modes Choose(pick=1):\n    option 1:\n        restore 4 life to self:leader All\n    option 2:\n        draw 2");

        AddAstSnippetBtn(tplBar, "召唤",
            "summon 1 \"Fairy\"");

        AddAstSnippetBtn(tplBar, "授予子能力",
            "grant_ability to $self:\n    last_words:\n        draw 1");

        // 升级为 CodeEdit：行号 + 语法高亮 + # 搜卡候选栏（179 号文档 C4）。
        _astJsonEdit = new AstCodeEdit
        {
            Text = _ctrl.AstJsonText,
            CustomMinimumSize = new Vector2(0, 360),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.None,
            CardSource = BuildCardSourceForIde(),
        };
        _astJsonEdit.AddThemeFontSizeOverride("font_size", 13);
        _astJsonEdit.SetupIdeFeatures();
        _endpointHost.AddChild(_astJsonEdit);
    }

    /// <summary>
    /// # 搜卡候选栏的数据源：当前激活卡池的全部卡（含衍生），
    /// 同时提供中文名与英文名供模糊匹配（179 号文档 C4）。
    /// </summary>
    private Func<IEnumerable<(int Id, string Zh, string En, bool IsToken)>> BuildCardSourceForIde()
    {
        return () =>
        {
            try
            {
                return CurrentPoolCards().Select(c => (
                    c.CardId,
                    !string.IsNullOrWhiteSpace(c.NameChs) ? c.NameChs : (c.Name ?? ""),
                    c.Name ?? "",
                    c.IsToken));
            }
            catch { return Enumerable.Empty<(int, string, string, bool)>(); }
        };
    }

    private void AddAstSnippetBtn(Control host, string label, string snippet)
    {
        var b = new Button { Text = "+ " + label, CustomMinimumSize = new Vector2(0, 28) };
        SvWbTheme.StyleButton(b);
        b.Pressed += () =>
        {
            if (_astJsonEdit == null || !IsInstanceValid(_astJsonEdit)) return;
            _astJsonEdit.InsertTextAtCaret(snippet);
        };
        host.AddChild(b);
    }
    private void BuildFormEndpoint()
    {
        var def = _ctrl.CurrentDef;
        if (def == null) return;

        if (_ctrl.FormIsReadOnly)
        {
            var warn = new PanelContainer();
            warn.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                new Color(0.20f, 0.12f, 0.04f, 0.9f), SvWbTheme.StatusWarn, 8, 1, 2));
            var wl = new Label
            {
                Text = "⚠ 本卡有超出表单表达能力的效果（下列基础属性仍可改，" +
                       "但效果只能在脚本端/积木端编辑）：\n" + _ctrl.FormReadOnlyReason,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            wl.AddThemeFontSizeOverride("font_size", 12);
            wl.AddThemeColorOverride("font_color", new Color("FDE68A"));
            warn.AddChild(wl);
            _endpointHost.AddChild(warn);
        }

        var fm = _ctrl.FormModel;
        if (fm == null) { _endpointHost.AddChild(new Label { Text = "（表单模型为空）" }); return; }

        // 可编辑：这些是表单端真正能改的字段
        // 卡名只有一个。分中英两个输入框等于存两套名字，
        // 玩家改了一个另一个还是旧的，卡面上到底显示哪个全看谁最后写。
        _formNameEdit = new LineEdit { Text = fm.NameChs ?? fm.NameEng ?? "", CustomMinimumSize = new Vector2(260, 32) };
        _endpointHost.AddChild(EditRow("卡牌名称", _formNameEdit));

        _formCostSpin = MakeSpin(fm.Cost, 0, 30);
        _endpointHost.AddChild(EditRow("基础费用", _formCostSpin));

        _formAtkSpin = MakeSpin(fm.Attack, 0, 99);
        _endpointHost.AddChild(EditRow("攻击力", _formAtkSpin));

        _formLifeSpin = MakeSpin(fm.Life, 0, 99);
        _endpointHost.AddChild(EditRow("生命值", _formLifeSpin));

        _formKindPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 32) };
        _formKindPicker.AddItem("随从", 0);
        _formKindPicker.AddItem("法术", 1);
        _formKindPicker.AddItem("护符", 2);
        _formKindPicker.Selected = fm.Kind == "Spell" ? 1 : fm.Kind == "Amulet" ? 2 : 0;
        SvWbTheme.StyleButton(_formKindPicker);
        SvWbTheme.StyleOptionPopup(_formKindPicker);
        _endpointHost.AddChild(EditRow("卡种", _formKindPicker));

        _formClanPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 32) };
        // 职业列表从当前卡池的 Schema 读，不写死：
        // 卡池可以自定义职业，写死一张 8 职业表会让自定义职业永远选不到。
        // 职业名在卡包里已本地化（就是「皇家」这种显示名），直接显示即可。
        var clans = new List<string>();
        foreach (var cl in _ctrl.Workspace.Schema.Clans.All.OrderBy(x => x.Name, StringComparer.Ordinal))
            if (!string.IsNullOrWhiteSpace(cl.Name)) clans.Add(cl.Name);
        if (clans.Count == 0) clans.Add("中立");
        for (int i = 0; i < clans.Count; i++) _formClanPicker.AddItem(clans[i], i);
        int ci = clans.IndexOf(fm.Clan);
        _formClanPicker.Selected = ci >= 0 ? ci : 0;
        SvWbTheme.StyleButton(_formClanPicker);
        SvWbTheme.StyleOptionPopup(_formClanPicker);
        _endpointHost.AddChild(EditRow("职业", _formClanPicker));

        // 兵种 / 关键词：文本编辑，逗号分隔
        _formTraitsEdit = new LineEdit
        {
            Text = string.Join(", ", fm.Traits ?? new List<string>()),
            CustomMinimumSize = new Vector2(260, 32),
            PlaceholderText = "逗号分隔，如 士兵, 指挥官",
        };
        _endpointHost.AddChild(EditRow("兵种", _formTraitsEdit));

        _formKeywordsEdit = new LineEdit
        {
            Text = string.Join(", ", fm.Keywords ?? new List<string>()),
            CustomMinimumSize = new Vector2(260, 32),
            PlaceholderText = "逗号分隔，如 ward, storm",
        };
        _endpointHost.AddChild(EditRow("关键词", _formKeywordsEdit));

        // Token 标记：决定这张卡能否进构筑。
        // Token 只能由效果召唤 / 生成，不能直接放进牌组。
        _formTokenCheck = new CheckBox { Text = "Token（衍生卡，不能进构筑）", ButtonPressed = fm.IsToken };
        _formTokenCheck.AddThemeFontSizeOverride("font_size", 12);
        _endpointHost.AddChild(EditRow("卡牌属性", _formTokenCheck));

        _endpointHost.AddChild(new Label
        {
            Text = "构筑修正 " + (def.DeckModifiers?.Count ?? 0) + " 条 / 能力 "
                   + (def.Abilities?.Count ?? 0) + " 条（这两类请在脚本端编辑）",
        });
    }

    private static SpinBox MakeSpin(int value, int min, int max)
    {
        var s = new SpinBox { MinValue = min, MaxValue = max, Value = value,
                             CustomMinimumSize = new Vector2(120, 32) };
        return s;
    }

    private static Control EditRow(string label, Control editor)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var a = new Label { Text = label, CustomMinimumSize = new Vector2(120, 0) };
        a.AddThemeFontSizeOverride("font_size", 12);
        a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(a);

        row.AddChild(editor);
        return row;
    }

    /// <summary>把表单端的编辑结果写回 FormModel，再交给控制器编译。</summary>
    private bool ApplyFormEdits()
    {
        var fm = _ctrl.FormModel;
        if (fm == null) return false;

        // 一个名字写进两个字段：NameChs 是显示名，NameEng 是同值（旧字段，卡名查询用）。
        string nm = _formNameEdit?.Text ?? fm.NameChs;
        fm.NameChs = nm;
        fm.NameEng = nm;
        fm.Cost = _formCostSpin != null ? (int)_formCostSpin.Value : fm.Cost;
        fm.Attack = _formAtkSpin != null ? (int)_formAtkSpin.Value : fm.Attack;
        fm.Life = _formLifeSpin != null ? (int)_formLifeSpin.Value : fm.Life;
        fm.Kind = _formKindPicker?.Selected switch { 1 => "Spell", 2 => "Amulet", _ => "Follower" };
        if (_formClanPicker != null && _formClanPicker.ItemCount > 0)
            fm.Clan = _formClanPicker.GetItemText(_formClanPicker.Selected);

        fm.Traits = SplitList(_formTraitsEdit?.Text);
        fm.Keywords = SplitList(_formKeywordsEdit?.Text);
        if (_formTokenCheck != null) fm.IsToken = _formTokenCheck.ButtonPressed;

        return _ctrl.CompileFromForm(fm);
    }

    private static List<string> SplitList(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        return s.Split(new[] { ',', '，', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
    }
    private void BuildBlockEndpoint()
    {
        if (_ctrl.BlockModel == null)
        {
            _endpointHost.AddChild(new Label { Text = "（积木模型为空）" });
            return;
        }

        var hint = new Label
        {
            Text = "五彩积木树。事件帽下可增删动作块 —— 改完点下方【立即编译并同步四端】。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _endpointHost.AddChild(hint);

        var tree = new BlockTreeEditorWidget();
        // 先绑控制器再 Build：三栏工作台的调色盘、全树增删改、
        // 孔位镶嵌器全部要经控制器落盘并重编译。
        tree.BindController(_ctrl);
        tree.Build(_ctrl.BlockModel, _store.CardPrinter, editable: true);
        _blockEditor = tree;
        _endpointHost.AddChild(tree);
    }


    private bool TryLocate(Block blk, out int hatIndex, out int slotIndex)
    {
        hatIndex = -1; slotIndex = -1;
        var hats = _ctrl.BlockModel?.Hats;
        if (hats == null || blk == null) return false;

        for (int h = 0; h < hats.Count; h++)
        {
            int si = hats[h].Stacks?.IndexOf(blk) ?? -1;
            if (si >= 0) { hatIndex = h; slotIndex = si; return true; }
        }
        return false;
    }
    private void BuildEnglishEndpoint()
    {
        var spec = _ctrl.CnlSpec;
        if (spec == null) { _endpointHost.AddChild(new Label { Text = "（英文规格为空）" }); return; }

        var hint = new Label
        {
            Text = "官方英文卡面。可编辑后点【立即编译并同步四端】；也可点下方模板按钮插入常用句式。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _endpointHost.AddChild(hint);

        // 模板插入按钮：让玩家不用背英文语法
        var tplRow = new HBoxContainer();
        tplRow.AddThemeConstantOverride("separation", 6);
        _endpointHost.AddChild(tplRow);

        AddTemplateButton(tplRow, "Fanfare: draw a card.");
        AddTemplateButton(tplRow, "Fanfare: deal 2 damage to an enemy follower.");
        AddTemplateButton(tplRow, "Ward");
        AddTemplateButton(tplRow, "Last Words: draw a card.");

        _engNameEdit = new LineEdit { Text = spec.Name ?? "", CustomMinimumSize = new Vector2(300, 32) };
        _endpointHost.AddChild(EditRow("Cardsheet Name", _engNameEdit));

        _engTraitsEdit = new LineEdit
        {
            Text = string.Join(", ", spec.Traits ?? new List<string>()),
            CustomMinimumSize = new Vector2(300, 32),
            PlaceholderText = "逗号分隔，如 士兵, 指挥官",
        };
        _endpointHost.AddChild(EditRow("Traits", _engTraitsEdit));

        var txtTitle = new Label { Text = "Official English Text" };
        txtTitle.AddThemeFontSizeOverride("font_size", 13);
        txtTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        _endpointHost.AddChild(txtTitle);

        // 英文端同样是 CodeEdit：英文 CNL 里也写 #卡号，同样需要搜卡栏（179 号文档 C4）。
        _engTextEdit = new AstCodeEdit
        {
            Text = spec.EngText ?? "",
            CustomMinimumSize = new Vector2(0, 240),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CardSource = BuildCardSourceForIde(),
        };
        _engTextEdit.AddThemeFontSizeOverride("font_size", 13);
        _engTextEdit.SetupIdeFeatures();
        _endpointHost.AddChild(_engTextEdit);
    }

    private void AddTemplateButton(HBoxContainer host, string snippet)
    {
        var b = new Button { Text = snippet.Length > 26 ? snippet.Substring(0, 24) + "…" : snippet };
        b.TooltipText = snippet;
        SvWbTheme.StyleButton(b);
        b.Pressed += () =>
        {
            if (_engTextEdit == null) return;
            string cur = _engTextEdit.Text ?? "";
            _engTextEdit.Text = string.IsNullOrWhiteSpace(cur) ? snippet : cur.TrimEnd() + "\n" + snippet;
        };
        host.AddChild(b);
    }

    /// <summary>把英文端的编辑结果写回 CnlSpec 再交给控制器编译。</summary>
    private bool ApplyEnglishEdits()
    {
        var spec = _ctrl.CnlSpec;
        if (spec == null) return false;

        var rebuilt = new ShadowverseFan.KernelV3.Diy.DiyWorkspace.CnlCardSpec
        {
            CardId = spec.CardId,
            Name = _engNameEdit?.Text ?? spec.Name,
            NameChs = spec.NameChs,
            Kind = spec.Kind,
            Cost = spec.Cost,
            Attack = spec.Attack,
            Life = spec.Life,
            Clan = spec.Clan,
            Traits = SplitList(_engTraitsEdit?.Text),
            EngText = _engTextEdit?.Text ?? spec.EngText,
        };

        return _ctrl.CompileFromEnglishCnl(rebuilt);
    }
    private static Control Row(string label, string value)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var a = new Label { Text = label, CustomMinimumSize = new Vector2(140, 0) };
        a.AddThemeFontSizeOverride("font_size", 12);
        a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(a);

        var b = new Label { Text = value };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        row.AddChild(b);
        return row;
    }

    // ---------------------------------------------------------------- 操作

    private void OnCompileCurrent()
    {
        bool ok = false;
        switch (_active)
        {
            case DiyEndpointKind.Script:
                // ③ 端吃 PyAST DSL 的效果块（纯能力列表，无卡头）
                if (_astJsonEdit != null) ok = _ctrl.CompileFromCanonicalAst(_astJsonEdit.Text);
                break;
            case DiyEndpointKind.Form:
                ok = ApplyFormEdits();
                break;
            case DiyEndpointKind.EnglishCnl:
                ok = ApplyEnglishEdits();
                break;
            case DiyEndpointKind.Block:
                // 积木端是可编辑的（增删移动作块），必须真的重新编译。
                // 少了这一支，点底部编译按钮只会提示「只读投影」。
                ok = _ctrl.CompileFromBlocks(_ctrl.BlockModel);
                break;
            default:
                RefreshAll();
                _statusLabel.Text = "当前端为只读投影，已从 AST 重投影";
                return;
        }

        if (!ok)
        {
            _statusLabel.Text = "✘ " + string.Join("；", _ctrl.LastErrors.Take(3));
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusDanger);
        }
        else
        {
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
            RefreshAll();
        }
    }

    private void OnVerify()
    {
        bool ok = _ctrl.VerifyRoundTripIsomorphism(out string diff);
        if (ok)
        {
            _statusLabel.Text = "✔ 四端同构校验 PASS";
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
        }
        else
        {
            _statusLabel.Text = "✘ 同构校验失败：" + diff;
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusDanger);
        }
    }

    /// <summary>弹出词条注册窗，注册完立刻可用于所有端。</summary>
    private void ShowDefineDialog()
    {
        var dlg = new DefineEntryDialog();
        AddChild(dlg);
        dlg.Setup();
        dlg.OnDefined += (kind, eng, chs, min, max) =>
        {
            switch (kind)
            {
                // 英文 ID 供 AST/脚本引用，中文名只做渲染（180 号文档 D11）。
                case "counter": _ctrl.DefineCustomCounter(eng, chs, min, max); break;
                case "clan":    _ctrl.DefineCustomClan(chs); break;
                default:        _ctrl.DefineCustomTrait(chs, min, max); break;
            }
            _statusLabel.Text = _ctrl.LastStatusMessage;
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
            RefreshAll();
        };
    }

    private void OnSave()
    {
        // 第 0 项是占位「（选择卡包）」——把它当目录名会写出一个叫
        // data/packs/（选择卡包）/ 的垃圾包，而玩家以为存进了目标卡包。
        string packId = _packPicker != null && _packPicker.Selected > 0
            ? _packPicker.GetItemText(_packPicker.Selected)
            : _ctrl.LoadedOriginPackId;

        // 从已有卡改出来的，必须问清楚是**改原卡**还是**另存新卡** ——
        // 不问就默认覆盖，玩家想留个原版时会直接把官方卡改没了。
        if (_ctrl.IsEditingExistingCard)
        {
            var dlg = new AcceptDialog
            {
                Title = "选择保存方式",
                DialogText = "当前卡牌源自已有卡《" + DeckEditorViewModel.DisplayName(_ctrl.CurrentDef)
                             + "》(#" + _ctrl.LoadedOriginCardId + ")。\n"
                             + "请选择直接修改这张卡本身，还是另存为一张全新的卡牌：",
                OkButtonText = "📝 修改原卡本身",
            };
            dlg.AddButton("✨ 另存为新卡（分配新 ID）", true, "save_new");

            dlg.Confirmed += () =>
            {
                _ctrl.SaveCurrentCardToPackAny(packId, overwriteExisting: true, out string m);
                FinishSave(m);
                dlg.QueueFree();
            };
            dlg.CustomAction += act =>
            {
                if (act == "save_new")
                {
                    _ctrl.SaveCurrentCardToPackAny(packId, overwriteExisting: false, out string m);
                    FinishSave(m);
                    dlg.Hide();
                    dlg.QueueFree();
                }
            };
            dlg.Canceled += () => dlg.QueueFree();
            AddChild(dlg);
            dlg.PopupCentered();
            return;
        }

        _ctrl.SaveCurrentCardToPackAny(packId, overwriteExisting: true, out string msg);
        FinishSave(msg);
    }

    private void FinishSave(string msg)
    {
        FillLoadPicker();
        _statusLabel.Text = msg;
        _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
    }

    public DiyWorkshopController Controller => _ctrl;
}
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
    private Label _astLabel;
    private Label _statusLabel;
    private VBoxContainer _endpointHost;
    /// <summary>积木工作台实例。复用而非每次重建：它内部保存着当前能力域与选中积木。</summary>
    private BlockTreeEditorWidget _blockEditor;
    private TextEdit _astJsonEdit;
    private OptionButton _packPicker;
    private Control _hover;

    // 表单端可编辑控件（每次切到表单页签重建）
    private LineEdit _formNameEdit;
    private LineEdit _formNameEngEdit;
    private SpinBox _formCostSpin;
    private SpinBox _formAtkSpin;
    private SpinBox _formLifeSpin;
    private OptionButton _formKindPicker;
    private OptionButton _formClanPicker;
    private LineEdit _formTraitsEdit;
    private LineEdit _formKeywordsEdit;

    // 英文端可编辑控件
    private LineEdit _engNameEdit;
    private LineEdit _engTraitsEdit;
    private TextEdit _engTextEdit;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        _ctrl = new DiyWorkshopController(store);
        _ctrl.OnCardUpdated += RefreshAll;
        BuildUi();
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

        // 从现有卡池导入一张真卡：把测试集里的卡拉进工坊看它的四端投影。
        var loadPicker = new OptionButton { CustomMinimumSize = new Vector2(180, 32) };
        SvWbTheme.StyleButton(loadPicker);
        SvWbTheme.StyleOptionPopup(loadPicker);
        loadPicker.AddItem("📂 加载现有卡牌…", 0);
        var allCards = _store.Catalog.AllCards.Values.OrderBy(c => c.CardId).ToList();
        for (int ci = 0; ci < allCards.Count; ci++)
        {
            var cd = allCards[ci];
            string nm = string.IsNullOrWhiteSpace(cd.NameChs) ? (cd.Name ?? "") : cd.NameChs;
            loadPicker.AddItem(cd.CardId + " " + nm, ci + 1);
        }
        loadPicker.ItemSelected += idx =>
        {
            int k = (int)idx;
            if (k <= 0 || k - 1 >= allCards.Count) return;
            _ctrl.LoadExistingCard(allCards[k - 1]);
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
        row.AddChild(loadPicker);

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
        int i = 0;
        foreach (var pid in _store.Catalog.Packs.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            _packPicker.AddItem(pid, i++);
        }
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

        _astLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _astLabel.AddThemeFontSizeOverride("font_size", 11);
        _astLabel.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
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
        AddEndpointTab(tabs, "③ AST 语法树端", DiyEndpointKind.Script);
        AddEndpointTab(tabs, "④ 官方英文", DiyEndpointKind.EnglishCnl);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddChild(scroll);
        SvWbTheme.StyleScroll(scroll);

        // 禁掉横向滚动：右栏一旦出现水平滚动条，说明有控件被最小宽度撑破了，
        // 那是 Bug 不是需求 —— 应该让它纵向滚，而不是让玩家左右拖。
        scroll.HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled;

        _endpointHost = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _endpointHost.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_endpointHost);

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

    private void RefreshAll()
    {
        if (_ctrl?.CurrentDef == null) return;

        var def = _ctrl.CurrentDef;
        _cardPreview.Setup(def, _store.CardPrinter, 0, 3, showCopyPips: false);
        _cardPreview.SetCardSize(192, 254);

        _chinesePreview.Text = SvWbTheme.FormatRichCardText(_ctrl.GetChineseCardText());
        _astLabel.Text = _ctrl.GetCanonicalAstText();
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
    /// ③ AST 语法树端：直接编辑 CardDef 的 AST JSON。
    ///
    /// 取代原来的 ReshadowScript 脚本端 —— 那套自定义语法是手写的文本版 AST，
    /// 每加一个 EffectNode 字段都要同时改解析与打印两端，漏一个逗号就报错。
    /// AST JSON 由 AstJsonCodec 跟随类型，加字段只在那里各补一行。
    /// </summary>
    private void BuildScriptEndpoint()
    {
        var hint = new Label
        {
            Text = "直接编写卡片的抽象语法树（AST JSON，支持 // 注释与尾随逗号）。" +
                   "点击下方模板按钮可在光标处一键插入对应节点：",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _endpointHost.AddChild(hint);

        var tplBar = new HFlowContainer();
        tplBar.AddThemeConstantOverride("h_separation", 6);
        tplBar.AddThemeConstantOverride("v_separation", 4);
        _endpointHost.AddChild(tplBar);

        AddAstSnippetBtn(tplBar, "入场曲能力壳",
            "{\n  \"trigger\": { \"event\": \"OnPlay\" },\n  \"body\": [\n    " +
            "{ \"$op\": \"DealDamage\", \"target\": { \"owner\": \"Opponent\", \"zone\": \"Board\", " +
            "\"mode\": \"Choose\", \"count\": 1 }, \"amount\": 3 }\n  ]\n}");

        AddAstSnippetBtn(tplBar, "伤害 DealDamage",
            "{ \"$op\": \"DealDamage\", \"target\": \"$self\", \"amount\": 2 }");

        AddAstSnippetBtn(tplBar, "抽牌 Draw",
            "{ \"$op\": \"MoveZone\", \"target\": { \"owner\": \"Self\", \"zone\": \"Deck\", " +
            "\"mode\": \"First\", \"count\": 2 }, \"to_zone\": \"Hand\" }");

        AddAstSnippetBtn(tplBar, "身材 ModifyStats",
            "{ \"$op\": \"ModifyStats\", \"target\": \"$self\", \"op\": \"Add\", \"atk\": 2, \"life\": 2 }");

        AddAstSnippetBtn(tplBar, "关键词 ModifyKeyword",
            "{ \"$op\": \"ModifyKeyword\", \"target\": \"$self\", \"op\": \"Grant\", \"keyword\": \"ward\" }");

        AddAstSnippetBtn(tplBar, "伤害光环 DamageModifier",
            "{ \"$op\": \"DamageModifier\", \"target\": \"$self\", \"kind\": \"TakenCap\", " +
            "\"source_mask\": \"All\", \"amount\": 3, \"is_reduction\": true }");

        AddAstSnippetBtn(tplBar, "模式分支 BranchMode",
            "{ \"$op\": \"BranchMode\", \"mode\": \"Choose\", \"pick_count\": 1, \"options\": [" +
            "[ { \"$op\": \"RestoreLife\", \"target\": \"$self\", \"amount\": 4 } ], " +
            "[ { \"$op\": \"DealDamage\", \"target\": \"$self\", \"amount\": 4 } ] ] }");

        AddAstSnippetBtn(tplBar, "锁定随机数 RandomRange",
            "{ \"$num\": \"RandomRange\", \"min\": 2, \"max\": 10, \"roll_id\": 0 }");

        AddAstSnippetBtn(tplBar, "条件 Conditional",
            "{ \"$op\": \"Conditional\", \"condition\": { \"$bool\": \"Compare\", " +
            "\"left\": { \"$num\": \"Counter\", \"counter\": \"combo\" }, \"cmp\": \"Ge\", " +
            "\"right\": { \"$num\": \"Const\", \"v\": 3 } }, \"then\": [] }");

        _astJsonEdit = new TextEdit
        {
            Text = _ctrl.AstJsonText,
            CustomMinimumSize = new Vector2(0, 360),
            SizeFlagsVertical = SizeFlags.ExpandFill,
            WrapMode = TextEdit.LineWrappingMode.None,
        };
        _astJsonEdit.AddThemeFontSizeOverride("font_size", 13);
        _endpointHost.AddChild(_astJsonEdit);
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
        _formNameEdit = new LineEdit { Text = fm.NameChs ?? "", CustomMinimumSize = new Vector2(260, 32) };
        _endpointHost.AddChild(EditRow("卡名（中文）", _formNameEdit));

        _formNameEngEdit = new LineEdit { Text = fm.NameEng ?? "", CustomMinimumSize = new Vector2(260, 32) };
        _endpointHost.AddChild(EditRow("卡名（英文）", _formNameEngEdit));

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
        string[] clans = { "Neutral", "Forestcraft", "Swordcraft", "Runecraft",
                           "Dragoncraft", "Abysscraft", "Havencraft", "Portalcraft" };
        for (int i = 0; i < clans.Length; i++) _formClanPicker.AddItem(clans[i], i);
        int ci = Array.IndexOf(clans, fm.Clan);
        _formClanPicker.Selected = ci >= 0 ? ci : 0;
        SvWbTheme.StyleButton(_formClanPicker);
        SvWbTheme.StyleOptionPopup(_formClanPicker);
        _endpointHost.AddChild(EditRow("职业", _formClanPicker));

        // 兵种 / 关键词：文本编辑，逗号分隔
        _formTraitsEdit = new LineEdit
        {
            Text = string.Join(", ", fm.Traits ?? new List<string>()),
            CustomMinimumSize = new Vector2(260, 32),
            PlaceholderText = "逗号分隔，如 Officer, Commander",
        };
        _endpointHost.AddChild(EditRow("兵种", _formTraitsEdit));

        _formKeywordsEdit = new LineEdit
        {
            Text = string.Join(", ", fm.Keywords ?? new List<string>()),
            CustomMinimumSize = new Vector2(260, 32),
            PlaceholderText = "逗号分隔，如 ward, storm",
        };
        _endpointHost.AddChild(EditRow("关键词", _formKeywordsEdit));

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

        fm.NameChs = _formNameEdit?.Text ?? fm.NameChs;
        fm.NameEng = _formNameEngEdit?.Text ?? fm.NameEng;
        fm.Cost = _formCostSpin != null ? (int)_formCostSpin.Value : fm.Cost;
        fm.Attack = _formAtkSpin != null ? (int)_formAtkSpin.Value : fm.Attack;
        fm.Life = _formLifeSpin != null ? (int)_formLifeSpin.Value : fm.Life;
        fm.Kind = _formKindPicker?.Selected switch { 1 => "Spell", 2 => "Amulet", _ => "Follower" };
        if (_formClanPicker != null && _formClanPicker.ItemCount > 0)
            fm.Clan = _formClanPicker.GetItemText(_formClanPicker.Selected);

        fm.Traits = SplitList(_formTraitsEdit?.Text);
        fm.Keywords = SplitList(_formKeywordsEdit?.Text);

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
            PlaceholderText = "逗号分隔，如 Officer, Commander",
        };
        _endpointHost.AddChild(EditRow("Traits", _engTraitsEdit));

        var txtTitle = new Label { Text = "Official English Text" };
        txtTitle.AddThemeFontSizeOverride("font_size", 13);
        txtTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        _endpointHost.AddChild(txtTitle);

        _engTextEdit = new TextEdit
        {
            Text = spec.EngText ?? "",
            CustomMinimumSize = new Vector2(0, 240),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _engTextEdit.AddThemeFontSizeOverride("font_size", 13);
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
                if (_astJsonEdit != null) ok = _ctrl.CompileFromAstJson(_astJsonEdit.Text);
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
                case "counter": _ctrl.DefineCustomCounter(eng, chs, min, max); break;
                case "clan":    _ctrl.DefineCustomClan(eng, chs); break;
                default:        _ctrl.DefineCustomTrait(eng, chs); break;
            }
            _statusLabel.Text = _ctrl.LastStatusMessage;
            _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
            RefreshAll();
        };
    }

    private void OnSave()
    {
        string packId = _packPicker != null && _packPicker.ItemCount > 0
            ? _packPicker.GetItemText(_packPicker.Selected) : "testset-v2";

        _ctrl.SaveCurrentCardToPack(packId, out string msg);
        _statusLabel.Text = msg;
        _statusLabel.AddThemeColorOverride("font_color", SvWbTheme.StatusValid);
    }

    public DiyWorkshopController Controller => _ctrl;
}
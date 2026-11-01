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
    private TextEdit _scriptEdit;
    private OptionButton _packPicker;
    private Control _hover;

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
        var bar = new PanelContainer { CustomMinimumSize = new Vector2(0, 58) };
        bar.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.BorderSubtle, 0, 0, 4));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        bar.AddChild(row);

        var back = new Button { Text = "← 返回牌组一览" };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBackToDecks?.Invoke();
        row.AddChild(back);

        var title = new Label { Text = "DIY 四端同构造卡工坊" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        row.AddChild(title);

        row.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        var packLbl = new Label { Text = "目标卡包:" };
        packLbl.AddThemeFontSizeOverride("font_size", 12);
        packLbl.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(packLbl);

        _packPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 34) };
        SvWbTheme.StyleButton(_packPicker);
        int i = 0;
        foreach (var pid in _store.Catalog.Packs.Keys.OrderBy(x => x, StringComparer.Ordinal))
        {
            _packPicker.AddItem(pid, i++);
        }
        row.AddChild(_packPicker);

        var saveBtn = new Button { Text = "💾 保存进卡包", CustomMinimumSize = new Vector2(160, 34) };
        SvWbTheme.StyleButton(saveBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        saveBtn.Pressed += OnSave;
        row.AddChild(saveBtn);

        return bar;
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
        AddEndpointTab(tabs, "③ 脚本代码端", DiyEndpointKind.Script);
        AddEndpointTab(tabs, "④ 官方英文", DiyEndpointKind.EnglishCnl);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        pane.AddChild(scroll);

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

    private void AddEndpointTab(HBoxContainer host, string caption, DiyEndpointKind kind)
    {
        var b = new Button { Text = caption };
        SvWbTheme.StyleButton(b, isPrimary: _active == kind);
        b.Pressed += () => { _active = kind; RefreshEndpoint(); };
        host.AddChild(b);
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
        foreach (var c in _endpointHost.GetChildren()) c.QueueFree();

        switch (_active)
        {
            case DiyEndpointKind.Script: BuildScriptEndpoint(); break;
            case DiyEndpointKind.Form: BuildFormEndpoint(); break;
            case DiyEndpointKind.Block: BuildBlockEndpoint(); break;
            case DiyEndpointKind.EnglishCnl: BuildEnglishEndpoint(); break;
        }
    }

    private void BuildScriptEndpoint()
    {
        var hint = new Label
        {
            Text = "ReshadowScript 源码。改完点下方【立即编译并同步四端】。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _endpointHost.AddChild(hint);

        _scriptEdit = new TextEdit
        {
            Text = _ctrl.ScriptText,
            CustomMinimumSize = new Vector2(0, 380),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _scriptEdit.AddThemeFontSizeOverride("font_size", 13);
        _endpointHost.AddChild(_scriptEdit);
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
                Text = "⚠ 本卡超出表单表达能力，表单端为只读：\n" + _ctrl.FormReadOnlyReason,
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
            };
            wl.AddThemeFontSizeOverride("font_size", 12);
            wl.AddThemeColorOverride("font_color", new Color("FDE68A"));
            warn.AddChild(wl);
            _endpointHost.AddChild(warn);
        }

        _endpointHost.AddChild(Row("卡名（中文）", DeckEditorViewModel.DisplayName(def)));
        _endpointHost.AddChild(Row("基础费用", def.BaseCost.ToString()));
        _endpointHost.AddChild(Row("攻击 / 生命", def.BaseAttack + " / " + def.BaseLife));
        _endpointHost.AddChild(Row("职业",
            def.Clan.IsValid ? SvWbTheme.ClanDisplayZh(def.Clan.Name) : "中立"));
        _endpointHost.AddChild(Row("兵种",
            def.Traits != null && def.Traits.Count > 0
                ? string.Join("·", def.Traits.Select(t => t.Name)) : "无"));
        _endpointHost.AddChild(Row("构筑修正数", (def.DeckModifiers?.Count ?? 0).ToString()));
        _endpointHost.AddChild(Row("能力条数", (def.Abilities?.Count ?? 0).ToString()));

        _endpointHost.AddChild(new Label { Text = "（表单为只读投影；要改请切到脚本端或积木端）" });
    }

    private void BuildBlockEndpoint()
    {
        if (_ctrl.BlockModel == null)
        {
            _endpointHost.AddChild(new Label { Text = "（积木模型为空）" });
            return;
        }

        var tree = new BlockTreeEditorWidget();
        tree.Build(_ctrl.BlockModel, _store.CardPrinter);
        _endpointHost.AddChild(tree);
    }

    private void BuildEnglishEndpoint()
    {
        var spec = _ctrl.CnlSpec;
        if (spec == null) { _endpointHost.AddChild(new Label { Text = "（英文规格为空）" }); return; }

        _endpointHost.AddChild(Row("Card Name", spec.Name ?? ""));
        _endpointHost.AddChild(Row("Clan", spec.Clan ?? "Neutral"));
        _endpointHost.AddChild(Row("Traits", string.Join(", ", spec.Traits ?? new List<string>())));

        var engTitle = new Label { Text = "官方英文卡面文本" };
        engTitle.AddThemeFontSizeOverride("font_size", 13);
        engTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        _endpointHost.AddChild(engTitle);

        var box = new PanelContainer { CustomMinimumSize = new Vector2(0, 200) };
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.BorderSubtle, 8, 1, 0));
        var rich = new RichTextLabel
        {
            BbcodeEnabled = false,
            Text = spec.EngText ?? "",
            FitContent = true,
            ScrollActive = false,
        };
        rich.AddThemeFontSizeOverride("normal_font_size", 13);
        box.AddChild(rich);
        _endpointHost.AddChild(box);
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
                if (_scriptEdit != null) ok = _ctrl.CompileFromScript(_scriptEdit.Text);
                break;
            default:
                // 其余三端目前为只读投影，编译退化为「用当前 AST 重投影」
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
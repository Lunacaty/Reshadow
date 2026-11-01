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

        var defineBtn = new Button { Text = "➕ 注册新词条", CustomMinimumSize = new Vector2(150, 34) };
        SvWbTheme.StyleButton(defineBtn, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
        defineBtn.Pressed += ShowDefineDialog;
        row.AddChild(defineBtn);

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
        _endpointHost.AddChild(EditRow("卡种", _formKindPicker));

        _formClanPicker = new OptionButton { CustomMinimumSize = new Vector2(160, 32) };
        string[] clans = { "Neutral", "Forestcraft", "Swordcraft", "Runecraft",
                           "Dragoncraft", "Abysscraft", "Havencraft", "Portalcraft" };
        for (int i = 0; i < clans.Length; i++) _formClanPicker.AddItem(clans[i], i);
        int ci = Array.IndexOf(clans, fm.Clan);
        _formClanPicker.Selected = ci >= 0 ? ci : 0;
        SvWbTheme.StyleButton(_formClanPicker);
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
        tree.Build(_ctrl.BlockModel, _store.CardPrinter, editable: true,
                   onAddAction: hat => AddActionBlock(hat),
                   onRemoveAction: blk => RemoveActionBlock(blk),
                   onMoveAction: (blk, dir) => MoveActionBlock(blk, dir));
        _endpointHost.AddChild(tree);
    }

    /// <summary>往某个事件帽下追加一个「抽 1 张牌」动作块（最常见、也最容易看出效果）。</summary>
    private void AddActionBlock(Block hat)
    {
        if (hat == null) return;
        hat.Stacks ??= new List<Block>();
        hat.Stacks.Add(new Block { Opcode = "act_draw", IntA = 1 });
        _ctrl.CompileFromBlocks(_ctrl.BlockModel);
        RefreshAll();
    }

    private void RemoveActionBlock(Block blk)
    {
        var hats = _ctrl.BlockModel?.Hats;
        if (hats == null) return;
        foreach (var h in hats)
        {
            if (h.Stacks != null && h.Stacks.Remove(blk)) { RecompileBlocks(); return; }
            if (h.ElseStacks != null && h.ElseStacks.Remove(blk)) { RecompileBlocks(); return; }
        }
    }

    private void MoveActionBlock(Block blk, int dir)
    {
        var hats = _ctrl.BlockModel?.Hats;
        if (hats == null) return;
        foreach (var h in hats)
        {
            var list = h.Stacks;
            if (list == null) continue;
            int idx = list.IndexOf(blk);
            if (idx < 0) continue;
            int ni = idx + dir;
            if (ni < 0 || ni >= list.Count) return;
            (list[idx], list[ni]) = (list[ni], list[idx]);
            RecompileBlocks();
            return;
        }
    }

    private void RecompileBlocks()
    {
        _ctrl.CompileFromBlocks(_ctrl.BlockModel);
        RefreshAll();
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
                if (_scriptEdit != null) ok = _ctrl.CompileFromScript(_scriptEdit.Text);
                break;
            case DiyEndpointKind.Form:
                ok = ApplyFormEdits();
                break;
            case DiyEndpointKind.EnglishCnl:
                ok = ApplyEnglishEdits();
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
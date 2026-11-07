using System;
using Godot;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// 注册自定义词条的小窗（兵种 / 计数器 / 职业）。
///
/// 这三类是「内容词条」，由 GameSchema 统一发号 ——
/// DIY 作者不该为了加一个「机械」兵种去改引擎源码。
/// </summary>
public partial class DefineEntryDialog : Control
{
    public event Action<string, string, string, int, int> OnDefined;
    // kind, nameEng, nameChs, min, max

    private OptionButton _kindPicker;
    private LineEdit _engEdit;
    private LineEdit _chsEdit;
    private SpinBox _minSpin;
    private SpinBox _maxSpin;
    private Label _status;

    public void Setup()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 95;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(520, 380));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, new Color("D4AF37"), 12, 2, 16));

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 10);
        panel.AddChild(v);

        var title = new Label { Text = "➕ 注册自定义词条" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        v.AddChild(title);

        var hint = new Label
        {
            Text = "兵种 / 计数器 / 职业 都由 GameSchema 统一发号，注册后立即可在所有端使用。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        v.AddChild(hint);

        _kindPicker = new OptionButton { CustomMinimumSize = new Vector2(200, 34) };
        _kindPicker.AddItem("兵种 Trait", 0);
        _kindPicker.AddItem("计数器 Counter", 1);
        _kindPicker.AddItem("职业 Clan", 2);
        SvWbTheme.StyleButton(_kindPicker);
        SvWbTheme.StyleOptionPopup(_kindPicker);
        v.AddChild(Field("类别", _kindPicker));

        // 恢复「英文内部 ID + 中文显示名」双输入框（180 号文档 D11）。
        // 英文 ID 要能被 AST / PyAST 脚本引用（如 sword_intent），
        // 中文名只用于卡面与界面渲染（如 剑意）。
        _engEdit = new LineEdit { CustomMinimumSize = new Vector2(260, 34),
                                  PlaceholderText = "英文 ID，如 sword_intent（可留空）" };
        v.AddChild(Field("英文 ID", _engEdit));

        _chsEdit = new LineEdit { CustomMinimumSize = new Vector2(260, 34),
                                  PlaceholderText = "显示名，如 剑意" };
        v.AddChild(Field("显示名", _chsEdit));

        _minSpin = new SpinBox { MinValue = 0, MaxValue = 999, Value = 0,
                                 CustomMinimumSize = new Vector2(120, 34) };
        v.AddChild(Field("最小值（仅计数器）", _minSpin));

        _maxSpin = new SpinBox { MinValue = 0, MaxValue = 999, Value = 10,
                                 CustomMinimumSize = new Vector2(120, 34) };
        v.AddChild(Field("最大值（仅计数器）", _maxSpin));

        _status = new Label { Text = "" };
        _status.AddThemeFontSizeOverride("font_size", 11);
        _status.AddThemeColorOverride("font_color", SvWbTheme.StatusDanger);
        v.AddChild(_status);

        var btnRow = new HBoxContainer();
        btnRow.AddThemeConstantOverride("separation", 12);
        v.AddChild(btnRow);

        var ok = new Button { Text = "✔ 注册", CustomMinimumSize = new Vector2(160, 40) };
        SvWbTheme.StyleButton(ok, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        ok.Pressed += OnConfirm;
        btnRow.AddChild(ok);

        var cancel = new Button { Text = "取消", CustomMinimumSize = new Vector2(120, 40) };
        SvWbTheme.StyleButton(cancel);
        cancel.Pressed += QueueFree;
        btnRow.AddChild(cancel);
    }

    private static Control Field(string label, Control editor)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var a = new Label { Text = label, CustomMinimumSize = new Vector2(150, 0) };
        a.AddThemeFontSizeOverride("font_size", 12);
        a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(a);

        row.AddChild(editor);
        return row;
    }

    private void OnConfirm()
    {
        string eng = (_engEdit?.Text ?? "").Trim();
        string chs = (_chsEdit?.Text ?? "").Trim();

        // 英文 ID 留空时退回显示名 —— 中文标识符 PyAST 词法器也支持，
        // 只是脚本里写起来不方便，所以不强制。
        if (string.IsNullOrEmpty(eng)) eng = chs;
        if (string.IsNullOrEmpty(chs)) chs = eng;

        if (string.IsNullOrEmpty(eng)) { _status.Text = "名称不能为空"; return; }

        string kind = _kindPicker?.Selected switch
        {
            1 => "counter",
            2 => "clan",
            _ => "trait",
        };

        int min = _minSpin != null ? (int)_minSpin.Value : 0;
        int max = _maxSpin != null ? (int)_maxSpin.Value : 10;

        OnDefined?.Invoke(kind, eng, chs, min, max);
        QueueFree();
    }
}
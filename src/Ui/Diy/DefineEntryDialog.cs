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
        panel.Size = new Vector2(520, 380);
        panel.Position = new Vector2(380, 170);
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
        v.AddChild(Field("类别", _kindPicker));

        _engEdit = new LineEdit { CustomMinimumSize = new Vector2(260, 34),
                                  PlaceholderText = "英文名，如 Mecha" };
        v.AddChild(Field("英文名", _engEdit));

        _chsEdit = new LineEdit { CustomMinimumSize = new Vector2(260, 34),
                                  PlaceholderText = "中文名，如 机甲" };
        v.AddChild(Field("中文名", _chsEdit));

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

        if (string.IsNullOrEmpty(eng)) { _status.Text = "英文名不能为空"; return; }
        if (string.IsNullOrEmpty(chs)) chs = eng;

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
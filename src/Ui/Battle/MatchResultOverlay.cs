using System;
using Godot;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 终局胜负结算浮层：VICTORY（璀璨金）/ DEFEAT（深绯红）。
/// 提供 [再战一局] 与 [返回牌组一览] 两个出口。
/// </summary>
public partial class MatchResultOverlay : Control
{
    public event Action OnRematch;
    public event Action OnBackToDecks;

    public void Setup(bool isVictory, int turnCount, bool isNetworkMatch = false)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 85;

        var dim = new ColorRect
        {
            Color = isVictory ? new Color(0.04f, 0.06f, 0.10f, 0.82f)
                              : new Color(0.10f, 0.02f, 0.03f, 0.85f),
        };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        Color accent = isVictory ? new Color("FDE047") : new Color("DC2626");

        var panel = new PanelContainer();
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(560, 320));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            isVictory ? new Color(0.10f, 0.09f, 0.04f, 0.96f)
                      : new Color(0.12f, 0.04f, 0.05f, 0.96f),
            accent, 14, 3, 20));

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 14);
        v.Alignment = BoxContainer.AlignmentMode.Center;
        panel.AddChild(v);

        var main = new Label
        {
            Text = isVictory ? "VICTORY" : "DEFEAT",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        main.AddThemeFontSizeOverride("font_size", 52);
        main.AddThemeColorOverride("font_color", accent);
        v.AddChild(main);

        var sub = new Label
        {
            Text = isVictory ? "胜  利" : "败  北",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeFontSizeOverride("font_size", 22);
        sub.AddThemeColorOverride("font_color", Colors.White);
        v.AddChild(sub);

        var turns = new Label
        {
            Text = "共 " + turnCount + " 回合",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        turns.AddThemeFontSizeOverride("font_size", 13);
        turns.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        v.AddChild(turns);

        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddThemeConstantOverride("separation", 16);
        v.AddChild(row);

        var rematch = new Button { Text = isNetworkMatch ? "🏠 返回联机房间" : "⚔ 再战一局", CustomMinimumSize = new Vector2(200, 48) };
        SvWbTheme.StyleButton(rematch, isPrimary: true, customAccent: accent);
        rematch.Pressed += () => OnRematch?.Invoke();
        row.AddChild(rematch);

        var back = new Button { Text = "← 返回牌组一览", CustomMinimumSize = new Vector2(200, 48) };
        SvWbTheme.StyleButton(back);
        back.Pressed += () => OnBackToDecks?.Invoke();
        row.AddChild(back);
    }
}
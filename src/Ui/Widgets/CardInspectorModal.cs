using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 右键卡牌检视窗。
///
/// 左栏：放大卡面 + 卡包来源 + 卡号。
/// 右栏：完整简中效果 + 激奏/结晶/纹章/信仰四类子卡详情。
///
/// 子卡分栏是这块的重点：很多卡的「关联纹章」才是它真正的效果，
/// 只印主卡会让玩家以为这张牌是白板。
/// </summary>
public partial class CardInspectorModal : Control
{
    public event Action OnClosed;

    public static CardInspectorModal Create(
        CardDef def, ChineseCardPrinter printer, string packLabel = null)
    {
        var modal = new CardInspectorModal();
        modal.Build(def, printer, packLabel);
        return modal;
    }

    private void Build(CardDef def, ChineseCardPrinter printer, string packLabel)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        // 全屏暗化遮罩：点击空白处关闭
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        dim.MouseFilter = MouseFilterEnum.Stop;
        dim.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                Close();
        };
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.AccentCyan, 12, 2, 18));
        panel.SetAnchorsPreset(LayoutPreset.Center);
        panel.CustomMinimumSize = new Vector2(880, 540);
        panel.Position = new Vector2(
            (GetViewportRect().Size.X - 880) / 2,
            (GetViewportRect().Size.Y - 540) / 2);
        AddChild(panel);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 10);
        panel.AddChild(outer);

        // ---- 顶部标题栏 ----
        var titleRow = new HBoxContainer();
        outer.AddChild(titleRow);

        var title = new Label
        {
            Text = (string.IsNullOrWhiteSpace(def.NameChs) ? def.Name : def.NameChs),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        titleRow.AddChild(title);

        var closeBtn = new Button { Text = "✕" };
        SvWbTheme.StyleButton(closeBtn);
        closeBtn.Pressed += Close;
        titleRow.AddChild(closeBtn);

        // ---- 主体：左卡面 / 右详情 ----
        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 16);
        body.SizeFlagsVertical = SizeFlags.ExpandFill;
        outer.AddChild(body);

        // 左栏
        var leftCol = new VBoxContainer { CustomMinimumSize = new Vector2(280, 0) };
        leftCol.AddThemeConstantOverride("separation", 8);
        body.AddChild(leftCol);

        var cardWidget = new CardVisualWidget();
        cardWidget.Setup(def, printer);
        cardWidget.CustomMinimumSize = new Vector2(258, 364);
        leftCol.AddChild(cardWidget);

        var meta = new Label
        {
            Text = $"来自卡包: [{packLabel ?? (string.IsNullOrEmpty(def.PackId) ? "内置" : def.PackId)}]\n"
                 + $"CardId: {def.CardId}" + (def.IsToken ? "\n类型: 衍生卡(不可收藏)" : ""),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        meta.AddThemeFontSizeOverride("font_size", 11);
        meta.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        leftCol.AddChild(meta);

        // 右栏
        var rightCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rightCol.AddThemeConstantOverride("separation", 10);
        body.AddChild(rightCol);

        rightCol.AddChild(Section("卡面效果", printer.PrintCard(def), SvWbTheme.TextPrimary, 13));

        // 四类子卡分栏
        AddSubCard(rightCol, "激奏", def.AccelerateDef, printer);
        AddSubCard(rightCol, "结晶", def.CrystallizeDef, printer);
        AddSubCard(rightCol, "关联纹章", def.CrestDef, printer);
        AddSubCard(rightCol, "关联信仰", def.FaithDef, printer);

        if (def.HasFuse) 
            rightCol.AddChild(Section("融合", "本卡可作为融合主体，消耗指定材料。",
                                       SvWbTheme.AccentPurple, 11));
    }

    private void AddSubCard(VBoxContainer col, string label, CardDef sub, ChineseCardPrinter printer)
    {
        if (sub == null) return;
        string text = sub.Abilities == null || sub.Abilities.Count == 0
            ? $"（{label}：无效果）"
            : printer.PrintCard(sub);
        col.AddChild(Section(label, text, SvWbTheme.AccentGold, 12));
    }

    private static PanelContainer Section(string label, string text, Color color, int fontSize)
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.BorderSubtle, 8, 1, 0));

        var v = new VBoxContainer();
        v.AddThemeConstantOverride("separation", 4);
        box.AddChild(v);

        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", fontSize);
        head.AddThemeColorOverride("font_color", color);
        v.AddChild(head);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            Text = SvWbTheme.FormatRichCardText(text),
            FitContent = true,
            ScrollActive = false,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        body.AddThemeFontSizeOverride("normal_font_size", 12);
        body.AddThemeFontSizeOverride("bold_font_size", 12);
        v.AddChild(body);
        return box;
    }

    private void Close()
    {
        OnClosed?.Invoke();
        QueueFree();
    }
}

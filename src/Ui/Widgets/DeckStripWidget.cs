using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>构筑台右侧的条状缩略卡（一行 38px）。左键减 1，右键开检视窗。</summary>
public partial class DeckStripWidget : PanelContainer
{
    public CardDef Card { get; private set; }
    public event Action<CardDef> OnLeftClick;
    public event Action<CardDef> OnRightClick;

    private Label _countLabel;

    public void Setup(CardDef def, int count, int limit)
    {
        Card = def ?? throw new ArgumentNullException(nameof(def));
        CustomMinimumSize = new Vector2(0, 38);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;

        Color clanCol = SvWbTheme.ClanColor(def.Clan.IsValid ? def.Clan.Name : null);
        AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.BorderSubtle, 6, 1, 0));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        AddChild(row);

        // 费用块
        var costBox = new PanelContainer { CustomMinimumSize = new Vector2(32, 0) };
        costBox.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color("0284C7"), SvWbTheme.AccentCyan, 5, 1, 0));
        var costLbl = new Label
        {
            Text = def.BaseCost.ToString(),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        costLbl.AddThemeFontSizeOverride("font_size", 13);
        costLbl.AddThemeColorOverride("font_color", Colors.White);
        costBox.AddChild(costLbl);
        row.AddChild(costBox);

        // 卡名（带职业色微光）
        bool hasMod = def.DeckModifiers != null && def.DeckModifiers.Count > 0;
        var nameLbl = new Label
        {
            Text = (!string.IsNullOrWhiteSpace(def.NameChs) ? def.NameChs : def.Name)
                   + (hasMod ? "  ★规则" : ""),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
        };
        nameLbl.AddThemeFontSizeOverride("font_size", 12);
        nameLbl.AddThemeColorOverride("font_color",
            hasMod ? SvWbTheme.AccentPurple : clanCol.Lerp(Colors.White, 0.5f));
        row.AddChild(nameLbl);

        // 张数徽章
        _countLabel = new Label { VerticalAlignment = VerticalAlignment.Center };
        _countLabel.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(_countLabel);
        UpdateCount(count, limit);
    }

    public void UpdateCount(int count, int limit)
    {
        if (_countLabel == null) return;
        // 上限被 OverrideSelfCopyLimit 改过时显示 ×N/M，否则只显示 ×N。
        _countLabel.Text = limit != 3 ? $"×{count}/{limit}" : $"×{count}";
        _countLabel.AddThemeColorOverride("font_color",
            count >= limit ? SvWbTheme.AccentGold : SvWbTheme.TextSecondary);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex == MouseButton.Left) { OnLeftClick?.Invoke(Card); AcceptEvent(); }
        else if (mb.ButtonIndex == MouseButton.Right) { OnRightClick?.Invoke(Card); AcceptEvent(); }
    }
}

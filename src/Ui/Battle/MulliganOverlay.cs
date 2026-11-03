using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 开局起手换牌（Mulligan）全屏浮层。
///
/// 点击卡牌标记 / 取消「↻ 换掉」，底部大按钮确认。
/// 交互上刻意保留战场可见：暗化只压到 0.78，玩家能瞥见对面的场面。
/// </summary>
public partial class MulliganOverlay : Control
{
    public event Action OnMulliganConfirmed;

    private BattleSessionController _ctrl;
    private readonly HashSet<int> _selectedIds = new();
    private Control _cardsHost;
    private Button _confirmBtn;
    private CardHoverTooltip _tooltip;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 75;

        var dim = new ColorRect { Color = new Color(0.03f, 0.05f, 0.10f, 0.78f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        // 横幅水平居中、贴上方；用 KeepCentered 带负偏移实现「不居中但跟随窗口」。
        var banner = new PanelContainer { Size = new Vector2(600, 74) };
        SvWbTheme.KeepCentered(this, banner, new Vector2(600, 74),
            new Vector2(0f, -292f));
        banner.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.07f, 0.10f, 0.18f, 0.95f), new Color("D4AF37"), 12, 2, 12));
        AddChild(banner);

        var bv = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        banner.AddChild(bv);

        string orderTag = _ctrl.IsSecondPlayer(_ctrl.MyPlayerId) ? "【后手】" : "【先手】";
        var title = new Label
        {
            Text = orderTag + "  决定起手牌 / MULLIGAN",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        bv.AddChild(title);

        var sub = new Label
        {
            Text = "点击要换掉的手牌（先抽取等量新牌，再洗回牌组）",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeFontSizeOverride("font_size", 12);
        sub.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        bv.AddChild(sub);

        _cardsHost = new Control { MouseFilter = MouseFilterEnum.Pass };
        _cardsHost.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_cardsHost);

        _confirmBtn = new Button
        {
            Text = "✔ 全部保留并开局",
            CustomMinimumSize = new Vector2(240, 52),
            Size = new Vector2(240, 52),
        };
        SvWbTheme.KeepCentered(this, _confirmBtn, new Vector2(240, 52),
            new Vector2(0f, 205f));
        SvWbTheme.StyleButton(_confirmBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        _confirmBtn.AddThemeFontSizeOverride("font_size", 18);
        _confirmBtn.Pressed += Confirm;
        AddChild(_confirmBtn);

        _tooltip = new CardHoverTooltip { ZIndex = 95 };
        AddChild(_tooltip);

        // 窗口尺寸变化时重排起手牌：只算一次会在切换全屏后全部错位。
        Resized += RebuildCards;

        RebuildCards();
    }

    private void RebuildCards()
    {
        foreach (var c in _cardsHost.GetChildren()) c.QueueFree();

        var hand = _ctrl.Hand(_ctrl.MyPlayerId);
        int n = hand.Count;
        const float cardW = 168f, cardH = 222f, gap = 24f;
        float totalW = n * cardW + Math.Max(0, n - 1) * gap;
        // 跟随视口宽度居中：写死 1280 会让全屏下的起手牌偏在左边。
        var mvp = Size.X > 0 ? Size : GetViewportRect().Size;
        float startX = (mvp.X - totalW) * 0.5f;
        const float baseY = 230f;

        for (int i = 0; i < n; i++)
        {
            var ent = hand[i];
            bool marked = _selectedIds.Contains(ent.EntityId);

            var slot = new Control
            {
                Position = new Vector2(startX + i * (cardW + gap), marked ? baseY - 22f : baseY),
                Size = new Vector2(cardW, cardH + 36f),
                MouseFilter = MouseFilterEnum.Pass,
            };
            _cardsHost.AddChild(slot);

            var cardWdg = new CardVisualWidget();
            cardWdg.Setup(ent.Def, _ctrl.Store.CardPrinter, 0, 3, showCopyPips: false);
            cardWdg.SetCardSize(cardW, cardH);

            var bound = ent;
            cardWdg.OnLeftClick += _ => ToggleCard(bound.EntityId);
            cardWdg.OnHoverEntered += (cd, cc) => _tooltip.ShowForCard(cd, _ctrl.Store.CardPrinter, cc);
            cardWdg.OnHoverExited += _ => _tooltip.HideTooltip();
            slot.AddChild(cardWdg);

            var tag = new PanelContainer
            {
                Position = new Vector2(24f, cardH + 6f),
                Size = new Vector2(cardW - 48f, 26f),
                MouseFilter = MouseFilterEnum.Ignore,
            };
            tag.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                marked ? new Color("B45309") : new Color(0.08f, 0.11f, 0.18f, 0.85f),
                marked ? new Color("FDE047") : SvWbTheme.BorderSubtle, 6, 1, 2));
            var lbl = new Label
            {
                Text = marked ? "↻ 换掉此牌" : "✔ 保留",
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            lbl.AddThemeColorOverride("font_color", marked ? Colors.White : SvWbTheme.TextMuted);
            tag.AddChild(lbl);
            slot.AddChild(tag);
        }

        _confirmBtn.Text = _selectedIds.Count == 0
            ? "✔ 全部保留并开局"
            : $"↻ 替换 {_selectedIds.Count} 张卡牌并开局";
    }

    private void ToggleCard(int entityId)
    {
        if (!_selectedIds.Remove(entityId)) _selectedIds.Add(entityId);
        RebuildCards();
    }

    private void Confirm()
    {
        var hand = _ctrl.Hand(_ctrl.MyPlayerId);
        var returning = hand.Where(e => _selectedIds.Contains(e.EntityId)).ToList();

        _ctrl.ConfirmPlayerMulligan(returning);
        OnMulliganConfirmed?.Invoke();
        QueueFree();
    }
}
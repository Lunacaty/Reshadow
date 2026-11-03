using System;
using Godot;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui;

/// <summary>
/// 主界面 · Astral Sanctum。
///
/// 视觉语言全部复用牌组一览那一套：同一张星象背景、同一套面板盒、
/// 同一组强调色。主界面不该是另一种风格的门面 —— 它是同一个圣殿的中庭。
///
/// 按钮自上而下：开始对战 / 牌组管理 / DIY 造卡工坊 / 卡包卡池管理 / 退出。
/// </summary>
public partial class MainMenuView : Control
{
    public event Action OnStartBattle;
    public event Action OnLanBattle;
    public event Action OnDeckManagement;
    public event Action OnDiyWorkshop;
    public event Action OnPackManager;
    public event Action OnQuit;

    private GameSessionStore _store;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        BuildTitle();
        BuildButtons();
        BuildFooter();
    }

    private void BuildTitle()
    {
        var box = new VBoxContainer();
        AddChild(box);
        box.SetAnchorsPreset(LayoutPreset.TopLeft);
        box.Position = new Vector2(0, 96);
        box.Size = new Vector2(1280, 0);
        box.AddThemeConstantOverride("separation", 6);
        box.MouseFilter = MouseFilterEnum.Ignore;

        var title = new Label
        {
            Text = "RESHADOW",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        title.AddThemeFontSizeOverride("font_size", 64);
        title.AddThemeColorOverride("font_color", new Color("FDE68A"));
        // 金色描边让标题在星轨背景上也立得住
        title.AddThemeConstantOverride("outline_size", 8);
        title.AddThemeColorOverride("font_outline_color", new Color(0.15f, 0.10f, 0.02f, 0.85f));
        title.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(title);

        var sub = new Label
        {
            Text = "影 之 诗 · 超 凡 世 界",
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        sub.AddThemeFontSizeOverride("font_size", 18);
        sub.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        sub.MouseFilter = MouseFilterEnum.Ignore;
        box.AddChild(sub);

        var rule = new ColorRect
        {
            Color = new Color(SvWbTheme.AccentGold, 0.55f),
            CustomMinimumSize = new Vector2(320, 2),
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        box.AddChild(rule);
    }

    private void BuildButtons()
    {
        var col = new VBoxContainer();
        AddChild(col);
        col.SetAnchorsPreset(LayoutPreset.TopLeft);
        col.Size = new Vector2(320, 0);

        // 不再写死 (480,300)：那个坐标是按 1280x720 算的，1920x1080 下会整体偏左上。
        // 改成跟随视口尺寸居中，全屏切换也不跑偏。
        void CenterButtons()
        {
            if (!IsInstanceValid(col)) return;
            var vp = Size;
            if (vp.X <= 0 || vp.Y <= 0) vp = GetViewportRect().Size;
            col.Position = new Vector2(Mathf.Max(12f, (vp.X - 320f) * 0.5f),
                                       Mathf.Max(12f, vp.Y * 0.42f));
        }
        Resized += CenterButtons;
        CenterButtons();
        col.AddThemeConstantOverride("separation", 14);

        col.AddChild(MakeButton("⚔  开 始 对 战", true, SvWbTheme.AccentGold, () => OnStartBattle?.Invoke()));
        col.AddChild(MakeButton("🌐  局 域 网 联 机", false, SvWbTheme.AccentCyan, () => OnLanBattle?.Invoke()));
        col.AddChild(MakeButton("🃏  牌 组 管 理", false, null, () => OnDeckManagement?.Invoke()));
        col.AddChild(MakeButton("🛠  DIY 造卡工坊", false, SvWbTheme.AccentPurple, () => OnDiyWorkshop?.Invoke()));
        col.AddChild(MakeButton("⚙  卡包 / 卡池管理", false, null, () => OnPackManager?.Invoke()));

        var gap = new Control { CustomMinimumSize = new Vector2(0, 10) };
        col.AddChild(gap);

        col.AddChild(MakeButton("✕  退 出 游 戏", false, SvWbTheme.StatusDanger, () => OnQuit?.Invoke()));
    }

    private static Button MakeButton(string text, bool primary, Color? accent, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(320, 56),
        };
        b.AddThemeFontSizeOverride("font_size", 20);
        SvWbTheme.StyleButton(b, primary, accent);
        b.Pressed += onPressed;
        return b;
    }

    private void BuildFooter()
    {
        int cards = 0, decks = 0;
        try
        {
            cards = _store?.Catalog?.AllCards?.Count ?? 0;
            decks = _store?.SavedDecks?.Count ?? 0;
        }
        catch { }

        var stat = new Label { Text = cards + " 张卡 · " + decks + " 副牌组" };
        AddChild(stat);
        stat.SetAnchorsPreset(LayoutPreset.BottomLeft);
        stat.Position = new Vector2(28, 690);
        stat.AddThemeFontSizeOverride("font_size", 12);
        stat.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        stat.MouseFilter = MouseFilterEnum.Ignore;

        var ver = new Label { Text = "v1.0", HorizontalAlignment = HorizontalAlignment.Right };
        AddChild(ver);
        ver.SetAnchorsPreset(LayoutPreset.BottomRight);
        ver.Position = new Vector2(1180, 690);
        ver.Size = new Vector2(72, 0);
        ver.AddThemeFontSizeOverride("font_size", 12);
        ver.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        ver.MouseFilter = MouseFilterEnum.Ignore;
    }
}
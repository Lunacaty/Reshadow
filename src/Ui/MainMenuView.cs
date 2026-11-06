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
    public event Action OnCardGallery;
    public event Action OnQuit;

    /// <summary>启动参数 --join-local=&lt;卡池&gt;：第二实例自动以 127.0.0.1 进入加入态。</summary>
    public event Action<string> OnAutoJoinLocal;

    private GameSessionStore _store;
    private string _autoJoinPoolId;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        BuildTitle();
        BuildButtons();
        BuildFooter();

        // 「本机双开」的第二实例带 --join-local=<卡池>。
        // Godot 把命令行 "--" 之后的自定义参数原样透传，GetCmdlineArgs() 可读；
        // 用 Uri 转义是为了卡池 id 含中文/空格时仍能在命令行里完整传递。
        // 解析结果延迟一帧发出：上层先订阅事件，再把大厅切出来。
        foreach (var arg in OS.GetCmdlineArgs())
        {
            if (!arg.StartsWith("--join-local=", StringComparison.Ordinal)) continue;
            _autoJoinPoolId = Uri.UnescapeDataString(arg.Substring("--join-local=".Length));
            CallDeferred(MethodName.EmitAutoJoinLocal);
            break;
        }
    }

    private void EmitAutoJoinLocal() => OnAutoJoinLocal?.Invoke(_autoJoinPoolId);

    private void BuildTitle()
    {
        var box = new VBoxContainer();
        AddChild(box);
        box.SetAnchorsPreset(LayoutPreset.TopLeft);
        box.Position = new Vector2(0, 96);
        // 标题栏宽度跟随视口：写死 1280 会让全屏下大标题偏在左半屏。
        void LayoutTitle()
        {
            if (!IsInstanceValid(box)) return;
            var vp = Size.X > 0 ? Size : GetViewportRect().Size;
            box.Position = new Vector2(0, Mathf.Max(40f, vp.Y * 0.12f));
            box.Size = new Vector2(vp.X, 0);
        }
        Resized += LayoutTitle;
        LayoutTitle();
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

        col.AddThemeConstantOverride("separation", 10);

        // 不再写死 (480,300)：那个坐标是按 1280x720 算的。
        //
        // 更要紧的是**总高**：7 个按钮 × 56 + 间距 14 ≈ 486px，
        // 起点 vp.Y*0.42（720 下是 302）算下来底部到 788px，
        // 而 project.godot 是 canvas_items 拉伸模式，逻辑画布恒为 720 ——
        // 第 7 个按钮「退出游戏」永远掉在屏幕外。
        // 现在按钮压到 44px、间距 10px，并把起点钳在「底部留 42px 底栏」之上。
        void CenterButtons()
        {
            if (!IsInstanceValid(col)) return;
            var vp = Size;
            if (vp.X <= 0 || vp.Y <= 0) vp = GetViewportRect().Size;
            float totalH = col.GetCombinedMinimumSize().Y;
            float y = Mathf.Clamp((vp.Y - totalH) * 0.60f,
                                  200f,
                                  Mathf.Max(200f, vp.Y - totalH - 42f));
            col.Position = new Vector2(Mathf.Max(12f, (vp.X - 320f) * 0.5f), y);
        }
        Resized += CenterButtons;
        // 首次布局时子控件还没排完，GetCombinedMinimumSize 可能为 0，需延迟再算一次。
        CallDeferred(MethodName.CenterButtonsDeferred);

        col.AddChild(MakeButton("⚔  开 始 对 战", true, SvWbTheme.AccentGold, () => OnStartBattle?.Invoke()));
        col.AddChild(MakeButton("🌐  局 域 网 联 机", false, SvWbTheme.AccentCyan, () => OnLanBattle?.Invoke()));
        col.AddChild(MakeButton("🃏  牌 组 管 理", false, null, () => OnDeckManagement?.Invoke()));
        col.AddChild(MakeButton("🛠  DIY 造卡工坊", false, SvWbTheme.AccentPurple, () => OnDiyWorkshop?.Invoke()));
        col.AddChild(MakeButton("⚙  卡包 / 卡池管理", false, null, () => OnPackManager?.Invoke()));
        col.AddChild(MakeButton("📚  卡 片 一 览", false, null, () => OnCardGallery?.Invoke()));

        var gap = new Control { CustomMinimumSize = new Vector2(0, 6) };
        col.AddChild(gap);

        col.AddChild(MakeButton("✕  退 出 游 戏", false, SvWbTheme.StatusDanger, () => OnQuit?.Invoke()));
    }

    /// <summary>延迟重算按钮列位置（首帧子控件还没排完，高度会算成 0）。</summary>
    private void CenterButtonsDeferred() => EmitSignal(SignalName.Resized);

    private static Button MakeButton(string text, bool primary, Color? accent, Action onPressed)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(320, 44),
        };
        b.AddThemeFontSizeOverride("font_size", 18);
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
        stat.AddThemeFontSizeOverride("font_size", 12);
        stat.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        stat.MouseFilter = MouseFilterEnum.Ignore;

        var ver = new Label { Text = "v1.0", HorizontalAlignment = HorizontalAlignment.Right };
        AddChild(ver);
        ver.SetAnchorsPreset(LayoutPreset.BottomRight);
        ver.AddThemeFontSizeOverride("font_size", 12);
        ver.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        ver.MouseFilter = MouseFilterEnum.Ignore;

        // 底栏跟随视口底部：写死 690 在全屏下会悬在屏幕半腰。
        void LayoutFooter()
        {
            if (!IsInstanceValid(stat) || !IsInstanceValid(ver)) return;
            var vp = Size.X > 0 ? Size : GetViewportRect().Size;
            stat.Position = new Vector2(28f, vp.Y - 32f);
            ver.Position = new Vector2(vp.X - 100f, vp.Y - 32f);
        }
        Resized += LayoutFooter;
        LayoutFooter();
        ver.Size = new Vector2(72, 0);
        ver.AddThemeFontSizeOverride("font_size", 12);
        ver.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        ver.MouseFilter = MouseFilterEnum.Ignore;
    }
}
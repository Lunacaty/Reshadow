using System;
using System.Text.RegularExpressions;
using Godot;

namespace ShadowverseFan.Ui.Theme;

/// <summary>
/// 《影之诗：超凡世界》暗黑玻璃风（SV:WB Dark Glass）全局样式工厂。
///
/// 所有 UI 组件统一从这里取色与 StyleBoxFlat。
/// 严禁各处散落裸 Color、严禁使用 Godot 默认灰框 ——
/// 一旦有人绕过这个工厂，界面会立刻退回「一堆灰扑扑的默认按钮」。
/// </summary>
public static class SvWbTheme
{
    // ---- 核心背景与面板色 ----
    public static readonly Color BgAbyss      = new("090D16");
    public static readonly Color BgPanel      = new(0.07f, 0.09f, 0.15f, 0.94f);
    public static readonly Color BgSurface    = new("1A2234");
    public static readonly Color BgHover      = new("243048");
    public static readonly Color BgPressed    = new("0F172A");

    // ---- 边框与强调色 ----
    public static readonly Color BorderSubtle = new("2A3652");
    public static readonly Color AccentCyan   = new("00E5FF");
    public static readonly Color AccentGold   = new("F59E0B");
    public static readonly Color AccentPurple = new("C084FC");

    // ---- 语义状态色 ----
    public static readonly Color StatusValid  = new("10B981");
    public static readonly Color StatusWarn   = new("F59E0B");
    public static readonly Color StatusDanger = new("EF4444");

    // ---- 文本色 ----
    public static readonly Color TextPrimary  = new("F8FAFC");
    public static readonly Color TextSecondary= new("CBD5E1");
    public static readonly Color TextMuted    = new("64748B");

    /// <summary>获取八大职业的代表主题色。</summary>
    public static Color ClanColor(string clanName) => (clanName ?? "").ToLowerInvariant() switch
    {
        "forestcraft" => new Color("10B981"),
        "swordcraft"  => new Color("EAB308"),
        "runecraft"   => new Color("6366F1"),
        "dragoncraft" => new Color("EF4444"),
        "abysscraft" or "shadowcraft" or "bloodcraft" => new Color("A855F7"),
        "havencraft"  => new Color("FDE047"),
        "portalcraft" => new Color("06B6D4"),
        _             => new Color("94A3B8"),
    };

    /// <summary>职业英文名 → 官方简中名。</summary>
    public static string ClanDisplayZh(string clanName) => (clanName ?? "").ToLowerInvariant() switch
    {
        "forestcraft" => "精灵",
        "swordcraft"  => "皇家",
        "runecraft"   => "巫师",
        "dragoncraft" => "龙族",
        "abysscraft"  => "梦魇",
        "shadowcraft" => "死灵",
        "bloodcraft"  => "吸血鬼",
        "havencraft"  => "主教",
        "portalcraft" => "超越者",
        "neutral"     => "中立",
        _             => string.IsNullOrWhiteSpace(clanName) ? "中立" : clanName,
    };

    /// <summary>创建标准磨砂暗色面板 StyleBox。</summary>
    public static StyleBoxFlat MakePanelBox(
        Color? bg = null, Color? border = null,
        int radius = 8, int borderWidth = 1, int shadowSize = 6)
    {
        return new StyleBoxFlat
        {
            BgColor = bg ?? BgPanel,
            BorderColor = border ?? BorderSubtle,
            CornerRadiusTopLeft = radius,
            CornerRadiusTopRight = radius,
            CornerRadiusBottomLeft = radius,
            CornerRadiusBottomRight = radius,
            BorderWidthLeft = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthBottom = borderWidth,
            ShadowColor = new Color(0, 0, 0, 0.45f),
            ShadowSize = shadowSize,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 8,
            ContentMarginBottom = 8,
        };
    }

    /// <summary>
    /// 为 Button 挂载超凡世界风格的完整状态集。
    ///
    /// 三个不再犯的错误：
    ///   1. 填充色不能按 accent 的分通道乘系数（旧写法 accent.R*0.22 等），
    ///      那会把金色 F59E0B 压成污浊的橄榄褐 —— 既不亮也不像金。
    ///      现在填充统一用深海军蓝，强调色只出现在**描边与辉光**上，
    ///      整组按钮因此共享同一种材质感，靠边光区分主次。
    ///   2. 必须覆盖 focus —— Godot 默认主题会给获得焦点的按钮画一圈
    ///      系统蓝方框，这就是「一眼看上去还是默认样式」的主因。
    ///   3. 按下态不能只加深，还要把描边换成金色，给一个明确的点击反馈。
    /// </summary>
    public static void StyleButton(Button btn, bool isPrimary = false, Color? customAccent = null)
    {
        Color accent = customAccent ?? (isPrimary ? AccentGold : BorderSubtle);
        bool lit = isPrimary || customAccent.HasValue;

        // 深海军蓝底：比 BgSurface 更沉，让描边与文字浮起来
        Color fill       = new("141C2E");
        Color fillHover  = new("1E2A42");
        Color fillPress  = new("0B1120");
        Color fillDis    = new("0F1420");

        Color border     = lit ? accent : new Color("2A3652");
        Color borderHov  = lit ? Brighten(accent, 1.35f) : AccentCyan;
        Color borderPrs  = AccentGold;

        btn.AddThemeStyleboxOverride("normal",
            ButtonBox(fill, border, lit ? 2 : 1, lit ? 6 : 0, lit ? accent : null));
        btn.AddThemeStyleboxOverride("hover",
            ButtonBox(fillHover, borderHov, 2, 12, borderHov));
        btn.AddThemeStyleboxOverride("pressed",
            ButtonBox(fillPress, borderPrs, 2, 2, null));
        btn.AddThemeStyleboxOverride("disabled",
            ButtonBox(fillDis, new Color("1B2436"), 1, 0, null));
        // 关键：不覆盖会露出 Godot 默认的焦点蓝框
        btn.AddThemeStyleboxOverride("focus",
            ButtonBox(fillHover, borderHov, 2, 10, borderHov));

        btn.AddThemeColorOverride("font_color", lit ? Brighten(accent, 1.9f) : TextPrimary);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_pressed_color", new Color("FDE68A"));
        btn.AddThemeColorOverride("font_focus_color", Colors.White);
        btn.AddThemeColorOverride("font_disabled_color", TextMuted);
        btn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    /// <summary>把颜色朝白色提亮（factor &gt; 1 变亮，&lt; 1 变暗）。</summary>
    public static Color Brighten(Color c, float factor)
        => new(Math.Min(1f, c.R * factor), Math.Min(1f, c.G * factor), Math.Min(1f, c.B * factor), c.A);

    /// <summary>
    /// 按钮专用的圆角盒。
    ///
    /// glow 非空时给一层同色柔和外发光 —— 主按钮因此有「被点亮」的感觉，
    /// 而次按钮保持干净无光，主次一眼可分。
    /// </summary>
    public static StyleBoxFlat ButtonBox(Color bg, Color border, int borderWidth,
                                        int glow, Color? glowColor)
    {
        return new StyleBoxFlat
        {
            BgColor = bg,
            BorderColor = border,
            CornerRadiusTopLeft = 10,
            CornerRadiusTopRight = 10,
            CornerRadiusBottomLeft = 10,
            CornerRadiusBottomRight = 10,
            BorderWidthLeft = borderWidth,
            BorderWidthRight = borderWidth,
            BorderWidthTop = borderWidth,
            BorderWidthBottom = borderWidth,

            // 顶部一道更亮的描边，模拟光源从上方打下来
            AntiAliasing = true,

            ShadowColor = glowColor.HasValue
                ? new Color(glowColor.Value.R, glowColor.Value.G, glowColor.Value.B, 0.38f)
                : new Color(0, 0, 0, 0.40f),
            ShadowSize = glow,
            ShadowOffset = new Vector2(0, 1),

            ContentMarginLeft = 18,
            ContentMarginRight = 18,
            ContentMarginTop = 10,
            ContentMarginBottom = 10,
        };
    }

    /// <summary>
    /// 把 ChineseCardPrinter 输出的纯文本转成带高亮的 BBCode。
    ///
    /// 顺序有讲究：【构筑规则】必须先替换 —— 否则它会被通用的
    /// 【([^】]+)】规则先吃掉，变成普通的金色能力头。
    /// </summary>
    public static string FormatRichCardText(string plainZh)
    {
        if (string.IsNullOrEmpty(plainZh)) return "[color=#64748B]（白板无效果）[/color]";
        string s = plainZh;
        s = s.Replace("【构筑规则】", "[color=#C084FC][b]【构筑规则】[/b][/color]");
        s = Regex.Replace(s, @"【([^】]+)】", "[color=#FBBF24][b]【$1】[/b][/color]");
        s = Regex.Replace(s, @"《([^》]+)》", "[color=#38BDF8]《$1》[/color]");
        return s;
    }
}

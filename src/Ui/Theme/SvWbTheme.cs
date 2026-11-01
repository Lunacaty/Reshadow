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

    /// <summary>为 Button 一键挂载超凡世界风格的四态。</summary>
    public static void StyleButton(Button btn, bool isPrimary = false, Color? customAccent = null)
    {
        Color accent = customAccent ?? (isPrimary ? AccentCyan : BorderSubtle);
        Color bgNorm = isPrimary
            ? new Color(accent.R * 0.22f, accent.G * 0.28f, accent.B * 0.35f, 0.95f)
            : BgSurface;
        Color bgHov = isPrimary
            ? new Color(accent.R * 0.35f, accent.G * 0.42f, accent.B * 0.50f, 1.0f)
            : BgHover;

        btn.AddThemeStyleboxOverride("normal", MakePanelBox(bgNorm, accent, 6, isPrimary ? 2 : 1, 3));
        btn.AddThemeStyleboxOverride("hover", MakePanelBox(bgHov, AccentCyan, 6, 2, 6));
        btn.AddThemeStyleboxOverride("pressed", MakePanelBox(BgPressed, AccentGold, 6, 2, 2));
        btn.AddThemeStyleboxOverride("disabled", MakePanelBox(new Color("0F1420"), new Color("1E2638"), 6, 1, 0));
        btn.AddThemeColorOverride("font_color", TextPrimary);
        btn.AddThemeColorOverride("font_hover_color", Colors.White);
        btn.AddThemeColorOverride("font_disabled_color", TextMuted);
        btn.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
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

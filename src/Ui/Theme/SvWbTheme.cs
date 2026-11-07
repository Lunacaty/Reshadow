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
    /// <summary>
    /// 职业配色。
    ///
    /// 职业名在数据里已经是中文（「皇家」「精灵」…），这里按中文匹配。
    /// 自定义职业没有预设色，用名字哈希稳定分配一个 ——
    /// 全部落进默认灰会让玩家分不清哪个是哪个职业。
    /// </summary>
    public static Color ClanColor(string clanName)
    {
        string k = (clanName ?? "").Trim();
        return k switch
        {
            "精灵" => new Color("10B981"),
            "皇家护卫" => new Color("EAB308"),
            "巫师" => new Color("6366F1"),
            "龙族" => new Color("EF4444"),
            "梦魇" => new Color("A855F7"),
            "主教" => new Color("FDE047"),
            "超越者" => new Color("06B6D4"),
            "中立" => new Color("94A3B8"),
            _ => HashColor(k),
        };
    }

    /// <summary>自定义职业：按名字哈希稳定取色（同名永远同色，不同名尽量不同）。</summary>
    private static Color HashColor(string name)
    {
        if (string.IsNullOrEmpty(name)) return new Color("94A3B8");
        unchecked
        {
            int h = 17;
            foreach (char c in name) h = h * 31 + c;
            float hue = (h & 0x7FFFFFFF) % 360;
            return Color.FromHsv(hue, 0.55f, 0.85f);
        }
    }

    /// <summary>
    /// 职业名的显示形式。
    ///
    /// 职业名在程序外就已本地化（卡包 clan 字段存的就是「皇家」），
    /// 这里原样返回 —— 代码里不出现任何职业中英对照表。
    /// </summary>
    /// <summary>
    /// 职业名的显示形式：**原样返回**。
    ///
    /// 职业名不是需要翻译的术语，它就是玩家在卡包里写的那个名字
    /// （卡包 clan 字段存的就是「皇家」这种）。查询任何词表都是错的 ——
    /// 那等于在游戏里又塞了一套词条，而玩家的自定义职业名会被覆盖。
    /// </summary>
    public static string ClanDisplayZh(string clanName) => clanName ?? "";

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
    /// OptionButton / 任意按钮的**弹出菜单**换肤。
    ///
    /// PopupMenu 是独立主题，StyleButton 管不到它 ——
    /// 不做这一步，下拉展开时会弹出一张系统灰白菜单，
    /// 和深色界面完全不搭，是第二明显的默认样式残留。
    /// </summary>
    public static void StyleOptionPopup(OptionButton picker)
    {
        if (picker == null) return;

        // OptionButton.FitToLongestItem 默认为 true：一旦把几百张卡塞进去，
        // 它会无视 CustomMinimumSize，把宽度撑到最长条目的宽度（约 360~420px），
        // 整行被顶出屏幕右边界。这里全局锁死，过长文字用省略号。
        picker.FitToLongestItem = false;
        picker.ClipText = true;
        picker.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;

        var popup = picker.GetPopup();
        if (popup == null) return;

        var panel = new StyleBoxFlat
        {
            BgColor = new Color("131A29"),
            BorderColor = new Color("2E3B57"),
            CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8,
            CornerRadiusBottomRight = 8,
            BorderWidthLeft = 1, BorderWidthRight = 1,
            BorderWidthTop = 1, BorderWidthBottom = 1,
            ShadowColor = new Color(0, 0, 0, 0.55f),
            ShadowSize = 12,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 6,
            ContentMarginBottom = 6,
        };
        var hover = new StyleBoxFlat
        {
            BgColor = new Color("243048"),
            CornerRadiusTopLeft = 6,
            CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6,
            CornerRadiusBottomRight = 6,
        };
        var separator = new StyleBoxLine { Color = new Color("2E3B57"), Thickness = 1 };

        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", hover);
        popup.AddThemeStyleboxOverride("separator", separator);
        popup.AddThemeColorOverride("font_color", TextSecondary);
        popup.AddThemeColorOverride("font_hover_color", Colors.White);
        popup.AddThemeColorOverride("font_disabled_color", TextMuted);
        popup.AddThemeFontSizeOverride("font_size", 13);
    }

    /// <summary>
    /// 给滚动容器换上暗色细滚动条。
    ///
    /// 不做这件事，Godot 会给每个 ScrollContainer 画默认的浅灰滚动条 ——
    /// 在星象深色底上像两条贴上去的白胶带，是「一眼看去仍是默认样式」
    /// 最明显的残留。项目里 9 个滚动容器原本一个都没处理。
    /// </summary>
    public static void StyleScroll(ScrollContainer scroll)
    {
        if (scroll == null) return;

        scroll.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

        // 滚动条是容器内部的 VScrollBar / HScrollBar 子节点，
        // 主题覆盖必须打到子节点上才生效。
        ApplyScrollBar(scroll.GetNodeOrNull<VScrollBar>("VScrollBar"));
        ApplyScrollBar(scroll.GetNodeOrNull<HScrollBar>("HScrollBar"));
    }

    private static void ApplyScrollBar(ScrollBar bar)
    {
        if (bar == null) return;

        bar.AddThemeStyleboxOverride("scroll", new StyleBoxEmpty());
        bar.AddThemeStyleboxOverride("grabber", ScrollGrabber("2E3B57"));
        bar.AddThemeStyleboxOverride("grabber_highlight", ScrollGrabber("4A5C82"));
        bar.AddThemeStyleboxOverride("grabber_pressed", ScrollGrabber(AccentGold.ToHtml(false)));
    }

    private static StyleBoxFlat ScrollGrabber(string hex) => new()
    {
        BgColor = new Color(hex),
        CornerRadiusTopLeft = 4,
        CornerRadiusTopRight = 4,
        CornerRadiusBottomLeft = 4,
        CornerRadiusBottomRight = 4,
    };

    /// <summary>
    /// 把 ChineseCardPrinter 输出的纯文本转成带高亮的 BBCode。
    ///
    /// 顺序有讲究：【构筑规则】必须先替换 —— 否则它会被通用的
    /// 【([^】]+)】规则先吃掉，变成普通的金色能力头。
    /// </summary>
    /// <summary>
    /// 把本体与四类子卡（激奏 / 结晶 / 关联纹章 / 关联信仰）拼成一段完整富文本。
    ///
    /// 早先只有构筑台右键弹窗手动拼了子卡，战斗中的左上角详情面板、
    /// 悬浮窗、卡片图鉴全都只调 PrintCard(本体) ——
    /// 玩家摸到「入场曲：获得纹章：XXX」的卡，点开详情完全看不到那个纹章
    /// 是什么效果（179 号文档 D4）。
    /// </summary>
    public static string FormatFullCardText(
        KernelV3.Ast.CardDef def, KernelV3.Lang.ChineseCardPrinter printer)
    {
        if (def == null || printer == null) return "";
        var sb = new System.Text.StringBuilder(FormatRichCardText(printer.PrintCard(def)));

        void AppendSub(string tag, KernelV3.Ast.CardDef sub, bool showCost)
        {
            if (sub?.Abilities == null || sub.Abilities.Count == 0) return;
            string head = showCost
                ? "【" + tag + " (" + sub.BaseCost + "费)】"
                : "【" + tag + "：" + (sub.NameChs ?? sub.Name) + "】";
            sb.Append("\n[color=#38BDF8][b]").Append(head).Append("[/b][/color]\n")
              .Append(FormatRichCardText(printer.PrintCard(sub)));
        }
        AppendSub("激奏", def.AccelerateDef, true);
        AppendSub("结晶", def.CrystallizeDef, true);
        AppendSub("关联纹章", def.CrestDef, false);
        AppendSub("关联信仰", def.FaithDef, false);
        return sb.ToString();
    }

    /// <summary>
    /// 纯显示层：把 AST 规范文本 / 对局日志里的 #卡牌ID 换成高亮卡名。
    ///
    /// 数据层（AST、PyAST 源码、存档 JSON）永远保持 #90011110 这种纯数字 ——
    /// 它跨语言、跨改名都稳定。只有显示层做富文本映射（179 号文档 C3）。
    /// </summary>
    public static string FormatAstWithCardNames(
        string rawAstText, Func<int, KernelV3.Ast.CardDef> cardLookup = null)
    {
        if (string.IsNullOrEmpty(rawAstText)) return "";

        // AST 语法里有 [enhance(4)] / [dur=ThisTurn] 这种方括号，
        // 直接喂给 RichTextLabel 会被 BBCode 解析器当成未知标签吞掉，
        // 必须先把 [ 转义掉。
        string safe = rawAstText.Replace("[", "[lb]");

        return Regex.Replace(safe, @"#(\d{4,9})\b", m =>
        {
            if (!int.TryParse(m.Groups[1].Value, out int cid)) return m.Value;
            var def = cardLookup?.Invoke(cid);
            string name = def != null
                ? (!string.IsNullOrWhiteSpace(def.NameChs) ? def.NameChs : def.Name)
                : KernelV3.Packs.AuthoritativeNames.ChineseOf(cid);

            if (string.IsNullOrWhiteSpace(name))
                return $"[color=#94A3B8]#{cid}[/color]";

            return $"[bgcolor=#1E293B][color=#38BDF8][b]《{name}》[/b][/color]"
                 + $"[color=#64748B](#{cid})[/color][/bgcolor]";
        });
    }

    public static string FormatRichCardText(string plainZh)
    {
        if (string.IsNullOrEmpty(plainZh)) return "[color=#64748B]（白板无效果）[/color]";
        string s = plainZh;
        s = s.Replace("【构筑规则】", "[color=#C084FC][b]【构筑规则】[/b][/color]");
        s = Regex.Replace(s, @"【([^】]+)】", "[color=#FBBF24][b]【$1】[/b][/color]");
        s = Regex.Replace(s, @"《([^》]+)》", "[color=#38BDF8]《$1》[/color]");
        return s;
    }

    /// <summary>
    /// 让固定尺寸的弹窗面板在任意窗口尺寸 / 全屏切换下始终保持屏幕中央。
    ///
    /// 取代「写死 1280x720 基准坐标」—— 那种写法在 1920x1080 下会整体偏左上。
    /// </summary>
    public static void KeepCentered(Control host, Control panel, Vector2 panelSize, Vector2 offset = default)
    {
        if (host == null || panel == null) return;
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        panel.Size = panelSize;
        panel.CustomMinimumSize = panelSize;

        void UpdatePos()
        {
            if (!GodotObject.IsInstanceValid(host) || !GodotObject.IsInstanceValid(panel)) return;
            var vp = host.Size;
            if (vp.X <= 0 || vp.Y <= 0) vp = host.GetViewportRect().Size;
            panel.Position = new Vector2(
                Mathf.Max(12f, (vp.X - panelSize.X) * 0.5f + offset.X),
                Mathf.Max(12f, (vp.Y - panelSize.Y) * 0.5f + offset.Y));
        }

        host.Resized += UpdatePos;
        UpdatePos();
    }
}

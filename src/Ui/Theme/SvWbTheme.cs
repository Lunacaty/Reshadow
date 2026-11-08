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
    /// <summary>AST 预览用的全语法 token 正则（与右侧 CodeEdit 的 7 色系同构）。</summary>
    /// <summary>
    /// ⚠ 必须与 AstCodeEdit.GradientAstHighlighter.TokenRegex **逐字一致**。
    /// 188 号文档 缺陷四 要求左侧预览与右侧编辑器「完全一致的 7 大色系」，
    /// 两边正则一旦分叉（此前左侧缺 22 个英文 CNL 词），同一条卡在两侧就会高亮不同。
    /// </summary>
    private static readonly Regex AstPreviewTokenRegex = new(
        @"(?<comment>//.*$)" +
        @"|(?<str>""[^""]*""|【[^】]+】|《[^》]+》)" +
        @"|(?<card>#\d+\b)" +
        @"|(?<slot>\$[a-zA-Z0-9_]+|\b(?:ctr|prop|sum|split|trait|clan):[a-zA-Z0-9_\u4e00-\u9fa5]+)" +
        @"|(?<trig>\b(?:fanfare|last_words|evolve|super_evolve|strike|clash|engage|aura|turn_start|turn_end|crest|faith|accelerate|crystallize|on_\w+|Fanfare|Last\s+Words|Evolve|Super-Evolve|Strike|Clash|Engage|Activate|Countdown|Necromancy|Earth\s+Rite|Combo|Rally|Overflow|Wrath|Spellboost|Skybound\s+Art|Super\s+Skybound\s+Art|Enhance|Fuse|Reanimate|Invoke)\b:?)" +
        @"|(?<act>\b(?:card|deck_mod|keywords|deal|draw|restore|summon|add_to_hand|destroy|banish|discard|bounce|buff|set_stats|grant_keyword|remove_keyword|discover|reanimate|gain_crest|if|elif|else|repeat|for_each|modes|option|pay|where|counter|select|transform|invoke_ability|grant_ability|move|give|gain|damage|life|leader|followers?|amulets?|spells?|cards?)\b)" +
        @"|(?<kw>\b(?:ward|rush|storm|bane|drain|ambush|intimidate|barrier|ignores_ward|free_evolve|free_super_evolve|immune_\w+|cannot_\w+|Ward|Rush|Storm|Bane|Drain|Ambush|Aura|Intimidate|Barrier)\b)" +
        @"|(?<tgt>\b(?:self|opponent|any|board|hand|deck|cemetery|banished|crest_area|ex_area|All|Choose|Random|First|Last|Distinct|Follower|Spell|Amulet|Crest|Faith|Leader|ThisTurn|Permanent|UntilEndOfOpponentTurn)\b)" +
        @"|(?<num>[+-]?\d+(?:/[+-]?\d+)?)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// AST 规范形预览 -> BBCode。
    ///
    /// 188 号文档 缺陷四：此前只把 #卡号 换成卡名，其余 fanfare: / deal / $self /
    /// ctr:pp 全部落在 default_color 的暗灰单色里，既没有语法层次也没有渐变。
    /// 现在按行扫描全语法正则，与右侧 GradientAstHighlighter 使用**完全同构**的
    /// 7 大色系，左侧预览与右侧编辑器看起来是同一套配色。
    /// </summary>
    public static string FormatAstWithCardNames(
        string rawAstText, Func<int, KernelV3.Ast.CardDef> cardLookup = null)
    {
        if (string.IsNullOrEmpty(rawAstText)) return "";

        var sb = new System.Text.StringBuilder(rawAstText.Length * 8);
        int lastIdx = 0;

        // 普通片段：整句奶白 -> 鎏金渐变（浅淡，不抢 token 的视觉）。
        void FlushPlain(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return;
            sb.Append(WrapSentenceGradient(plain.Replace("[", "[lb]"), GradGold.Light, GradGold.Main));
        }

        foreach (Match m in AstPreviewTokenRegex.Matches(rawAstText))
        {
            if (!m.Success || m.Length == 0) continue;
            if (m.Index > lastIdx) FlushPlain(rawAstText.Substring(lastIdx, m.Index - lastIdx));

            if (m.Groups["card"].Success && int.TryParse(m.Value.AsSpan(1), out int cid))
            {
                var def = cardLookup?.Invoke(cid);
                string name = def != null
                    ? (!string.IsNullOrWhiteSpace(def.NameChs) ? def.NameChs : def.Name)
                    : KernelV3.Packs.AuthoritativeNames.ChineseOf(cid);

                if (string.IsNullOrWhiteSpace(name))
                    sb.Append(WrapSentenceGradient(m.Value, new Color("FEF08A"), new Color("EAB308")));
                else
                    sb.Append("[bgcolor=#1E293B]")
                      .Append(WrapSentenceGradient("《" + name + "》", GradCyan.Light, new Color("38BDF8"), bold: true))
                      .Append("[color=#64748B](#").Append(cid).Append(")[/color][/bgcolor]");
            }
            else if (m.Groups["comment"].Success)
            {
                sb.Append(WrapSentenceGradient(m.Value.Replace("[", "[lb]"),
                    new Color("94A3B8"), new Color("475569")));
            }
            else
            {
                var (light, main, bold) = m.Groups["trig"].Success ? (new Color("FFF9DB"), new Color("F59E0B"), true)
                                        : m.Groups["act"].Success  ? (GradPurple.Light, GradPurple.Main, true)
                                        : m.Groups["slot"].Success ? (GradCrimson.Light, new Color("FB7185"), false)
                                        : (m.Groups["kw"].Success || m.Groups["str"].Success)
                                              ? (GradGreen.Light, new Color("10B981"), true)
                                        : m.Groups["tgt"].Success  ? (GradCyan.Light, new Color("06B6D4"), false)
                                        : (new Color("FEF08A"), new Color("EAB308"), false);
                sb.Append(WrapSentenceGradient(m.Value.Replace("[", "[lb]"), light, main, bold));
            }
            lastIdx = m.Index + m.Length;
        }

        if (lastIdx < rawAstText.Length) FlushPlain(rawAstText.Substring(lastIdx));
        return sb.ToString();
    }
    // ========================================================================
    // 整句渐变渲染管线（187 号文档 专题二）
    //
    // 全游戏 55+ 个 UI 文件不可能逐个改色，因此在主题层建三条统一通道：
    //   通道 A · RichTextLabel  -> WrapSentenceGradient（逐字 [color] 插值）
    //   通道 B · Label / Button  -> ApplyGradientText（94° 线性渐变 shader）
    //   通道 C · _Draw() 手绘字  -> DrawGradientString（逐字插值 + 描边）
    // ========================================================================

    /// <summary>
    /// 预设渐变色对（浅主色 -> 主色）。
    ///
    /// ⚠ 浅色**不是越白越好**：早先用的 #FFF3CF / #E0F7FA 之类饱和度只有
    /// 0.09~0.19、亮度 0.93~0.97，实际观感就是纯白，整句渐变只看到后半段在变色。
    /// 现在统一按「同色相、明度提高、饱和度压到主色约 62%」推导，
    /// 起点仍是明显的彩度色，与主色同属一个色系，过渡才有层次。
    /// </summary>
    public static readonly (Color Light, Color Main) GradGold    = (new Color("F7DEA1"), new Color("E3BD63"));
    public static readonly (Color Light, Color Main) GradCyan    = (new Color("5EE8F7"), new Color("00E5FF"));
    public static readonly (Color Light, Color Main) GradPurple  = (new Color("D3AEF7"), new Color("C084FC"));
    public static readonly (Color Light, Color Main) GradGreen   = (new Color("84F7CD"), new Color("34D399"));
    public static readonly (Color Light, Color Main) GradCrimson = (new Color("F78699"), new Color("F43F5E"));

    /// <summary>
    /// 按主色推导「浅主色 -> 主色」。
    /// 浅主色取「向奶白提亮 62%」，保证同色系内的过渡自然。
    /// 主色本身就是白/浅灰时，退回默认的奶白 -> 鎏金。
    /// </summary>
    public static (Color Light, Color Main) DeriveGradientPair(Color mainColor)
    {
        if (mainColor.S < 0.12f && mainColor.V > 0.85f) return GradGold;

        // 早先用「向奶白 #FFF8E7 插值 62%」，同样会把起点冲成近白。
        // 改为保持主色色相，只提明度、压饱和度。
        mainColor.ToHsv(out float h, out float s, out float v);
        float lightV = Mathf.Min(0.97f, Mathf.Max(0.90f, v + (1f - v) * 0.85f));
        float lightS = s * 0.62f;
        return (Color.FromHsv(h, lightS, lightV), mainColor);
    }

    /// <summary>
    /// 通道 A：把整句包装成逐字渐变 BBCode（供 RichTextLabel 使用）。
    ///
    /// 以「整个句子」为单位从浅主色线性过渡到主色 —— 这正是需求里那张
    /// CSS 案例的效果：一个字一个色阶，而不是整块同色。
    /// 空白与换行原样输出（不被 [color] 包裹），避免行首缩进被染色。
    /// </summary>
    public static string WrapSentenceGradient(string sentence, Color lightCol, Color mainCol,
                                              bool bold = false)
    {
        if (string.IsNullOrEmpty(sentence)) return "";
        var sb = new System.Text.StringBuilder(sentence.Length * 24);
        if (bold) sb.Append("[b]");
        int len = sentence.Length;
        for (int i = 0; i < len; i++)
        {
            char ch = sentence[i];
            if (ch is (char)13 or (char)10 or (char)32) { sb.Append(ch); continue; }
            float t = len <= 1 ? 0f : (float)i / (len - 1);
            string hex = lightCol.Lerp(mainCol, t).ToHtml(false);
            sb.Append("[color=#").Append(hex).Append((char)93).Append(ch).Append("[/color]");
        }
        if (bold) sb.Append("[/b]");
        return sb.ToString();
    }

    // ---- 通道 B：Label / Button 的 94° 线性渐变材质 ----
    private static Shader _textGradShader;

    /// <summary>
    /// 通道 B：给普通 Label / Button 挂上「浅主色 -> 主色」渐变材质。
    /// 按控件 UV 做 94°（近似横向略偏纵）插值，保留低亮度像素（描边）不被染亮。
    /// </summary>
    /// <summary>
    /// 通道 B：给 Label 这类**纯文字控件**挂「浅主色 -> 主色」渐变材质。
    ///
    /// ⚠ 绝对不要往 Button / PanelContainer 这类容器上挂：
    /// Material 作用于整个控件树的绘制结果，会把子控件的 COLOR 一起改写，
    /// 实测按钮文字会直接消失（只剩边框）。只对 Label 使用。
    /// </summary>
    /// <param name="spanWidth">显式指定渐变跨度；传 0 表示按文字实际宽度自动计算。</param>
    public static void ApplyGradientText(CanvasItem ctrl, Color? mainColor = null, float spanWidth = 0f)
    {
        if (ctrl is not Label lbl) return;
        var pair = mainColor.HasValue ? DeriveGradientPair(mainColor.Value) : GradGold;

        // 187 号文档原稿用 UV.x 算渐变比例 —— 那是**错的**：
        // Godot 里每个字符是独立 Glyph Quad，UV 是该字符在字体图集里的局部坐标，
        // 结果会变成「每个字自己内部从左到右变色」，完全不是整句渐变。
        // 正确做法：vertex() 里取控件局部像素坐标 VERTEX.x，fragment() 里除以 span_w。
        //
        // 189 号文档 问题 2：居中对齐的 Label（RESHADOW / VICTORY 这类）只传 span_w
        // 从 x=0 起算是不够的 —— 1280 宽容器里文字画在 480~800，
        // v_x/240 早就 clamp 到 1.0，整句直接变成纯终点色。
        // 必须再加 start_x，从文字真正的起始像素开始算。
        _textGradShader ??= new Shader
        {
            Code = @"shader_type canvas_item;
uniform vec4 col_light : source_color = vec4(1.0, 0.953, 0.812, 1.0);
uniform vec4 col_main  : source_color = vec4(0.890, 0.741, 0.388, 1.0);
uniform float start_x = 0.0;
uniform float span_w = 240.0;
varying float v_x;
void vertex() {
    v_x = VERTEX.x;
}
void fragment() {
    vec4 tex = texture(TEXTURE, UV);
    float t = clamp((v_x - start_x) / max(1.0, span_w), 0.0, 1.0);
    vec3 grad = mix(col_light.rgb, col_main.rgb, t);
    // 描边与阴影亮度低，保留原色，避免整块糊掉
    float lum = dot(COLOR.rgb, vec3(0.299, 0.587, 0.114));
    vec3 final_rgb = lum < 0.20 ? COLOR.rgb : grad;
    COLOR = vec4(final_rgb, tex.a * COLOR.a);
}
"
        };

        // 量出文字真实宽度与容器宽度，据此推 start_x / span_w。
        var font = lbl.GetThemeDefaultFont() ?? ThemeDB.FallbackFont;
        int fs = lbl.HasThemeFontSizeOverride("font_size") ? lbl.GetThemeFontSize("font_size") : 14;
        float textW = (font != null && !string.IsNullOrEmpty(lbl.Text))
            ? font.GetStringSize(lbl.Text, HorizontalAlignment.Left, -1, fs).X
            : 180f;
        float boxW = lbl.Size.X > 1f ? lbl.Size.X : textW;
        float actualSpan = spanWidth > 1f ? spanWidth : Mathf.Max(24f, Mathf.Min(textW, boxW));

        float startX = lbl.HorizontalAlignment switch
        {
            HorizontalAlignment.Center => Mathf.Max(0f, (boxW - actualSpan) * 0.5f),
            HorizontalAlignment.Right  => Mathf.Max(0f, boxW - actualSpan),
            _                          => 0f,
        };

        var mat = new ShaderMaterial { Shader = _textGradShader };
        mat.SetShaderParameter("col_light", pair.Light);
        mat.SetShaderParameter("col_main", pair.Main);
        mat.SetShaderParameter("start_x", startX);
        mat.SetShaderParameter("span_w", actualSpan);
        lbl.Material = mat;
    }

    // ---- 189 号文档 问题 3：全局自动渐变挂载器 ----
    //
    // 手动去 55 个 UI 文件里改几百处 new Label 不现实，改为挂 SceneTree.NodeAdded：
    // 任何 Label 进树时自动补材质，覆盖全游戏，包括以后新增的界面。
    private static bool _autoHookInstalled;

    /// <summary>安装全局 Label 自动渐变钩子（幂等，可重复调用）。</summary>
    public static void InstallGlobalLabelGradientHook(SceneTree tree)
    {
        if (tree == null) return;
        if (!_autoHookInstalled)
        {
            _autoHookInstalled = true;
            tree.NodeAdded += OnNodeAddedToTree;
        }
        // 已在树里的 Label 也刷一遍，避免钩子装晚了漏掉首屏。
        ApplyGradientRecursively(tree.Root);
    }

    private static void ApplyGradientRecursively(Node root)
    {
        if (root == null) return;
        if (root is Label lbl) BindAutoGradient(lbl);
        foreach (var child in root.GetChildren())
            ApplyGradientRecursively(child);
    }

    private static void OnNodeAddedToTree(Node node)
    {
        if (node is Label lbl)
            Callable.From(() => BindAutoGradient(lbl)).CallDeferred();
    }


    /// <summary>
    /// 给单个 Label 绑渐变。
    ///
    /// 已经手动指定过渐变材质的 Label 一律跳过，不覆盖作者的意图；
    /// Resized 里重建材质，保证文字变长/换行后渐变区间仍然贴合实际宽度。
    /// </summary>
    private static void BindAutoGradient(Label lbl)
    {
        if (lbl == null || !GodotObject.IsInstanceValid(lbl)) return;
        if (lbl.Material is ShaderMaterial) return;

        void RefreshMat()
        {
            if (!GodotObject.IsInstanceValid(lbl)) return;
            if (lbl.Material is ShaderMaterial) return;

            Color? mainCol = lbl.HasThemeColorOverride("font_color")
                ? lbl.GetThemeColor("font_color")
                : null;

            // 基础顶点色必须是纯白，否则 COLOR 乘算会把浅主色压暗；
            // 描边仍由 font_outline_color 控制，不受影响。
            lbl.AddThemeColorOverride("font_color", Colors.White);
            ApplyGradientText(lbl, mainCol);
        }

        RefreshMat();
        lbl.Resized += RefreshMat;
    }

    /// <summary>
    /// 标题 / 弹窗头 / 主菜单标题的统一入口。
    /// 187 号文档 缺陷一 第 2 点：全项目只有 1 处调了 ApplyGradientText，其余全是素色。
    /// </summary>
    public static Label StyleLabel(string text, int fontSize, Color? mainColor = null,
                                   float spanWidth = 240f, bool bold = false)
    {
        var lbl = new Label { Text = text };
        lbl.AddThemeFontSizeOverride("font_size", fontSize);
        ApplyGradientText(lbl, mainColor, spanWidth);
        return lbl;
    }

    /// <summary>
    /// 通道 C：_Draw() 里的整句渐变绘制，替代裸 DrawString。
    /// 逐字按「字符中心 / 整句宽度」取插值位置，并先画深色描边再画亮色字身。
    /// </summary>
    public static void DrawGradientString(
        CanvasItem canvas, Font font, Vector2 pos, string text,
        int fontSize, Color lightCol, Color mainCol, Color? outlineCol = null)
    {
        if (canvas == null || font == null || string.IsNullOrEmpty(text)) return;
        Color dark = outlineCol ?? new Color(0.05f, 0.03f, 0.01f, 0.90f);
        float totalW = Mathf.Max(1f, font.GetStringSize(text, HorizontalAlignment.Left, -1, fontSize).X);
        float curX = 0f;

        for (int i = 0; i < text.Length; i++)
        {
            string ch = text[i].ToString();
            float cw = font.GetStringSize(ch, HorizontalAlignment.Left, -1, fontSize).X;
            float t = Mathf.Clamp((curX + cw * 0.5f) / totalW, 0f, 1f);
            Color col = lightCol.Lerp(mainCol, t);
            Vector2 pp = pos + new Vector2(curX, 0f);
            canvas.DrawString(font, pp + new Vector2(1.2f, 1.5f), ch, HorizontalAlignment.Left, -1, fontSize, dark);
            canvas.DrawString(font, pp, ch, HorizontalAlignment.Left, -1, fontSize, col);
            curX += cw;
        }
    }

    /// <summary>
    /// 卡面文本 -> 富文本 BBCode，**每一段都做整句渐变**（187 号文档 专题二）。
    ///
    /// 三种片段各用一套色对：
    ///   普通描述  ：奶白 #FFF3CF -> 鎏金 #E3BD63（需求里的核心案例）
    ///   【关键词】：亮金白 #FFFBEB -> 琥珀金 #F59E0B
    ///   《卡名》  ：冰白 #E0F7FA -> 星辉青 #38BDF8
    /// 【构筑规则】单独走紫色系。
    /// </summary>
    public static string FormatRichCardText(string plainZh)
    {
        if (string.IsNullOrEmpty(plainZh))
            return WrapSentenceGradient("（白板无效果）", new Color("CBD5E1"), new Color("64748B"));

        var lightKeyword = new Color("FFFBEB");
        var mainKeyword  = new Color("F59E0B");
        var lightCard    = new Color("E0F7FA");
        var mainCard     = new Color("38BDF8");

        var lines = plainZh.Split((char)10);
        var outLines = new System.Collections.Generic.List<string>(lines.Length);

        foreach (var line in lines)
        {
            // 切出 【…】、《…》 与普通句子片段，每段独立做浅主色->主色渐变。
            var tokens = Regex.Split(line, @"(【[^】]+】|《[^》]+》)");
            var sb = new System.Text.StringBuilder();
            foreach (var tk in tokens)
            {
                if (string.IsNullOrEmpty(tk)) continue;
                if (tk == "【构筑规则】")
                    sb.Append(WrapSentenceGradient(tk, GradPurple.Light, GradPurple.Main, bold: true));
                else if (tk.StartsWith("【") && tk.EndsWith("】"))
                    sb.Append(WrapSentenceGradient(tk, lightKeyword, mainKeyword, bold: true));
                else if (tk.StartsWith("《") && tk.EndsWith("》"))
                    sb.Append(WrapSentenceGradient(tk, lightCard, mainCard, bold: true));
                else
                    sb.Append(WrapSentenceGradient(tk, GradGold.Light, GradGold.Main));
            }
            outLines.Add(sb.ToString());
        }
        return string.Join(((char)10).ToString(), outLines);
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

using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 场上棋子（随从 / 护符）。
///
/// 随从：顶部圆拱金框立绘窗 + 左下蓝剑 + 右下红盾 + 底中【能力标记】。
/// 护符：切角矩形金框 + 底中【能力标记】+ 右下大号吟唱倒计时。
///
/// 底部标记不是「契」纹章，而是**能力类型的图示**：
///   · 红色书页 = 有谢幕曲（Last Words）
///   · 绿色旗帜 = 有时机能力（在 XX 时），触发时发出一圈光效
///   · 两者都没有 = 留空，不画任何图标
/// 把「无能力」也画成一个徽章是错的 —— 那会让玩家以为每张卡都有东西。
/// </summary>
public partial class BoardUnitWidget : Control
{
    public RuntimeEntity Entity { get; private set; }

    /// <summary>动画调度器（由 BattleScreenRoot 注入，全局共享一份）。</summary>
    public static BoardAnimDirector Director;


    public event Action<RuntimeEntity> OnClicked;
    public event Action<RuntimeEntity> OnRightClicked;
    public event Action<RuntimeEntity, Vector2> OnDragAttackStarted;
    public event Action<RuntimeEntity, Vector2> OnDragAttackMoved;
    public event Action<RuntimeEntity, Vector2> OnDragAttackReleased;

    private bool _selected;
    private bool _canAttack;

    /// <summary>
    /// 候选态：当前有一处挂起选择正在等玩家点它。
    /// 与 _selected 不同 —— _selected 是「已被选中的那个」，
    /// _isCandidate 是「可以被点的那些」。
    /// </summary>
    private bool _isCandidate;

    /// <summary>
    /// 压暗态：有挂起选择时，非候选棋子要淡出，
    /// 让玩家的视线自动落到可选目标上。
    /// </summary>
    private bool _dimmed;
    private UnitAbilityBadge _badge = UnitAbilityBadge.None;
    private float _triggerFlash;
    private float _pulseTime;
    private bool _dragging;

    public const float UnitW = 96f;
    public const float UnitH = 124f;
    private const float W = UnitW;
    private const float H = UnitH;

    /// <summary>设置挂起选择的候选态。</summary>
    public void SetChoiceState(bool isCandidate, bool dimmed)
    {
        _isCandidate = isCandidate;
        _dimmed = dimmed;
        QueueRedraw();
    }

    public void Setup(RuntimeEntity e, bool selected, bool canAttack)
    {
        Entity = e ?? throw new ArgumentNullException(nameof(e));
        _selected = selected;
        _canAttack = canAttack;
        _badge = UnitAbilityBadgeResolver.Resolve(e.Def);

        CustomMinimumSize = new Vector2(W, H);
        Size = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        QueueRedraw();
    }

    /// <summary>
    /// 把动画状态写进控件的**真实** Position / Scale / Modulate。
    ///
    /// 为什么不能用 DrawSetTransform 在 _Draw 里偏移：
    ///   那样控件本身仍占着座位坐标，于是
    ///     · 命中判定（GetGlobalRect）与视觉位置错位 —— 攻击点不中随从；
    ///     · 动画结束后视觉「瞬移」回座位，但玩家以为是别的卡；
    ///     · 与前后端分离冲突：UI 层出现了两个位置真相（绘制位置 / 控件位置）。
    ///   改为直接移动控件：视觉、命中、布局三者永远一致。
    ///
    /// 由 BoardAnimDirector 每帧驱动（见 BattleScreenRoot._Process）。
    /// </summary>
    public void ApplyMotion(UnitMotion m)
    {
        // 座位坐标**永远**由容器布局决定，控件自己不去猜。
        //
        // 早先的做法是「第一次进入动画时把当前 Position 记成 _seatPosition」——
        // 可那一刻容器可能还没完成布局，记下来的是 (0,0) 或上一帧的旧值，
        // 动画结束后棋子就停在了错误的位置。
        // 表现是「同一排里只有正在播动画的那张位置歪掉」。
        //
        // 现在：座位 = 容器给的布局结果（Position - 已经叠加的偏移），
        // 每帧都重新对齐，不缓存。
        Vector2 seat = Position - _appliedOffset;

        if (m.IsIdle)
        {
            if (_motionApplied)
            {
                Position = seat;
                Scale = Vector2.One;
                Modulate = Colors.White;
                _appliedOffset = Vector2.Zero;
                _motionApplied = false;
            }
            return;
        }

        Position = seat + m.Offset;
        _appliedOffset = m.Offset;

        float flipScale = Mathf.Abs(Mathf.Cos(Mathf.Pi * m.FlipX));
        Scale = new Vector2(m.Scale * flipScale, m.Scale);

        Modulate = new Color(1, 1, 1, Mathf.Clamp(m.Alpha, 0f, 1f));
        _motionApplied = true;
    }

    private bool _motionApplied;
    private Vector2 _appliedOffset;


    /// <summary>触发时机能力时调用：播放一圈光效。</summary>
    public void PlayTriggerFlash()
    {
        if (!UnitAbilityBadgeResolver.FlashesOnTrigger(_badge)) return;
        _triggerFlash = 1f;
    }

    public override void _Process(double delta)
    {
        // 有动画在跑就每帧重绘（变换在 _Draw 里应用）。
        if (Entity != null && Director != null && Director.IsAnimating(Entity.EntityId))
            QueueRedraw();

        // 候选圈需要持续脉动，所以有候选时也必须每帧重绘。
        if (_isCandidate)
        {
            _pulseTime += (float)delta;
            QueueRedraw();
        }

        if (_triggerFlash <= 0f) return;
        _triggerFlash = Mathf.Max(0f, _triggerFlash - (float)delta * 1.6f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (Entity?.Def == null) return;

        var def = Entity.Def;
        // 动画变换由 BoardUnitWidget.ApplyMotion 写到控件的 Position/Scale 上，
        // 不在 _Draw 里做 —— 见 ApplyMotion 的注释说明为什么必须如此。
        var font = ThemeDB.FallbackFont;
        Color clanCol = SvWbTheme.ClanColor(def.Clan.IsValid ? def.Clan.Name : null);

        bool isAmulet = def.Kind is CardKind.Amulet or CardKind.Crest;

        if (isAmulet) DrawAmuletFrame(clanCol, def);
        else DrawFollowerArch(clanCol, def);

        DrawArtwork(clanCol, def, isAmulet);

        if (def.Kind == CardKind.Follower)
        {
            DrawSwordBadge(new Vector2(16f, H - 22f), Entity.Attack, font);
            DrawShieldBadge(new Vector2(W - 16f, H - 22f), Entity.Life, font);
        }

        DrawAbilityBadge(new Vector2(W * 0.5f, H - 12f), font);

        if (isAmulet) DrawCountdown(def, font);

        if (Entity.IsEvolved || Entity.IsSuperEvolved)
        {
            Color glow = Entity.IsSuperEvolved ? SvWbTheme.AccentPurple : SvWbTheme.AccentGold;
            DrawRect(new Rect2(0, 0, W, H), glow, false, 3f);
        }

        // 候选态：金色呼吸瞄准圈。
        // 这是 SelectTarget 挂起时给玩家的唯一视觉线索 ——
        // 没有它，玩家只能靠「点了有反应/没反应」来猜哪些是合法目标。
        if (_isCandidate)
        {
            float pulse = 0.55f + 0.45f * Mathf.Sin(_pulseTime * 4f);
            var gold = new Color(0.98f, 0.85f, 0.28f, pulse);
            DrawRect(new Rect2(-5, -5, W + 10, H + 10), gold, false, 3f);
            DrawRect(new Rect2(-9, -9, W + 18, H + 18),
                     new Color(0.98f, 0.85f, 0.28f, pulse * 0.35f), false, 1.5f);

            // 四角瞄准刻线
            float c = 14f;
            var corners = new[]
            {
                (new Vector2(-5, -5), new Vector2(-5 + c, -5), new Vector2(-5, -5 + c)),
                (new Vector2(W + 5, -5), new Vector2(W + 5 - c, -5), new Vector2(W + 5, -5 + c)),
                (new Vector2(-5, H + 5), new Vector2(-5 + c, H + 5), new Vector2(-5, H + 5 - c)),
                (new Vector2(W + 5, H + 5), new Vector2(W + 5 - c, H + 5), new Vector2(W + 5, H + 5 - c)),
            };
            foreach (var (o, a, b) in corners)
            {
                DrawLine(o, a, gold, 2.5f);
                DrawLine(o, b, gold, 2.5f);
            }
        }

        // 压暗态：非候选棋子淡出
        if (_dimmed)
            DrawRect(new Rect2(0, 0, W, H), new Color(0.02f, 0.03f, 0.06f, 0.62f));

        if (_selected) DrawRect(new Rect2(-3, -3, W + 6, H + 6), SvWbTheme.AccentCyan, false, 2.5f);

        // 可攻击光环：绿圈（还原原版「这个随从能动了」的提示）
        if (_canAttack && !_selected)
            DrawRect(new Rect2(-2, -2, W + 4, H + 4), new Color(0.20f, 0.83f, 0.60f, 0.85f), false, 2.5f);

        // 时机能力触发光效：一圈扩散的光环
        if (_triggerFlash > 0f)
        {
            float a = _triggerFlash;
            float r = (1f - a) * 42f;
            DrawArc(new Vector2(W * 0.5f, H * 0.5f), 40f + r, 0, Mathf.Tau, 36,
                    new Color(0.35f, 1.0f, 0.55f, a * 0.9f), 3f);
        }
    }

    private void DrawFollowerArch(Color clanCol, CardDef def)
    {
        // 顶部圆拱：近似「圆拱窗」的上半弧 + 直身
        Vector2[] frame =
        {
            new(3f, 34f),
            new(W * 0.22f, 8f),
            new(W * 0.5f, 2f),
            new(W * 0.78f, 8f),
            new(W - 3f, 34f),
            new(W - 3f, H - 6f),
            new(3f, H - 6f),
        };
        // 外框按稀有度上色。桌面上卡很小，只染最外一圈 ——
        // 整块铺满稀有度色会让场面花掉，影响落子判断。
        DrawColoredPolygon(frame, RarityRim(def));

        Vector2[] inner =
        {
            new(6f, 36f),
            new(W * 0.24f, 12f),
            new(W * 0.5f, 6f),
            new(W * 0.76f, 12f),
            new(W - 6f, 36f),
            new(W - 6f, H - 9f),
            new(6f, H - 9f),
        };
        DrawColoredPolygon(inner, new Color("161228"));
    }

    /// <summary>
    /// 稀有度外框色（与 CardVisualWidget.RarityPalette 的 rim 一致）。
    ///
    /// 桌面卡很小，只染最外一圈；整块铺满稀有度色会让场面花掉。
    /// </summary>
    private static Color RarityRim(CardDef d) => (d?.Rarity ?? 0) switch
    {
        1 => new Color("B07A4A"),
        2 => new Color("C8D0DC"),
        3 => new Color("E6B23C"),
        4 => new Color("C77DFF"),
        _ => new Color("B07A4A"),
    };

    private void DrawAmuletFrame(Color clanCol, CardDef def)
    {
        Vector2[] frame =
        {
            new(8f, 4f), new(W - 8f, 4f), new(W - 3f, 16f),
            new(W - 3f, H - 6f), new(3f, H - 6f), new(3f, 16f),
        };
        DrawColoredPolygon(frame, new Color("D6C7B2"));

        Vector2[] inner =
        {
            new(11f, 7f), new(W - 11f, 7f), new(W - 6f, 18f),
            new(W - 6f, H - 9f), new(6f, H - 9f), new(6f, 18f),
        };
        DrawColoredPolygon(inner, new Color("161228"));
    }

    /// <summary>
    /// 把立绘按棋子外轮廓逐行裁切后画出来。
    ///
    /// 参数 src 是「已按比例裁好的源矩形」，dst 是目标矩形。
    /// 对每一行：算出该行在轮廓内的左右边界，只画这一段。
    /// </summary>
    private void DrawArtClippedToOutline(Texture2D tex, Rect2 dst, Rect2 src, bool isAmulet)
    {
        const int ROW = 2;
        int rows = Mathf.CeilToInt(dst.Size.Y / ROW);
        var poly = InnerOutline(isAmulet);

        for (int i = 0; i < rows; i++)
        {
            float y0 = dst.Position.Y + i * ROW;
            float h = Mathf.Min(ROW, dst.Position.Y + dst.Size.Y - y0);
            if (h <= 0f) break;

            // 取该行中线问轮廓边界，避免边缘抖动
            var (left, right) = SpanAt(poly, y0 + h * 0.5f);
            if (right - left <= 0.5f) continue;

            float x0 = left;
            float full = right - left;

            // 对应的源矩形：按比例映射到 src
            float v0 = (y0 - dst.Position.Y) / dst.Size.Y;
            float vh = h / dst.Size.Y;
            var s = new Rect2(
                src.Position.X + (0f) * src.Size.X,
                src.Position.Y + v0 * src.Size.Y,
                src.Size.X,
                vh * src.Size.Y);

            DrawTextureRectRegion(tex, new Rect2(x0, y0, full, h), s);
        }
    }

    /// <summary>
    /// 立绘可绘区域的**外框内沿**多边形。
    ///
    /// 必须与 DrawFollowerArch / DrawAmuletFrame 里那个 inner 多边形是同一组坐标 ——
    /// 之前另写了一条贝塞尔曲线去近似，形状对不上，
    /// 于是边框和立绘之间露出了一圈底色（看起来就是「立绘比框小一圈」）。
    /// 现在两边共用这一个定义，永远严丝合缝。
    /// </summary>
    private static Vector2[] InnerOutline(bool isAmulet)
    {
        if (isAmulet)
        {
            return new Vector2[]
            {
                new(11f, 7f), new(W - 11f, 7f), new(W - 6f, 18f),
                new(W - 6f, H - 9f), new(6f, H - 9f), new(6f, 18f),
            };
        }

        // 与 DrawFollowerArch 的 inner 完全一致，再整体内缩 1px
        // （描边有宽度，不内缩会让立绘压到描边上）。
        return new Vector2[]
        {
            new(7f, 37f),
            new(W * 0.24f, 13f),
            new(W * 0.5f, 7f),
            new(W * 0.76f, 13f),
            new(W - 7f, 37f),
            new(W - 7f, H - 10f),
            new(7f, H - 10f),
        };
    }

    /// <summary>多边形的外接矩形。</summary>
    private static Rect2 OutlineBounds(Vector2[] poly)
    {
        float minX = float.MaxValue, minY = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue;

        foreach (var p in poly)
        {
            if (p.X < minX) minX = p.X;
            if (p.X > maxX) maxX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.Y > maxY) maxY = p.Y;
        }

        return new Rect2(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// 求多边形在水平线 y 处的左右边界（扫描线裁剪的核心）。
    ///
    /// 遍历每条边，凡是跨越 y 的边就求交点，取最小与最大。
    /// 直接用 inner 多边形算，不再另做近似曲线 —— 形状必然一致。
    /// </summary>
    private static (float left, float right) SpanAt(Vector2[] poly, float y)
    {
        float min = float.MaxValue, max = float.MinValue;

        for (int i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];

            // 水平边不参与求交
            if (Mathf.IsEqualApprox(a.Y, b.Y)) continue;

            float lo = Mathf.Min(a.Y, b.Y);
            float hi = Mathf.Max(a.Y, b.Y);
            if (y < lo || y > hi) continue;

            float t = (y - a.Y) / (b.Y - a.Y);
            float x = a.X + (b.X - a.X) * t;

            if (x < min) min = x;
            if (x > max) max = x;
        }

        return min > max ? (0f, 0f) : (min, max);
    }

    /// <summary>
    /// 从纹理里取一块与目标区域等宽高比的源矩形（居中裁切）。
    ///
    /// 这样 DrawTextureRectRegion 画出来既不变形，也不会超出目标矩形 ——
    /// 等于用源矩形实现了「按边框裁剪」。
    /// </summary>
    private static Rect2 FitSourceRect(Vector2 texSize, Vector2 boxSize)
    {
        if (texSize.X <= 0 || texSize.Y <= 0 || boxSize.X <= 0 || boxSize.Y <= 0)
            return new Rect2(0, 0, texSize.X, texSize.Y);

        float boxAspect = boxSize.X / boxSize.Y;
        float texAspect = texSize.X / texSize.Y;

        if (texAspect > boxAspect)
        {
            // 纹理更宽：左右各裁掉一部分
            float w = texSize.Y * boxAspect;
            return new Rect2((texSize.X - w) * 0.5f, 0, w, texSize.Y);
        }
        else
        {
            // 纹理更高：上下各裁掉一部分
            float h = texSize.X / boxAspect;
            return new Rect2(0, (texSize.Y - h) * 0.5f, texSize.X, h);
        }
    }

    /// <summary>
    /// 棋子的可绘区域多边形（上拱下方）。
    ///
    /// 立绘必须被这个形状裁掉 —— 之前用的是矩形 Rect2，
    /// 于是拱顶被方形立绘填满，看起来「立绘超出了边框」。
    /// </summary>
    private static Vector2[] ArtClipPolygon(bool isAmulet)
    {
        if (isAmulet)
        {
            return new Vector2[]
            {
                new(11f, 9f), new(W - 11f, 9f),
                new(W - 6f, 20f), new(W - 6f, H - 12f),
                new(6f, H - 12f), new(6f, 20f),
            };
        }

        // 随从：与 DrawFollowerArch 的 inner 多边形一致，再内缩 2px
        return new Vector2[]
        {
            new(9f, 38f),
            new(W * 0.24f, 14f),
            new(W * 0.5f, 8f),
            new(W * 0.76f, 14f),
            new(W - 9f, 38f),
            new(W - 9f, H - 11f),
            new(9f, H - 11f),
        };
    }

    private void DrawArtwork(Color clanCol, CardDef def, bool isAmulet)
    {
        // 立绘区域必须是**内框的外接矩形**，不能手写常量。
        // 之前写死 Rect2(8,14,W-16,H-30)，而内框顶点在 y=7 ——
        // 拱顶那 7px 根本没画立绘，露出底色，看起来就是「上方有黑色缺口」。
        var art = OutlineBounds(InnerOutline(isAmulet));

        var tex = ShadowverseFan.Ui.CardArtLoader.Load(def.CardId);
        if (tex != null)
        {
            // 立绘必须被棋子边框裁掉。
            // DrawTextureRect 是**等比铺满**目标矩形：卡牌立绘的宽高比
            // 与棋子的 96x124 不同，等比缩放后必然有一边溢出，
            // 直接画会盖住刚画好的边框，看起来像贴歪了的贴纸。
            //
            // 用 ClipChildren 行不通（那是 Control 的子节点裁剪，
            // 对 _Draw 里的自绘无效），只能靠 CanvasItem 的
            // draw_set_clip / ClipContents 或手动计算贴合区域。
            // 这里用「按 art 比例裁源矩形」：取纹理中心最大可用块，
            // 做到不变形且绝不越界。
            var src = FitSourceRect(tex.GetSize(), art.Size);

            // DrawTextureRectRegion 的源矩形必须落在纹理内。
            // 浮点宽高比不同会差出一两个像素，越界时 Godot 直接画空白 ——
            // 表现就是「路径对、文件在、却是白块」。这里钳到纹理边界内。
            var ts = tex.GetSize();
            float sw = Mathf.Min(src.Size.X, ts.X);
            float sh = Mathf.Min(src.Size.Y, ts.Y);
            float sx = Mathf.Clamp(src.Position.X, 0f, Mathf.Max(0f, ts.X - sw));
            float sy = Mathf.Clamp(src.Position.Y, 0f, Mathf.Max(0f, ts.Y - sh));

            // 按拱形轮廓逐行裁切。
            //
            // CanvasItem 没有「按多边形裁剪自绘」的 API，ClipContents 只能裁矩形，
            // 所以用扫描线：每一行只画该行在轮廓内的那一段。
            //
            // 行高 2px 足够 —— 棋子只有 96 宽，再细肉眼也看不出阶梯。
            DrawArtClippedToOutline(tex, art, new Rect2(sx, sy, sw, sh), isAmulet);
            return;
        }

        // 棋子只有 96x124，压得太暗就成了一块黑方块。
        // 用职业色作为主调，再叠一层亮心，让「这是什么职业的随从」一眼可辨。
        Color top = clanCol.Darkened(0.45f).Lerp(new Color("141020"), 0.30f);
        Color bot = clanCol.Darkened(0.20f).Lerp(new Color("0B0F1A"), 0.35f);
        DrawRect(art, bot);
        DrawRect(new Rect2(art.Position.X, art.Position.Y, art.Size.X, art.Size.Y * 0.48f), top);

        Vector2 c = art.Position + art.Size * 0.5f;
        DrawCircle(c, art.Size.X * 0.34f, clanCol.Lightened(0.45f) with { A = 0.38f });
        DrawCircle(c, art.Size.X * 0.20f, clanCol.Lightened(0.65f) with { A = 0.30f });
        DrawArc(c, art.Size.X * 0.32f, 0, Mathf.Tau, 30,
                new Color(0.90f, 0.94f, 1f, 0.16f), 1.2f);
        DrawArc(c, art.Size.X * 0.22f, 0, Mathf.Tau, 24,
                new Color(0.90f, 0.94f, 1f, 0.12f), 1.0f);
    }

    private void DrawSwordBadge(Vector2 c, int atk, Font font)
    {
        Vector2[] poly =
        {
            new(c.X, c.Y - 15f), new(c.X + 10f, c.Y - 2f), new(c.X + 5f, c.Y + 3f),
            new(c.X + 8f, c.Y + 8f), new(c.X + 3f, c.Y + 13f),
            new(c.X - 3f, c.Y + 13f), new(c.X - 8f, c.Y + 8f),
            new(c.X - 5f, c.Y + 3f), new(c.X - 10f, c.Y - 2f),
        };
        DrawColoredPolygon(poly, new Color("D97706"));
        var inner = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            inner[i] = c + (poly[i] - c) * 0.80f;
        DrawColoredPolygon(inner, new Color("1D4ED8"));

        string t = atk.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 15);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y + 6f), t,
                   HorizontalAlignment.Left, -1, 15, Colors.White);
    }

    private void DrawShieldBadge(Vector2 c, int life, Font font)
    {
        Vector2[] poly =
        {
            new(c.X, c.Y - 15f), new(c.X + 11f, c.Y - 10f), new(c.X + 10f, c.Y + 2f),
            new(c.X + 6f, c.Y + 10f), new(c.X, c.Y + 15f), new(c.X - 6f, c.Y + 10f),
            new(c.X - 10f, c.Y + 2f), new(c.X - 11f, c.Y - 10f),
        };
        DrawColoredPolygon(poly, new Color("D97706"));
        var inner = new Vector2[poly.Length];
        for (int i = 0; i < poly.Length; i++)
            inner[i] = c + (poly[i] - c) * 0.80f;
        DrawColoredPolygon(inner, new Color("991B1B"));

        string t = life.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 15);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y + 6f), t,
                   HorizontalAlignment.Left, -1, 15, Colors.White);
    }

    /// <summary>
    /// 底部能力标记 —— 三态：红书页（谢幕曲）/ 绿旗（时机能力）/ 空（都没有）。
    /// 空态**不画任何东西**。
    /// </summary>
    private void DrawAbilityBadge(Vector2 c, Font font)
    {
        switch (_badge)
        {
            case UnitAbilityBadge.LastWords: DrawRedPageIcon(c); break;
            case UnitAbilityBadge.Triggered: DrawGreenFlagIcon(c); break;
            default: break; // None：留空
        }
    }

    /// <summary>红色书页：一个上窄下宽的书页轮廓 + 两道书页纹。</summary>
    private void DrawRedPageIcon(Vector2 c)
    {
        float w = 13f, h = 15f;
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f));

        Vector2[] page =
        {
            new(c.X - w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.5f, c.Y - h * 0.5f),
            new(c.X + w * 0.42f, c.Y + h * 0.5f),
            new(c.X - w * 0.42f, c.Y + h * 0.5f),
        };
        DrawColoredPolygon(page, new Color("DC2626"));
        DrawPolyline(new[] { page[0], page[1], page[2], page[3], page[0] },
                     new Color("FCA5A5"), 1.2f);

        DrawLine(new Vector2(c.X - w * 0.25f, c.Y - h * 0.22f),
                 new Vector2(c.X + w * 0.25f, c.Y - h * 0.22f),
                 new Color(1f, 0.85f, 0.85f, 0.75f), 1f);
        DrawLine(new Vector2(c.X - w * 0.22f, c.Y + h * 0.05f),
                 new Vector2(c.X + w * 0.22f, c.Y + h * 0.05f),
                 new Color(1f, 0.85f, 0.85f, 0.6f), 1f);
    }

    /// <summary>绿色旗帜：旗杆 + 三角旗面。</summary>
    private void DrawGreenFlagIcon(Vector2 c)
    {
        DrawCircle(c, 9.5f, new Color(0.05f, 0.06f, 0.09f, 0.85f));

        // 旗杆
        DrawLine(new Vector2(c.X - 4f, c.Y - 8f), new Vector2(c.X - 4f, c.Y + 8f),
                 new Color("E2E8F0"), 1.6f);

        Vector2[] flag =
        {
            new(c.X - 4f, c.Y - 8f),
            new(c.X + 7f, c.Y - 4.5f),
            new(c.X - 4f, c.Y - 1f),
        };
        DrawColoredPolygon(flag, new Color("22C55E"));
        DrawPolyline(new[] { flag[0], flag[1], flag[2], flag[0] },
                     new Color("BBF7D0"), 1.1f);
    }

    private void DrawCountdown(CardDef def, Font font)
    {
        int cd = 0;
        try
        {
            var key = CurrentController?.Schema.Counter("countdown");
            if (key.HasValue && Entity.Counters != null
                && Entity.Counters.TryGetValue(key.Value, out int v)) cd = v;
        }
        catch { }

        if (cd <= 0) return;

        string t = cd.ToString();
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, 28);
        var pos = new Vector2(W - sz.X - 6f, H - 10f);
        DrawString(font, pos + new Vector2(1.5f, 1.5f), t,
                   HorizontalAlignment.Left, -1, 28, new Color(0, 0, 0, 0.8f));
        DrawString(font, pos, t, HorizontalAlignment.Left, -1, 28, new Color("E0F2FE"));
    }

    public static BattleSessionController CurrentController { get; set; }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb)
        {
            if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    OnClicked?.Invoke(Entity);
                    if (_canAttack)
                    {
                        _dragging = true;
                        OnDragAttackStarted?.Invoke(Entity, mb.GlobalPosition);
                    }
                }
                else if (_dragging)
                {
                    _dragging = false;
                    OnDragAttackReleased?.Invoke(Entity, mb.GlobalPosition);
                }
                AcceptEvent();
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                OnRightClicked?.Invoke(Entity);
                AcceptEvent();
            }
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            OnDragAttackMoved?.Invoke(Entity, mm.GlobalPosition);
        }
    }
}
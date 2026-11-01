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

    private const float W = 96f;
    private const float H = 124f;

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

    /// <summary>触发时机能力时调用：播放一圈光效。</summary>
    public void PlayTriggerFlash()
    {
        if (!UnitAbilityBadgeResolver.FlashesOnTrigger(_badge)) return;
        _triggerFlash = 1f;
    }

    public override void _Process(double delta)
    {
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
        var font = ThemeDB.FallbackFont;
        Color clanCol = SvWbTheme.ClanColor(def.Clan.IsValid ? def.Clan.Name : null);

        bool isAmulet = def.Kind is CardKind.Amulet or CardKind.Crest;

        if (isAmulet) DrawAmuletFrame(clanCol);
        else DrawFollowerArch(clanCol);

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

    private void DrawFollowerArch(Color clanCol)
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
        DrawColoredPolygon(frame, new Color("D6C7B2"));

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

    private void DrawAmuletFrame(Color clanCol)
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

    private void DrawArtwork(Color clanCol, CardDef def, bool isAmulet)
    {
        var art = new Rect2(8f, 14f, W - 16f, H - 30f);

        string path = $"res://assets/cards/{def.CardId}.png";
        if (ResourceLoader.Exists(path))
        {
            var tex = GD.Load<Texture2D>(path);
            if (tex != null) { DrawTextureRect(tex, art, false); return; }
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
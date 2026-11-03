using System;
using Godot;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 主战者勋章：半圆拱形立绘框 + 镶嵌式 EP/SEP 双进化球 + 右侧红盾生命。
///
/// 进化球直接镶在拱框左右两侧（原版设计），不是浮在别处 ——
/// 玩家的手会自然落在拱框两侧找「进化」。
/// </summary>
public partial class LeaderMedallionWidget : Control
{
    public RuntimeEntity Leader { get; private set; }
    public bool IsEnemy { get; private set; }

    public event Action OnLeaderClicked;
    public event Action<bool> OnEvolveOrbClicked;   // true = 超进化

    private BattleSessionController _ctrl;
    private int _playerId;

    private bool _isCandidate;
    private bool _dimmed;
    private float _pulseTime;

    /// <summary>挂起选择时的候选态（金色呼吸圈）/ 压暗态。</summary>
    public void SetChoiceState(bool isCandidate, bool dimmed)
    {
        _isCandidate = isCandidate;
        _dimmed = dimmed;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!_isCandidate) return;
        _pulseTime += (float)delta;
        QueueRedraw();
    }

    // 紧贴拱框左右肩部（拱框 l=34, w=172 -> 左肩 36 / 右肩 202），
    // 早先写 -34 会让进化球脱离拱框飞到沙地上。
    private static readonly Vector2 EpOrbOffset = new(2f, 0f);
    private static readonly Vector2 SepOrbOffset = new(168f, 0f);
    private const float OrbRadius = 20f;

    public void Setup(BattleSessionController ctrl, int playerId, bool isEnemy)
    {
        _ctrl = ctrl;
        _playerId = playerId;
        IsEnemy = isEnemy;
        Leader = ctrl.Leader(playerId);

        CustomMinimumSize = new Vector2(240, 150);
        Size = new Vector2(240, 150);
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.PointingHand;
        QueueRedraw();
    }

    // 拱框几何（唯一真相：下面所有球与盾都从这几个常量推出来，
    // 不再各自写死坐标 —— 之前 EP 球跑到 x=2、SEP 在 x=168，
    // 距中线差了 118 与 48，肉眼一看就是歪的）。
    private const float FrameL = 34f;
    private const float FrameW = 172f;
    // 拱框在控件内的上边距。
    // 控件高 150，拱框 108 高 -> 余量 42。
    // 我方贴窗口底部时拱框要往下压、敌方贴顶部时要往上抬，
    // 否则贴边后框会被窗口边缘切掉。
    private const float FrameT = 4f;
    private const float FrameH = 108f;
    private const float FrameR = FrameL + FrameW;           // 206
    private const float FrameCx = FrameL + FrameW * 0.5f;   // 120 —— 拱框中线

    /// <summary>拱顶在控件内的 y（翻转前）。</summary>
    /// <summary>
    /// 视觉血量补偿：后端已扣血但受击动画还没播时，
    /// 盾牌显示受击前的数值，命中那一帧才跳变。
    /// </summary>
    public int VisualLifeOffset
    {
        get => _visualLifeOffset;
        set
        {
            if (_visualLifeOffset == value) return;
            _visualLifeOffset = value;
            // 与 BoardUnitWidget 同理：控件不是每帧重绘的，
            // setter 里不主动 QueueRedraw 的话，
            // 受击销账把值归零时盾牌数字会一直停在旧值上。
            QueueRedraw();
        }
    }

    private int _visualLifeOffset;

    public const float DomeTopY = FrameT;

    /// <summary>平直边在控件内的 y（翻转前）。</summary>
    public const float FlatEdgeY = FrameT + FrameH;

    /// <summary>
    /// 拱顶朝上画（我方），或垂直翻转画（敌方）。
    ///
    /// 原版里敌方主战者是从自己视角摆在下方，所以拱顶朝下 ——
    /// 直接复制我方的形状会让两个人的框看起来「一个正一个倒」却没倒。
    /// </summary>
    private float FlipY(float y) => IsEnemy ? (FrameT + FrameH - (y - FrameT)) : y;

    public override void _Draw()
    {
        if (Leader?.Def == null) return;
        var font = ThemeDB.FallbackFont;

        var clanCol = SvWbTheme.ClanColor(Leader.Def.Clan.IsValid ? Leader.Def.Clan.Name : null);

        float l = FrameL, w = FrameW, t = FrameT, h = FrameH;

        // 半圆拱形框：敌方把拱顶翻到下方
        Vector2[] arch =
        {
            new(l, FlipY(t + h)),
            new(l, FlipY(t + 42f)),
            new(l + w * 0.16f, FlipY(t + 12f)),
            new(l + w * 0.5f, FlipY(t)),
            new(l + w * 0.84f, FlipY(t + 12f)),
            new(l + w, FlipY(t + 42f)),
            new(l + w, FlipY(t + h)),
        };
        DrawColoredPolygon(arch, new Color("D6C7B2"));

        // 内层轮廓：把外轮廓的每个顶点向形状中心收缩。
        // 不能拿「翻转后的基点」去做仿射 —— arch 已经是翻转坐标，
        // 再减一次翻转基点会把比例算反，敌方框会缩成一条缝。
        Vector2 shapeCenter = new(FrameCx, FlipY(t + h * 0.5f));
        Vector2[] inner = new Vector2[arch.Length];
        for (int i = 0; i < arch.Length; i++)
            inner[i] = shapeCenter + (arch[i] - shapeCenter) * 0.92f;
        DrawColoredPolygon(inner, clanCol.Darkened(0.72f).Lerp(new Color("141020"), 0.5f));

        // 立绘窗内的职业纹章
        Vector2 c = new(l + w * 0.5f, FlipY(t + h * 0.55f));
        DrawCircle(c, 30f, clanCol.Lightened(0.10f) with { A = 0.35f });
        DrawArc(c, 30f, 0, Mathf.Tau, 32, new Color(0.9f, 0.94f, 1f, 0.25f), 1.5f);
        string initial = SvWbTheme.ClanDisplayZh(Leader.Def.Clan.IsValid ? Leader.Def.Clan.Name : null);
        if (initial.Length > 1) initial = initial.Substring(0, 1);
        var isz = font.GetStringSize(initial, HorizontalAlignment.Left, -1, 30);
        DrawString(font, new Vector2(c.X - isz.X * 0.5f, c.Y + 10f), initial,
                   HorizontalAlignment.Left, -1, 30, Colors.White);

        // 生命红盾（右侧，垂直位置同样跟随翻转）
        Vector2 shield = new(FrameR - 4f, FlipY(t + 46f));
        Vector2[] sp =
        {
            new(shield.X, shield.Y - 26f), new(shield.X + 22f, shield.Y - 18f),
            new(shield.X + 21f, shield.Y + 6f), new(shield.X + 12f, shield.Y + 22f),
            new(shield.X, shield.Y + 30f), new(shield.X - 12f, shield.Y + 22f),
            new(shield.X - 21f, shield.Y + 6f), new(shield.X - 22f, shield.Y - 18f),
        };
        DrawColoredPolygon(sp, new Color("D97706"));
        var spi = new Vector2[sp.Length];
        for (int i = 0; i < sp.Length; i++) spi[i] = shield + (sp[i] - shield) * 0.82f;
        DrawColoredPolygon(spi, new Color("991B1B"));

        int shownLife = Math.Max(0, Leader.Life + VisualLifeOffset);
        string life = shownLife.ToString();
        var lsz = font.GetStringSize(life, HorizontalAlignment.Left, -1, 22);
        DrawString(font, new Vector2(shield.X - lsz.X * 0.5f, shield.Y + 8f), life,
                   HorizontalAlignment.Left, -1, 22, Colors.White);

        // 不再印「XX主战者」—— 职业已经由立绘窗里的汉字纹章表达了，
        // 框下再写一遍是冗余，而且会顶到窗口边界。

        if (_ctrl == null) return;

        // EP / SEP 进化球：必须关于拱框中线 FrameCx **严格对称**。
        // 之前 EP 在 x=2、SEP 在 x=168，距中线 118 / 48 ——
        // 肉眼看起来就是「EP 悬空吊在左边、SEP 贴着框」。
        int ep = _ctrl.ReadCounter(_playerId, "ep");
        int sep = _ctrl.ReadCounter(_playerId, "sep");

        // 球心落在拱框两肩上，且左右等距；
        // 纵向贴着横梁内侧（我方在上沿、敌方在下沿）。
        float orbY = FlipY(t + 8f);
        float orbDx = FrameW * 0.5f - 14f;   // 距中线的水平偏移，左右同值
        DrawEvolutionOrb(new Vector2(FrameCx - orbDx, orbY), ep,
                         new Color("F59E0B"), "EP", font);
        DrawEvolutionOrb(new Vector2(FrameCx + orbDx, orbY), sep,
                         new Color("A855F7"), "SEP", font);

        // 候选态：主战者也能被点选时，套一圈金色呼吸光环。
        // 没有它，玩家会以为「只有随从能选」，对着主战者犹豫。
        if (_isCandidate)
        {
            float pulse = 0.55f + 0.45f * Mathf.Sin(_pulseTime * 4f);
            var gold = new Color(0.98f, 0.85f, 0.28f, pulse);
            DrawRect(new Rect2(FrameL - 8f, FrameT - 8f, FrameW + 16f, FrameH + 16f),
                     gold, false, 3f);
            DrawRect(new Rect2(FrameL - 13f, FrameT - 13f, FrameW + 26f, FrameH + 26f),
                     new Color(0.98f, 0.85f, 0.28f, pulse * 0.32f), false, 1.5f);
        }

        if (_dimmed)
            DrawRect(new Rect2(0, 0, Size.X, Size.Y), new Color(0.02f, 0.03f, 0.06f, 0.62f));
        DrawAuraShieldBadge();
    }

    private void DrawEvolutionOrb(Vector2 c, int count, Color tint, string label, Font font)
    {
        bool usable = count > 0;
        DrawCircle(c + new Vector2(1.5f, 2f), OrbRadius + 2f, new Color(0, 0, 0, 0.55f));
        DrawCircle(c, OrbRadius + 2f, new Color("92400E"));
        DrawCircle(c, OrbRadius, usable ? tint.Darkened(0.35f) : new Color("1E293B"));
        DrawArc(c, OrbRadius, 0, Mathf.Tau, 28,
                usable ? new Color("FEF08A") : new Color("475569"), 2f);

        // 球内菱形豆：数量即剩余进化点
        int shown = Math.Min(count, 3);
        for (int i = 0; i < shown; i++)
        {
            Vector2 d = c + new Vector2((i - (shown - 1) * 0.5f) * 9f, 0f);
            Vector2[] bean =
            {
                new(d.X, d.Y - 4f), new(d.X + 3f, d.Y),
                new(d.X, d.Y + 4f), new(d.X - 3f, d.Y),
            };
            DrawColoredPolygon(bean, new Color("FEF3C7"));
        }

        var sz = font.GetStringSize(label, HorizontalAlignment.Left, -1, 9);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y + OrbRadius + 12f), label,
                   HorizontalAlignment.Left, -1, 9,
                   usable ? new Color("FDE68A") : new Color("64748B"));
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left) return;

        // 与绘制用同一套推导，避免「看到的球」和「点得到的球」错位。
        float orbY = FlipY(FrameT + 8f);
        float orbDx = FrameW * 0.5f - 14f;
        var epC = new Vector2(FrameCx - orbDx, orbY);
        var sepC = new Vector2(FrameCx + orbDx, orbY);

        if (mb.Position.DistanceTo(sepC) <= OrbRadius + 3f) { OnEvolveOrbClicked?.Invoke(true); AcceptEvent(); }
        else if (mb.Position.DistanceTo(epC) <= OrbRadius + 3f) { OnEvolveOrbClicked?.Invoke(false); AcceptEvent(); }
        else { OnLeaderClicked?.Invoke(); AcceptEvent(); }
    }

    /// <summary>主战者挂着减伤/上限光环时，在红盾上方挂一枚护盾徽章。</summary>
    private void DrawAuraShieldBadge()
    {
        var st = BoardUnitWidget.CurrentController?.State;
        var e = Leader;
        if (st == null || e == null) return;

        bool any = false;
        foreach (var pair in st.QueryDamageModifiers(e))
        {
            any = true;
            break;
        }
        if (!any) return;

        var tex = KeywordSvgAtlas.Get("badge_shield");
        if (tex == null) return;
        DrawTextureRect(tex, new Rect2(new Vector2(Size.X + 2f, 4f), new Vector2(22, 22)), false);
    }
}

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

    public override void _Draw()
    {
        if (Leader?.Def == null) return;
        var font = ThemeDB.FallbackFont;

        var clanCol = SvWbTheme.ClanColor(Leader.Def.Clan.IsValid ? Leader.Def.Clan.Name : null);

        // 半圆拱形框
        float w = 172f, h = 108f, l = 34f, t = 14f;
        Vector2[] arch =
        {
            new(l, t + h),
            new(l, t + 42f),
            new(l + w * 0.16f, t + 12f),
            new(l + w * 0.5f, t),
            new(l + w * 0.84f, t + 12f),
            new(l + w, t + 42f),
            new(l + w, t + h),
        };
        DrawColoredPolygon(arch, new Color("D6C7B2"));

        Vector2[] inner = new Vector2[arch.Length];
        for (int i = 0; i < arch.Length; i++)
            inner[i] = new Vector2(l + 5f, t + 6f) + (arch[i] - new Vector2(l, t)) * 0.94f;
        DrawColoredPolygon(inner, clanCol.Darkened(0.72f).Lerp(new Color("141020"), 0.5f));

        // 立绘窗内的职业纹章
        Vector2 c = new(l + w * 0.5f, t + h * 0.55f);
        DrawCircle(c, 30f, clanCol.Lightened(0.10f) with { A = 0.35f });
        DrawArc(c, 30f, 0, Mathf.Tau, 32, new Color(0.9f, 0.94f, 1f, 0.25f), 1.5f);
        string initial = SvWbTheme.ClanDisplayZh(Leader.Def.Clan.IsValid ? Leader.Def.Clan.Name : null);
        if (initial.Length > 1) initial = initial.Substring(0, 1);
        var isz = font.GetStringSize(initial, HorizontalAlignment.Left, -1, 30);
        DrawString(font, new Vector2(c.X - isz.X * 0.5f, c.Y + 10f), initial,
                   HorizontalAlignment.Left, -1, 30, Colors.White);

        // 生命红盾（右侧）
        Vector2 shield = new(l + w - 4f, t + 46f);
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

        string life = Leader.Life.ToString();
        var lsz = font.GetStringSize(life, HorizontalAlignment.Left, -1, 22);
        DrawString(font, new Vector2(shield.X - lsz.X * 0.5f, shield.Y + 8f), life,
                   HorizontalAlignment.Left, -1, 22, Colors.White);

        // 名字
        // 严禁印出内部代号 Leader0 / Leader1 —— 那是调试名。
        string nm = SvWbTheme.ClanDisplayZh(
            Leader.Def.Clan.IsValid ? Leader.Def.Clan.Name : null) + "主战者";
        var nsz = font.GetStringSize(nm, HorizontalAlignment.Left, -1, 12);
        DrawString(font, new Vector2(l + w * 0.5f - nsz.X * 0.5f, t + h - 6f), nm,
                   HorizontalAlignment.Left, -1, 12, new Color("E2E8F0"));

        if (_ctrl == null) return;

        // EP / SEP 进化球（镶在拱框左右）
        int ep = _ctrl.ReadCounter(_playerId, "ep");
        int sep = _ctrl.ReadCounter(_playerId, "sep");
        // 敌方主战者的进化球镶在拱框下沿，我方的镶在上沿（与原版一致）。
        float orbY = IsEnemy ? (t + h - 10f) : (t + 10f);
        DrawEvolutionOrb(new Vector2(EpOrbOffset.X, orbY), ep,
                         new Color("F59E0B"), "EP", font);
        DrawEvolutionOrb(new Vector2(SepOrbOffset.X, orbY), sep,
                         new Color("A855F7"), "SEP", font);
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

        float orbY = IsEnemy ? (14f + 108f - 10f) : (14f + 10f);
        var epC = new Vector2(EpOrbOffset.X, orbY);
        var sepC = new Vector2(SepOrbOffset.X, orbY);

        if (mb.Position.DistanceTo(sepC) <= OrbRadius + 3f) { OnEvolveOrbClicked?.Invoke(true); AcceptEvent(); }
        else if (mb.Position.DistanceTo(epC) <= OrbRadius + 3f) { OnEvolveOrbClicked?.Invoke(false); AcceptEvent(); }
        else { OnLeaderClicked?.Invoke(); AcceptEvent(); }
    }
}
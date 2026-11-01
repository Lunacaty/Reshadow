using System;
using Godot;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 右侧垂直指挥列：
///   [能量点+1] → [敌方能量点梯形牌] → (大圆回合按钮) → [我方能量点梯形牌 + 绿宝石豆]。
/// </summary>
public partial class RightCommandColumnWidget : Control
{
    public event Action OnEndTurnClicked;
    public event Action OnExtraPpClicked;

    private BattleSessionController _ctrl;
    private bool _turnBtnHovered;

    private static readonly Rect2 ExtraPpRect = new(10, 0, 196, 42);
    private static readonly Rect2 EnemyPpRect = new(14, 52, 188, 62);
    private static readonly Vector2 TurnBtnCenter = new(108, 198);
    private const float TurnBtnRadius = 68f;
    private static readonly Rect2 AllyPpRect = new(10, 282, 196, 86);

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        CustomMinimumSize = new Vector2(216, 380);
        Size = new Vector2(216, 380);
        MouseFilter = MouseFilterEnum.Stop;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ctrl?.State == null) return;
        var font = ThemeDB.FallbackFont;

        // 1. 能量点 +1
        bool canEx = _ctrl.CanUseExtraPp();
        DrawRect(ExtraPpRect, canEx ? new Color("065F46") : new Color(0.10f, 0.13f, 0.16f, 0.88f));
        DrawRect(ExtraPpRect, canEx ? new Color("34D399") : new Color("475569"), false, 1.8f);
        DrawCircle(ExtraPpRect.Position + new Vector2(16, 21), 8f,
                   canEx ? new Color("10B981") : new Color("0F172A"));
        DrawString(font, ExtraPpRect.Position + new Vector2(32, 27), "能量点 +1",
                   HorizontalAlignment.Left, -1, 15,
                   canEx ? Colors.White : new Color("64748B"));

        // 2. 敌方能量点
        int foePp = _ctrl.ReadCounter(1, "pp");
        int foeMax = _ctrl.ReadCounter(1, "max_pp");
        DrawTrapezoid(EnemyPpRect, 0f, 14f);
        DrawString(font, EnemyPpRect.Position + new Vector2(22, 38), "能量点",
                   HorizontalAlignment.Left, -1, 13, new Color("CBD5E1"));
        DrawString(font, EnemyPpRect.Position + new Vector2(80, 40), foePp.ToString(),
                   HorizontalAlignment.Left, -1, 22, Colors.White);
        DrawString(font, EnemyPpRect.Position + new Vector2(108, 38), "/ " + foeMax,
                   HorizontalAlignment.Left, -1, 13, new Color("94A3B8"));

        // 3. 回合按钮
        bool myTurn = _ctrl.IsMyTurn && !_ctrl.IsWaitingForInput;
        DrawCircle(TurnBtnCenter, TurnBtnRadius + 5f, new Color("D4AF37"));
        DrawCircle(TurnBtnCenter, TurnBtnRadius,
                   myTurn ? (_turnBtnHovered ? new Color("1D4ED8") : new Color("1E3A8A"))
                          : new Color("1E293B"));
        DrawArc(TurnBtnCenter, TurnBtnRadius + 5f, 0, Mathf.Tau, 48, new Color("FEF08A"), 2.2f);

        string l1 = myTurn ? "您的" : "对手";
        DrawCentered(font, TurnBtnCenter + new Vector2(0, -6), l1, 22, Colors.White);
        DrawCentered(font, TurnBtnCenter + new Vector2(0, 20), "回合", 22, Colors.White);
        if (myTurn)
            DrawCentered(font, TurnBtnCenter + new Vector2(0, 40), "(点击结束)", 11,
                         new Color("FDE68A"));

        // 4. 我方能量点
        int myPp = _ctrl.ReadCounter(0, "pp");
        int myMax = _ctrl.ReadCounter(0, "max_pp");
        int cap = Math.Max(1, Math.Min(12, _ctrl.State.Rules.MaxPpCap));
        DrawTrapezoid(AllyPpRect, 14f, 0f);

        DrawString(font, AllyPpRect.Position + new Vector2(22, 34), "能量点",
                   HorizontalAlignment.Left, -1, 13, new Color("CBD5E1"));
        DrawString(font, AllyPpRect.Position + new Vector2(80, 36), myPp.ToString(),
                   HorizontalAlignment.Left, -1, 24, Colors.White);
        DrawString(font, AllyPpRect.Position + new Vector2(112, 34), "/" + myMax,
                   HorizontalAlignment.Left, -1, 14, new Color("94A3B8"));

        float orbY = AllyPpRect.Position.Y + 64f;
        float spacing = 166f / cap;
        for (int i = 0; i < cap; i++)
        {
            Vector2 oc = new(AllyPpRect.Position.X + 22f + i * spacing, orbY);
            Color fill = i < myPp ? new Color("22C55E")
                       : i < myMax ? new Color("334155")
                       : new Color("0F172A");
            DrawCircle(oc, 5.5f, fill);
            DrawArc(oc, 5.5f, 0, Mathf.Tau, 16,
                    i < myPp ? new Color("86EFAC") : new Color("64748B"), 1.2f);
        }
    }

    private void DrawCentered(Font font, Vector2 c, string t, int size, Color col)
    {
        var sz = font.GetStringSize(t, HorizontalAlignment.Left, -1, size);
        DrawString(font, new Vector2(c.X - sz.X * 0.5f, c.Y), t,
                   HorizontalAlignment.Left, -1, size, col);
    }

    private void DrawTrapezoid(Rect2 r, float topInset, float botInset)
    {
        Vector2[] poly =
        {
            new(r.Position.X + topInset, r.Position.Y),
            new(r.End.X - topInset, r.Position.Y),
            new(r.End.X - botInset, r.End.Y),
            new(r.Position.X + botInset, r.End.Y),
        };
        DrawColoredPolygon(poly, new Color(0.11f, 0.24f, 0.23f, 0.92f));
        DrawPolyline(new[] { poly[0], poly[1], poly[2], poly[3], poly[0] },
                     new Color("D4AF37"), 2f);
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm)
        {
            bool hov = mm.Position.DistanceTo(TurnBtnCenter) <= TurnBtnRadius;
            if (hov != _turnBtnHovered) { _turnBtnHovered = hov; QueueRedraw(); }
        }
        else if (@event is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
        {
            if (mb.Position.DistanceTo(TurnBtnCenter) <= TurnBtnRadius)
            {
                OnEndTurnClicked?.Invoke();
                AcceptEvent();
            }
            else if (ExtraPpRect.HasPoint(mb.Position))
            {
                OnExtraPpClicked?.Invoke();
                AcceptEvent();
            }
        }
    }
}
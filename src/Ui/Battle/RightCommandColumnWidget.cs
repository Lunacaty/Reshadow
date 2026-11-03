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
        // 高 420：下方 Extra PP 框底边在 374+42=416，
        // 控件只有 380 高的话那块会落在控件外，鼠标事件根本收不到。
        CustomMinimumSize = new Vector2(216, 420);
        Size = new Vector2(216, 420);
        MouseFilter = MouseFilterEnum.Stop;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_ctrl?.State == null) return;
        var font = ThemeDB.FallbackFont;

        // 1. 能量点 +1 —— 只有「后手那一侧」才画。
        //    我方后手 -> 画在我方 PP 牌下方；敌方后手 -> 画在敌方 PP 牌上方。
        bool foeIsSecond = _ctrl.IsSecondPlayer(1);
        bool allyIsSecond = _ctrl.IsSecondPlayer(0);

        if (foeIsSecond)
            DrawExtraPpBox(new Rect2(10, 0, 196, 42), 1, font);
        if (allyIsSecond)
            DrawExtraPpBox(new Rect2(10, 374, 196, 42), 0, font);

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

        // 3. 回合按钮 —— 只有两种状态：
        //    红色「敌方回合」（不可点）/ 蓝色「回合结束」（可点）。
        //    之前写成「您的回合 / 对手回合 + (点击结束)」，三种文案混在一起，
        //    而且灰底既不表示「等待」也不表示「可操作」。
        bool myTurn = _ctrl.IsMyTurn && !_ctrl.IsWaitingForInput;

        DrawCircle(TurnBtnCenter, TurnBtnRadius + 5f, new Color("D4AF37"));
        DrawCircle(TurnBtnCenter, TurnBtnRadius,
                   myTurn ? (_turnBtnHovered ? new Color("1D4ED8") : new Color("1E3A8A"))
                          : (_turnBtnHovered ? new Color("B91C1C") : new Color("7F1D1D")));
        DrawArc(TurnBtnCenter, TurnBtnRadius + 5f, 0, Mathf.Tau, 48,
                myTurn ? new Color("FEF08A") : new Color("FCA5A5"), 2.2f);

        string label = myTurn ? "回合结束" : "敌方回合";
        DrawCentered(font, TurnBtnCenter + new Vector2(0, 8), label, 21, Colors.White);

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

    /// <summary>
    /// 画 Extra PP 指示框。
    /// 金/灰双态由 IsExtraPpAvailableInPhase 决定 ——
    /// 那个方法直接读内核的 UsedEarly/LateExtraPp，不在这里重新实现规则。
    /// </summary>
    private void DrawExtraPpBox(Rect2 r, int playerId, Font font)
    {
        bool avail = _ctrl.IsExtraPpAvailableInPhase(playerId);
        Color gold = new Color("F59E0B");
        Color goldLight = new Color("FDE047");
        Color gray = new Color("475569");
        Color grayDim = new Color("334155");

        DrawRect(r, avail ? new Color(0.28f, 0.20f, 0.04f, 0.92f)
                          : new Color(0.10f, 0.13f, 0.16f, 0.88f));
        DrawRect(r, avail ? gold : gray, false, 1.8f);

        Vector2 dot = new(r.Position.X + 16f, r.Position.Y + r.Size.Y * 0.5f);
        DrawCircle(dot, 8f, avail ? goldLight : grayDim);
        DrawArc(dot, 8f, 0, Mathf.Tau, 20, avail ? gold : gray, 1.4f);

        DrawString(font, new Vector2(r.Position.X + 32f, r.Position.Y + r.Size.Y * 0.5f + 5f),
                   "能量点 +1", HorizontalAlignment.Left, -1, 15,
                   avail ? goldLight : new Color("64748B"));
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
                // 敌方回合时按钮画的是红色「敌方回合」，点击必须无效 ——
                // 原来这里无条件 Invoke，玩家能把 AI 的回合点掉。
                bool canEnd = _ctrl.IsMyTurn && !_ctrl.IsWaitingForInput;
                if (canEnd) OnEndTurnClicked?.Invoke();
                AcceptEvent();
            }
            // 我方后手时，Extra PP 框画在下方（Y=374..416）；
            // 敌方后手时画在上方（Y=0..42）。两个位置都要能点。
            else if (ExtraPpRect.HasPoint(mb.Position)
                     || new Rect2(10, 374, 196, 42).HasPoint(mb.Position))
            {
                OnExtraPpClicked?.Invoke();
                AcceptEvent();
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 战场底层画布：中央暖沙石板竞技场 + 四周暗石外环 +
/// 双方 5 格纹章圆槽 + 区域计数条 + 出牌投放区高亮 + 攻击瞄准箭头。
///
/// 全部用 _Draw 矢量绘制 —— 没有 assets/ 时也能出画面。
/// </summary>
public partial class BattlefieldCanvas : Control
{
    public BattleSessionController Controller { get; set; }

    public bool IsDraggingPlayCard { get; set; }
    public bool IsDraggingAttack { get; set; }
    public Vector2 AttackDragStart { get; set; }
    public Vector2 AttackDragCurrent { get; set; }

    /// <summary>挂了 SelectTarget 时是否要画「底部中央 → 鼠标」的金色虚线瞄准线。</summary>
    public bool IsAiming { get; set; }

    /// <summary>鼠标当前位置（瞄准线终点）。</summary>
    public Vector2 AimTarget { get; set; }

    public event Action<RuntimeEntity> OnCrestClicked;

    // 敌方右上 5 个纹章圆槽（上 3 下 2）
    public static readonly Vector2[] EnemyCrestCenters =
    {
        new(902, 42), new(956, 42), new(1010, 42),
        new(929, 92), new(983, 92),
    };

    // 我方左下 5 个纹章圆槽（上 2 下 3）
    public static readonly Vector2[] AllyCrestCenters =
    {
        new(124, 566), new(178, 566),
        new(97, 618), new(151, 618), new(205, 618),
    };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Pass;
    }

    /// <summary>
    /// 中央竞技场椭圆的中心与半轴（设计基准 1280x720 下的局部坐标）。
    ///
    /// 布局必须引用这组数字 —— 棋盘、主战者都要对齐到竞技场，
    /// 而不是对齐到「屏幕正中」。竞技场中心是 (595,352)，
    /// 屏幕中心却是 (640,360)，差 45px；按屏幕中心摆棋子，
    /// 整排随从就会偏右、且与椭圆对不上。
    /// </summary>
    /// <summary>
    /// 我方左下 5 个纹章圆槽：**距窗口底部距离恒定**（上层 154px、下层 102px）。
    ///
    /// 早先写死 y=566/618，那是 720p 下的绝对值 —— 窗口一高，
    /// 纹章区就浮到半空；窗口一矮就被切掉。改用 sz.Y - 常量。
    /// </summary>
    public static Vector2[] GetAllyCrestCenters(Vector2 sz)
    {
        float h = sz.Y > 0f ? sz.Y : 720f;
        return new Vector2[]
        {
            new(124f, h - 154f), new(178f, h - 154f),
            new(97f,  h - 102f), new(151f, h - 102f), new(205f, h - 102f),
        };
    }

    /// <summary>敌方右上 5 个纹章圆槽：距窗口右缘与顶边距离恒定。</summary>
    public static Vector2[] GetEnemyCrestCenters(Vector2 sz)
    {
        float w = sz.X > 0f ? sz.X : 1280f;
        float dx = w - 1280f;
        return new Vector2[]
        {
            new(902f + dx, 42f), new(956f + dx, 42f), new(1010f + dx, 42f),
            new(929f + dx, 92f), new(983f + dx, 92f),
        };
    }

    public static readonly Vector2 ArenaCenter = new(595f, 352f);
    public const float ArenaRadiusX = 368f;
    public const float ArenaRadiusY = 208f;

    public override void _Draw()
    {
        Vector2 sz = Size;
        if (sz.X <= 0 || sz.Y <= 0) return;
        var font = ThemeDB.FallbackFont;

        // 1. 外围暗石与林木
        DrawRect(new Rect2(0, 0, sz.X, sz.Y), new Color("1B2A26"));
        DrawCircle(new Vector2(sz.X * 0.10f, sz.Y * 0.22f), 300f, new Color("2E4A3A"));
        DrawCircle(new Vector2(sz.X * 0.92f, sz.Y * 0.78f), 280f, new Color("264034"));

        // 按实际视口缩放竞技场，使它与 LayoutBattlefield 摆的棋子对齐
        float sx = sz.X / 1280f;
        float sy = sz.Y / 720f;

        // 2. 中央暖沙竞技场（外暗内明的对比是这块的灵魂）
        Vector2 arena = new(ArenaCenter.X * sx, ArenaCenter.Y * sy);
        DrawEllipse(arena, 470f * sx, 285f * sy, new Color("6E8560") { A = 0.45f });
        DrawEllipse(arena, 420f * sx, 250f * sy, new Color("C9AE86"));
        DrawEllipse(arena, 368f * sx, 208f * sy, new Color("E0CCA6"));
        DrawLine(new Vector2(arena.X - 355f * sx, arena.Y - 22f * sy),
                 new Vector2(arena.X + 355f * sx, arena.Y - 22f * sy),
                 new Color(0.48f, 0.39f, 0.28f, 0.30f), 2.0f);

        if (IsDraggingPlayCard)
        {
            var dropZone = new Rect2(arena.X - 345f * sx, arena.Y - 177f * sy,
                                     700f * sx, 315f * sy);
            DrawRect(dropZone, new Color(0.0f, 0.90f, 1.0f, 0.13f));
            DrawRect(dropZone, SvWbTheme.AccentCyan, false, 2.5f);
        }

        if (Controller?.State == null) return;

        DrawCrestCluster(1, GetEnemyCrestCenters(sz), font);
        DrawCrestCluster(0, GetAllyCrestCenters(sz), font);

        // 敌方信息栏固定右上角；我方信息栏固定在距窗口底部 54px 处
        DrawZoneCounterBar(1, new Rect2(sz.X - 238f, 18f, 218f, 34f), true, font);
        DrawZoneCounterBar(0, new Rect2(68f, sz.Y - 54f, 206f, 34f), false, font);

        DrawEnemyHandBacks(Controller.Hand(1).Count, new Vector2(265f, 4f), font);

        // 3. 攻击瞄准箭头（最上层）
        if (IsDraggingAttack)
            DrawAttackArrow(AttackDragStart, AttackDragCurrent);

        // 4. SelectTarget 瞄准虚线：从底部中央指向鼠标。
        //    原版在「请选择目标」时会从玩家侧拉出一条虚线，
        //    让人一眼知道「现在这一下点击会作用到哪」。
        if (IsAiming)
            DrawAimDashedLine(AimTarget);
    }

    /// <summary>56 段单位圆的 (cos, sin)，进程内只算一次。</summary>
    private static readonly Vector2[] UnitCircle56 = BuildUnitCircle(56);

    private static Vector2[] BuildUnitCircle(int seg)
    {
        var pts = new Vector2[seg];
        for (int i = 0; i < seg; i++)
        {
            float a = i * Mathf.Tau / seg;
            pts[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
        }
        return pts;
    }

    /// <summary>复用的顶点缓冲：_Draw 每帧都跑，不能再 new。</summary>
    private readonly Vector2[] _ellipsePts = new Vector2[56];

    private void DrawEllipse(Vector2 c, float rx, float ry, Color col)
    {
        // 每帧 new Vector2[56] 是纯垃圾：三个椭圆就是 168 个对象，
        // 拖拽手牌时每帧都跑。单位圆预先算好，只做缩放平移写入复用缓冲。
        for (int i = 0; i < _ellipsePts.Length; i++)
        {
            var u = UnitCircle56[i];
            _ellipsePts[i] = new Vector2(c.X + u.X * rx, c.Y + u.Y * ry);
        }
        DrawColoredPolygon(_ellipsePts, col);
    }

    /// <summary>
    /// 从底部中央向我方鼠标位置画一条金色虚线瞄准线，末端带小箭头。
    ///
    /// 用「分段 draw」而不是引擎的虚线 API：Godot 4 没有内置虚线绘制，
    /// 手工按固定步长切段反而更可控（步长与间隙都能按需调）。
    /// </summary>
    private void DrawAimDashedLine(Vector2 target)
    {
        Vector2 start = new(Size.X * 0.5f, Size.Y - 40f);
        Vector2 delta = target - start;
        float dist = delta.Length();
        if (dist < 24f) return;

        Vector2 dir = delta / dist;
        const float dash = 14f, gap = 9f;
        var col = new Color(0.98f, 0.85f, 0.28f, 0.80f);

        for (float d = 0; d < dist - 12f; d += dash + gap)
        {
            float e = Mathf.Min(d + dash, dist - 12f);
            DrawLine(start + dir * d, start + dir * e, col, 2.5f);
        }

        // 末端箭头
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 tip = target;
        var head = new[]
        {
            tip,
            tip - dir * 18f + perp * 9f,
            tip - dir * 18f - perp * 9f,
        };
        DrawColoredPolygon(head, new Color(0.98f, 0.85f, 0.28f, 0.9f));
    }

    private void DrawAttackArrow(Vector2 from, Vector2 to)
    {
        if (from.DistanceTo(to) < 8f) return;
        DrawLine(from, to, new Color("FDE047") { A = 0.85f }, 5f);
        DrawLine(from, to, new Color(1f, 1f, 1f, 0.55f), 2f);

        Vector2 dir = (to - from).Normalized();
        Vector2 perp = new(-dir.Y, dir.X);
        var head = new[]
        {
            to,
            to - dir * 22f + perp * 11f,
            to - dir * 22f - perp * 11f,
        };
        DrawColoredPolygon(head, new Color("FDE047"));
    }

    private void DrawCrestCluster(int playerId, Vector2[] centers, Font font)
    {
        var crests = Controller.Crests(playerId);

        for (int i = 0; i < centers.Length; i++)
        {
            Vector2 c = centers[i];
            DrawCircle(c, 22f, new Color(0.14f, 0.18f, 0.16f, 0.62f));
            DrawArc(c, 22f, 0, Mathf.Tau, 24, new Color(0.45f, 0.52f, 0.48f, 0.40f), 1.5f);

            if (i >= crests.Count) continue;
            var ent = crests[i];
            if (ent == null) continue;

            DrawCircle(c, 21f, new Color("0284C7"));
            DrawArc(c, 22f, 0, Mathf.Tau, 28, new Color("FDE047"), 2.5f);

            // 中文首字（不印英文缩写）：从简中卡名取第一个汉字。
            string full = DeckEditorViewModel.DisplayName(ent.Def);
            string nm = full.Length > 0 ? full.Substring(0, 1) : "?";
            Vector2 tw = font.GetStringSize(nm, HorizontalAlignment.Left, -1, 15);
            DrawString(font, new Vector2(c.X - tw.X * 0.5f, c.Y + 6f),
                       nm, HorizontalAlignment.Left, -1, 15, Colors.White);

            // 右下角数值角标：吟唱倒计时或信仰值
            int badgeVal = 0;
            bool isFaith = ent.IsFaith
                           || ent.Def?.Kind == ShadowverseFan.KernelV3.Ast.CardKind.Faith;
            bool showBadge = false;
            try
            {
                if (isFaith)
                {
                    // 信仰是整局永驻资源：值为 0 也必须常驻显示角标，
                    // 否则玩家会以为信仰图标丢了。
                    var faKey = Controller.Schema.Counter("faith");
                    if (ent.Counters != null) ent.Counters.TryGetValue(faKey, out badgeVal);
                    showBadge = true;
                }
                else
                {
                    var cdKey = Controller.Schema.Counter("countdown");
                    if (ent.Counters != null
                        && ent.Counters.TryGetValue(cdKey, out int cdv) && cdv > 0)
                    {
                        badgeVal = cdv;
                        showBadge = true;
                    }
                }
            }
            catch { }

            if (showBadge)
            {
                Vector2 bc = new(c.X + 14f, c.Y + 14f);
                DrawCircle(bc, 9f, isFaith ? new Color("6D28D9") : new Color("B45309"));
                DrawArc(bc, 9f, 0, Mathf.Tau, 18, new Color("FDE047"), 1.2f);
                string bs = badgeVal.ToString();
                Vector2 bsz = font.GetStringSize(bs, HorizontalAlignment.Left, -1, 11);
                DrawString(font, new Vector2(bc.X - bsz.X * 0.5f, bc.Y + 4f), bs,
                           HorizontalAlignment.Left, -1, 11, Colors.White);
            }
        }
    }

    private void DrawZoneCounterBar(int playerId, Rect2 r, bool showHand, Font font)
    {
        DrawRect(r, new Color(0.06f, 0.08f, 0.08f, 0.88f));
        DrawRect(r, new Color(0.40f, 0.44f, 0.40f, 0.65f), false, 1f);

        // 直接读列表长度：EnumerateZone 会建枚举器 + LINQ Count，
        // _Draw 每帧跑两次，属于纯开销。
        int deck = Controller.State.Players[playerId].Deck.Count;
        int grave = Controller.State.Players[playerId].Cemetery.Count;
        int crest = Controller.Crests(playerId).Count;

        string txt = showHand
            ? $"\U0001F0CF {Controller.Hand(playerId).Count}   \U0001F4E6 {deck}   \U0001FAA6 {grave}"
            : $"\u2746 {crest}   \U0001F4E6 {deck}   \U0001FAA6 {grave}";

        DrawString(font, r.Position + new Vector2(10f, 23f), txt,
                   HorizontalAlignment.Left, -1, 13, new Color("E2E8F0"));
    }

    private void DrawEnemyHandBacks(int count, Vector2 origin, Font font)
    {
        // 标准卡牌比例 52x72（不是 24x82 的细长蓝条）。
        // 牌多时自适应收紧间距，绝不向右/向上溢出。
        int n = Math.Min(count, 9);
        const float cw = 52f, ch = 72f;
        float step = n <= 1 ? cw : Math.Min(cw - 6f, (420f - cw) / (n - 1));

        for (int i = 0; i < n; i++)
        {
            float x = origin.X + i * step;
            var r = new Rect2(x, origin.Y, cw, ch);

            // 深酒红底 + 鎏金描边
            DrawRect(r, new Color("3B1220"));
            DrawRect(r, new Color("B45309"), false, 1.6f);

            // 中心菱形背纹
            Vector2 c = new(x + cw * 0.5f, origin.Y + ch * 0.5f);
            Vector2[] gem =
            {
                new(c.X, c.Y - 12f), new(c.X + 9f, c.Y),
                new(c.X, c.Y + 12f), new(c.X - 9f, c.Y),
            };
            DrawPolyline(new[] { gem[0], gem[1], gem[2], gem[3], gem[0] },
                         new Color("FCD34D") { A = 0.75f }, 1.4f);
            DrawCircle(c, 3f, new Color("FCD34D") { A = 0.55f });
        }

        if (count > 9)
            DrawString(font, new Vector2(origin.X + 9 * step + 4f, origin.Y + 40f),
                       "+" + (count - 9), HorizontalAlignment.Left, -1, 13,
                       new Color("FCD34D"));
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || !mb.Pressed) return;
        if (mb.ButtonIndex != MouseButton.Left) return;

        // 命中判定必须用与绘制同一套动态坐标。
        //
        // 早先 _Draw 走 GetAllyCrestCenters(sz)（贴窗口底部），
        // 而这里还是写死的 AllyCrestCenters（y=566/618）——
        // 窗口一拉高，纹章画在底部、判定却留在半空，点上去毫无反应。
        Vector2 sz = Size;
        if (sz.X <= 0 || sz.Y <= 0) return;

        if (TryHitCrest(mb.Position, GetAllyCrestCenters(sz), 0)) return;
        TryHitCrest(mb.Position, GetEnemyCrestCenters(sz), 1);
    }

    /// <summary>命中某个纹章圆槽就派发事件并吃掉这次点击。</summary>
    private bool TryHitCrest(Vector2 at, Vector2[] centers, int playerId)
    {
        for (int i = 0; i < centers.Length; i++)
        {
            if (at.DistanceTo(centers[i]) > 22f) continue;

            var crests = Controller?.Crests(playerId);
            if (crests != null && i < crests.Count && crests[i] != null)
            {
                OnCrestClicked?.Invoke(crests[i]);
                AcceptEvent();
                return true;
            }
        }
        return false;
    }
}
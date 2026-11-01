using Godot;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 《影之诗：超凡世界》星象圣殿背景画布。
///
/// 第一版用纯 ColorRect 铺满整屏 #090D16，看起来像深色 IDE。
/// 这里换成程序化渐变 + 金青双色星轨同心圆 + 斜向圣光带，
/// 零贴图依赖就有「圣殿穹顶」的质感。
/// </summary>
public partial class AstralBackgroundCanvas : Control
{
    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        Vector2 sz = Size;
        if (sz.X <= 0 || sz.Y <= 0) return;

        // 1. 垂直渐变（深皇家紫 → 近黑 → 幽蓝）
        var top = new Color("0B0A1F");
        var mid = new Color("120F2A");
        var bot = new Color("070A14");
        int bands = 40;
        for (int i = 0; i < bands; i++)
        {
            float f = i / (float)(bands - 1);
            Color c = f < 0.5f ? top.Lerp(mid, f * 2f) : mid.Lerp(bot, (f - 0.5f) * 2f);
            DrawRect(new Rect2(0, f * sz.Y, sz.X, sz.Y / bands + 1f), c);
        }

        // 2. 星轨同心圆（以屏幕上方偏中为圆心，营造穹顶感）
        Vector2 dome = new(sz.X * 0.5f, sz.Y * 0.12f);
        var ringGold = new Color(0.83f, 0.68f, 0.35f, 0.10f);
        var ringCyan = new Color(0.35f, 0.85f, 1.0f, 0.07f);
        for (int i = 1; i <= 7; i++)
        {
            float r = sz.Y * (0.35f + i * 0.16f);
            DrawArc(dome, r, Mathf.Pi * 0.08f, Mathf.Pi * 0.92f, 64,
                    i % 2 == 0 ? ringGold : ringCyan, i % 3 == 0 ? 1.6f : 1.0f);
        }

        // 3. 斜向圣光带（右上 → 左下）
        for (int i = 0; i < 3; i++)
        {
            float off = i * 260f;
            var a = new Vector2(sz.X * 0.72f - off, 0);
            var b = new Vector2(sz.X * 0.18f - off, sz.Y);
            DrawLine(a, b, new Color(0.72f, 0.80f, 1.0f, 0.035f), 90f);
        }

        // 4. 散落星点
        var rng = new RandomNumberGenerator();
        rng.Seed = 20240501;
        for (int i = 0; i < 120; i++)
        {
            float x = rng.Randf() * sz.X;
            float y = rng.Randf() * sz.Y;
            float r = rng.Randf() * 1.4f + 0.4f;
            float alpha = 0.10f + rng.Randf() * 0.35f;
            DrawCircle(new Vector2(x, y), r, new Color(1f, 1f, 1f, alpha));
        }

        // 5. 底部暗角，压住画面下沿
        DrawRect(new Rect2(0, sz.Y * 0.82f, sz.X, sz.Y * 0.18f),
                 new Color(0.02f, 0.03f, 0.06f, 0.45f));
    }
}

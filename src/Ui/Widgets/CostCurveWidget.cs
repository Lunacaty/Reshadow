using System;
using System.Linq;
using Godot;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Widgets;

/// <summary>
/// 1~8+ 费用曲线柱状图。
///
/// 用 Control 自绘而不是堆 8 个 Panel —— 柱子高度随数据变化，
/// 用节点树表达要反复调 anchor，自绘一次 DrawRect 就够。
/// </summary>
public partial class CostCurveWidget : Control
{
    private int[] _bins = new int[8];
    
    public void SetData(int[] bins)
    {
        _bins = bins ?? new int[8];
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = Size;
        if (size.X <= 0 || size.Y <= 0) return;

        int max = _bins.Length == 0 ? 0 : _bins.Max();
        if (max <= 0) max = 1;

        int n = 8;
        float gap = 4f;
        float barW = (size.X - gap * (n - 1)) / n;
        float labelH = 14f;
        float baseY = size.Y - labelH;

        var font = ThemeDB.FallbackFont;
        int fs = 10;

        for (int i = 0; i < n; i++)
        {
            float x = i * (barW + gap);
            int v = i < _bins.Length ? _bins[i] : 0;
            float h = baseY * (v / (float)max);
            if (v > 0 && h < 3) h = 3;

            // 柱体：青蓝渐变（用两段矩形近似）
            if (h > 0)
            {
                var top = new Color("00E5FF");
                var bot = new Color(0.02f, 0.30f, 0.55f, 0.95f);
                DrawRect(new Rect2(x, baseY - h, barW, h), bot);
                DrawRect(new Rect2(x, baseY - h, barW, h * 0.45f), top);
            }
            else
            {
                DrawRect(new Rect2(x, baseY - 2, barW, 2), new Color("2A3652"));
            }

            // 柱顶数字
            if (v > 0)
            {
                string t = v.ToString();
                float tw = font.GetStringSize(t, HorizontalAlignment.Left, -1, fs).X;
                DrawString(font, new Vector2(x + (barW - tw) / 2, baseY - h - 2),
                           t, HorizontalAlignment.Left, -1, fs, new Color("F8FAFC"));
            }

            // 底部费用标签
            string lbl = i < 7 ? (i + 1).ToString() : "8+";
            float lw = font.GetStringSize(lbl, HorizontalAlignment.Left, -1, fs).X;
            DrawString(font, new Vector2(x + (barW - lw) / 2, size.Y - 2),
                       lbl, HorizontalAlignment.Left, -1, fs, new Color("94A3B8"));
        }
    }
}

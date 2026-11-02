using System;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 伤害/治疗飘字：在目标中心点上方弹出、放大回弹、上飘并淡出。
///
/// 层级必须高于 BattleVfxDirector（ZIndex 88），否则会被特效层的
/// 扩散圆环盖住；描边必须双层（外黑内亮），否则在深色立绘与浅色沙地
/// 上都会糊成一片。
/// </summary>
public partial class DamageFloat : Control
{
    private float _age;
    private float _life;
    private Vector2 _from;
    private Vector2 _rise;
    private string _text;
    private bool _heal;

    public void Setup(int amount, Vector2 centerPos, bool heal = false)
    {
        _heal = heal;
        _text = (heal ? "+" : "-") + Math.Abs(amount);
        Size = new Vector2(120, 52);

        // 以传入的实体中心点为基准居中偏上定位
        _from = centerPos - new Vector2(Size.X * 0.5f, 36f);
        _rise = new Vector2(0f, -56f);
        _life = 0.90f;
        _age = 0f;
        Position = _from;
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 88;
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= _life) { QueueFree(); return; }

        float t = _age / _life;
        float e = Ease.OutCubic(t);
        Position = _from + _rise * e;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float t = Mathf.Clamp(_age / _life, 0f, 1f);
        float pop = t < 0.18f ? Ease.OutBack(t / 0.18f) : 1f;
        float scale = 0.75f + 0.35f * pop;
        float alpha = t < 0.65f ? 1f : 1f - (t - 0.65f) / 0.35f;

        var font = ThemeDB.FallbackFont;
        int fontSize = Mathf.RoundToInt(32 * scale);
        var size = font.GetStringSize(_text, HorizontalAlignment.Left, -1, fontSize);

        var drawPos = new Vector2((Size.X - size.X) * 0.5f, 36f);

        Color main = _heal ? new Color("4ADE80") : new Color("EF4444");
        Color rim = _heal ? new Color("DCFCE7") : new Color("FEF08A");
        Color dark = new Color(0.04f, 0.02f, 0.02f, 0.92f * alpha);

        // 外圈黑底描边 + 内圈高光描边，确保在任何卡面和沙地上都醒目
        Vector2[] outerOffsets =
        {
            new(3, 0), new(-3, 0), new(0, 3), new(0, -3),
            new(2, 2), new(-2, 2), new(2, -2), new(-2, -2),
        };
        foreach (var off in outerOffsets)
            DrawString(font, drawPos + off, _text, HorizontalAlignment.Left, -1, fontSize, dark);

        foreach (var off in new[] { new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1) })
            DrawString(font, drawPos + off, _text, HorizontalAlignment.Left, -1, fontSize, rim with { A = alpha * 0.85f });

        DrawString(font, drawPos, _text, HorizontalAlignment.Left, -1, fontSize, main with { A = alpha });
    }
}
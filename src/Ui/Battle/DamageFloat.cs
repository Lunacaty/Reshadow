using System;
using Godot;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 伤害浮字：受到伤害时从目标头顶浮出、上飘并淡出。
///
/// 自己驱动生命周期（_Process 里推进），
/// 不依赖父级 Rebuild —— 否则玩家一操作就把数字抹掉了。
/// </summary>
public partial class DamageFloat : Control
{
    private float _age;
    private float _life;
    private Vector2 _from;
    private Vector2 _rise;
    private string _text;
    private int _amount;
    private bool _heal;

    public void Setup(int amount, Vector2 pos, bool heal = false)
    {
        _amount = amount;
        _heal = heal;
        _text = (heal ? "+" : "-") + Math.Abs(amount);
        _from = pos;
        _rise = new Vector2(0, -54f);
        _life = 0.85f;
        _age = 0f;
        Position = pos;
        Size = new Vector2(96, 44);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 60;
    }

    public override void _Process(double delta)
    {
        _age += (float)delta;
        if (_age >= _life) { QueueFree(); return; }

        float t = _age / _life;
        // 先快后慢地上飘，末段淡出
        float e = Ease.OutCubic(t);
        Position = _from + _rise * e;
        QueueRedraw();
    }

    public override void _Draw()
    {
        float t = Mathf.Clamp(_age / _life, 0f, 1f);
        // 前 15% 是「弹出来」的放大，之后稳定
        float pop = t < 0.15f ? Ease.OutBack(t / 0.15f) : 1f;
        float scale = 0.7f + 0.3f * pop;
        float alpha = t < 0.6f ? 1f : 1f - (t - 0.6f) / 0.4f;

        var font = ThemeDB.FallbackFont;
        int fontSize = Mathf.RoundToInt(30 * scale);
        var size = font.GetStringSize(_text, HorizontalAlignment.Left, -1, fontSize);

        var center = new Vector2(Size.X * 0.5f - size.X * 0.5f, 30f);

        Color main = _heal ? new Color("4ADE80") : new Color("F87171");
        Color dark = new Color(0.06f, 0.04f, 0.04f, 0.85f * alpha);

        // 描边：深色先画一圈，保证在任何底色上都读得清
        foreach (var off in new[] { new Vector2(2,0), new Vector2(-2,0), new Vector2(0,2), new Vector2(0,-2) })
            DrawString(font, center + off, _text, HorizontalAlignment.Left, -1, fontSize,
                       dark);

        DrawString(font, center, _text, HorizontalAlignment.Left, -1, fontSize,
                   main with { A = alpha });
    }
}
using System;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 一簇会自己飘散并淡出的光点。
///
/// 用于：卡牌打出时化作粒子、随从离场时破碎成光点。
/// 自己驱动生命周期，挂在 _overlayLayer 上，不随 Rebuild 销毁。
/// </summary>
public partial class ParticleBurst : Control
{
    private struct P
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Life;
        public float Age;
        public float Size;
        public Color Tint;
    }

    private P[] _pts = Array.Empty<P>();
    private float _total;

    /// <summary>
    /// 配置一簇光点。
    /// </summary>
    /// <param name="origin">起点（相对本控件）。</param>
    /// <param name="count">数量。</param>
    /// <param name="spread">初速度范围。</param>
    /// <param name="tint">主色调；每个点会在其附近抖动。</param>
    /// <param name="gravity">正的向下坠，负的向上飘。</param>
    public void Setup(Vector2 origin, int count, float spread, Color tint,
                      float life = 0.7f, float gravity = 60f, float size = 3.2f)
    {
        var rng = new Random(1337 + count * 31);
        _pts = new P[count];
        _total = life;

        for (int i = 0; i < count; i++)
        {
            float ang = (float)(rng.NextDouble() * Math.PI * 2.0);
            float spd = spread * (0.35f + (float)rng.NextDouble() * 0.65f);
            _pts[i] = new P
            {
                Pos = origin + new Vector2(
                    (float)(rng.NextDouble() - 0.5) * 46f,
                    (float)(rng.NextDouble() - 0.5) * 62f),
                Vel = new Vector2(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd - gravity * 0.4f),
                Life = life * (0.55f + (float)rng.NextDouble() * 0.45f),
                Age = 0f,
                Size = size * (0.5f + (float)rng.NextDouble() * 0.9f),
                Tint = tint.Lerp(Colors.White, (float)rng.NextDouble() * 0.55f),
            };
        }

        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 58;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        bool alive = false;

        for (int i = 0; i < _pts.Length; i++)
        {
            ref var p = ref _pts[i];
            if (p.Age >= p.Life) continue;
            p.Age += dt;
            p.Vel += new Vector2(0, 130f * dt);
            p.Vel *= 1f - 1.6f * dt;
            p.Pos += p.Vel * dt;
            alive = true;
        }

        if (!alive) { QueueFree(); return; }
        QueueRedraw();
    }

    public override void _Draw()
    {
        for (int i = 0; i < _pts.Length; i++)
        {
            var p = _pts[i];
            if (p.Age >= p.Life) continue;
            float t = p.Age / p.Life;
            float alpha = 1f - t;
            float r = p.Size * (1f - t * 0.55f);
            DrawCircle(p.Pos, r, p.Tint with { A = alpha * 0.95f });
            // 一点外发光，让光点在深色底上更亮
            DrawCircle(p.Pos, r * 2.1f, p.Tint with { A = alpha * 0.20f });
        }
    }
}
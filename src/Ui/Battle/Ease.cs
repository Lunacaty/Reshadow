using System;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>缓动曲线集合。</summary>
public static class Ease
{
    public static float Linear(float t) => t;

    public static float OutCubic(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u;
    }

    public static float InCubic(float t) => t * t * t;

    public static float InOutCubic(float t)
        => t < 0.5f ? 4f * t * t * t : 1f - Mathf.Pow(-2f * t + 2f, 3f) * 0.5f;

    /// <summary>末尾过冲的弹性曲线：入场「弹」起来的观感来源。</summary>
    public static float OutBack(float t)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    public static float OutQuint(float t)
    {
        float u = 1f - t;
        return 1f - u * u * u * u * u;
    }

    public static float InQuad(float t) => t * t;
    public static float OutQuad(float t) => 1f - (1f - t) * (1f - t);
}
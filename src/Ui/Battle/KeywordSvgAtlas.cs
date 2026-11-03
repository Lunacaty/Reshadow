using System.Collections.Generic;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 关键词与光环的 SVG 矢量贴图生成与缓存池（Godot 4 内置 ThorVG）。
///
/// 为什么用 SVG 而不是手写 DrawPolygon：
///   DrawRect / DrawLine 凑不出金属渐变与贝塞尔曲线，画出来是一块块纯色。
///   SVG 原生支持 linearGradient / radialGradient / path，
///   光栅化一次后缓存成 ImageTexture，每帧只是一次 DrawTextureRect。
/// </summary>
public static class KeywordSvgAtlas
{
    private static readonly Dictionary<string, Texture2D> _cache = new();

    /// <summary>取一张贴图；未登记的 key 返回 null（调用方回落到纯色绘制）。</summary>
    public static Texture2D Get(string key, float scale = 2.0f)
    {
        if (string.IsNullOrEmpty(key)) return null;
        string ck = key + "@" + scale.ToString("F1");
        if (_cache.TryGetValue(ck, out var hit)) return hit;

        string svg = BuildSvgXml(key);
        if (string.IsNullOrEmpty(svg)) return null;

        var img = new Image();
        if (img.LoadSvgFromString(svg, scale) != Error.Ok) return null;
        var tex = ImageTexture.CreateFromImage(img);
        _cache[ck] = tex;
        return tex;
    }

    public static void ClearCache() => _cache.Clear();

    private const string FrameWard = $"""
        <svg width="104" height="132" viewBox="0 0 104 132" xmlns="http://www.w3.org/2000/svg">
          <defs>
            <linearGradient id="wg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stop-color="#FEF08A"/>
              <stop offset="35%" stop-color="#F59E0B"/>
              <stop offset="70%" stop-color="#B45309"/>
              <stop offset="100%" stop-color="#FDE047"/>
            </linearGradient>
          </defs>
          <path d="M52 1 L84 10 L102 36 L102 96 L84 126 L52 131 L20 126 L2 96 L2 36 L20 10 Z"
                fill="none" stroke="url(#wg)" stroke-width="5.5" stroke-linejoin="bevel"/>
          <path d="M52 5 L80 13 L96 38 L96 94 L80 121 L52 126 L24 121 L8 94 L8 38 L24 13 Z"
                fill="none" stroke="#FFFBEB" stroke-width="1.3" stroke-opacity="0.85"/>
          <polygon points="52,0 61,8 52,18 43,8" fill="url(#wg)" stroke="#78350F" stroke-width="1"/>
        </svg>
        """;
    private const string FrameBarrier = $"""
        <svg width="104" height="132" viewBox="0 0 104 132" xmlns="http://www.w3.org/2000/svg">
          <defs>
            <radialGradient id="bg" cx="50%" cy="50%" r="55%">
              <stop offset="0%" stop-color="#38BDF8" stop-opacity="0.06"/>
              <stop offset="72%" stop-color="#0284C7" stop-opacity="0.22"/>
              <stop offset="100%" stop-color="#E0F2FE" stop-opacity="0.72"/>
            </radialGradient>
          </defs>
          <path d="M52 4 L96 28 L96 104 L52 128 L8 104 L8 28 Z" fill="url(#bg)"
                stroke="#7DD3FC" stroke-width="2.5"/>
          <path d="M52 4 L52 36 M96 28 L68 46 M8 28 L36 46 M36 46 L68 46 L82 76 L68 104 L36 104 L22 76 Z"
                fill="none" stroke="#E0F2FE" stroke-width="1.1" stroke-opacity="0.45"/>
        </svg>
        """;
    private const string FrameImmune = $"""
        <svg width="104" height="132" viewBox="0 0 104 132" xmlns="http://www.w3.org/2000/svg">
          <defs>
            <linearGradient id="ig" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stop-color="#A78BFA"/>
              <stop offset="50%" stop-color="#FDE047"/>
              <stop offset="100%" stop-color="#38BDF8"/>
            </linearGradient>
          </defs>
          <path d="M52 2 L98 26 L98 106 L52 130 L6 106 L6 26 Z" fill="none"
                stroke="url(#ig)" stroke-width="3" stroke-linejoin="round"/>
          <path d="M52 10 L90 31 L90 101 L52 122 L14 101 L14 31 Z" fill="none"
                stroke="#F5F3FF" stroke-width="1" stroke-opacity="0.55"/>
        </svg>
        """;
    private const string FrameAmbush = $"""
        <svg width="104" height="132" viewBox="0 0 104 132" xmlns="http://www.w3.org/2000/svg">
          <defs>
            <linearGradient id="ag" x1="0" y1="1" x2="0" y2="0">
              <stop offset="0%" stop-color="#4C1D95" stop-opacity="0.85"/>
              <stop offset="100%" stop-color="#1E1B4B" stop-opacity="0"/>
            </linearGradient>
          </defs>
          <rect x="4" y="60" width="96" height="68" fill="url(#ag)"/>
          <path d="M40 12 A16 16 0 1 0 64 12 A12 12 0 1 1 40 12 Z" fill="#C4B5FD" opacity="0.9"/>
        </svg>
        """;
    private const string FrameAura = $"""
        <svg width="104" height="132" viewBox="0 0 104 132" xmlns="http://www.w3.org/2000/svg">
          <defs>
            <linearGradient id="ug" x1="0" y1="0" x2="1" y2="0">
              <stop offset="0%" stop-color="#34D399" stop-opacity="0.9"/>
              <stop offset="100%" stop-color="#A7F3D0" stop-opacity="0.25"/>
            </linearGradient>
          </defs>
          <path d="M52 66 C24 44 8 52 2 74 C16 70 28 72 40 80 Z" fill="url(#ug)"/>
          <path d="M52 66 C80 44 96 52 102 74 C88 70 76 72 64 80 Z" fill="url(#ug)"/>
        </svg>
        """;
    private const string BadgeBane = $"""
        <svg width="28" height="28" viewBox="0 0 28 28" xmlns="http://www.w3.org/2000/svg">
          <defs><linearGradient id="gn" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stop-color="#6EE7B7"/><stop offset="100%" stop-color="#065F46"/>
          </linearGradient></defs>
          <circle cx="14" cy="14" r="12" fill="#052E16" stroke="url(#gn)" stroke-width="2"/>
          <path d="M8 8 L20 20 M20 8 L8 20" stroke="#6EE7B7" stroke-width="2.4"/>
        </svg>
        """;
    private const string BadgeDrain = $"""
        <svg width="28" height="28" viewBox="0 0 28 28" xmlns="http://www.w3.org/2000/svg">
          <defs><radialGradient id="bd" cx="50%" cy="40%" r="60%">
            <stop offset="0%" stop-color="#F87171"/><stop offset="100%" stop-color="#7F1D1D"/>
          </radialGradient></defs>
          <circle cx="14" cy="14" r="12" fill="#1C0A0A" stroke="#B91C1C" stroke-width="1.6"/>
          <path d="M14 5 C19 12 20 16 14 22 C8 16 9 12 14 5 Z" fill="url(#bd)"/>
        </svg>
        """;
    private const string BadgeFreeEvolve = $"""
        <svg width="28" height="28" viewBox="0 0 28 28" xmlns="http://www.w3.org/2000/svg">
          <defs><linearGradient id="fe" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stop-color="#FDE047"/><stop offset="100%" stop-color="#F59E0B"/>
          </linearGradient></defs>
          <path d="M14 2 L17 11 L26 14 L17 17 L14 26 L11 17 L2 14 L11 11 Z" fill="url(#fe)"/>
        </svg>
        """;
    private const string BadgeShield = $"""
        <svg width="28" height="28" viewBox="0 0 28 28" xmlns="http://www.w3.org/2000/svg">
          <defs><linearGradient id="sh" x1="0" y1="0" x2="0" y2="1">
            <stop offset="0%" stop-color="#FEF3C7"/><stop offset="100%" stop-color="#B45309"/>
          </linearGradient></defs>
          <path d="M14 3 L24 8 L24 15 C24 21 19 24 14 26 C9 24 4 21 4 15 L4 8 Z"
                fill="url(#sh)" stroke="#78350F" stroke-width="1.4"/>
        </svg>
        """;
    private const string RingImmune = $"""
        <svg width="132" height="132" viewBox="0 0 132 132" xmlns="http://www.w3.org/2000/svg">
          <defs><linearGradient id="ri" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stop-color="#FDE047" stop-opacity="0.9"/>
            <stop offset="100%" stop-color="#A78BFA" stop-opacity="0.35"/>
          </linearGradient></defs>
          <circle cx="66" cy="66" r="58" fill="none" stroke="url(#ri)" stroke-width="3"
                  stroke-dasharray="10 6"/>
          <circle cx="66" cy="66" r="48" fill="none" stroke="#FEF08A" stroke-width="1"
                  stroke-opacity="0.4"/>
        </svg>
        """;

    private const string FrameIntimidate = $"""
        <svg width="104" height="132" viewBox="0 0 104 132"
             xmlns="http://www.w3.org/2000/svg">
          <defs>
            <linearGradient id="itg" x1="0" y1="0" x2="1" y2="1">
              <stop offset="0%" stop-color="#7F1D1D"/>
              <stop offset="55%" stop-color="#3B0764"/>
              <stop offset="100%" stop-color="#1E1B4B"/>
            </linearGradient>
          </defs>
          <path d="M52 2 L94 20 L100 70 L52 130 L4 70 L10 20 Z"
                fill="none" stroke="url(#itg)" stroke-width="4"/>
          <path d="M14 30 L22 44 L14 58 M90 30 L82 44 L90 58 M30 118 L38 106 L46 118"
                fill="none" stroke="#DC2626" stroke-width="2.2"/>
        </svg>
        """;

    private const string BadgeMultiAttack = $"""
        <svg width="28" height="28" viewBox="0 0 28 28"
             xmlns="http://www.w3.org/2000/svg">
          <defs><linearGradient id="ma" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0%" stop-color="#FDE047"/>
            <stop offset="100%" stop-color="#B45309"/>
          </linearGradient></defs>
          <circle cx="14" cy="14" r="12" fill="#1C1207" stroke="url(#ma)" stroke-width="1.8"/>
          <path d="M8 19 L17 6 M12 20 L21 7" stroke="#FDE047"
                stroke-width="2" stroke-linecap="round"/>
        </svg>
        """;


    private static string BuildSvgXml(string key) => key switch
    {
        "frame_ward" => FrameWard,
        "frame_barrier" => FrameBarrier,
        "frame_immune" => FrameImmune,
        "frame_ambush" => FrameAmbush,
        "frame_intimidate" => FrameIntimidate,
        "frame_aura" => FrameAura,
        "badge_bane" => BadgeBane,
        "badge_drain" => BadgeDrain,
        "badge_free_evolve" => BadgeFreeEvolve,
        "badge_multi_attack" => BadgeMultiAttack,
        "badge_shield" => BadgeShield,
        "ring_immune" => RingImmune,
        _ => null,
    };
}

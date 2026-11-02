using System;
using System.Collections.Generic;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>棋子当前的动画变换（全部由 BoardAnimDirector 驱动）。</summary>
public struct UnitMotion
{
    /// <summary>相对座位的像素偏移。入场从下方钻出时给正 Y。</summary>
    public Vector2 Offset;
    /// <summary>缩放。</summary>
    public float Scale;
    /// <summary>水平翻面程度：0=正面，1=完整翻过（宽度压到 0 再展开）。</summary>
    public float FlipX;
    /// <summary>0..1 透明度。</summary>
    public float Alpha;

    public static UnitMotion Idle => new()
    {
        Offset = Vector2.Zero,
        Scale = 1f,
        FlipX = 0f,
        Alpha = 1f,
    };

    public bool IsIdle => Offset == Vector2.Zero
                         && Mathf.IsEqualApprox(Scale, 1f)
                         && Mathf.IsEqualApprox(FlipX, 0f)
                         && Mathf.IsEqualApprox(Alpha, 1f);
}

/// <summary>
/// 战场棋子动画调度器。
///
/// 关键设计：动画状态按 **EntityId** 存在这里，而不是挂在控件上。
/// BattleScreenRoot.Rebuild() 每次都会销毁重建全部棋子控件 ——
/// 状态若挂在控件上，玩家一操作动画就断在半路。
/// 控件每帧从 Director 查自己的变换，重建后自然接上。
/// </summary>
public sealed class BoardAnimDirector
{
    private sealed class Track
    {
        public UnitMotion Motion = UnitMotion.Idle;
        public readonly List<Anim> Running = new();
    }

    private readonly Dictionary<long, Track> _tracks = new();

    /// <summary>
    /// 正在「选攻击目标」的棋子。
    ///
    /// 这类棋子要持续上浮 + 上下浮动，表示「已被玩家拿起、等你选对象」。
    /// 它不影响真实状态，属于纯表现，因此不占表演队列。
    /// </summary>
    private long _aimingEntity = -1;
    private float _aimPhase;

    public void SetAiming(long entityId)
    {
        if (_aimingEntity == entityId) return;
        _aimingEntity = entityId;
        _aimPhase = 0f;
    }

    public void ClearAiming()
    {
        if (_aimingEntity >= 0 && _tracks.TryGetValue(_aimingEntity, out var t))
            t.Motion = UnitMotion.Idle;
        _aimingEntity = -1;
    }

    private Track TrackOf(long id)
    {
        if (!_tracks.TryGetValue(id, out var t))
        {
            t = new Track();
            _tracks[id] = t;
        }
        return t;
    }

    public UnitMotion MotionOf(long entityId)
        => _tracks.TryGetValue(entityId, out var t) ? t.Motion : UnitMotion.Idle;

    /// <summary>该棋子是否还在播动画（控件据此决定要不要每帧重绘）。</summary>
    public bool IsAnimating(long entityId)
        => entityId == _aimingEntity
        || (_tracks.TryGetValue(entityId, out var t) && t.Running.Count > 0);

    public void Clear(long entityId) => _tracks.Remove(entityId);

    public void ClearAll() => _tracks.Clear();

    /// <summary>把一段动画挂到某个棋子上。</summary>
    public void Play(long entityId, Anim anim)
    {
        if (anim == null) return;
        var t = TrackOf(entityId);
        t.Running.Add(anim);
    }

    /// <summary>每帧推进。</summary>
    public void Step(float dt)
    {
        // 选目标浮动：独立于一次性动画，持续到 ClearAiming 为止
        if (_aimingEntity >= 0 && _tracks.TryGetValue(_aimingEntity, out var aiming))
        {
            _aimPhase += dt;
            float bob = Mathf.Sin(_aimPhase * 6.2f) * 5f;
            aiming.Motion.Offset = new Vector2(0, -18f + bob);
        }

        if (_tracks.Count == 0) return;

        List<long> finished = null;

        foreach (var kv in _tracks)
        {
            var t = kv.Value;
            if (t.Running.Count == 0) continue;

            for (int i = t.Running.Count - 1; i >= 0; i--)
            {
                if (!t.Running[i].Step(dt))
                {
                    t.Running.RemoveAt(i);
                }
            }

            if (t.Running.Count == 0 && t.Motion.IsIdle && kv.Key != _aimingEntity)
            {
                (finished ??= new List<long>()).Add(kv.Key);
            }
        }

        // 动画播完且回到静止态就清掉轨道，避免字典无限增长
        if (finished != null)
            foreach (var id in finished) _tracks.Remove(id);
    }

    // ---------------------------------------------------------------- 预设动画

    /// <summary>
    /// 入场：从座位**下方**钻上来。
    ///
    /// 起点偏移 = 棋子高度，正好完全藏在座位下沿之外，
    /// 再配上淡入，看起来就是从地里冒出来。
    /// </summary>
    public void PlayEnter(long entityId, float height = 124f)
    {
        var t = TrackOf(entityId);
        t.Motion = UnitMotion.Idle;
        t.Running.Clear();

        Play(entityId, new Anim(0.34f, Ease.OutBack, v =>
        {
            t.Motion.Offset = new Vector2(0, height * (1f - v));
            t.Motion.Alpha = Mathf.Min(1f, v * 1.8f);
        }, () => { t.Motion = UnitMotion.Idle; }));
    }

    /// <summary>
    /// 离场：破碎成光点。
    ///
    /// 棋子本身缩小 + 淡出，同时由 BattleScreenRoot 在该位置放一簇粒子。
    /// </summary>
    public void PlayLeave(long entityId, Action onDone = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.28f, Ease.InCubic, v =>
        {
            t.Motion.Scale = 1f - v * 0.75f;
            t.Motion.Alpha = 1f - v;
            t.Motion.Offset = new Vector2(0, -v * 18f);
        }, () =>
        {
            t.Motion = UnitMotion.Idle;
            onDone?.Invoke();
        }));
    }

    /// <summary>
    /// 进化 / 超进化：快速升空 → 水平翻面 → 砸回来。
    ///
    /// 三段合成一条曲线，避免中途被别的动画打断时停在半空。
    /// </summary>
    public void PlayEvolve(long entityId, bool super)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        float rise = super ? 92f : 74f;
        Color flash = super ? new Color("C084FC") : new Color("F59E0B");
        _ = flash;

        Play(entityId, new Anim(0.62f, Ease.Linear, v =>
        {
            if (v < 0.42f)
            {
                // ① 升空：先快后慢
                float k = Ease.OutCubic(v / 0.42f);
                t.Motion.Offset = new Vector2(0, -rise * k);
                t.Motion.FlipX = Ease.InOutCubic(k) * 0.5f;
                t.Motion.Scale = 1f + 0.06f * k;
            }
            else if (v < 0.58f)
            {
                // ② 顶点翻面：宽度压到 0 再张开，看起来是转了 180°
                float k = (v - 0.42f) / 0.16f;
                t.Motion.Offset = new Vector2(0, -rise);
                t.Motion.FlipX = 0.5f + k * 0.5f;
                t.Motion.Scale = 1.06f;
            }
            else
            {
                // ③ 砸回来：加速下落 + 落地小幅回弹
                float k = Ease.InCubic((v - 0.58f) / 0.42f);
                t.Motion.Offset = new Vector2(0, -rise * (1f - k));
                t.Motion.FlipX = 0f;
                t.Motion.Scale = 1f + 0.10f * Mathf.Sin(k * Mathf.Pi);
            }
        }, () => { t.Motion = UnitMotion.Idle; }));
    }

    /// <summary>
    /// 被超进化随从破坏：先弹向自己的主战者，砸到后再破碎。
    /// </summary>
    public void PlayKnockToLeader(long entityId, Vector2 targetOffset, Action onDone = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.40f, Ease.OutQuint, v =>
        {
            t.Motion.Offset = targetOffset * v;
            t.Motion.Scale = 1f - v * 0.30f;
            t.Motion.FlipX = v * 0.35f;
            t.Motion.Alpha = 1f - Mathf.Max(0f, (v - 0.75f) / 0.25f);
        }, () =>
        {
            t.Motion = UnitMotion.Idle;
            onDone?.Invoke();
        }));
    }

    /// <summary>攻击冲锋：撞向目标再弹回原位。</summary>
    public void PlayLunge(long entityId, Vector2 targetOffset, Action onHit = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.34f, Ease.Linear, v =>
        {
            if (v < 0.42f)
            {
                t.Motion.Offset = targetOffset * Ease.InQuad(v / 0.42f) * 0.82f;
            }
            else
            {
                float k = (v - 0.42f) / 0.58f;
                t.Motion.Offset = targetOffset * 0.82f * (1f - Ease.OutQuad(k));
            }
        }, () =>
        {
            t.Motion = UnitMotion.Idle;
            onHit?.Invoke();
        }));
    }

    /// <summary>受击抖动（未死亡时）。</summary>
    public void PlayHitShake(long entityId)
    {
        var t = TrackOf(entityId);
        Play(entityId, new Anim(0.18f, Ease.Linear, v =>
        {
            float amp = 6f * (1f - v);
            t.Motion.Offset = new Vector2(Mathf.Sin(v * 46f) * amp, 0);
        }, () => { t.Motion = UnitMotion.Idle; }));
    }
}
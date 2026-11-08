using System;
using System.Collections.Generic;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>棋子当前的动画变换（全部由 BoardAnimDirector 驱动）。</summary>
public struct UnitMotion
{
    public Vector2 Offset;

    /// <summary>
    /// 布局重排偏移：与 Offset 分开一通道，因为两者语义不同。
    /// Offset 是攻击/受击等一次性演出，LayoutOffset 是「我还钉在旧槽位」的残留位移。
    /// 混用一个通道会让攻击动画把布板位移冲掉。
    /// </summary>
    public Vector2 LayoutOffset;

    public float Scale;
    public float FlipX;
    public float Alpha;

    public static UnitMotion Idle => new()
    {
        Offset = Vector2.Zero,
        LayoutOffset = Vector2.Zero,
        Scale = 1f,
        FlipX = 0f,
        Alpha = 1f,
    };

    public bool IsIdle => Offset == Vector2.Zero
                         && LayoutOffset == Vector2.Zero
                         && Mathf.IsEqualApprox(Scale, 1f)
                         && Mathf.IsEqualApprox(FlipX, 0f)
                         && Mathf.IsEqualApprox(Alpha, 1f);
}

/// <summary>
/// 战场棋子动画调度器（按 EntityId 存储轨道状态，跨 Rebuild 存活）。
/// </summary>
public sealed class BoardAnimDirector
{
    private sealed class Track
    {
        public UnitMotion Motion = UnitMotion.Idle;
        public readonly List<Anim> Running = new();
    }

    /// <summary>
    /// 动画结束复位 Motion，但**保留 LayoutOffset**。
    ///
    /// LayoutOffset 是「被钉在旧槽位上的残余偏移」，由布局重排自行收敛。
    /// 直接赋 UnitMotion.Idle 会把它清零，正在平滑过渡的随从就会瞬间跳到新位置 ——
    /// 表现是「被攻击/被选中时先往下瞬移一下」。
    /// </summary>
    private static void ResetKeepLayout(Track t)
    {
        if (t == null) return;
        var keep = t.Motion.LayoutOffset;
        t.Motion = UnitMotion.Idle;
        t.Motion.LayoutOffset = keep;
    }

    private readonly Dictionary<long, Track> _tracks = new();

    private long _aimingEntity = -1;
    private float _aimPhase;

    /// <summary>
    /// 无条件动画：玩家按住随从准备攻击选目标时，随从立即上浮并呼吸浮动。
    ///
    /// 必须调用 TrackOf 确保轨道存在 —— 早先只写了 _aimingEntity，
    /// 而 Step 里用 TryGetValue 查轨道；随从入场动画播完后轨道会被清掉，
    /// TryGetValue 恒为 false，浮动偏移一行都没执行过。
    /// </summary>
    public void SetAiming(long entityId)
    {
        if (_aimingEntity == entityId) return;
        ClearAiming();
        _aimingEntity = entityId;
        _aimPhase = 0f;
        var t = TrackOf(entityId);
        t.Motion.Offset = new Vector2(0f, -18f);
        t.Motion.Scale = 1.06f;
        t.Motion.Alpha = 1f;
    }

    public void ClearAiming()
    {
        if (_aimingEntity >= 0 && _tracks.TryGetValue(_aimingEntity, out var t))
        {
            if (t.Running.Count == 0) ResetKeepLayout(t);
        }
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

    public bool IsAnimating(long entityId)
        => entityId == _aimingEntity
        || (_tracks.TryGetValue(entityId, out var t) && (t.Running.Count > 0 || !t.Motion.IsIdle));

    public void Clear(long entityId) => _tracks.Remove(entityId);

    public void ClearAll()
    {
        _tracks.Clear();
        _aimingEntity = -1;
    }


    /// <summary>
    /// 把随从**视觉上钉在旧槽位**：控件已经在新槽位，用 LayoutOffset 把它拉回去。
    ///
    /// 用于「有随从阵亡但离场动画还没播完」的窗口期 ——
    /// 这时候幸存者绝不能提前补位，否则碎裂动画还没演完，人已经被别人顶走了。
    /// </summary>
    public void PinAtOldSlot(long entityId, Vector2 oldTopLeft, Vector2 newTopLeft)
    {
        var t = TrackOf(entityId);
        if (t == null) return;
        t.Motion.LayoutOffset = oldTopLeft - newTopLeft;
    }

    /// <summary>
    /// 离场动画播完后调用：把钉住的随从用 0.22s OutCubic 滑到新槽位。
    ///
    /// 曲线与时长对应 CSS 的「transition: transform .22s cubic-bezier(.22,1,.36,1)」，
    /// 起步快、收尾缓，观感是「被吸过去」而不是机械匀速。
    /// </summary>
    public void PlayLayoutReflow(IEnumerable<long> entityIds, float duration = 0.22f)
    {
        if (entityIds == null) return;
        foreach (var id in entityIds)
        {
            if (!_tracks.TryGetValue(id, out var t)) continue;
            Vector2 start = t.Motion.LayoutOffset;
            if (start.LengthSquared() < 1f)
            {
                t.Motion.LayoutOffset = Vector2.Zero;
                continue;
            }

            Play(id, new Anim(duration, Ease.OutCubic, v =>
            {
                t.Motion.LayoutOffset = start * (1f - v);
            }, () =>
            {
                t.Motion.LayoutOffset = Vector2.Zero;
            }));
        }
    }

    public void Play(long entityId, Anim anim)
    {
        if (anim == null) return;
        var t = TrackOf(entityId);
        t.Running.Add(anim);
    }

    public void Step(float dt)
    {
        // 无条件动画：选目标浮动独立于事件队列，每帧实时更新
        if (_aimingEntity >= 0)
        {
            var aiming = TrackOf(_aimingEntity);
            _aimPhase += dt;
            float bob = Mathf.Sin(_aimPhase * 6.5f) * 5.5f;
            aiming.Motion.Offset = new Vector2(0f, -18f + bob);
            aiming.Motion.Scale = 1.06f;
            aiming.Motion.Alpha = 1f;
        }

        if (_tracks.Count == 0) return;

        List<long> finished = null;

        foreach (var kv in _tracks)
        {
            var t = kv.Value;
            if (t.Running.Count > 0)
            {
                for (int i = t.Running.Count - 1; i >= 0; i--)
                {
                    if (!t.Running[i].Step(dt))
                        t.Running.RemoveAt(i);
                }
            }

            if (t.Running.Count == 0 && t.Motion.IsIdle && kv.Key != _aimingEntity)
            {
                (finished ??= new List<long>()).Add(kv.Key);
            }
        }

        if (finished != null)
            foreach (var id in finished) _tracks.Remove(id);
    }

    // ---------------------------------------------------------------- 预设动画

    /// <summary>
    /// 预备入场：随从在后端已入场、但入场动画还在队列中排队时，
    /// 先设为隐身沉底，防止「第 1 张还在入场、第 3 张已经站在场上」。
    /// </summary>
    public void PrepareEnter(long entityId, float height = 124f)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();
        t.Motion = new UnitMotion
        {
            Offset = new Vector2(0f, height),
            Scale = 1f,
            FlipX = 0f,
            Alpha = 0f,
        };
    }

    /// <summary>入场：从座位下方钻出。</summary>
    /// <summary>身材增益/减益弹跳：buff 时向上膨胀发光，否则下压收缩。</summary>
    public void PlayStatPulse(long entityId, bool isBuff)
    {
        var t = TrackOf(entityId);
        if (t == null) return;
        float peak = isBuff ? 0.24f : -0.16f;
        float lift = isBuff ? -12f : 8f;
        Play(entityId, new Anim(0.28f, Ease.OutBack, v =>
        {
            float wave = Mathf.Sin(v * Mathf.Pi);
            t.Motion.Scale = 1f + peak * wave;
            t.Motion.Offset = new Vector2(0f, lift * wave);
        }, () => { ResetKeepLayout(t); }));
    }

    public void PlayEnter(long entityId, float height = 124f)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.28f, Ease.OutBack, v =>
        {
            if (_aimingEntity == entityId) return;
            t.Motion.Offset = new Vector2(0f, height * (1f - v));
            t.Motion.Alpha = Mathf.Clamp(v * 2.0f, 0f, 1f);
            t.Motion.Scale = 1f;
        }, () =>
        {
            if (_aimingEntity != entityId) ResetKeepLayout(t);
        }));
    }

    /// <summary>普通破坏/消失离场：原地缩小、上浮并淡出。</summary>
    public void PlayLeave(long entityId, Action onDone = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.26f, Ease.InCubic, v =>
        {
            t.Motion.Scale = Mathf.Max(0.15f, 1f - v * 0.75f);
            t.Motion.Alpha = Mathf.Clamp(1f - v, 0f, 1f);
            t.Motion.Offset = new Vector2(0f, -v * 20f);
        }, () =>
        {
            ResetKeepLayout(t);
            onDone?.Invoke();
        }));
    }

    /// <summary>进化 / 超进化（阻塞性）：升空 → 顶点翻面 → 砸落。</summary>
    public void PlayEvolve(long entityId, bool super)
    {
        var t = TrackOf(entityId);

        // 191 号文档 问题 3：进化动画会把「槽位重排」的平移动画杀掉，
        // 收尾的 ResetKeepLayout 又把残余 LayoutOffset 永久锁死 ——
        // 于是进化时若能力召唤了新随从（A 的槽位左移），A 会永远卡在旧位置。
        //
        // 修法：Clear 之前先把未走完的偏移抓下来，在升空阶段顺滑归零，
        // 砸落时自然落进新分配的槽位。
        Vector2 startLayoutOffset = t.Motion.LayoutOffset;
        t.Running.Clear();

        float rise = super ? 92f : 74f;

        Play(entityId, new Anim(0.58f, Ease.Linear, v =>
        {
            t.Motion.Alpha = 1f;

            // 前 42%（升空阶段）把旧槽位偏移插值归零，
            // 砸落结束时 LayoutOffset 已经是 0，不会被 ResetKeepLayout 锁死。
            float layoutT = Mathf.Clamp(v / 0.42f, 0f, 1f);
            t.Motion.LayoutOffset = startLayoutOffset * (1f - Ease.OutCubic(layoutT));

            if (v < 0.42f)
            {
                float k = Ease.OutCubic(v / 0.42f);
                t.Motion.Offset = new Vector2(0f, -rise * k);
                t.Motion.FlipX = Ease.InOutCubic(k) * 0.5f;
                t.Motion.Scale = 1f + 0.12f * k;
            }
            else if (v < 0.58f)
            {
                float k = (v - 0.42f) / 0.16f;
                t.Motion.Offset = new Vector2(0f, -rise);
                t.Motion.FlipX = 0.5f + k * 0.5f;
                t.Motion.Scale = 1.12f;
            }
            else
            {
                float k = Ease.InCubic((v - 0.58f) / 0.42f);
                t.Motion.Offset = new Vector2(0f, -rise * (1f - k));
                t.Motion.FlipX = 0f;
                t.Motion.Scale = 1f + 0.12f * Mathf.Sin(k * Mathf.Pi);
            }
        }, () => { ResetKeepLayout(t); }));
    }

    /// <summary>被超进化随从击杀：高速弹射砸向其所属主战者，撞击后碎裂。</summary>
    public void PlayKnockToLeader(long entityId, Vector2 targetOffset, Action onDone = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();

        Play(entityId, new Anim(0.36f, Ease.InQuad, v =>
        {
            t.Motion.Offset = targetOffset * v;
            t.Motion.Scale = 1.05f - v * 0.25f;
            t.Motion.FlipX = v * 0.25f;
            t.Motion.Alpha = v < 0.85f ? 1f : Mathf.Clamp((1f - v) / 0.15f, 0f, 1f);
        }, () =>
        {
            ResetKeepLayout(t);
            onDone?.Invoke();
        }));
    }

    /// <summary>攻击冲锋：撞向目标再弹回原位。</summary>
    public void PlayLunge(long entityId, Vector2 targetOffset, Action onHit = null)
    {
        var t = TrackOf(entityId);
        t.Running.Clear();
        bool hitFired = false;

        Play(entityId, new Anim(0.28f, Ease.Linear, v =>
        {
            t.Motion.Alpha = 1f;
            if (v < 0.45f)
            {
                t.Motion.Offset = targetOffset * Ease.InQuad(v / 0.45f) * 0.82f;
            }
            else
            {
                if (!hitFired)
                {
                    hitFired = true;
                    onHit?.Invoke();
                }
                float k = (v - 0.45f) / 0.55f;
                t.Motion.Offset = targetOffset * 0.82f * (1f - Ease.OutQuad(k));
            }
        }, () =>
        {
            if (!hitFired) onHit?.Invoke();
            ResetKeepLayout(t);
        }));
    }

    /// <summary>受击抖动。</summary>
    public void PlayHitShake(long entityId)
    {
        var t = TrackOf(entityId);
        Play(entityId, new Anim(0.18f, Ease.Linear, v =>
        {
            float amp = 7f * (1f - v);
            t.Motion.Offset = new Vector2(Mathf.Sin(v * 48f) * amp, 0f);
        }, () =>
        {
            if (_aimingEntity != entityId) ResetKeepLayout(t);
        }));
    }
}
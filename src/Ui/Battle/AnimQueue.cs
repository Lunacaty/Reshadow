using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 单段顺序演出序列（内部步骤严格串行）。
///
/// IsBlocking 区分两类演出：
///   false = 非阻塞事件动画（入场、攻击、掉血、抽牌、离场）
///           播放期间玩家仍可出牌、拖拽、发起攻击
///   true  = 阻塞性动画（进化 / 超进化）
///           一旦进入队列就锁定玩家的状态操作，直到本段播完
/// </summary>
public sealed class Seq
{
    private readonly List<Anim> _steps = new();

    public bool IsBlocking { get; private set; }
    public bool IsEmpty => _steps.Count == 0;
    public int Count => _steps.Count;

    public Seq MarkBlocking(bool blocking = true)
    {
        IsBlocking = blocking;
        return this;
    }

    public Seq Then(Anim a)
    {
        if (a != null) _steps.Add(a);
        return this;
    }

    /// <summary>
    /// 追加一段纯等待。
    ///
    /// 必须作为**步骤**排进 _steps，不能累加到单独的 _gap 字段 ——
    /// 早先那样写会让等待跑在它前面的 Do() 之前，
    /// 于是「先干等一段，最后一帧才触发动作」。
    /// </summary>
    public Seq Wait(float seconds)
    {
        if (seconds > 0f)
            _steps.Add(new Anim(seconds, Ease.Linear, null));
        return this;
    }

    /// <summary>追加一个瞬时动作（Duration = 0，不占帧、不吞时间）。</summary>
    public Seq Do(Action act)
    {
        bool fired = false;
        _steps.Add(new Anim(0f, Ease.Linear, _ =>
        {
            if (fired) return;
            fired = true;
            act?.Invoke();
        }));
        return this;
    }

    /// <summary>
    /// 推进本序列。返回 true 表示尚未播完；
    /// leftover 输出播完后本帧剩余的时间，供 AnimQueue 无缝传给下一段。
    /// </summary>
    public bool Step(float dt, out float leftover)
    {
        float remain = Mathf.Max(0f, dt);

        while (_steps.Count > 0)
        {
            var a = _steps[0];
            if (a.Finished)
            {
                _steps.RemoveAt(0);
                continue;
            }

            if (a.Duration <= 0f)
            {
                a.Step(0f);
                _steps.RemoveAt(0);
                continue;
            }

            float left = Mathf.Max(0f, a.Duration - a.Elapsed);
            if (remain < left - 1e-5f)
            {
                a.Step(remain);
                leftover = 0f;
                return true;
            }

            a.Step(left);
            remain = Mathf.Max(0f, remain - left);
            _steps.RemoveAt(0);
        }

        leftover = remain;
        return false;
    }

    public void Clear() => _steps.Clear();
}

/// <summary>
/// 表演队列：先进先出。
///
/// Busy       = 有动画在播或排队 → 用于延迟「Choose 弹窗」与「胜负结算窗」
/// HasBlocking = 队列中存在阻塞性动画（进化/超进化）→ 用于锁玩家操作
///</summary>
public sealed class AnimQueue
{
    private readonly Queue<Seq> _pending = new();
    private Seq _current;

    public int PendingCount => _pending.Count + (_current != null ? 1 : 0);
    public bool IsPlaying => _current != null;
    public bool Busy => _current != null || _pending.Count > 0;

    public bool HasBlocking
        => (_current != null && _current.IsBlocking) || _pending.Any(s => s.IsBlocking);

    public void Enqueue(Seq seq)
    {
        if (seq == null || seq.IsEmpty) return;
        _pending.Enqueue(seq);
    }

    /// <summary>
    /// 推进队列。同一帧内的剩余时间无缝结转给下一段 Seq，绝不吞帧。
    ///
    /// 早先的做法是每切换一段就调用一次 Step(0)，本帧剩余的时间直接丢弃 ——
    /// 队列里步骤一多，累积丢帧相当可观。
    /// </summary>
    public bool Step(float dt)
    {
        float remain = Mathf.Max(0f, dt);
        bool anyStepped = false;

        while (true)
        {
            if (_current == null)
            {
                if (_pending.Count == 0) return anyStepped;
                _current = _pending.Dequeue();
            }

            anyStepped = true;
            bool alive = _current.Step(remain, out float leftover);
            if (alive) return true;

            _current = null;
            remain = leftover;
            if (_pending.Count == 0) return anyStepped;
        }
    }

    public void Clear()
    {
        _pending.Clear();
        _current = null;
    }
}
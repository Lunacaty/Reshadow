using System;
using System.Collections.Generic;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 按时间播放的表现层。
///
/// 与「布局重建」分开：Rebuild 只负责把控件摆到当前状态的位置，
/// 真正「发生了什么」的演出由这里排队播放。
/// </summary>
public sealed class Seq
{
    private readonly List<Anim> _steps = new();
    private float _gap;

    public bool IsEmpty => _steps.Count == 0 && _gap <= 0f;
    public int Count => _steps.Count;

    /// <summary>追加一段动画。</summary>
    public Seq Then(Anim a)
    {
        if (a != null) _steps.Add(a);
        return this;
    }

    /// <summary>追加一段纯等待（让两段演出之间留呼吸）。</summary>
    public Seq Wait(float seconds)
    {
        _gap += Mathf.Max(0f, seconds);
        return this;
    }

    /// <summary>追加一个瞬时动作（不占时间）。</summary>
    public Seq Do(Action act)
    {
        // 瞬时动作：Duration 设为 0，Step 一次即完成。
        // 用 0.001 的话，第一帧只消耗 0.001 的一部分，
        // 下一帧还会再 Step 一次 —— OnUpdate 触发两次，动作重复执行。
        bool fired = false;
        _steps.Add(new Anim(0f, Ease.Linear, _ =>
        {
            if (fired) return;
            fired = true;
            act?.Invoke();
        }));
        return this;
    }

    /// <summary>推进。返回 true 表示整段还在播。</summary>
    public bool Step(float dt)
    {
        if (_gap > 0f)
        {
            _gap -= dt;
            if (_gap > 0f) return true;
            // 等待用掉了 dt 的一部分，剩下的顺延给后面的动画。
            // 注意符号：_gap 此时 <= 0，多余时间 = -_gap。
            dt = -_gap;
            _gap = 0f;
        }

        // 本帧剩余可用时间。每完成一段就扣掉它消耗的部分。
        float remain = dt;

        while (_steps.Count > 0)
        {
            var a = _steps[0];

            // 已经播完的直接出队。不能再 Step ——
            // Anim.Step 在 Elapsed 达到 Duration 时会再调一次 OnUpdate，
            // 而瞬时动作的 OnUpdate 就是 Do 的内容，会重复触发。
            if (a.Finished)
            {
                _steps.RemoveAt(0);
                continue;
            }

            float left = Mathf.Max(0f, a.Duration - a.Elapsed);

            // 关键：剩余时间不够播完本段，就只消耗「本帧剩下的全部时间」，
            // 然后返回、等下帧继续。
            //
            // 早先写的是 consume = Min(remain, left) 且 remain 不足时直接
            // return true，看起来没问题；但当 remain 恰好为 0 时，
            // consume = 0、Anim.Step(0) 返回「未完成」、remain 一分不减，
            // 循环虽然退出了，下一帧又从头来 —— 队列表现为永远 Busy，
            // 后面的动画一段都播不出来。
            if (remain < left)
            {
                // 本帧时间不足以播完本段：消耗掉剩余时间，本帧结束。
                // 必须真的把 remain 用掉，否则 Elapsed 不增加，
                // 下一帧进来 remain 又是同样的值 —— 队列永久卡在这一段。
                a.Step(remain);
                return true;
            }

            bool alive = a.Step(left);
            remain -= left;

            if (alive) return true;

            _steps.RemoveAt(0);

            // 剩下的时间继续给下一段；如果没有了，就本帧到此为止，
            // 但队列还有内容，仍要报告 Busy。
            if (remain <= 0.0001f)
                return _steps.Count > 0 || _gap > 0f;
        }

        return false;
    }

    public void Clear()
    {
        _steps.Clear();
        _gap = 0f;
    }
}

/// <summary>
/// 表演队列：先进先出。
///
/// 玩家可以在队列播放期间自由操作（拖手牌、控制随从、看卡面），
/// 但**与真实状态有关的演出**（攻击结算、掉血、抽牌、进化、终局）
/// 必须一段播完再播下一段 —— 否则多个结果会同时糊在一起，
/// 玩家根本看不出先后因果。
/// </summary>
public sealed class AnimQueue
{
    private readonly Queue<Seq> _pending = new();
    private Seq _current;

    /// <summary>队列里还有多少段没播（含正在播的）。</summary>
    public int PendingCount => _pending.Count + (_current != null ? 1 : 0);

    public bool IsPlaying => _current != null;

    /// <summary>是否应该拦住「下一段演出」—— 正在播或有积压。</summary>
    public bool Busy => _current != null || _pending.Count > 0;

    public void Enqueue(Seq seq)
    {
        if (seq == null || seq.IsEmpty) return;
        _pending.Enqueue(seq);
    }

    /// <summary>推进。返回 true 表示这一帧有演出在动（调用方据此重绘）。</summary>
    public bool Step(float dt)
    {
        if (_current == null)
        {
            if (_pending.Count == 0) return false;
            _current = _pending.Dequeue();
        }

        bool alive = _current.Step(dt);
        if (!alive)
        {
            _current = null;
            // 同一帧顺势接下一段，避免中间空一帧的顿挫
            if (_pending.Count > 0)
            {
                _current = _pending.Dequeue();
                _current.Step(0f);
                return true;
            }
        }
        return _current != null;
    }

    public void Clear()
    {
        _pending.Clear();
        _current = null;
    }
}
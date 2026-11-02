using System;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 可逐帧推进的动画。
///
/// Duration == 0 表示**瞬时动作**：同一帧内立即完成、不消耗时间。
/// 早先构造函数把 duration 钳到 0.001，于是每个 Seq.Do() 都变成一个
/// 「需要两帧才能完成」的小动画，跨段切换时白白吞掉一整帧 ——
/// 这正是队列推进速度只有实际 1/5 的根源之一。
/// </summary>
public sealed class Anim
{
    public float Duration { get; }
    public float Elapsed { get; private set; }
    public Func<float, float> Curve { get; }
    public Action<float> OnUpdate { get; }
    public Action OnDone { get; private set; }

    private bool _instantDone;

    public bool Finished => Duration <= 0f ? _instantDone : Elapsed >= Duration - 1e-5f;
    public float T => Duration <= 0f ? 1f : Mathf.Clamp(Elapsed / Duration, 0f, 1f);
    public float Value => Curve(T);

    public Anim(float duration, Func<float, float> curve, Action<float> onUpdate, Action onDone = null)
    {
        Duration = Mathf.Max(0f, duration);
        Curve = curve ?? Ease.Linear;
        OnUpdate = onUpdate;
        OnDone = onDone;
    }

    public bool Step(float dt)
    {
        if (Finished) return false;

        if (Duration <= 0f)
        {
            _instantDone = true;
            OnUpdate?.Invoke(1f);
            var done0 = OnDone;
            OnDone = null;
            done0?.Invoke();
            return false;
        }

        Elapsed += dt;
        if (Elapsed >= Duration - 1e-5f)
        {
            Elapsed = Duration;
            OnUpdate?.Invoke(1f);
            var done = OnDone;
            OnDone = null;
            done?.Invoke();
            return false;
        }

        OnUpdate?.Invoke(Value);
        return true;
    }

    public void Cancel() { OnDone = null; }
}
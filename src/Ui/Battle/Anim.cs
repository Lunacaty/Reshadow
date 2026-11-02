using System;
using Godot;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 可逐帧推进的动画。
///
/// 不用 Godot Tween：Tween 绑定节点生命周期，而战场每次 Rebuild
/// 都会整体销毁重建控件，Tween 会在中途因为目标被 QueueFree 而静默失效。
/// 这里用纯数据 + 每帧推进，由 BattleScreenRoot 统一驱动。
/// </summary>
public sealed class Anim
{
    public float Duration { get; }
    public float Elapsed { get; private set; }
    public Func<float, float> Curve { get; }
    public Action<float> OnUpdate { get; }
    public Action OnDone { get; private set; }

    /// <summary>浮点误差容忍：Elapsed 与 Duration 相差小于 1e-5 就算播完，
    /// 否则 0.0001f 这类极短动画可能永远差最后一位、队列卡死。</summary>
    public bool Finished => Elapsed >= Duration - 1e-5f;
    public float T => Duration <= 0f ? 1f : Mathf.Clamp(Elapsed / Duration, 0f, 1f);
    public float Value => Curve(T);
    public string Debug => "dur=" + Duration.ToString("F4")
        + " el=" + Elapsed.ToString("F4") + " done=" + Finished;

    public Anim(float duration, Func<float, float> curve, Action<float> onUpdate, Action onDone = null)
    {
        Duration = Mathf.Max(0.001f, duration);
        Curve = curve ?? Ease.Linear;
        OnUpdate = onUpdate;
        OnDone = onDone;
    }

    public bool Step(float dt)
    {
        if (Finished) return false;
        Elapsed += dt;
        if (Elapsed >= Duration)
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
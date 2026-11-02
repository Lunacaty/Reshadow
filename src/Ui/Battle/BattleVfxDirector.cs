using System;
using System.Collections.Generic;
using Godot;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 战斗视觉特效导演（全程序化几何光效，零外部贴图依赖）。
///
/// 挂载在 BattleScreenRoot 最顶层（ZIndex=80），MouseFilter=Ignore。
/// 它不改内核一行代码：动作产生的 EventLog 由根节点喂进来，
/// 这里只负责把「发生了什么」翻译成「看起来怎样」。
///
/// 九类演出：
///   1 交战冲撞  2 伤害飘字  3 屏障抵消  4 治疗圣光  5 破坏碎裂
///   6 消失漩涡  7 入场法阵  8 变身棱镜  9 进化光柱
/// </summary>
public partial class BattleVfxDirector : Control
{
    private sealed class ActiveFx
    {
        public string Kind;
        public Vector2 Pos;
        public string Text;
        public Color PrimaryColor;
        public float Elapsed;
        public float Duration;
    }

    private readonly List<ActiveFx> _effects = new();

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 80;
    }

    public int ActiveEffectCount => _effects.Count;

    public override void _Process(double delta)
    {
        if (_effects.Count == 0) return;
        float dt = (float)delta;
        for (int i = _effects.Count - 1; i >= 0; i--)
        {
            _effects[i].Elapsed += dt;
            if (_effects[i].Elapsed >= _effects[i].Duration) _effects.RemoveAt(i);
        }
        QueueRedraw();
    }

    private void Push(string kind, Vector2 pos, Color col, float dur, string text = null)
    {
        _effects.Add(new ActiveFx
        {
            Kind = kind, Pos = pos, PrimaryColor = col, Duration = dur, Text = text,
        });
        QueueRedraw();
    }

    // ---------------------------------------------------------------- 触发接口

    public void PlayAttackLunge(Control attackerWidget, Vector2 attackerCenter, Vector2 defenderCenter)
    {
        // 棋子本体的位移**不在这里做**。
        //
        // 早先用 Godot Tween 直接推 attackerWidget.Position，但攻击结算
        // 紧接着就 Rebuild()，那个控件当场被 QueueFree ——
        // Tween 绑在失效节点上，一帧都不会播。
        // 位移改由 BattleScreenRoot 通过 BoardAnimDirector 驱动
        // （按 EntityId 存，重建后能接上）。
        //
        // 这里只负责「命中闪光」这一层纯特效，它与控件生命周期无关。
        Push("slash", defenderCenter, new Color("FDE047"), 0.35f);
    }

    public void PlayDamage(Vector2 targetCenter, int amount, Control targetWidget = null)
    {
        if (targetWidget != null && IsInstanceValid(targetWidget))
        {
            var tw = CreateTween();
            tw.TweenProperty(targetWidget, "modulate", new Color(2.2f, 0.45f, 0.45f), 0.06f);
            tw.TweenProperty(targetWidget, "modulate", Colors.White, 0.18f);
        }
        Push(amount > 0 ? "damage" : "barrier", targetCenter,
             amount > 0 ? new Color("EF4444") : new Color("38BDF8"), 0.75f,
             amount > 0 ? $"-{amount}" : "✦ 屏障 0");
    }

    public void PlayHeal(Vector2 targetCenter, int amount)
        => Push("heal", targetCenter, new Color("10B981"), 0.75f, $"+{amount}");

    public void PlayDestroy(Vector2 unitCenter, string cardName = "")
        => Push("destroy", unitCenter, new Color("DC2626"), 0.48f,
                string.IsNullOrEmpty(cardName) ? "破坏" : $"💀 {cardName}");

    public void PlayBanish(Vector2 unitCenter, string cardName = "")
        => Push("banish", unitCenter, new Color("38BDF8"), 0.48f, "🌀 消失");

    public void PlayEnterField(Vector2 unitCenter, Control unitWidget = null)
    {
        if (unitWidget != null && IsInstanceValid(unitWidget))
        {
            unitWidget.PivotOffset = unitWidget.Size * 0.5f;
            unitWidget.Scale = new Vector2(1.30f, 1.30f);
            var tw = CreateTween();
            tw.TweenProperty(unitWidget, "scale", Vector2.One, 0.22f)
              .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
        }
        Push("summon", unitCenter, new Color("FBBF24"), 0.40f);
    }

    public void PlayEvolve(Vector2 unitCenter, bool isSuper)
        => Push("evolve", unitCenter,
                isSuper ? new Color("C084FC") : new Color("F59E0B"), 0.65f,
                isSuper ? "★ 超进化!" : "✦ 进化!");

    public void PlayTransform(Vector2 unitCenter)
        => Push("transform", unitCenter, new Color("22D3EE"), 0.48f, "❖ 变身");

    // ---------------------------------------------------------------- 绘制

    public override void _Draw()
    {
        if (_effects.Count == 0) return;
        var font = ThemeDB.FallbackFont;

        foreach (var fx in _effects)
        {
            float t = Mathf.Clamp(fx.Elapsed / fx.Duration, 0f, 1f);
            float alpha = 1f - t;

            switch (fx.Kind)
            {
                case "slash":
                {
                    float r = 18f + t * 48f;
                    DrawArc(fx.Pos, r, 0, Mathf.Tau, 32,
                            new Color(1f, 0.9f, 0.4f, alpha), 3.5f);
                    Vector2 d1 = new Vector2(1, -1).Normalized() * (36f * (0.4f + t * 0.8f));
                    Vector2 d2 = new Vector2(1, 1).Normalized() * (32f * (0.4f + t * 0.8f));
                    DrawLine(fx.Pos - d1, fx.Pos + d1, new Color(1f, 1f, 1f, alpha), 4f);
                    DrawLine(fx.Pos - d2, fx.Pos + d2, new Color(1f, 0.8f, 0.2f, alpha), 3f);
                    break;
                }
                case "damage":
                case "barrier":
                case "heal":
                {
                    // 只画扩散圆环 —— 数字统一交给 DamageFloat。
                    // 早先这里也 DrawString 了一份数字，于是同一处伤害
                    // 会叠两个数字（一个跟着特效走、一个独立上飘），
                    // 看起来就是「糊成一团」。
                    float r = 18f + t * 44f;
                    DrawCircle(fx.Pos, r * 0.55f, fx.PrimaryColor with { A = alpha * 0.28f });
                    DrawArc(fx.Pos, r, 0, Mathf.Tau, 32,
                            fx.PrimaryColor with { A = alpha * 0.90f }, 3.5f);

                    if (fx.Kind == "barrier" && !string.IsNullOrEmpty(fx.Text))
                    {
                        Vector2 p = fx.Pos + new Vector2(-32f, -12f - t * 32f);
                        DrawString(font, p + new Vector2(2f, 2f), fx.Text,
                                   HorizontalAlignment.Left, -1, 22, new Color(0, 0, 0, alpha));
                        DrawString(font, p, fx.Text, HorizontalAlignment.Left, -1, 22,
                                   fx.PrimaryColor.Lightened(0.25f) with { A = alpha });
                    }
                    break;
                }
                case "destroy":
                {
                    float r = 14f + t * 52f;
                    DrawCircle(fx.Pos, r * 0.65f, new Color(0.55f, 0.05f, 0.08f, alpha * 0.45f));
                    DrawArc(fx.Pos, r, 0, Mathf.Tau, 28, new Color(0.95f, 0.2f, 0.2f, alpha), 3f);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = i * Mathf.Tau / 6f + 0.3f;
                        Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
                        DrawLine(fx.Pos + dir * (t * 12f), fx.Pos + dir * r,
                                 new Color(1f, 0.4f, 0.4f, alpha), 2.2f);
                    }
                    break;
                }
                case "banish":
                {
                    float r = Mathf.Max(2f, 52f * (1f - t));
                    DrawArc(fx.Pos, r, t * 6f, t * 6f + Mathf.Pi * 1.6f, 28,
                            new Color(0.22f, 0.88f, 1f, alpha), 3.5f);
                    DrawArc(fx.Pos, r * 0.6f, -t * 8f, -t * 8f + Mathf.Pi * 1.5f, 24,
                            new Color(0.75f, 0.52f, 0.98f, alpha), 2.5f);
                    break;
                }
                case "summon":
                case "transform":
                {
                    float r = 20f + t * 44f;
                    DrawArc(fx.Pos, r, 0, Mathf.Tau, 36,
                            fx.PrimaryColor with { A = alpha }, 2.5f);
                    DrawArc(fx.Pos, r * 0.7f, 0, Mathf.Tau, 24,
                            Colors.White with { A = alpha * 0.7f }, 1.5f);
                    break;
                }
                case "evolve":
                {
                    float pillarW = 54f * (1f - t * 0.6f);
                    DrawRect(new Rect2(fx.Pos.X - pillarW * 0.5f, fx.Pos.Y - 120f,
                                       pillarW, 160f),
                             fx.PrimaryColor with { A = alpha * 0.38f });
                    DrawArc(fx.Pos, 22f + t * 64f, 0, Mathf.Tau, 40,
                            fx.PrimaryColor with { A = alpha }, 3.5f);
                    DrawArc(fx.Pos, 12f + t * 42f, 0, Mathf.Tau, 32,
                            new Color(1f, 0.96f, 0.75f, alpha), 2.5f);
                    DrawString(font, fx.Pos + new Vector2(-36f, -32f - t * 28f), fx.Text,
                               HorizontalAlignment.Left, -1, 20,
                               new Color(1f, 0.96f, 0.7f, alpha));
                    break;
                }
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 卡牌虚拟机 —— 帧栈 + 挂起 + 原子性
//
// 主循环 StepUntilBlocked() 的四个退出点：
//   1. 帧栈空 && 触发队列空  → Completed
//   2. 需要玩家输入          → WaitingForInput（此时 Pending 非空）
//   3. 游戏结束              → GameOver
//   4. 步数超限              → Faulted（防玩家 DIY 出无限循环）
//
// 关键不变量：Pending 非空时，任何推进都不会发生。
// 这保证了效果结算的原子性 —— 不会出现「效果执行了一半」的状态。
// ============================================================================

public sealed partial class CardVm
{
    private readonly IVmWorld _world;
    private readonly VmEval _eval;
    private readonly List<VmFrame> _frames = new();
    private readonly Queue<VmEvent> _triggerQueue = new();

    private int _nextChoiceId = 1;
    private int _faultBudget = 5000;

    public CardVm(IVmWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _eval = new VmEval(world);
    }

    public VmStatus Status { get; private set; } = VmStatus.Idle;
    public PendingChoice Pending { get; private set; }
    public string FaultMessage { get; private set; }
    public int StepsExecuted { get; private set; }

    public IReadOnlyList<VmFrame> Frames => _frames;
    public int PendingTriggerCount => _triggerQueue.Count;

    // ---------- 压入能力 ----------

    /// <summary>把一个能力的 Body 压入帧栈，开始执行。</summary>
    public void PushAbility(AbilityDef ability, VmScope scope)
    {
        if (ability?.Body == null || ability.Body.Count == 0) return;
        _frames.Add(new VmFrame
        {
            Scope = scope ?? new VmScope { Owner = ZoneScope.Self },
            Code = ability.Body,
            Ability = ability,
        });
    }

    /// <summary>压入一段动作序列（用于替代同步递归）。</summary>
    public void PushBody(IReadOnlyList<EffectNode> body, VmScope scope)
    {
        if (body == null || body.Count == 0) return;
        _frames.Add(new VmFrame
        {
            Scope = scope ?? new VmScope { Owner = ZoneScope.Self },
            Code = body,
        });
    }

    /// <summary>把事件放进触发队列。</summary>
    public void EnqueueEvent(VmEvent ev)
    {
        if (ev != null) _triggerQueue.Enqueue(ev);
    }

    // ---------- 主循环 ----------

    /// <summary>
    /// 推进到「需要外部输入」或「完成」为止。
    /// 这是整个战斗引擎唯一的推进点 —— 没有别的地方会偷偷执行动作。
    /// </summary>
    public VmStatus StepUntilBlocked()
    {
        StepsExecuted = 0;

        try
        {
            while (true)
            {
                // 不变量：有挂起选择时绝不推进
                if (Pending != null) { Status = VmStatus.WaitingForInput; return Status; }

                // 帧栈空：把下一个事件转成帧
                if (_frames.Count == 0)
                {
                    if (_triggerQueue.Count > 0)
                    {
                        if (++StepsExecuted > _faultBudget) return Fault("触发链超过步数上限");
                        FlushNextTrigger();
                        continue;
                    }
                    Status = VmStatus.Idle;
                    return Status;
                }

                if (++StepsExecuted > _faultBudget) return Fault("执行步数超过上限");

                var frame = _frames[^1];

                // 当前帧执行完毕
                if (frame.IsDone)
                {
                    _frames.RemoveAt(_frames.Count - 1);
                    continue;
                }

                var node = frame.Code[frame.Ip];

                // 节点级前置条件
                if (node.If != null && !_eval.EvalBool(node.If, frame.Scope))
                {
                    frame.Ip++;
                    continue;
                }

                // 执行一个原子节点。返回 true 表示已挂起等输入。
                bool blocked = ExecNode(node, frame);
                if (blocked) { Status = VmStatus.WaitingForInput; return Status; }

                // 节点执行成功才推进 Ip（挂起时不推进，恢复后重跑本节点）
                frame.Ip++;
            }
        }
        catch (VmFaultException ex)
        {
            return Fault(ex.Message);
        }
    }

    private VmStatus Fault(string msg)
    {
        FaultMessage = msg;
        Status = VmStatus.Faulted;
        _frames.Clear();
        _triggerQueue.Clear();
        Pending = null;
        return Status;
    }

    // ---------- 挂起与恢复 ----------

    /// <summary>设置挂起状态。返回 true（表示已阻塞）。</summary>
    private bool Block(PendingChoice choice)
    {
        choice = new PendingChoice
        {
            ChoiceId = _nextChoiceId++,
            Player = choice.Player,
            Kind = choice.Kind,
            Candidates = choice.Candidates,
            MinSelect = choice.MinSelect,
            MaxSelect = choice.MaxSelect,
            Slot = choice.Slot,
            Prompt = choice.Prompt,
        };
        Pending = choice;
        return true;
    }

    /// <summary>
    /// 玩家做出选择，把结果写回槽位并清除挂起。
    /// 调用后需要再次 StepUntilBlocked()。
    /// </summary>
    public void SubmitChoice(int choiceId, IReadOnlyList<int> selectedEntityIds)
    {
        if (Pending == null) throw new VmFaultException("当前没有挂起的选择");
        if (Pending.ChoiceId != choiceId)
            throw new VmFaultException($"选择 id 不匹配：期望 {Pending.ChoiceId}，收到 {choiceId}");

        int count = selectedEntityIds?.Count ?? 0;
        if (count < Pending.MinSelect || count > Pending.MaxSelect)
            throw new VmFaultException($"选择数量非法：允许 {Pending.MinSelect}~{Pending.MaxSelect}，收到 {count}");

        foreach (var id in selectedEntityIds ?? Array.Empty<int>())
            if (!Pending.Candidates.Contains(id))
                throw new VmFaultException($"实体 {id} 不在候选列表中");

        var frame = _frames.Count > 0 ? _frames[^1] : null;
        if (frame == null) throw new VmFaultException("没有可恢复的帧");

        var refs = new List<EntityRef>();
        foreach (var id in selectedEntityIds ?? Array.Empty<int>())
        {
            var live = _world.EnumerateZone(ZoneRef.Board, ZoneScope.Any)
                .Concat(_world.EnumerateZone(ZoneRef.Hand, ZoneScope.Any))
                .Concat(_world.EnumerateZone(ZoneRef.Deck, ZoneScope.Any))
                .Concat(_world.EnumerateZone(ZoneRef.Cemetery, ZoneScope.Any))
                .FirstOrDefault(e => e.EntityId == id);
            if (live != null) refs.Add(EntityRef.From(live));
        }
        frame.Scope.BindRefs(Pending.Slot, refs);
        frame.Scope.Chosen = refs.Count > 0 ? refs[0] : null;

        // 选择完成 → 推进 Ip，让主循环执行下一条指令。
        // 若不推进，DoSelect 会再次挂起（它不知道自己已经被满足了）。
        frame.Ip++;

        Pending = null;
        Status = VmStatus.Running;
    }

    // ---------- 事件转帧 ----------

    /// <summary>从触发队列取一个事件，转成执行帧。</summary>
    private void FlushNextTrigger()
    {
        var ev = _triggerQueue.Dequeue();
        _world.EmitEvent(ev);
        // 真正的触发器匹配由 Battle 层完成；VM 只负责「有帧就执行」
    }

    public VmEval Eval => _eval;
}

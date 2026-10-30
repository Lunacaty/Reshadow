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
    /// <summary>新回合开始：清空每回合触发计数。</summary>
    public void ResetTriggerCounts() => _triggerCounts.Clear();

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

                // 状态结算：把 Life <= 0 的场上实体移入墓地并触发 OnDestroy。
                // 不循环到底是为了让每次只处理一层，避免事件风暴。
                if (_world.ProcessStateBasedActions() > 0)
                {
                    if (++StepsExecuted > _faultBudget) return Fault("状态结算次数超过上限");
                }
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

        // ---- 模式抉择：候选是模式下标，不是实体 ID ----
        if (Pending.Kind == ChoiceKind.SelectMode)
        {
            var branchNode = frame.Ip < frame.Code.Count ? frame.Code[frame.Ip] as Ast.BranchModeNode : null;
            frame.Ip++;
            var chosenModes = selectedEntityIds ?? Array.Empty<int>();
            Pending = null;
            Status = VmStatus.Running;

            if (branchNode != null)
            {
                // 帧栈后进先出，倒序压入才能正序执行
                for (int i = chosenModes.Count - 1; i >= 0; i--)
                {
                    int idx = chosenModes[i];
                    if (idx >= 0 && idx < branchNode.Options.Count)
                        PushBody(branchNode.Options[idx], frame.Scope.Derive());
                }
            }
            return;
        }

        var refs = new List<EntityRef>();
        foreach (var id in selectedEntityIds ?? Array.Empty<int>())
        {
            var live = _world.EnumerateZone(ZoneRef.Board, -1)
                .Concat(_world.EnumerateZone(ZoneRef.Hand, -1))
                .Concat(_world.EnumerateZone(ZoneRef.Deck, -1))
                .Concat(_world.EnumerateZone(ZoneRef.Cemetery, -1))
                .FirstOrDefault(e => e.EntityId == id);
            if (live != null) refs.Add(EntityRef.From(live));
        }
        frame.Scope.BindRefs(Pending.Slot, refs);
        frame.Scope.Chosen = refs.Count > 0 ? refs[0] : null;

        // 不推进 Ip：挂起时主循环没有前进，恢复后必须**重跑本节点**。
        // 节点靠「槽位已绑定」判断玩家已经选过，从而继续执行而不是再次挂起。
        // （DoSelect 同样依赖这一点：它检查槽位非空后直接放行）
        Pending = null;
        Status = VmStatus.Running;
    }

    // ---------- 事件转帧 ----------

    /// <summary>从触发队列取一个事件，转成执行帧。</summary>
    /// <summary>
    /// 从触发队列取一个事件，扫描所有在场能力的 TriggerSpec，匹配上的压入执行帧。
    ///
    /// 之前这里只是把事件重新 Emit 一次，什么也不匹配 ——
    /// 「每当 X 时，Y」类能力永远不会触发。
    /// </summary>
    private void FlushNextTrigger()
    {
        var ev = _triggerQueue.Dequeue();

        // 候选能力 = 印制在卡上的 + 运行时授予的
        var byEntity = new Dictionary<int, List<(RuntimeEntity e, Ast.AbilityDef ab)>>();

        // 必须扫描所有「能力可能生效」的区域：
        //   场上、EX 区、手牌（魔力增幅）、牌库（瞬念召唤）、纹章区、墓地（部分谢幕曲）
        // 只扫 Board + ExArea 会让 ActiveZone=Hand 的能力永远收不到事件。
        var candidates = _world.EnumerateZone(ZoneRef.Board, -1)
            .Concat(_world.EnumerateZone(ZoneRef.ExArea, -1))
            .Concat(_world.EnumerateZone(ZoneRef.Hand, -1))
            .Concat(_world.EnumerateZone(ZoneRef.Deck, -1))
            .Concat(_world.EnumerateZone(ZoneRef.CrestArea, -1))
            .ToList();

        foreach (var e in candidates)
        {
            if (e.Def == null) { }
            if (e.Def != null)
            {
                foreach (var ab in e.Def.Abilities)
                    AddCandidate(byEntity, e, ab);
            }

            // 运行时授予的能力（GrantAbility）不写在 CardDef 里，必须单独扫
            foreach (var (ge, gab) in _world.GrantedAbilities)
            {
                if (ge != null && ge.EntityId == e.EntityId) AddCandidate(byEntity, ge, gab);
            }
        }

        foreach (var kv in byEntity)
        {
            var e = kv.Value[0].e;
            foreach (var (_, ab) in kv.Value)
            {
                if (ab.Trigger == null) continue;
                if (ab.Trigger.Event != ev.Kind) continue;
                if (!MatchesTrigger(ab.Trigger, ev, e)) continue;
                if (_eval.EvalBool(ab.Condition, new VmScope { Controller = e.PlayerId }) == false) continue;

                var scope = new VmScope
                {
                    Owner = ZoneScope.Self,
                    Controller = e.PlayerId,
                    Source = EntityRef.From(e),
                    Trigger = ev.Subject,
                    Event = ev,
                };
                PushBody(ab.Body, scope);
            }
        }
    }

    private static void AddCandidate(
        Dictionary<int, List<(RuntimeEntity e, Ast.AbilityDef ab)>> map,
        RuntimeEntity e, Ast.AbilityDef ab)
    {
        if (ab == null) return;
        if (!map.TryGetValue(e.EntityId, out var list))
        {
            list = new List<(RuntimeEntity, Ast.AbilityDef)>();
            map[e.EntityId] = list;
        }
        list.Add((e, ab));
    }

    /// <summary>判断一次事件是否满足某个触发器的过滤条件。</summary>
    /// <summary>
    /// 判断一次事件是否满足某个触发器。
    ///
    /// 四个过滤维度都必须生效：
    ///   ActiveZone          —— 只有卡在监听区域时才响应（手牌里的卡不该响应场面事件）
    ///   DuringTurn          —— 只在指定回合归属时生效
    ///   SubjectFilter       —— 事件主体过滤
    ///   SecondaryFilter     —— 事件副体过滤（Follower Strike 要求攻击目标是随从）
    ///   MaxTriggersPerTurn  —— 每回合触发上限
    /// </summary>
    private bool MatchesTrigger(TriggerSpec trig, VmEvent ev, RuntimeEntity owner)
    {
        // 1. 区域：卡必须位于它声明的监听区域
        var need = trig.ActiveZone switch
        {
            ActiveZoneScope.Hand => ZoneRef.Hand,
            ActiveZoneScope.Deck => ZoneRef.Deck,
            ActiveZoneScope.ExArea => ZoneRef.ExArea,
            _ => ZoneRef.Board,
        };
        if (owner.Zone != need) return false;

        // 2. 回合归属
        if (trig.DuringTurn != TurnOwnerScope.Any)
        {
            bool isMyTurn = _currentTurnOwner == owner.PlayerId;
            if (trig.DuringTurn == TurnOwnerScope.Self && !isMyTurn) return false;
            if (trig.DuringTurn == TurnOwnerScope.Opponent && isMyTurn) return false;
        }

        // 3. 主体过滤：null 表示「本卡自己」
        // 没有主体的全局事件（TurnStart / TurnEnd）视为「本卡」，
        // 否则「At the start of your turn, ...」这类能力永远收不到。
        if (trig.SubjectFilter == null)
        {
            if (ev.Subject != null && ev.Subject.Value.EntityId != owner.EntityId) return false;
        }
        else
        {
            if (ev.Subject == null) return false;
            var scope0 = new VmScope { Owner = ZoneScope.Self, Controller = owner.PlayerId };
            var pool0 = _eval.ResolveRef(trig.SubjectFilter, scope0);
            if (!pool0.Any(r => r.EntityId == ev.Subject.Value.EntityId)) return false;
        }

        // 4. 副体过滤：Follower Strike 要求「攻击目标是随从」
        if (trig.SecondaryFilter != null)
        {
            if (ev.Object == null) return false;
            var scope1 = new VmScope { Owner = ZoneScope.Self, Controller = owner.PlayerId };
            var pool = _eval.ResolveRef(trig.SecondaryFilter, scope1);
            if (!pool.Any(r => r.EntityId == ev.Object.Value.EntityId)) return false;
        }

        // 5. 每回合触发上限
        if (trig.MaxTriggersPerTurn > 0)
        {
            int key = owner.EntityId;
            int used = _triggerCounts.GetValueOrDefault(key);
            if (used >= trig.MaxTriggersPerTurn) return false;
            _triggerCounts[key] = used + 1;
        }

        return true;
    }

    /// <summary>把主体过滤表达式套到事件主体上求值。</summary>


    /// <summary>用快照判断已离场实体是否满足过滤（谢幕曲读 LKI 时用）。</summary>

    private readonly Dictionary<int, int> _triggerCounts = new();

    /// <summary>当前回合归属的玩家编号，由 Battle 层每回合设置。</summary>
    public int CurrentTurnOwner
    {
        get => _currentTurnOwner;
        set => _currentTurnOwner = value;
    }

    private int _currentTurnOwner;

    public VmEval Eval => _eval;
}

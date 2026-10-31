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
    // 待机列（R-3.3）：同一时机触发的多个能力必须按固定优先级排序，
    // 而不是简单先进先出。否则「入场曲」和「场上出现时」会按无关顺序结算。
    // 已入列的条目即锁定，之后剥夺能力不影响它。
    private readonly List<VmEvent> _triggerQueue = new();

    private int _nextChoiceId = 1;
    private int _faultBudget = 5000;

    /// <summary>
    /// 具名牌组表。卡面 "Replace your deck with the Apocalypse Deck"
    /// 按名字在这里解析成整套卡；未注册的名字会在结算时抛错。
    /// </summary>
    public static DeckDefTable DeckDefs { get; set; } = DeckDefTable.CreateDefault();

    /// <summary>符号表。代价执行需要它取 pp / shadow 的真实 Id。</summary>
    public static GameSchema Schema { get; set; } = GameSchema.CreateDefault();

    public CardVm(IVmWorld world)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _eval = new VmEval(world);

        // 符号表必须与世界对齐：SymbolId 的 Id 由 GameSchema 分配，
        // 而 BattleState.ReadCounter 按整数 Id 索引字典。
        // 若虚拟机与对局各持一个 CreateDefault() 实例，运行时动态注册的符号
        // （mode_activated_mask / cards_returned_to_hand / fuse_kinds …）
        // 会在两个实例里拿到不同 Id —— 读计数器直接串槽。
        if (world is Battle.BattleState bs0 && bs0.Schema != null) Schema = bs0.Schema;

        // 世界发出的事件必须进得了触发队列，否则「每当抽牌时」这类能力
        // 只对显式 EnqueueEvent 的几处（入场曲 / 攻撃时 / 回合头）有效。
        // 这里注册的是**唯一的**转发通道：世界先记账再调它，
        // 而它只入队不推进 —— 结算时机仍由 StepUntilBlocked 独占。
        _world.TriggerSink = OnWorldEvent;
    }

    /// <summary>
    /// 世界事件的转发落点。
    ///
    /// 只入队，不推进，也不写 EventLog（世界已经写过了）。
    /// 转发只发生在世界的 EmitEvent 里，而 EnqueueEvent 的显式调用点
    /// （入场曲 / 攻撃时 / 回合头 / 进化）走的是另一条路，两者没有交集，
    /// 所以同一条事件不会被既转发又入队两次。
    ///
    /// 递归由 StepUntilBlocked 的步数上限兜住：事件触发出的能力再发事件时
    /// 仍然排进同一个队列，由同一层主循环取用，不会增加调用栈深度。
    /// </summary>
    private void OnWorldEvent(VmEvent ev) => EnqueueEvent(ev);

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
        if (ev == null) return;

        // 按优先级有序插入（稳定：同优先级保持入列顺序）
        int i = _triggerQueue.Count;
        while (i > 0 && _triggerQueue[i - 1].EffectivePriority > ev.EffectivePriority) i--;
        _triggerQueue.Insert(i, ev);
    }

    /// <summary>
    /// 终局判定：一方主战者生命归 0、牌组抽空判负、或持有胜利卡牌。
    /// 三条来源与 TurnRunner 的胜负判定保持一致。
    /// </summary>
    private static bool IsGameOver(Battle.BattleState bs)
    {
        for (int i = 0; i < 2; i++)
        {
            var p = bs.Players[i];
            if (p == null) continue;
            if (p.Leader != null && p.Leader.Life <= 0) return true;
            if (p.LostByDeckOut) return true;
            if (p.WonByVictoryCard) return true;
        }
        return false;
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

                // 终局铁律：主战者生命归 0 之后，任何后续结算都必须停止。
                // 少了这一步，主战者归 0 后仍会继续跑谢幕曲 ——
                // 实测「击杀后谢幕曲把主战者奶回 2 点」，对局永远结束不了。
                if (_world is Battle.BattleState bsNow && IsGameOver(bsNow))
                {
                    _frames.Clear();
                    _triggerQueue.Clear();
                    Pending = null;
                    Status = VmStatus.GameOver;
                    return Status;
                }

                // 帧栈空：把下一个事件转成帧
                if (_frames.Count == 0)
                {
                    if (_triggerQueue.Count > 0)
                    {
                        if (++StepsExecuted > _faultBudget) return Fault("触发链超过步数上限");
                        FlushNextTrigger();
                        continue;
                    }
                    // 帧栈与触发队列都空了：本次结算正常收尾。
                    // 用 Completed 而不是 Idle 区分「还没开始」与「刚跑完」。
                    // 第一次调用且什么都没执行时仍然算 Completed —— 语义是
                    // 「此刻没有待办」，调用方读结果即可。
                    Status = VmStatus.Completed;
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

    /// <summary>
    /// 状态已因熔断而不可信。
    ///
    /// 熔断发生在任意一个原子节点中途（超步数 / 未知循环 / 宏缺失）：
    /// 此前已经扣的血、抽的牌、加的计数**都已经落到 BattleState 上**，
    /// 无法撤销 —— 完整回滚需要对整个对局做深拷贝，代价远超收益。
    ///
    /// 所以这里不做假回滚，而是把这个事实显式暴露出去：
    /// 调用方看到 true 就必须重建 BattleState，不能继续用当前这局。
    /// 沉默地继续才是真正危险的（分数、胜负都建立在半截状态上）。
    /// </summary>
    public bool StateCorrupted { get; private set; }

    private VmStatus Fault(string msg)
    {
        FaultMessage = msg;
        Status = VmStatus.Faulted;
        StateCorrupted = true;
        _frames.Clear();
        _triggerQueue.Clear();
        Pending = null;
        return Status;
    }

    /// <summary>
    /// 丢弃尚未结算的触发队列。
    ///
    /// 供测试的布置阶段使用：SpawnCard 现在会真的发出 OnEnterField（召唤进场的
    /// 随从否则不计连携、不触发「进入战场时」），但布置用的 SpawnCard 不该在
    /// 测试正式开始前就把监听者的能力跑掉。布置完成后调用一次即可。
    /// </summary>
    public void DropPendingTriggers() => _triggerQueue.Clear();

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

            // 「Whenever you select Modes, ...」的触发点：玩家确认选择之后、
            // 选项执行之前。信仰靠它每次 +1。
            _world.EmitEvent(new VmEvent
            {
                Kind = CoreEvent.OnSelectMode,
                PlayerId = frame.Scope.ResolvePlayer(frame.Scope.Owner),
            });
            StepUntilBlocked();

            if (branchNode != null)
            {
                // 多选模式按**选项编号升序**执行，与玩家点选顺序无关（76-c 6.2-7）。
                // 先升序排一遍，再倒序压栈 —— 帧栈后进先出，倒序压入才正序执行。
                var ordered = chosenModes.Where(x => x >= 0 && x < branchNode.Options.Count)
                                         .OrderBy(x => x).ToList();
                for (int i = ordered.Count - 1; i >= 0; i--)
                    PushBody(branchNode.Options[ordered[i]], frame.Scope.Derive());
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

        // 记下「本条指令刚被选完」，重入时节点据此放行。
    // 不能只看槽位非空 —— 同一条能力里的下一条 Select 会误判成「已选过」。
        frame.ResolvedChoiceIp = frame.Ip;

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
        if (_triggerQueue.Count == 0) return;
        var ev = _triggerQueue[0];
        _triggerQueue.RemoveAt(0);

        // 候选能力 = 印制在卡上的 + 运行时授予的
        var byEntity = new Dictionary<int, List<(RuntimeEntity e, Ast.AbilityDef ab)>>();

        // 必须扫描所有「能力可能生效」的区域：
        //   场上、EX 区、手牌（魔力增幅）、牌库（瞬念召唤）、纹章区、墓地（部分谢幕曲）
        // 只扫 Board + ExArea 会让 ActiveZone=Hand 的能力永远收不到事件。
        // 同时诱发多个能力时的入列排序（R-4.1 第 2 条）：
        //   当前回合玩家的纹章（旧->新） -> 当前回合玩家的场上卡（旧->新）
        //   -> 对手的纹章（旧->新） -> 对手的场上卡（旧->新）
        // 另外手牌 / 牌组 / 额外区里的监听卡也要扫，放在最后。
        int curP = _currentTurnOwner;
        int othP = 1 - curP;
        var candidates = new System.Collections.Generic.List<RuntimeEntity>();
        void AddZone(ZoneRef z, int pid)
        {
            foreach (var x in _world.EnumerateZone(z, pid)) candidates.Add(x);
        }
        AddZone(ZoneRef.CrestArea, curP);
        AddZone(ZoneRef.Board, curP);
        AddZone(ZoneRef.CrestArea, othP);
        AddZone(ZoneRef.Board, othP);
        AddZone(ZoneRef.ExArea, curP);
        AddZone(ZoneRef.ExArea, othP);
        AddZone(ZoneRef.Hand, curP);
        AddZone(ZoneRef.Hand, othP);
        AddZone(ZoneRef.Deck, curP);
        AddZone(ZoneRef.Deck, othP);
        // 墓地也必须扫：DestroyEntity 的顺序是「移区 -> 发事件」，
        // 谢幕曲卡收到 OnDestroy 时**已经在墓地**，不在场上。
        // 不扫墓地，全卡池的谢幕曲都是白板。
        // 放宽只在 MatchesTrigger 里按「事件主体就是它自己」逐条判定，
        // 墓地里的卡不会因为这条扫描而响应别人的死亡。
        AddZone(ZoneRef.Cemetery, curP);
        AddZone(ZoneRef.Cemetery, othP);
        // 放逐区同样要扫：「When this card leaves the field, banish it.」的卡
        // 在事件送达时**已经在放逐区**，不扫这里它的离场时能力永远不会触发
        // （实测：被放逐的卡「离场时打 3 点」打 0 点）。
        AddZone(ZoneRef.Banished, curP);
        AddZone(ZoneRef.Banished, othP);

        // 到期的纹章停在 ZoneRef.None（离场但不进任何区域）。
        // 它们也在这条事件上监听自己的谢幕曲，必须进候选池；
        // MatchesTrigger 的 graveSelf 会按 EntityId + EntrySerial 精确限定范围。
        if (ev.Subject != null)
        {
            var subj = _world.GetLive(ev.Subject.Value.EntityId, ev.Subject.Value.EntrySerial);
            if (subj != null && subj.Zone == ZoneRef.None && subj.Def?.Kind == CardKind.Crest
                && !candidates.Contains(subj))
                candidates.Add(subj);
        }

        foreach (var e in candidates)
        {
            if (e.Def == null) { }
            // 「Remove Last Words」移除的是 OnDestroy 能力本身，
            // 而谢幕曲不是关键词，从关键词表删「last words」拦不住它 ——
            // 必须在候选阶段就整条跳过。
            bool lastWordsRemoved = e.LastWordsRemoved;
            if (e.Def != null)
            {
                foreach (var ab in e.Def.Abilities)
                {
                    if (lastWordsRemoved && ab?.Trigger != null
                        && ab.Trigger.Event == CoreEvent.OnDestroy) continue;
                    AddCandidate(byEntity, e, ab);
                }
            }

            // 运行时授予的能力（GrantAbility）不写在 CardDef 里，必须单独扫
            foreach (var (ge, gab) in _world.GrantedAbilities)
            {
                if (ge != null && ge.EntityId == e.EntityId) AddCandidate(byEntity, ge, gab);
            }

            // 挂在资源计数器上的能力（信仰等）。
            // 例："give your faith 'Whenever an allied follower evolves, deal 1 damage'"。
            // 它们不属于任何卡，之前没有扫描，所以信仰掉上的被动永远不触发。
            if (e.IsFaith && e.FaithOwner >= 0)
            {
                foreach (var (pid, ctr, cab) in _world.GrantedCounterAbilities)
                {
                    if (pid != e.FaithOwner) continue;
                    if (ctr.Name != "faith") continue;
                    AddCandidate(byEntity, e, cab);
                }
            }
        }

        // 待压栈的能力：必须先「按主客场+旧新顺序」正向判完条件与代价，
        // 再倒序压进 LIFO 帧栈（倒序压栈才能正序执行）。
        // 不能把外层循环直接倒过来 —— 那样排在后面的能力会先扣掉 necromancy 的墓场
        // 与 earth rite 的土之印，前面的能力反而没资源可用。
        var toPush = new List<(IReadOnlyList<Ast.EffectNode> body, VmScope scope)>();

        // 同一时机里同名卡只发动 1 张（76-c 6.2-8 瞬念召唤铁律）。
        // 牌组里的两张同名 Invoke 卡都会收到 TurnStart，
        // 各自的能力体都是「把自己移到战场」，不去重就会两张一起上场。
        // 按 (CardId, 事件种类) 去重，键在本次入列内有效。
        var firedOnce = new HashSet<(int CardId, Core.CoreEvent Ev)>();

        foreach (var kv in byEntity)
        {
            var e = kv.Value[0].e;
            // 沉默：被剥夺能力的实体不再响应任何印制能力或已授予能力。
            if (e.IsSilenced) continue;
            foreach (var (_, ab) in kv.Value)
            {
                if (ab.Trigger == null) continue;
                // 并列事件："Whenever you play or Fuse a Loot card" 两个都要能触发。
                if (ab.Trigger.Event != ev.Kind && ab.Trigger.SecondaryEvent != ev.Kind) continue;

                // Evolve: / Super-Evolve: 冒号头只认玩家手动花费 EP/SEP 的进化。
                // 能力效果自动进化（"Evolve this follower"）不触发这两类头，
                // 只会触发 When/Whenever ... evolves 这类事件从句。
                if (ab.Trigger.ManualEvolveOnly && !ev.IsManualEvolve) continue;

                if (!MatchesTrigger(ab.Trigger, ev, e)) continue;

                // 条件与效果体必须共用同一个完整作用域。
                // 条件里出现的「本卡」「被破坏的那个随从」全部落到 Source / Trigger 上：
                //   <ev>: HasState(Evolved, Self)      → Source 未绑定时 Resolve 恒空，条件恒假
                //   Skybound Art: ctr:skybound_gauge   → 计数器挂在手牌实体上，没有 Source 就读成 0
                // 之前条件用的是只有 Controller 的空 scope，这些条件在判定阶段全部失明。
                var scope = new VmScope
                {
                    Owner = ZoneScope.Self,
                    Controller = e.PlayerId,
                    Source = EntityRef.From(e),
                    Trigger = ev.Subject,
                    Event = ev,
                };

                // $opposing：交战/交互事件里的「对面那一方」。
                // 之前这个槽位只有常量定义与解析器，运行时从未绑定过，
                // 于是「Deal 1 damage to the opposing follower」恒解析成空池 ——
                // 全卡池的 Clash: 伤害都是 0。
                if (ev.Object != null)
                    scope.BindRefs(TargetSpec.SlotOpposing, new[] { ev.Object.Value });
                if (_eval.EvalBool(ab.Condition, scope) == false) continue;

                // 代价检查：不能支付就不能发动。
                // 之前这里完全没有判定，所以 Enhance / Necromancy 等代价形同虚设。
                var ppKey = Schema.Counter("pp");
                var shadowKey = Schema.Counter("shadow");
                var earthKey = Schema.Counter("earth_sigil");
                if (!CostExecutor.CanPay(_world, e.PlayerId, e, ab.Costs, ppKey, shadowKey, earthKey)) continue;
                CostExecutor.Pay(_world, e.PlayerId, e, ab.Costs, ppKey, shadowKey, earthKey);

                // 只有「在自己卡上监听的牌库能力」(Invoke) 走同名去重。
                // 场上两个同名随从的 Whenever 必须各触发一次，不能被这条吃掉。
                if (ab.Trigger.ActiveZone == Core.ActiveZoneScope.Deck)
                {
                    int cid = e.Def?.CardId ?? 0;
                    if (cid != 0 && !firedOnce.Add((cid, ev.Kind))) continue;
                }

                toPush.Add((ab.Body, scope));
            }
        }

        // 倒序压栈 = 正序执行（_frames 是后进先出）。
        // 正序压栈会让整批能力的执行顺序反转，违反 76-c 4.1 铁律 2。
        for (int i = toPush.Count - 1; i >= 0; i--)
            PushBody(toPush[i].body, toPush[i].scope);
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

    /// <summary>
    /// 事件主体此刻被视同「在场」的依据。
    ///
    /// 打出法术时它还在手牌区（结算完才进墓地），
    /// 而它自己印的 Fanfare: 编译成 ActiveZone=Board。
    /// 只按区域判定的话，这一条 OnPlay 会被直接丢掉 —— 法术一次效果都不跑。
    ///
    /// 豁免只对事件主体自身生效，且只对「它被打出」这一条 OnPlay：
    /// 手牌 / 牌组里**其它**卡的监听语义一字不改。
    /// </summary>
    private static bool ActsAsOnField(VmEvent ev, RuntimeEntity owner)
    {
        if (ev.Subject == null) return false;
        if (ev.Subject.Value.EntityId != owner.EntityId) return false;
        if (ev.Subject.Value.EntrySerial != owner.EntrySerial) return false;
        return ev.Kind == CoreEvent.OnPlay;
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
            // 信仰与纹章住在纹章区，不在战场。
            // 若按默认 Board 判定，挂在信仰上的被动永远不会触发。
            _ => owner.Zone == ZoneRef.CrestArea ? ZoneRef.CrestArea : ZoneRef.Board,
        };
        // 豁免一：本卡自己的入场曲 —— 法术正在结算，它还没进墓地，此刻必须被当作在场。
        // 豁免二：本卡自己的谢幕曲 —— 破坏是「先移区、再发事件」，
        //   等 OnDestroy / OnLeaveField 送达时卡已经在墓地，按 Board 判定必然落空。
        //
        // 豁免二的两个限定缺一不可：
        //   · 只对 OnDestroy / OnLeaveField 这两类离场语义生效 —— 墓地里的卡
        //     仍然收不到 Fanfare / Strike / Evolve，非谢幕曲能力不会跟着复活；
        //   · 只对「事件主体就是这张卡」生效，且用 EntrySerial 锁定身份 ——
        //     EntityId 永不复用，EntrySerial 在移区时 +1，两者同时相等才说明
        //     这具尸体正是本次事件的主体，而不是另一张同名卡。
        // 「离场时」与「谢幕曲」都发生在实体**已经离开战场之后**：
        //   · OnDestroy    → 事件在移入墓地后发出
        //   · OnLeaveField → 事件在移出后发出（Banish 的落点是放逐区）
        // 只认墓地的豁免会让被放逐的卡完全收不到自己的离场时能力 ——
        // 实测「When this card leaves the field, deal 3 damage」在 Banish 路径下打 0 点。
        // 到期的纹章停在 ZoneRef.None（既不在场也不进任何区域），
        // 但它同样要能收到自己的「离场时 / 谢幕曲」。
        bool graveSelf = need == ZoneRef.Board
                         && (owner.Zone == ZoneRef.Cemetery
                             || owner.Zone == ZoneRef.Banished
                             || (owner.Zone == ZoneRef.None && owner.Def?.Kind == CardKind.Crest))
                         // 「当这张卡被弃掉 / 被放逐时」的触发点是**离场之后**，
                         // 事件到达时这张卡已经在墓地 / 放逐区，光看 Zone 会被挡下。
                         // 只放行 OnDestroy / OnLeaveField 会让这两类卡 100% 永不触发。
                         && (ev.Kind == CoreEvent.OnDestroy
                             || ev.Kind == CoreEvent.OnLeaveField
                             || ev.Kind == CoreEvent.OnBanish
                             || ev.Kind == CoreEvent.OnDiscard)
                         && ev.Subject != null
                         && ev.Subject.Value.EntityId == owner.EntityId
                         && ev.Subject.Value.EntrySerial == owner.EntrySerial;
        if (owner.Zone != need && !graveSelf && !(need == ZoneRef.Board && ActsAsOnField(ev, owner)))
            return false;

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
            // 「你（玩家）做了某件事」类事件：OnSelectMode / OnEarthRite /
            // OnSpellboost 等，事件主体是玩家而不是某张卡。
            //
            // 必须先按 PlayerId 过一遍归属，否则**对手**选模式也会给我方
            // Sham-Nacha 的信仰 +1；对手发动土之秘术也会触发我方监听。
            if (ev.PlayerId >= 0 && ev.PlayerId != owner.PlayerId) return false;

            // TurnStart / TurnEnd / BattleStart 的 Subject 恒为 null，
            // 早已通过上面的 `ev.Subject != null` 短路放行，**不能**列进这里 ——
            // 列进来等于给它们开一条「不校验主体」的后门，
            // 而 TurnStart 必须只让「事件主体就是自己」的能力触发，
            // 否则所有同名卡的未激活进度会被共享（实测 seq-7 的 mask 串成一串）。
            bool playerLevelEvent = ev.Kind == CoreEvent.OnSelectMode
                                 || ev.Kind == CoreEvent.OnEarthRite
                                 || ev.Kind == CoreEvent.OnSpellboost;
            if (!playerLevelEvent
                && ev.Subject != null && ev.Subject.Value.EntityId != owner.EntityId) return false;
        }
        else
        {
            if (ev.Subject == null) return false;
            if (!SubjectMatches(trig.SubjectFilter, ev.Subject.Value, owner)) return false;
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

    /// <summary>
    /// 把主体过滤表达式套到事件主体上求值。
    ///
    /// 判据的顺序很要紧：**通用「一张牌」过滤器先按归属判，其余一律按区域解析**。
    ///
    /// 为什么需要第一条：编译器为 "a card" 兜底出来的是一个
    /// 「Self 场上任取一个」的选择器 —— 区域是 Board、无条件、不具名。
    /// 而这类句子的主体在事件发生的那一刻根本不在场上：
    ///   · "Whenever you play a card" —— 法术打出时它还在手牌区结算
    ///   · "Whenever you draw a card" —— 抽到的牌在牌库/手牌之间移动
    /// 按区域解析必然找不到它，于是手牌监听卡对法术永远失明（实测 15 / 20）。
    ///
    /// 这条豁免**只对「不具名、无附加条件」的过滤器生效**，而且仍然要求
    /// 归属对得上（Self / Opponent / Any）。具名卡（CardId / CardName）、
    /// 带条件（is Follower / 费用 / 攻击力）、限定历史池或排序的过滤器
    /// 一律走原来的区域解析 —— 那才是它们真正的语义。
    /// 它也不是 ActsAsOnField 的放宽：那条只对「事件主体自身 · OnPlay」生效，
    /// 这里是「别的卡在监听」时的主体解析，两者的作用面互不重叠。
    /// </summary>
    private bool SubjectMatches(TargetSpec filter, EntityRef subject, RuntimeEntity owner)
    {
        if (IsGenericCardFilter(filter) && SubjectBelongsTo(filter.Owner, owner.PlayerId, subject))
            return true;

        // 判定作用域必须带上 Source / Trigger 两枚锚点。
        //
        // 编译器为「本卡自己」写出的主体过滤器是一个 **槽位** 目标
        // （SourceKind=Slot, Slot=$self），「事件主体」同理是 $trigger。
        // 槽位解析读的是 Registers，而 VmEval.ResolveRef 对 $self / $trigger
        // 的兜底回退分别指向 scope.Source / scope.Trigger。
        //
        // 此前这里只塞了 Controller，两个锚点都是 null，于是
        //   "Whenever this follower takes damage, give this follower +1/+0"
        // 这类「本卡自身」监听器的 SubjectFilter 恒解析为空池 —— 条件永远不成立，
        // 能力一次都不会触发（实测：伤害 2 点后 attack 仍是 2，能力静默空转）。
        // 这不是把 $trigger 泛化到所有场景：谁的锚点对应哪个槽位由 ResolveRef
        // 已有的分支决定，$self 仍然只指向本卡。
        var scope0 = new VmScope
        {
            Owner = ZoneScope.Self,
            Controller = owner.PlayerId,
            Source = EntityRef.From(owner),
            Trigger = subject,
        };
        // 槽位型过滤器（$self / $trigger）仍按槽位解析。
        if (filter.SourceKind == TargetSourceKind.Slot)
        {
            var pool0 = _eval.ResolveRef(filter, scope0);
            return pool0.Any(r => r.EntityId == subject.EntityId);
        }

    // 非槽位过滤器不能拿去「把 subject 扔进区域里捞」——事件主体此刻往往
    // 根本不在 filter 声明的区域里：
    //   打出一张 Loot 法术时它在 Hand；被融合的 Loot 在 FuseMaterial；
    //   被破坏的随从在 Cemetery；被放逐的在 Banished；被弃的在 Cemetery。
    // 去 Board 捞必然空池，于是「Whenever you play or Fuse a Loot card」
    // 「Whenever an allied amulet is destroyed」这类触发器在实战中全部哑火。
    // 正确做法：直接检验 subject 这个**候选**是否满足 filter 的归属/卡号/Where。
    bool MatchOne(EntityRef cand)
    {
        // filter 是**判定**（这张卡符不符合），不是「从池子里挑几张」。
        // 编译器给触发器主语留下的 Mode 可能是 Choose（沿用了动作语义），
        // 但主体判定里 Choose 毫无意义 —— 候选只有一个，谈不上挑。
        if (!SubjectBelongsTo(filter.Owner, owner.PlayerId, cand)) return false;
        if (filter.CardId > 0 && (cand.Snapshot?.CardId ?? 0) != filter.CardId) return false;
        if (!string.IsNullOrEmpty(filter.CardName)
            && !string.Equals(cand.Snapshot?.Name, filter.CardName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (filter.Where == null || (filter.Where is Ast.ConstBoolExpr cb && cb.Value)) return true;
        var sub = scope0.Derive();
        sub.BindRefs(TargetSpec.SlotItem, new[] { cand });
        return _eval.EvalBool(filter.Where, sub);
    }

    if (MatchOne(subject)) return true;

    // 融合事件的 Subject 是融合**主体**，材料停在 FuseMaterial 区。
    // 「Whenever you play or Fuse a Loot card」要检验的是被喂进去的那张。
    foreach (var m in _world.EnumerateZone(ZoneRef.FuseMaterial, owner.PlayerId))
    {
        if (m != null && m.FusedIntoId == subject.EntityId && MatchOne(EntityRef.From(m)))
            return true;
    }
    return false;
    }

    /// <summary>
    /// 「一张牌」的兜底选择器：场上、不具名、不筛条件、不排序、不查历史。
    ///
    /// 区域必须是 Board —— 编译器给 "a card" 兜底的就是 Board，
    /// 而 "your leader"（Leader）、"your hand"（Hand）这类选择器**不能**
    /// 被当成泛化主体，否则「主战者受到伤害」会被一张随从的伤害满足。
    /// </summary>
    private static bool IsGenericCardFilter(TargetSpec t)
    {
        if (t == null) return false;
        if (t.SourceKind != TargetSourceKind.Zone) return false;
        if (t.Zone != ZoneRef.Board) return false;
        if (t.CardId != 0 || !string.IsNullOrEmpty(t.CardName)) return false;
        if (t.Slot is { Length: > 0 }) return false;
        if (t.History != HistoryScope.None) return false;
        if (t.DistinctByName) return false;
        if (t.Sort != SortKey.None || t.SortDescending || t.TopN != 0) return false;
        if (t.Where != null && !(t.Where is ConstBoolExpr cb && cb.Value)) return false;
        return true;
    }

    /// <summary>事件主体是不是过滤表达式要求的归属方发出的。</summary>
    private static bool SubjectBelongsTo(ZoneScope wanted, int watcherPlayer, EntityRef subject)
    {
        int ownerId = subject.Snapshot?.PlayerId ?? -1;
        if (ownerId is < 0 or > 1) return false;
        return wanted switch
        {
            ZoneScope.Self => ownerId == watcherPlayer,
            ZoneScope.Opponent => ownerId == 1 - watcherPlayer,
            ZoneScope.Any => true,
            _ => false,
        };
    }


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

using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 原子节点执行器
//
// 每个节点的执行分两种结局：
//   · 立即完成 → 返回 false，主循环推进 Ip
//   · 需要玩家选 → 返回 true，主循环挂起；玩家选完后 Ip 不动，节点重跑
//
// 「重跑」之所以安全，是因为 Choose 型节点在挂起前不产生任何副作用 ——
// 它只是解析出候选列表然后返回 true。
// ============================================================================

public sealed partial class CardVm
{
    /// <summary>执行一个原子节点。返回 true 表示已挂起等输入。</summary>
    private bool ExecNode(EffectNode node, VmFrame frame)
    {
        var scope = frame.Scope;

        switch (node)
        {
            case SelectTargetNode n: return DoSelect(n, frame);
            case DealDamageNode n: return DoDamage(n, scope);
            case RestoreLifeNode n: return DoRestore(n, scope);
            case ModifyStatsNode n: return DoModifyStats(n, scope);
            case ModifyCostNode n: return DoModifyCost(n, scope);
            case ModifyKeywordNode n: return DoModifyKeyword(n, scope);
            case ModifyCounterNode n: return DoModifyCounter(n, scope);
            case MoveZoneNode n: return DoMoveZone(n, scope);
            case SpawnCardNode n: return DoSpawn(n, scope);
            case ReplaceDeckNode n: return DoReplaceDeck(n, scope);
            case EvolveNode n: return DoEvolve(n, scope);
            case TransformNode n: return DoTransform(n, scope);
            case ConditionalNode n: return DoConditional(n, scope);
            case LoopNode n: return DoLoop(n, frame);
            case BranchModeNode n: return DoBranch(n, frame);
            case GrantAbilityNode n: return DoGrant(n, scope);
            case GrantCounterAbilityNode n: return DoGrantCounter(n, scope);
            case InvokeAbilityNode n: return DoInvoke(n, scope);
            case MacroNode n: return DoMacro(n, scope);
            default:
                throw new VmFaultException("未知的动作节点: " + node.GetType().Name);
        }
    }

    /// <summary>
    /// 解析目标，若 Mode == Choose 则挂起等玩家选。
    /// 返回 null 表示已挂起（调用方必须直接 return true，不要执行后续动作）。
    /// </summary>
    private List<RuntimeEntity> ResolveOrBlock(TargetSpec spec, VmScope scope, string slot)
    {
        if (spec == null) return new List<RuntimeEntity>();

        if (spec.Mode == SelectMode.Choose)
        {
            // 若该槽位已经绑定了本次选择的实体，说明玩家已选过，直接取用
            var chosenBefore = scope.Read(slot);
            if (chosenBefore.Count > 0)
            {
                var liveChosen = new List<RuntimeEntity>();
                foreach (var cr in chosenBefore)
                {
                    var lv = _world.GetLive(cr.EntityId, cr.EntrySerial);
                    if (lv != null) liveChosen.Add(lv);
                }
                if (liveChosen.Count > 0) return liveChosen;
            }

            var pool = _eval.ResolveRef(spec, scope);
            // 能力选目标时要过滤对手的潜行 / 灵气：
            //   潜行(ambush)：既不能被攻击，也不能被能力选中
            //   灵气(aura)：不能被能力选中（但可以被攻击）
            //   威慑(intimidate)：只阻止攻击，不阻止能力选中
            // 之前这里没有任何检查，敌方法术可以随意点选你的灵气随从。
            var controller = scope.ResolvePlayer(Core.ZoneScope.Self);
            var ambushKw = Schema.Keyword("ambush");
            var auraKw = Schema.Keyword("aura");
            pool = pool.Where(r => {
                var sn = r.Snapshot;
                if (sn == null || sn.Zone != ZoneRef.Board) return true;

                // 土之印 / Clue 护符不能被能力选中（双方都不行）。
                var live = _world.GetLive(sn.EntityId, sn.EntrySerial);
                if (live != null && _world is Battle.BattleState bs && bs.IsProtectedAmulet(live))
                    return false;

                if (sn.PlayerId == controller) return true;   // 己方目标不受限
                return !sn.Keywords.Contains(ambushKw) && !sn.Keywords.Contains(auraKw);
            }).ToList();
            if (pool.Count == 0) return new List<RuntimeEntity>();

            int min = 1;
            int max = spec.Count != null ? _eval.EvalNum(spec.Count, scope) : 1;
            if (max <= 0) max = 1;
            if (max > pool.Count) max = pool.Count;
            if (min > max) min = max;

            // 只有一个候选且只选一个：直接确定，不必打扰玩家
            if (pool.Count == 1 && max == 1)
            {
                var only = _world.GetLive(pool[0].EntityId, pool[0].EntrySerial);
                if (only != null)
                {
                    scope.Bind(slot, new List<RuntimeEntity> { only });
                    return new List<RuntimeEntity> { only };
                }
            }

            Block(new PendingChoice
            {
                Player = scope.Owner,
                Kind = ChoiceKind.SelectTarget,
                Candidates = pool.Select(x => x.EntityId).ToList(),
                MinSelect = min,
                MaxSelect = max,
                Slot = slot,
            });
            return null;   // 已挂起
        }

        return _eval.Resolve(spec, scope);
    }

    // ---------- 选择 ----------

    private bool DoSelect(SelectTargetNode n, VmFrame frame)
    {
        // 玩家已经选过（槽位已被 SubmitChoice 绑定）—— 直接放行，不要再次挂起
        if (frame.Scope.Read(n.OutSlot).Count > 0) return false;

        var candidates = _eval.Resolve(n.Target, frame.Scope);

        // 能力选目标必须过滤对手的潜行(ambush)与灵气(aura)。
        // ResolveOrBlock 里已有同样的过滤，但 DoSelect 走的是 _eval.Resolve，
        // 不经过那里，所以这条路径之前对 ambush / aura 完全不设防。
        var controller = frame.Scope.ResolvePlayer(Core.ZoneScope.Self);
        var ambushKw = Schema.Keyword("ambush");
        var auraKw = Schema.Keyword("aura");
        candidates = candidates.Where(c =>
        {
            if (c == null || c.Zone != ZoneRef.Board) return true;
            if (c.PlayerId == controller) return true;
            return !c.Keywords.Contains(ambushKw) && !c.Keywords.Contains(auraKw);
        }).ToList();

        if (candidates.Count == 0) return false;   // 没得选就跳过

        if (n.Target.Mode == SelectMode.Choose)
        {
            int min = 1;
            int max = n.Target.Count != null ? _eval.EvalNum(n.Target.Count, frame.Scope) : 1;
            if (max <= 0) max = candidates.Count;
            return Block(new PendingChoice
            {
                Player = frame.Scope.Owner,
                Kind = ChoiceKind.SelectTarget,
                Candidates = candidates.Select(c => c.EntityId).ToList(),
                MinSelect = min,
                MaxSelect = max,
                Slot = n.OutSlot,
                Prompt = "请选择目标",
            });
        }

        frame.Scope.Bind(n.OutSlot, candidates);
        return false;
    }

    // ---------- 伤害 / 恢复 ----------

    private bool DoDamage(DealDamageNode n, VmScope scope)
    {
        var targets = ResolveOrBlock(n.Target, scope, TargetSpec.SlotSelected);
        if (targets == null) return true;   // 已挂起
        if (targets.Count == 0) return false;
        int total = _eval.EvalNum(n.Amount, scope);

        if (n.IsSplit && targets.Count > 0)
        {
            int each = total / targets.Count;
            foreach (var t in targets) ApplyDamage(t, each, n.IsAbility, scope);
        }
        else
        {
            foreach (var t in targets) ApplyDamage(t, total, n.IsAbility, scope);
        }
        // 注意：不写 $selected —— 槽位只能由 SelectTargetNode / SpawnCardNode 显式写入。
        // 否则 `Select X, deal 4 to it, boost self, banish it` 的最后一个 it 会指向 self。
        return false;
    }

    /// <summary>
    /// 能力造成的伤害，必须走世界的统一管线（屏障 / 超进化免伤 / 伤害上限）。
    /// </summary>
    private void ApplyDamage(RuntimeEntity t, int amount, bool isAbility, VmScope scope = null)
    {
        if (amount <= 0) return;

        // 潜行（Ambush）铁律：造成伤害的那个随从立即失去潜行。
        // 攻击宣言时已在 ExecuteCombat 里处理；这里补「能力造成伤害」的情形。
        var ambushKw = Schema.Keyword("ambush");
        if (scope?.Source != null)
        {
            var self = _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial);
            if (self != null && self.Keywords.Contains(ambushKw)) self.Keywords.Remove(ambushKw);
        }

        // 伤害来源玩家：取目标的对手（能力伤害通常是对手发起的）。
        int srcPlayer = t.PlayerId == 0 ? 1 : 0;
        _world.ApplyDamage(t, amount, srcPlayer);
    }

    private bool DoRestore(RestoreLifeNode n, VmScope scope)
    {
        var targets = ResolveOrBlock(n.Target, scope, TargetSpec.SlotSelected);
        if (targets == null) return true;   // 已挂起
        foreach (var t in targets)
        {
            int amt = n.FullRestore ? t.MaxLife : _eval.EvalNum(n.Amount, scope);
            t.Life = Math.Min(t.MaxLife, t.Life + amt);
            _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnHealed, Subject = EntityRef.From(t), Amount = amt });
        }
        // 不覆写 $selected（只有 SelectTargetNode / SpawnCardNode 可以写槽位）
        return false;
    }

    // ---------- 属性 / 费用 ----------

    private bool DoModifyStats(ModifyStatsNode n, VmScope scope)
    {
        var targets = ResolveOrBlock(n.Target, scope, TargetSpec.SlotSelected);
        if (targets == null) return true;   // 已挂起
        int atk = _eval.EvalNum(n.Attack, scope);
        int life = _eval.EvalNum(n.Life, scope);
        foreach (var t in targets)
        {
            switch (n.Op)
            {
                case StatModOp.Add: t.Attack += atk; t.Life += life; break;
                case StatModOp.Set: t.Attack = atk; t.Life = life; break;
                case StatModOp.Swap: (t.Attack, t.Life) = (t.Life, t.Attack); break;
            }

            // 限时修正必须登记，否则回合结束时无从撤销（会永久生效）
            if (n.Duration != DurationKind.Permanent)
                t.TempModifiers.Add(new TemporaryModifier
                {
                    Duration = n.Duration,
                    Attack = n.Op == StatModOp.Set ? 0 : atk,
                    Life = n.Op == StatModOp.Set ? 0 : life,
                });
        }
        return false;
    }

    private bool DoModifyCost(ModifyCostNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        int v = _eval.EvalNum(n.Value, scope);
        foreach (var t in targets)
        {
            t.Cost = n.Op switch
            {
                CostModOp.Add => Math.Max(0, t.Cost + v),
                CostModOp.Set => Math.Max(0, v),
                CostModOp.Halve => Math.Max(0, t.Cost / 2),
                _ => t.Cost,
            };
        }
        return false;
    }

    private bool DoModifyKeyword(ModifyKeywordNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            switch (n.Op)
            {
                case KeywordOp.Grant:
                    t.Keywords.Add(n.Keyword);
                    // 带参关键词把数值一并存下来，否则运行时不知道上限是多少
                    if (n.Value != null) t.KeywordValues[n.Keyword] = _eval.EvalNum(n.Value, scope);
                    else t.KeywordValues.Remove(n.Keyword);
                    if (n.Duration != DurationKind.Permanent)
                        t.TempModifiers.Add(new TemporaryModifier
                        {
                            Duration = n.Duration,
                            Keyword = n.Keyword,
                            IsKeywordGrant = true,
                        });
                    break;
                case KeywordOp.Remove:
                    t.Keywords.Remove(n.Keyword);
                    t.KeywordValues.Remove(n.Keyword);
                    break;
                case KeywordOp.SilenceAll:
                    t.Keywords.Clear();
                    t.KeywordValues.Clear();
                    t.IsSilenced = true;
                    break;
            }
        }
        return false;
    }

    private bool DoModifyCounter(ModifyCounterNode n, VmScope scope)
    {
        int delta = _eval.EvalNum(n.Delta, scope);

        // Target != null：修改每个目标实体自己的计数器
        if (n.Target != null)
        {
            var ents = _eval.Resolve(n.Target, scope);
            foreach (var e in ents)
            {
                int curE = _world.ReadEntityCounter(e, n.Counter);
                int nextE = n.Op switch
                {
                    CounterOp.Add => curE + delta,
                    CounterOp.Set => delta,
                    CounterOp.Fill => delta,
                    _ => curE,
                };
                _world.WriteEntityCounter(e, n.Counter, nextE);
                _world.EmitEvent(new VmEvent
                {
                    Kind = CoreEvent.OnCounterChange,
                    Amount = delta,
                    CounterName = n.Counter.Name,
                    PlayerId = e.PlayerId,
                });
            }
            return false;
        }

        // Target == null：修改主战者级计数器
        int cur = _world.ReadCounter(scope.ResolvePlayer(n.Owner), n.Counter);
        int next = n.Op switch
        {
            CounterOp.Add => cur + delta,
            CounterOp.Set => delta,
            CounterOp.Fill => delta,
            _ => cur,
        };
        int owner = scope.ResolvePlayer(n.Owner);
        _world.WriteCounter(owner, n.Counter, next);
        _world.EmitEvent(new VmEvent
        {
            Kind = CoreEvent.OnCounterChange,
            Amount = delta,
            CounterName = n.Counter.Name,
            PlayerId = owner,
        });
        return false;
    }

    private bool DoMoveZone(MoveZoneNode n, VmScope scope)
    {
        var targets = ResolveOrBlock(n.Target, scope, n.OutSlot);
        if (targets == null) return true;   // 已挂起等玩家选
        var moved = new List<RuntimeEntity>();
        foreach (var t in targets)
        {
            if (t.Zone == n.ToZone) continue;

            // 破坏走 DestroyEntity（它会发 OnDestroy、加墓场、记入破坏池）；
            // 弃牌 / 流放 / 回手走 MoveEntity（只移动）。
            // 之前一律用 MoveEntity，所以法术破坏不触发谢幕曲、
            // 也不加墓场，导致 Reanimate 找不到被法术解掉的随从。
            if (n.IsDestroy && t.Zone == ZoneRef.Board && n.ToZone == ZoneRef.Cemetery)
            {
                _world.DestroyEntity(t);
            }
            else
            {
                bool fromHand = t.Zone == ZoneRef.Hand;
                if (t.Zone == ZoneRef.Board && n.ToZone != ZoneRef.Board)
                    _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(t) });
                _world.MoveEntity(t, n.ToZone, t.PlayerId);

                // 弃牌（Hand → Cemetery，且不是 Destroy）要发 OnDiscard 并加墓场。
                // 官方：弃牌不触发谢幕曲、不进 DestroyedThisMatch，但 shadow +1。
                // 之前这里只做了 MoveEntity，所以所有「弃一张牌」法术
                // 既不会触发 Whenever you discard，也不加墓场。
                if (fromHand && n.ToZone == ZoneRef.Cemetery)
                {
                    var shadowKey = Schema.Counter("shadow");
                    var owner = t.PlayerId;
                    _world.WriteCounter(owner, shadowKey, _world.ReadCounter(owner, shadowKey) + 1);
                    _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnDiscard, Subject = EntityRef.From(t) });
                }
            }
            moved.Add(t);
        }

        // $created 只应记录「进入场上/手牌」的新对象。
        // 送去墓地或放逐时若也写入，后续的 "it" 会指向一具尸体，
        // 导致 "destroy it, then banish it" 里第二句指向错误。
        if (n.ToZone == ZoneRef.Board || n.ToZone == ZoneRef.Hand || n.ToZone == ZoneRef.ExArea)
            scope.Bind(n.OutSlot, moved);
        return false;
    }

    private bool DoSpawn(SpawnCardNode n, VmScope scope)
    {
        int count = _eval.EvalNum(n.Count, scope);
        var created = new List<RuntimeEntity>();
        for (int i = 0; i < count; i++)
        {
            RuntimeEntity e = null;
            if (n.SourceKind == CardSourceKind.CopyOfTarget)
            {
                var refs = _eval.ResolveRef(n.CopyFrom, scope);
                if (refs.Count == 0) break;
                // 接口上只有 3 参数版；exact 区分需要落到具体实现。
                e = _world is Battle.BattleState bs2
                    ? bs2.SpawnCopy(refs[0], n.ToZone, scope.ResolvePlayer(n.ToOwner), n.IsExactCopy)
                    : _world.SpawnCopy(refs[0], n.ToZone, scope.ResolvePlayer(n.ToOwner));
            }
            else if (n.SourceKind == CardSourceKind.CopyOfPrevious)
            {
                // "Summon a Megalorca. If you're in Overflow, summon 2 instead."
                // 这句没有卡名，宾语就是上一句召唤的那张卡。
                // 之前这里没有任何分支，SpawnCard 收到 CardId=0，产出空实体。
                int prevId = scope.LastSpawnedCardId;
                if (prevId <= 0) break;
                e = _world.SpawnCard(prevId, n.ToZone, scope.ResolvePlayer(n.ToOwner));
            }
            else
            {
                e = _world.SpawnCard(n.CardId, n.ToZone, scope.ResolvePlayer(n.ToOwner));
                if (e != null) scope.LastSpawnedCardId = n.CardId;
            }
            if (e != null) created.Add(e);
        }
        scope.Bind(n.OutSlot, created);
        return false;
    }

    /// <summary>
    /// 整副牌库替换："Replace your deck with the Apocalypse Deck"。
    ///
    /// 牌组名必须能在 DeckDefTable 里解析；解析不到就抛错。
    /// 这里不静默装空牌库 —— 空牌库会让玩家下一回合直接 DECK_OUT 判负，
    /// 一个"看起来成功"的失败比报错危险得多。
    /// </summary>
    private bool DoReplaceDeck(ReplaceDeckNode n, VmScope scope)
    {
        int pid = scope.ResolvePlayer(n.Owner);
        var cardIds = DeckDefs.Expand(n.DeckName);
        if (cardIds == null)
            throw new VmFaultException("未定义的具名牌组: [" + n.DeckName + "]");

        _world.ReplaceDeck(pid, n.DeckName, cardIds);
        return false;
    }

    private bool DoEvolve(EvolveNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            if (n.IsSuperEvolve) { t.IsSuperEvolved = true; t.IsEvolved = true; } else t.IsEvolved = true;

            // 超进化同时触发 Evolve 与 Super-Evolve。
            // 且超进化必须把 IsEvolved 也置位——否则「if this follower is evolved」
            // 这类条件在自动超进化后仍然为假。
            // 这是「能力效果自动进化」，不是玩家花 EP/SEP 手动进化。
            // IsManualEvolve = false 让 FlushNextTrigger 跳过 Evolve: / Super-Evolve: 冒号头，
            // 只让 When/Whenever ... evolves 这类事件从句收到事件。
            // 奥义槽依赖「自己随从进化/超进化的次数」，自动进化同样计入。
            var evoKey = Schema.Counter("evolve_count");
            var owner = t.PlayerId;
            _world.WriteCounter(owner, evoKey, _world.ReadCounter(owner, evoKey) + 1);

            _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnEvolve, Subject = EntityRef.From(t), IsManualEvolve = false });
            if (n.IsSuperEvolve)
                _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnSuperEvolve, Subject = EntityRef.From(t), IsManualEvolve = false });
        }
        // 不覆写 $selected（只有 SelectTargetNode / SpawnCardNode 可以写槽位）
        return false;
    }

    private bool DoTransform(TransformNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        if (targets.Count == 0) return false;

        // 变形源是具体卡名：直接变成该卡
        if (n.IntoCardId > 0)
        {
            foreach (var t in targets) _world.TransformEntity(t, n.IntoCardId);
            return false;
        }

        // 变形源是目标集合（"into exact copies of random followers in your deck"）
        if (n.CopySource != null)
        {
            var refs = _eval.ResolveRef(n.CopySource, scope);
            if (refs.Count == 0) return false;
            for (int i = 0; i < targets.Count; i++)
            {
                var src = refs[Math.Min(i, refs.Count - 1)];
                int intoId = src.Snapshot?.CardId ?? 0;
                if (intoId > 0) _world.TransformEntity(targets[i], intoId);
            }
            return false;
        }

        // 都没有：交给 Battle 层按 CardName 解析
        return false;
    }

    // ---------- 控制流 ----------

    /// <summary>
    /// 模式分支的子作用域。
    /// "give this follower 3 random abilities from the following" 会把目标写进 GrantTarget，
    /// 每个分支都应看到这个目标，因此先把它绑定到子作用域。
    /// </summary>
    private VmScope DeriveForBranch(BranchModeNode n, VmScope parent)
    {
        var sub = parent.Derive();
        if (n.GrantTarget != null)
        {
            var ents = _eval.Resolve(n.GrantTarget, parent);
            if (ents.Count > 0) sub.Bind(TargetSpec.SlotSelected, ents);
        }
        return sub;
    }

    private bool DoConditional(ConditionalNode n, VmScope scope)
    {
        var branch = _eval.EvalBool(n.Condition, scope) ? n.Then : n.Else;
        if (branch != null && branch.Count > 0) PushBody(branch, scope.Derive());
        return false;
    }

    private bool DoLoop(LoopNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        if (n.Body == null || n.Body.Count == 0) return false;

        // 防死循环：每层循环都有硬上限
        int max = Math.Clamp(n.MaxIterations, 1, 100);

        switch (n.Kind)
        {
            case LoopKind.Times:
            {
                int cnt = Math.Clamp(_eval.EvalNum(n.Count, scope), 0, max);
                for (int i = 0; i < cnt; i++) PushBody(n.Body, scope.Derive());
                return false;
            }
            case LoopKind.ForEach:
            {
                var items = _eval.Resolve(n.Collection, scope);
                foreach (var it in items.Take(max))
                {
                    var s = scope.Derive();
                    s.Bind(TargetSpec.SlotItem, new[] { it });
                    PushBody(n.Body, s);
                }
                return false;
            }
            default:
                throw new VmFaultException("RepeatUntil 暂未实现");
        }
    }

    private bool DoBranch(BranchModeNode n, VmFrame frame)
    {
        var opts = n.Options;
        if (opts == null || opts.Count == 0) return false;
        int pick = Math.Clamp(_eval.EvalNum(n.PickCount, frame.Scope), 1, opts.Count);

        switch (n.Kind)
        {
            case ModeDispatchKind.Random:
            {
                var idx = Enumerable.Range(0, opts.Count)
                    .OrderBy(_ => _world.Rng.Next()).Take(pick).ToList();
                // 帧栈后进先出 —— 随机选项同样要倒序压栈
                for (int k = idx.Count - 1; k >= 0; k--)
                    PushBody(opts[idx[k]], DeriveForBranch(n, frame.Scope));
                return false;
            }
            case ModeDispatchKind.Sequence:
                // 帧栈是后进先出，必须倒序压入才能正序执行
                for (int i = opts.Count - 1; i >= 0; i--)
                    PushBody(opts[i], DeriveForBranch(n, frame.Scope));
                return false;

            default:
                // Choose：挂起等玩家
                return Block(new PendingChoice
                {
                    Player = frame.Scope.Owner,
                    Kind = ChoiceKind.SelectMode,
                    Candidates = Enumerable.Range(0, opts.Count).ToList(),
                    MinSelect = 1,
                    MaxSelect = pick,
                    Slot = "$mode",
                    Prompt = "请选择模式",
                });
        }
    }

    // ---------- 授予 / 复刻 / 宏 ----------

    private bool DoGrant(GrantAbilityNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets) _world.GrantAbility(t, n.Granted);
        return false;
    }

    /// <summary>给资源计数器挂载能力（faith 等）。</summary>
    private bool DoGrantCounter(GrantCounterAbilityNode n, VmScope scope)
    {
        _world.GrantCounterAbility(scope.ResolvePlayer(n.Owner), n.Counter, n.Granted);
        return false;
    }

    private bool DoInvoke(InvokeAbilityNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            var ab = t.Def?.Abilities?.FirstOrDefault(a => a.Trigger != null && a.Trigger.Event == n.Event);
            if (ab != null) PushBody(ab.Body, scope.Derive());
        }
        return false;
    }

    /// <summary>
    /// 宏展开。
    ///
    /// 未实现的宏必须报错，不能静默跳过 ——
    /// 静默跳过意味着一张卡的能力在实战中凭空消失，而且没有任何痕迹。
    /// </summary>
    private bool DoMacro(MacroNode n, VmScope scope)
    {
        var args = n.Args?.Select(a => _eval.EvalNum(a, scope)).ToList() ?? new List<int>();
        var body = _world.ExpandMacro(n.Macro, args, scope);

        if (body == null || body.Count == 0)
            throw new VmFaultException("机制宏未实现：" + n.Macro.Name + "（展开结果为空，效果会凭空消失）");

        PushBody(body, scope.Derive());
        return false;
    }
}

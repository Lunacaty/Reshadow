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
            case DealDamageNode n: DoDamage(n, scope); return false;
            case RestoreLifeNode n: DoRestore(n, scope); return false;
            case ModifyStatsNode n: DoModifyStats(n, scope); return false;
            case ModifyCostNode n: DoModifyCost(n, scope); return false;
            case ModifyKeywordNode n: DoModifyKeyword(n, scope); return false;
            case ModifyCounterNode n: DoModifyCounter(n, scope); return false;
            case MoveZoneNode n: DoMoveZone(n, scope); return false;
            case SpawnCardNode n: DoSpawn(n, scope); return false;
            case EvolveNode n: DoEvolve(n, scope); return false;
            case TransformNode n: DoTransform(n, scope); return false;
            case ConditionalNode n: return DoConditional(n, scope);
            case LoopNode n: return DoLoop(n, frame);
            case BranchModeNode n: return DoBranch(n, frame);
            case GrantAbilityNode n: DoGrant(n, scope); return false;
            case InvokeAbilityNode n: DoInvoke(n, scope); return false;
            case MacroNode n: DoMacro(n, scope); return false;
            default:
                throw new VmFaultException("未知的动作节点: " + node.GetType().Name);
        }
    }

    // ---------- 选择 ----------

    private bool DoSelect(SelectTargetNode n, VmFrame frame)
    {
        var candidates = _eval.Resolve(n.Target, frame.Scope);
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

    private void DoDamage(DealDamageNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        if (targets.Count == 0) return;
        int total = _eval.EvalNum(n.Amount, scope);

        if (n.IsSplit && targets.Count > 0)
        {
            int each = total / targets.Count;
            foreach (var t in targets) ApplyDamage(t, each, n.IsAbility);
        }
        else
        {
            foreach (var t in targets) ApplyDamage(t, total, n.IsAbility);
        }
        // 注意：不写 $selected —— 槽位只能由 SelectTargetNode / SpawnCardNode 显式写入。
        // 否则 `Select X, deal 4 to it, boost self, banish it` 的最后一个 it 会指向 self。
    }

    private void ApplyDamage(RuntimeEntity t, int amount, bool isAbility)
    {
        if (amount <= 0) return;
        t.Life -= amount;
        _world.EmitEvent(new VmEvent
        {
            Kind = CoreEvent.OnDamaged,
            Subject = EntityRef.From(t),
            Amount = amount,
        });
    }

    private void DoRestore(RestoreLifeNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            int amt = n.FullRestore ? t.MaxLife : _eval.EvalNum(n.Amount, scope);
            t.Life = Math.Min(t.MaxLife, t.Life + amt);
            _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnHealed, Subject = EntityRef.From(t), Amount = amt });
        }
        if (targets.Count > 0) scope.Bind(TargetSpec.SlotSelected, targets);
    }

    // ---------- 属性 / 费用 ----------

    private void DoModifyStats(ModifyStatsNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
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
        }
        // 不写槽位（同 DoDamage 的理由）
    }

    private void DoModifyCost(ModifyCostNode n, VmScope scope)
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
    }

    private void DoModifyKeyword(ModifyKeywordNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            switch (n.Op)
            {
                case KeywordOp.Grant: t.Keywords.Add(n.Keyword); break;
                case KeywordOp.Remove: t.Keywords.Remove(n.Keyword); break;
                case KeywordOp.SilenceAll: t.Keywords.Clear(); t.IsSilenced = true; break;
            }
        }
    }

    private void DoModifyCounter(ModifyCounterNode n, VmScope scope)
    {
        int delta = _eval.EvalNum(n.Delta, scope);
        int cur = _world.ReadCounter(n.Owner, n.Counter);
        int next = n.Op switch
        {
            CounterOp.Add => cur + delta,
            CounterOp.Set => delta,
            CounterOp.Fill => delta,
            _ => cur,
        };
        _world.WriteCounter(n.Owner, n.Counter, next);
        _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnCounterChange, Amount = delta });
    }

    // ---------- 移区 / 生成 ----------

    private void DoMoveZone(MoveZoneNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        var moved = new List<RuntimeEntity>();
        foreach (var t in targets)
        {
            if (t.Zone == n.ToZone) continue;
            if (t.Zone == ZoneRef.Board && n.ToZone != ZoneRef.Board)
                _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(t) });
            _world.MoveEntity(t, n.ToZone, t.Owner);
            moved.Add(t);
        }
        scope.Bind(n.OutSlot, moved);
    }

    private void DoSpawn(SpawnCardNode n, VmScope scope)
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
                e = _world.SpawnCopy(refs[0], n.ToZone, n.ToOwner);
            }
            else
            {
                e = _world.SpawnCard(n.CardId, n.ToZone, n.ToOwner);
            }
            if (e != null) created.Add(e);
        }
        scope.Bind(n.OutSlot, created);
    }

    private void DoEvolve(EvolveNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            if (n.IsSuperEvolve) t.IsSuperEvolved = true; else t.IsEvolved = true;
            _world.EmitEvent(new VmEvent
            {
                Kind = n.IsSuperEvolve ? CoreEvent.OnSuperEvolve : CoreEvent.OnEvolve,
                Subject = EntityRef.From(t),
            });
        }
        if (targets.Count > 0) scope.Bind(TargetSpec.SlotSelected, targets);
    }

    private void DoTransform(TransformNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets) _world.TransformEntity(t, n.IntoCardId);
    }

    // ---------- 控制流 ----------

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
                foreach (var i in idx) PushBody(opts[i], frame.Scope.Derive());
                return false;
            }
            case ModeDispatchKind.Sequence:
                foreach (var o in opts) PushBody(o, frame.Scope.Derive());
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

    private void DoGrant(GrantAbilityNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets) _world.GrantAbility(t, n.Granted);
    }

    private void DoInvoke(InvokeAbilityNode n, VmScope scope)
    {
        var targets = _eval.Resolve(n.Target, scope);
        foreach (var t in targets)
        {
            var ab = t.Def?.Abilities?.FirstOrDefault(a => a.Trigger != null && a.Trigger.Event == n.Event);
            if (ab != null) PushBody(ab.Body, scope.Derive());
        }
    }

    private void DoMacro(MacroNode n, VmScope scope)
    {
        var args = n.Args?.Select(a => _eval.EvalNum(a, scope)).ToList() ?? new List<int>();
        var body = _world.ExpandMacro(n.Macro, args, scope);
        if (body != null && body.Count > 0) PushBody(body, scope.Derive());
    }
}

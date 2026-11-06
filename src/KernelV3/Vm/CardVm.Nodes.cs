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
    /// <summary>
    /// 登记「本实体受到了来自 scope.Source 这条能力的某次影响」。
    ///
    /// 只写进 EffectHistory 供左上角弹窗溯源，不参与任何规则判定。
    /// 同一来源卡的同一段能力对同一目标重复命中时只保留最早一条，
    /// 避免「每回合 +2/+2」把列表刷成几百条。
    /// </summary>
    private void RecordEffect(RuntimeEntity target, VmScope scope, string tag)
    {
        if (target == null || scope?.Source == null) return;

        var srcSnap = scope.Source.Value.Snapshot;
        var srcDef = srcSnap?.Def ?? (_world as Battle.BattleState)?.CardDefOf(srcSnap?.CardId ?? 0);
        if (srcDef == null) return;

        foreach (var old in target.EffectHistory)
            if (ReferenceEquals(old.SourceCard, srcDef)
                && ReferenceEquals(old.SourceAbility, scope.SourceAbility))
                return;

        target.EffectHistory.Add(new AppliedEffectRecord
        {
            SourceCard = srcDef,
            SourceAbility = scope.SourceAbility,
            Tag = tag ?? "",
        });
    }

    /// <summary>执行一个原子节点。返回 true 表示已挂起等输入。</summary>
    private bool ExecNode(EffectNode node, VmFrame frame)
    {
        var scope = frame.Scope;

        switch (node)
        {
            case RevealCardNode n: return DoReveal(n, scope);
            case DamageModifierNode n: return DoDamageModifier(n, frame);
            case SelectTargetNode n: return DoSelect(n, frame);
            case DealDamageNode n: return DoDamage(n, frame);
            case RestoreLifeNode n: return DoRestore(n, frame);
            case ModifyStatsNode n: return DoModifyStats(n, frame);
            case ModifyCostNode n: return DoModifyCost(n, frame);
            case ModifyKeywordNode n: return DoModifyKeyword(n, frame);
            case ModifyCounterNode n: return DoModifyCounter(n, frame);
            case MoveZoneNode n: return DoMoveZone(n, frame);
            case SpawnCardNode n: return DoSpawn(n, scope);
            case ReplaceDeckNode n: return DoReplaceDeck(n, scope);
            case EvolveNode n: return DoEvolve(n, frame);
            case TransformNode n: return DoTransform(n, frame);
            case MarkVictoryCardNode n: return DoMarkVictoryCard(n, scope);
            case ConditionalNode n: return DoConditional(n, scope);
            case LoopNode n: return DoLoop(n, frame);
            case BranchModeNode n: return DoBranch(n, frame);
            case GrantAbilityNode n: return DoGrant(n, frame);
            case GrantCounterAbilityNode n: return DoGrantCounter(n, scope);
            case InvokeAbilityNode n: return DoInvoke(n, scope, frame);
            case MacroNode n: return DoMacro(n, scope);
            default:
                throw new VmFaultException("未知的动作节点: " + node.GetType().Name);
        }
    }

    /// <summary>
    /// 解析目标，若 Mode == Choose 则挂起等玩家选。
    /// 返回 null 表示已挂起（调用方必须直接 return true，不要执行后续动作）。
    /// </summary>
    private List<RuntimeEntity> ResolveOrBlock(TargetSpec spec, VmScope scope, string slot, VmFrame frame)
    {
        if (spec == null) return new List<RuntimeEntity>();

        var normalized = NormalizeZoneWhere(spec);
        if (normalized.Mode == SelectMode.Choose)
        {
            // 只有「**本条指令**刚被玩家选完」才复用槽位。
            // 用「槽位非空」判断会串槽：同一能力里第二处 Choose 会拿到第一处的选择。
            bool justResolved = frame != null && frame.ResolvedChoiceIp == frame.Ip;
            if (justResolved)
            {
                if (frame != null) frame.ResolvedChoiceIp = -1;
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
            }
            // 新一轮选择：清掉上一次留下的同槽位结果。
            scope.Clear(slot);

            var pool = _eval.ResolveRef(normalized, scope);
            // 能力选目标时要过滤对手的潜行 / 灵气：
            //   潜行(ambush)：既不能被攻击，也不能被能力选中
            //   灵气(aura)：不能被能力选中（但可以被攻击）
            //   威慑(intimidate)：只阻止攻击，不阻止能力选中
            // 之前这里没有任何检查，敌方法术可以随意点选你的灵气随从。
            var controller = scope.ResolvePlayer(Core.ZoneScope.Self);
            var ambushKw = Schema.Keyword("ambush");
            var auraKw = Schema.Keyword("aura");
            var bsFilter = _world as Battle.BattleState;
            pool = pool.Where(r => {
                var sn = r.Snapshot;
                if (sn == null || sn.Zone != ZoneRef.Board) return true;

                // 土之印 / Clue 护符不能被能力选中（双方都不行）。
                var live = _world.GetLive(sn.EntityId, sn.EntrySerial);
                if (live != null && _world is Battle.BattleState bs && bs.IsProtectedAmulet(live))
                    return false;

                if (sn.PlayerId == controller) return true;   // 己方目标不受限

                // 必须走 HasEffectiveKeyword：潜行/灵气可能是光环授予的，
                // 直接查 Keywords 会让「当本卡在场时，我方随从获得潜行」失效。
                if (bsFilter != null && live != null)
                    return !bsFilter.HasEffectiveKeyword(live, ambushKw)
                        && !bsFilter.HasEffectiveKeyword(live, auraKw);
                return !sn.Keywords.Contains(ambushKw) && !sn.Keywords.Contains(auraKw);
            }).ToList();
            if (pool.Count == 0) return new List<RuntimeEntity>();

            int min = 1;
            int max = spec.Count != null ? _eval.EvalNum(spec.Count, scope) : 1;
            if (max <= 0) max = 1;
            if (max > pool.Count) max = pool.Count;
            if (min > max) min = max;

            // 只有一个候选且只选一个：直接确定，不必打扰玩家
            // 有预选答案排队时**不能**走「单候选自动绑定」：
            // 预扫描阶段是按当时的局面问的（可能有多个候选），
            // 这里若自动绑定就跳过了 Block，答案永远不会被消费，
            // 后续节点会拿到错位的答案。
            // 让它照常 Block 一次，由 StepUntilBlocked 用预选答案当场消解。
            bool hasPreCommit = scope.PreCommittedChoices != null
                                && scope.PreCommittedChoices.Count > 0;

            if (!hasPreCommit && pool.Count == 1 && max == 1)
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

        return _eval.Resolve(normalized, scope);
    }

    // ---------- 选择 ----------

    /// <summary>公开一张牌：只标记可见性，不改数值与区域。</summary>
    /// <summary>
    /// 主动能力里的伤害修正：把这条规则包装成一个挂在目标身上的
    /// PassiveAura 授予能力，由 QueryDamageModifiers 统一扫描。
    ///
    /// 这样「入场曲：直到对手回合结束，使 1 个友方随从受到的伤害 -2」
    /// 与「当本卡在场时，我方主战者受到的伤害 -2」走同一条求值路径，
    /// 不需要在伤害管线里为两种来源各写一遍。
    /// </summary>
    private bool DoDamageModifier(DamageModifierNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;   // 已挂起等玩家选

        foreach (var t in targets)
        {
            RecordEffect(t, scope, n.Kind + " " + n.Amount?.Op);
            var auraAb = new AbilityDef
            {
                Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
                Condition = BoolExpr.True,
                Body = new EffectNode[]
                {
                    new DamageModifierNode
                    {
                        Target = TargetSpec.Self,
                        Kind = n.Kind,
                        SourceMask = n.SourceMask,
                        Amount = n.Amount,
                        IsReduction = n.IsReduction,
                        Duration = n.Duration,
                    },
                },
            };
            _world.GrantAbility(t, auraAb, n.Duration);
        }
        return false;
    }

    private bool DoReveal(RevealCardNode n, VmScope scope)
    {
        foreach (var t in _eval.Resolve(NormalizeZoneWhere(n.Target), scope))
        {
            t.IsRevealed = true;
            // 公开手牌：UI 据此在手牌区播提示光。
            _world.EmitEvent(new VmEvent
            {
                Kind = CoreEvent.OnCardRevealed,
                Subject = EntityRef.From(t),
                PlayerId = t.PlayerId,
            });
        }
        return false;
    }

    /// <summary>
    /// 从卡包 PyAST 读进来的「区域目标」过滤条件，主语必须落在被筛的 $item 上。
    /// 打印器把 &lt;Follower&gt; / other 提权成区域修饰词，解析端用 DefaultSubject
    /// 还原 Where 时以 $self 为默认主语；执行端若照单全收，
    /// 「opponent:board&lt;Follower&gt;」会被解释成「本卡自己是随从」而恒假，
    /// 候选池静默变空。英文编译器在 TargetParser 里写的正是 $item，
    /// 这里在**执行时归一化**，只改本次解析所用的 spec，不写回 AST、不改卡面文本。
    /// </summary>
    internal static Ast.TargetSpec NormalizeZoneWhere(Ast.TargetSpec spec)
    {
        // Zone 与 CardId 都要归一化：`card:#10931110@self:board other history=...`
        // 的 SourceKind 是 CardId，漏掉它整段补丁失效（174 号文档 BUG1 第二重断链）。
        if (spec == null || (spec.SourceKind != Ast.TargetSourceKind.Zone
                          && spec.SourceKind != Ast.TargetSourceKind.CardId)) return spec;
        var w = NormalizeWhereForItem(spec.Where);
        if (ReferenceEquals(w, spec.Where)) return spec;
        return new Ast.TargetSpec
        {
            SourceKind = spec.SourceKind, Owner = spec.Owner, Zone = spec.Zone,
            Slot = spec.Slot, CardId = spec.CardId, CardName = spec.CardName,
            Where = w, Sort = spec.Sort, SortDescending = spec.SortDescending,
            TopN = spec.TopN, DistinctByName = spec.DistinctByName,
            DuplicatesOnly = spec.DuplicatesOnly, Mode = spec.Mode, Count = spec.Count,
            History = spec.History, IncludeLeader = spec.IncludeLeader,
            OmittedFields = spec.OmittedFields,
        };
    }

    private static Ast.BoolExpr NormalizeWhereForItem(Ast.BoolExpr w)
    {
        if (w == null || w is Ast.ConstBoolExpr) return w;
        if (w is Ast.AndExpr and)
        {
            bool changed = false;
            var items = new List<Ast.BoolExpr>();
            foreach (var it in and.Items)
            {
                var n = NormalizeWhereForItem(it);
                changed |= !ReferenceEquals(n, it);
                items.Add(n);
            }
            return changed ? new Ast.AndExpr(items) : w;
        }
        // 只有打印器能提权成区域修饰词的两类谓词会带这个 $self：
        //   · <Follower/Amulet/Spell> -> HasKindExpr
        //   · other                   -> NotSameEntityExpr
        // 其余谓词写在显式 where 里，解析时 _whereDepth>0，主语本来就是 $item，
        // 不能动（HasCardNameExpr.Of=$self 表示「与本卡同名」，是另一种语义）。
        static bool IsSelfOnly(Ast.TargetSpec of)
            => of != null && of.SourceKind == Ast.TargetSourceKind.Slot
                          && of.Slot == Ast.TargetSpec.SlotSelf;
        if (w is Ast.HasKindExpr hk && IsSelfOnly(hk.Of))
            return new Ast.HasKindExpr { Kind = hk.Kind, Of = Ast.TargetSpec.Item };
        if (w is Ast.NotSameEntityExpr ns && IsSelfOnly(ns.Of))
            return new Ast.NotSameEntityExpr { ExcludeSlot = ns.ExcludeSlot, Of = Ast.TargetSpec.Item };
        return w;
    }

    private bool DoSelect(SelectTargetNode n, VmFrame frame)
    {
        // 只有「**本条指令**刚被玩家选完」才放行。
        // 用「槽位非空」判断会串槽：同一能力里第二句 Select 看到第一句留下的
        // $selected 非空，就跳过玩家选择，把第二个效果打在第一个目标上。
        if (frame.ResolvedChoiceIp == frame.Ip)
        {
            frame.ResolvedChoiceIp = -1;
            return false;
        }
        // AppendToSlot 时不清空："Select an allied and enemy follower" 需要连选两次，
        // 第二次必须累加到第一次的结果上，否则只剩最后一个目标。
        if (!n.AppendToSlot) frame.Scope.Clear(n.OutSlot);

        var candidates = _eval.Resolve(NormalizeZoneWhere(n.Target), frame.Scope);

        // 能力选目标必须过滤对手的潜行(ambush)与灵气(aura)。
        // ResolveOrBlock 里已有同样的过滤，但 DoSelect 走的是 _eval.Resolve，
        // 不经过那里，所以这条路径之前对 ambush / aura 完全不设防。
        var controller = frame.Scope.ResolvePlayer(Core.ZoneScope.Self);
        var ambushKw = Schema.Keyword("ambush");
        var auraKw = Schema.Keyword("aura");
        var bsSelK = _world as Battle.BattleState;
        candidates = candidates.Where(c =>
        {
            if (c == null || c.Zone != ZoneRef.Board) return true;
            // 土之印 / Clue 这类「不可点选」的护符与 ResolveOrBlock 保持同一守卫。
            if (_world is Battle.BattleState bsSel && bsSel.IsProtectedAmulet(c)) return false;
            if (c.PlayerId == controller) return true;
            // 同 ResolveOrBlock：光环授予的潜行/灵气也要算数。
            if (bsSelK != null)
                return !bsSelK.HasEffectiveKeyword(c, ambushKw)
                    && !bsSelK.HasEffectiveKeyword(c, auraKw);
            return !c.Keywords.Contains(ambushKw) && !c.Keywords.Contains(auraKw);
        }).ToList();

        if (candidates.Count == 0) return false;   // 没得选就跳过

        if (n.Target.Mode == SelectMode.Choose)
        {
            int min = 1;
            int max = n.Target.Count != null ? _eval.EvalNum(n.Target.Count, frame.Scope) : 1;
            if (max <= 0) max = candidates.Count;
            // 候选不足时必须钳制，否则 PendingChoice 会要求选 3 个而只有 2 个可选。
            if (max > candidates.Count) max = candidates.Count;
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

        // 双写：$choose{K} 单独记本次，$selected 按 AppendToSlot 决定覆盖还是追加。
        frame.Scope.BindSelection(n.OutSlot, candidates.Select(EntityRef.From), n.AppendToSlot);
        return false;
    }

    // ---------- 伤害 / 恢复 ----------

    private bool DoDamage(DealDamageNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), frame.Scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;   // 已挂起
        if (targets.Count == 0) return false;
        int total = _eval.EvalNum(n.Amount, scope);

        // -1 是编译期的「原文省略了数值」哨兵（"Deal damage to X instead"）。
        // 正常情况下 InsteadWeaver.ApplyLens 已经把它换成前一句的伤害量；
        // 走到这里说明该语句没走替代路径，按 0 处理 —— 绝不能真的造成负伤害（回血）。
        if (total < 0) total = 0;

        if (n.IsSplit && targets.Count > 0)
        {
            // 分伤按进场顺序依次分配 min(剩余伤害, 目标当前生命)，直到伤害分完
            // （76-c 第 5 章第 12 条）。
            // 之前用整除平均分：7 点分给 2 个 5 血随从只掉 6 点，
            // 分给 8 个随从则每份 0 点 —— 伤害凭空蒸发。
            // 主战者不是「有生命的随从」，直接吃下全部剩余伤害；
            // 屏障随从照样按分配额度扣减 —— 抵消的是伤害，不是分配。
            int remaining = total;
            foreach (var t in targets)
            {
                if (remaining <= 0) break;
                int share = t.Zone == ZoneRef.Board ? Math.Min(remaining, Math.Max(0, t.Life)) : remaining;
                if (share <= 0) continue;
                remaining -= share;
                ApplyDamage(t, share, n.IsAbility, scope);
            }
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

        // 伤害来源玩家：优先取作用域里的施放者。
        // 硬猜「目标的对手」在 2v2 / 自伤 / 队友增益时全错，
        // 而 scope.Controller 就是这一次能力结算的真正发起方。
        int srcPlayer = scope?.Controller ?? (t.PlayerId == 0 ? 1 : 0);
        // IsAbility 必须贯通到伤害管线："不受战斗伤害" / "不受能力伤害" 等免疫
        // 按来源精确拦截，丢掉它会让两种伤害被同等对待。
        _world.ApplyDamage(t, amount, srcPlayer,
            isAbility ? Ast.DamageSourceMask.Ability : Ast.DamageSourceMask.Combat);
    }

    private bool DoRestore(RestoreLifeNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), frame.Scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;   // 已挂起
        foreach (var t in targets)
        {
            int before = t.Life;
            int amt = n.FullRestore ? t.MaxLife : _eval.EvalNum(n.Amount, scope);
            t.Life = Math.Min(t.MaxLife, t.Life + amt);
            // 事件数值是**实际回复量**：完全恢复时是「缺口」而不是 MaxLife，
            // 否则 4/5 回满会飘 +5（174 号文档 2.2）。
            int healed = Math.Max(0, t.Life - before);
            _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnHealed, Subject = EntityRef.From(t), Amount = healed });
        }

        return false;
    }

    // ---------- 属性 / 费用 ----------

    private bool DoModifyStats(ModifyStatsNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), frame.Scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;   // 已挂起

        foreach (var t in targets)
        {
            RecordEffect(t, scope, n.Op + " " + (n.Attack != null ? "攻" : "") + (n.Life != null ? "血" : ""));
            // 身材变化事件：UI 据此弹跳与飘字。
            // 攻击与生命**两个分量都要带上** —— 只给攻击会让「+2/+2」
            // 显示成「+2」，玩家看不出生命也涨了。
            int da = n.Attack != null ? _eval.EvalNum(n.Attack, scope) : 0;
            int dl = n.Life != null ? _eval.EvalNum(n.Life, scope) : 0;
            _world.EmitEvent(new VmEvent
            {
                Kind = CoreEvent.OnStatsModified,
                Subject = EntityRef.From(t),
                Amount = da,
                SecondaryAmount = dl,
                PlayerId = t.PlayerId,
            });
            // 「不受能力影响」：完全免疫攻防修改。
            // 「不受能力影响（攻防变化除外）」：放行，所以只查前者。
            // 自身能力改自身攻防要放行（IsImmuneToEffectFrom 已处理）。
            if (_world is Battle.BattleState bsStats
                && bsStats.IsImmuneToEffectFrom(t, scope?.Source, isStatMod: true)) continue;

            // 必须在**每个目标上**求值：翻倍这类表达式的属性读取锚点是 $item，
            // 在循环外求值会让所有目标都取第一个候选的身材。
            var itemScope = scope.Derive();
            itemScope.Bind(TargetSpec.SlotItem, new[] { t });

            // null = 不修改该项。绝不能当成 0 ——
            // 「Set 攻击为 4」把生命也赋 0 会让敌方全场随从当场被打死。
            int atk = n.Attack != null ? _eval.EvalNum(n.Attack, itemScope) : 0;
            int life = n.Life != null ? _eval.EvalNum(n.Life, itemScope) : 0;

            // Set 型必须在改之前抓原值快照 —— 撤销时是按原值还原，不是减增量。
            bool isSet = n.Op == StatModOp.Set;
            int prevAtk = isSet && n.Attack != null ? t.Attack : 0;
            int prevLife = isSet && n.Life != null ? t.Life : 0;
            int prevMax = isSet && n.Life != null ? t.MaxLife : 0;

            switch (n.Op)
            {
                case StatModOp.Add:
                    if (n.Attack != null) t.Attack += atk;
                    if (n.Life != null)
                    {
                        t.Life += life;
                        // 身材增减（+X/+Y 或 -X/-Y）**总是**同步增减上限。
                        //
                        // 旧实现在 life < 0 时写 `if (t.Life < t.MaxLife) t.MaxLife = t.Life;`，
                        // 把「受伤」与「减上限」混成一件事：
                        // 5/5 受过 2 点伤变成 3/5，再吃 -0/-1 → 生命 2，
                        // 判定 `2 < 5` 成立 → MaxLife 被砍成 2，
                        // 等于额外多扣了 2 点上限，还抹掉了它的「已受伤」状态。
                        t.MaxLife += life;
                    }
                    break;

                case StatModOp.Set:
                    if (n.Attack != null) t.Attack = atk;
                    if (n.Life != null) { t.Life = life; t.MaxLife = life; }
                    break;

                case StatModOp.Swap:
                    (t.Attack, t.Life) = (t.Life, t.Attack);
                    t.MaxLife = t.Life;
                    break;
            }

            // 限时修正必须登记，否则回合结束时无从撤销（会永久生效）。
            // Set 型登记原值快照（到期还原），Add 型登记增量（到期减回去）。
            if (n.Duration != DurationKind.Permanent)
                t.TempModifiers.Add(new TemporaryModifier
                {
                    Duration = n.Duration,
                    Attack = isSet ? prevAtk : atk,
                    Life = isSet ? prevLife : life,
                    IsStatSet = isSet,
                    SetAttack = isSet && n.Attack != null,
                    SetLife = isSet && n.Life != null,
                    PrevMaxLife = prevMax,
                });
        }
        return false;
    }

    private bool DoModifyCost(ModifyCostNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        // 必须走 ResolveOrBlock：否则带 Choose 的目标会绕过玩家手选，
        // 直接把全场候选都改一遍（174 号文档 2.1）。
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;
        int v = _eval.EvalNum(n.Value, scope);
        foreach (var t in targets)
        {
            RecordEffect(t, scope, n.Op + " 费用");
            int before = t.Cost;
            t.Cost = n.Op switch
            {
                CostModOp.Add => Math.Max(0, t.Cost + v),
                CostModOp.Set => Math.Max(0, v),
                // 费用减半向上取整（76-c 第 5 章第 11 条）：3 费 → 2 费，1 费 → 1 费。
                CostModOp.Halve => Math.Max(0, (t.Cost + 1) / 2),
                _ => t.Cost,
            };

            // 限时改费必须登记到临时修正里，否则到期无从撤销：
            // 之前 Duration 只写进 AST、运行时从不处理，
            // 于是「本回合费用 -1」永久生效，而卡面中文完全正常。
            if (n.Duration != DurationKind.Permanent && t.Cost != before)
            {
                t.TempModifiers.Add(new TemporaryModifier
                {
                    Duration = n.Duration,
                    IsCostMod = true,
                    Cost = t.Cost - before,
                });
            }
        }
        return false;
    }

    private bool DoModifyKeyword(ModifyKeywordNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;
        foreach (var t in targets)
        {
            RecordEffect(t, scope, n.Op + " " + n.Keyword.Name);
            // 关键词变化事件：UI 据此播徽章镶入 / 沉默环。
            int kd = n.Op == KeywordOp.Remove ? -1
                   : n.Op == KeywordOp.SilenceAll ? -99 : 1;
            _world.EmitEvent(new VmEvent
            {
                Kind = CoreEvent.OnKeywordModified,
                Subject = EntityRef.From(t),
                Amount = kd,
                CounterName = n.Keyword.Name,
                PlayerId = t.PlayerId,
            });
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
                    // 与 SilenceAll 同一道免疫闸门：
                    // 少了这一行，「移除守护 / 移除谢幕曲」可以绕过
                    // SilenceAll 的拦截，把免疫随从的词条一个个剥光。
                    if (_world is Battle.BattleState bsRm
                        && bsRm.IsImmuneToEffectFrom(t, scope?.Source)) break;
                    t.Keywords.Remove(n.Keyword);
                    t.KeywordValues.Remove(n.Keyword);
                    // 谢幕曲是一条 AbilityDef（OnDestroy），不是关键词 ——
                    // 光从关键词表里删掉「last words」拦不住它照常触发。
                    if (string.Equals(n.Keyword.Name, "last words", StringComparison.OrdinalIgnoreCase))
                        t.LastWordsRemoved = true;
                    break;
                case KeywordOp.SilenceAll:
                    // 「不受能力影响」与「（攻防变化除外）」都免疫沉默。
                    // 不拦的话，一张沉默就能把免疫词条本身剥掉 —— 自相矛盾。
                    if (_world is Battle.BattleState bsKw
                        && bsKw.IsImmuneToEffectFrom(t, scope?.Source)) break;
                    t.Keywords.Clear();
                    t.KeywordValues.Clear();
                    t.IsSilenced = true;
                    // 沉默同样要清掉挂上去的授予能力，否则它们还能继续触发。
                    if (_world is Battle.BattleState bsS) bsS.ClearGrantedAbilities(t);
                    break;
            }
        }
        return false;
    }

    private bool DoModifyCounter(ModifyCounterNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        int delta = _eval.EvalNum(n.Delta, scope);

        // Target != null：修改每个目标实体自己的计数器
        if (n.Target != null)
        {
            var ents = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
            if (ents == null) return true;
            foreach (var e in ents)
            {
                int curE = _world.ReadEntityCounter(e, n.Counter);
                int nextE = n.Op switch
                {
                    CounterOp.Add => curE + delta,
                    CounterOp.Set => delta,
                    // Fill = 填到至少 delta（不足才补，已超过不动），不是 Set。
                    CounterOp.Fill => Math.Max(curE, delta),
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

                // 吟唱被能力推进到 0：**当场**到期。
                //
                // 否则会变成不死钉子户：回合初的 StepCrestArea / StepBoardCountdown
                // 第一行是 `if (... cd <= 0) continue;`，已经归 0 的实体
                // 会被直接跳过 —— 既不当场触发谢幕曲，下回合也不会被回收。
                // curE 可能本来就是 0（这张护符没有吟唱），推进后变负；
                // 只有「从正数跨到 <= 0」才算真的到期，否则无吟唱的护符会被误杀。
                if (n.Counter.Name == "countdown" && curE > 0 && nextE <= 0)
                {
                    _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnCountdownExpire, Subject = EntityRef.From(e) });
                    if (e.Zone == ZoneRef.Board) _world.DestroyEntity(e);
                    else if (e.Zone == ZoneRef.CrestArea && _world is Battle.BattleState bsCrest)
                        bsCrest.ExpireCrest(e);
                }
            }
            return false;
        }

        // Target == null：默认修改主战者级计数器。
        //
        // 例外：X/Y/Z 是「单卡变量」—— 初值由 MountInnateProperties 写在实体上，
        // 读取侧（VmEval.NumOp.Counter）也优先读实体计数器。写侧必须与读侧对称，
        // 否则增量写进玩家级、能力永远读不到（7 张 ctr:X 卡因此全废）。
        // 旧编译产物（pack.json）把这两个节点编译成 Target=null，这里兜底。
        if (n.Counter.Name is "X" or "Y" or "Z")
        {
            var srcLive = scope.Source.HasValue
                ? _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial)
                : null;
            if (srcLive != null)
            {
                int curE = srcLive.Counters.TryGetValue(n.Counter, out var cv) ? cv : 0;
                int nextE = n.Op switch
                {
                    CounterOp.Add => curE + delta,
                    CounterOp.Set => delta,
                    // Fill = 填到至少 delta（不足才补，已超过不动），不是 Set。
                    CounterOp.Fill => Math.Max(curE, delta),
                    _ => curE,
                };
                srcLive.Counters[n.Counter] = nextE;
                _world.EmitEvent(new VmEvent
                {
                    Kind = CoreEvent.OnCounterChange,
                    Amount = delta,
                    CounterName = n.Counter.Name,
                    PlayerId = srcLive.PlayerId,
                });
                return false;
            }
        }

        int cur = _world.ReadCounter(scope.ResolvePlayer(n.Owner), n.Counter);
        int next = n.Op switch
        {
            CounterOp.Add => cur + delta,
            CounterOp.Set => delta,
            // Fill = 填到至少 delta，不是 Set。
            CounterOp.Fill => Math.Max(cur, delta),
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

        // 土之秘术的「消耗土之印」是独立的规则事件，
        // 与「吟唱归零」完全是两件事：
        //   · 前者是玩家主动消耗，要触发 OnEarthRite；
        //   · 后者是护符倒计时自然归零，触发 OnCountdownExpire。
        // 不在这里发的话，「每当发动土之秘术时」这类能力永远不会响应。
        if (n.Counter.Name == "earth_sigil" && delta < 0)
        {
            var srcLive = scope.Source.HasValue
                ? _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial)
                : null;
            _world.EmitEvent(new VmEvent
            {
                Kind = CoreEvent.OnEarthRite,
                PlayerId = owner,
                Subject = srcLive != null ? EntityRef.From(srcLive) : scope.Source,
            });
        }
        return false;
    }

    /// <summary>
    /// 这个 MoveZone 是不是「普通抽牌」（牌库顶 → 手牌）。
    ///
    /// 判据必须全部成立，缺一条就不是抽牌：
    ///   · 目标来自区域枚举（不是槽位 / 具名卡）
    ///   · 来源区 = Deck、目的地 = Hand
    ///   · Mode = First（牌库顶那一张；Random / Choose / All 都是「检索」）
    ///   · 没有 Where 过滤、没有排序、没有历史池、没有 TopN / 同名校验
    ///
    /// 带过滤的「从牌库搜索一张牌加手牌」卡面同样被编译成
    /// MoveZone(Deck → Hand)，但它**不是**抽牌：不能发 OnDraw，
    /// 更不能因为牌库里没搜到就判 DECK_OUT 负。
    /// </summary>
    private static bool IsPlainDraw(MoveZoneNode n)
    {
        var t = n.Target;
        if (t == null) return false;
        if (t.SourceKind != TargetSourceKind.Zone) return false;
        if (t.Zone != ZoneRef.Deck || n.ToZone != ZoneRef.Hand) return false;
        if (t.Mode != SelectMode.First) return false;
        if (t.History != HistoryScope.None) return false;
        if (t.Sort != SortKey.None || t.SortDescending || t.TopN != 0) return false;
        if (t.DistinctByName || t.CardId != 0 || !string.IsNullOrEmpty(t.CardName)) return false;
        if (t.Where != null && !(t.Where is ConstBoolExpr cb && cb.Value)) return false;
        return true;
    }

    /// <summary>
    /// 抽牌走世界的统一管线（OnDraw / 牌库抽空 / 爆牌全部由实现层负责）。
    /// 返回抽到的实体，供 $created 使用。
    /// </summary>
    private List<RuntimeEntity> DrawThroughPipeline(MoveZoneNode n, VmScope scope)
    {
        int count = Math.Max(0, _eval.EvalNum(n.Target.Count, scope));
        // owner 为 Any 时无法落到单一玩家：只能按施放者抽，绝不猜成「双方各抽」。
        int playerId = scope.ResolvePlayer(n.Target.Owner);
        if (playerId is < 0 or > 1) playerId = scope.Controller;
        if (count == 0) return new List<RuntimeEntity>();

        // 记录抽牌前的牌库，抽完取差集就是「这次真的抽到了哪些」。
        // 不能读事件：OnDraw 只进 EventLog，VM 侧拿不到本次的抽牌结果。
        var before = new HashSet<int>(_world.EnumerateZone(ZoneRef.Deck, playerId)
                                            .Select(e => e.EntityId));
        _world.DrawCards(playerId, count);
        return _world.EnumerateZone(ZoneRef.Hand, playerId)
                     .Where(e => !before.Contains(e.EntityId))
                     .ToList();
    }

    private bool DoMoveZone(MoveZoneNode n, VmFrame frame)
    {
        var scope = frame.Scope;

        // 「不受能力影响」：能力移区（回手 / 洗回牌组 / 放逐 / 破坏）一律无效。
        // 破坏也在这里拦：DestroyEntity 虽然也拦，但本方法有独立的
        // 执行分支，两条路径都要守住，否则一条漏了就等于没做。
        {
            // 逐个目标过滤，不能整批 All() 判定 ——
            // 「把 3 个敌方随从弹回手牌」里只要有一个不免疫，
            // All() 就为假，于是免疫的那个也被一起弹走了。
            var mvTargets = _eval.Resolve(NormalizeZoneWhere(n.Target), scope);
            var kept = new List<RuntimeEntity>();
            foreach (var t0 in mvTargets)
            {
                if (_world is Battle.BattleState bsMv2
                    && bsMv2.IsImmuneToEffectFrom(t0, scope?.Source))
                    continue;
                kept.Add(t0);
            }
            if (mvTargets.Count > 0 && kept.Count == 0) return false;
        }
        // 能力抽牌必须走统一管线：自己 MoveEntity 会绕开 OnDraw 与抽空判负。
        if (IsPlainDraw(n))
        {
            scope.Bind(n.OutSlot, DrawThroughPipeline(n, scope));
            return false;
        }

        var targets = ResolveOrBlock(n.Target, frame.Scope, n.OutSlot, frame);
        if (targets == null) return true;   // 已挂起等玩家选

        // 免疫目标在这里真正剔除 —— 上面的计数器只是「全免疫就整句跳过」的短路。
        {
            var bsTgt = _world as Battle.BattleState;
            if (bsTgt != null)
                targets = targets.Where(x => !bsTgt.IsImmuneToEffectFrom(x, scope?.Source)).ToList();
        }

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
                bool fromBoard = t.Zone == ZoneRef.Board;

                // 「X is the number of cards you returned」的计数写入点。
                // cards_returned_to_hand 的写入点在循环**之后**统一做（见下），
                // 这里不再逐个累加：
                //   "Do this 2 times: 'Return your hand to deck. Draw X cards.
                //    X is the number of cards you returned.'"
                // 累加会让第二轮读到第一轮的余数（5 → 10），一次抽 10 张。
                bool isBanish = n.ToZone == ZoneRef.Banished;
                var bsBanish = _world as Battle.BattleState;

                if (isBanish && bsBanish != null)
                {
                    // 放逐必须走 BanishEntity：它内部统一 MoveEntity + 发 OnBanish。
                    // 之前这里只调 MoveEntity，于是「当这张卡被放逐时」永远不触发，
                    // 「每当有卡被放逐」也全场失明。
                    bsBanish.BanishEntity(t);
                }
                else
                {
                    // OnLeaveField 由 BattleState.MoveEntity 在 leftBoard 时统一发出。
                    // 这里再手发一次会让每次能力移区都触发两遍「离场时」。
                    // 只有 MemoryWorld（自测桩）不替我们发，才由这里补。
                    if (fromBoard && n.ToZone != ZoneRef.Board && bsBanish == null)
                        _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(t) });

                    _world.MoveEntity(t, n.ToZone, t.PlayerId);

                    // MemoryWorld 没有 BanishEntity，放逐事件在这里补发。
                    if (isBanish && bsBanish == null)
                        _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnBanish, Subject = EntityRef.From(t) });
                }

                // 回手 → 卡面重置为印刷值：手牌里的卡不是「场上的那个随从」。
                // 之前回手只换区，5/1 已进化 MaxLife=4 的随从会带着全部临时状态
                // 回到手里再打出来，进化加成被白拿两次。
                if (fromBoard && n.ToZone == ZoneRef.Hand) ResetToPrintedValues(t);

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

        // 「X is the number of cards you returned」的计数：**按本次实际移动数覆写**。
        //
        // 必须覆写而非累加：Do this N times 里每一轮都要重新统计本轮退回的张数，
        // 累加会让第二轮读到第一轮的余数（5 → 10），一次抽 10 张。
        // 覆盖两条去向：回牌库（Shakdoh）与回手牌（"…return them to your hand"）。
        if (n.ToZone == ZoneRef.Deck
            || (n.ToZone == ZoneRef.Hand && !IsPlainDraw(n)))
        {
            var retKey = Schema.Counter("cards_returned_to_hand");
            _world.WriteCounter(scope.ResolvePlayer(ZoneScope.Self), retKey, moved.Count);
        }

        // 「X is the number of ... destroyed/banished/discarded **by this ability**」
        // 这类变量读的是**本次动作实际移动的张数**，不是全局累计。
        // 三个计数器分开写：一张卡可能既放逐又丢弃，共用一个槽会互相覆盖。
        {
            int pid = scope.ResolvePlayer(ZoneScope.Self);
            var byAbility = Schema.Counter("last_moved_count");
            _world.WriteCounter(pid, byAbility, moved.Count);

            if (n.ToZone == ZoneRef.Cemetery && n.IsDestroy)
                _world.WriteCounter(pid, Schema.Counter("cards_destroyed_by_ability"), moved.Count);
            else if (n.ToZone == ZoneRef.Cemetery && n.Target?.Zone == ZoneRef.Hand)
                // 「Discard all Clue amulets」：从手牌进墓地且不是 destroy，就是丢弃。
                _world.WriteCounter(pid, Schema.Counter("cards_discarded_by_ability"), moved.Count);
            if (n.ToZone == ZoneRef.Banished)
                _world.WriteCounter(pid, Schema.Counter("cards_banished_by_ability"), moved.Count);
        }

        // $created 只应记录「进入场上/手牌」的新对象。
        // 送去墓地或放逐时若也写入，后续的 "it" 会指向一具尸体，
        // 导致 "destroy it, then banish it" 里第二句指向错误。
        if (n.ToZone == ZoneRef.Board || n.ToZone == ZoneRef.Hand || n.ToZone == ZoneRef.ExArea)
            scope.Bind(n.OutSlot, moved);
        return false;
    }

    /// <summary>
    /// 从战场回到手牌 / 牌组等非战场区域时，把实体还原成卡面印刷值。
    /// 官方铁律：手牌里的卡永远是「这张卡」，不是「场上那个随从」——
    /// 攻击力、生命、费用、进化状态、限时修正一律回到印刷状态。
    /// </summary>
    private void ResetToPrintedValues(RuntimeEntity t)
    {
        // 激奏 / 结晶打出的卡，在手牌里必须显示**本体**卡面。
        // OriginalDef 是打出时记下的本体定义，回手 / 回收时据此还原。
        if (t.OriginalDef != null)
        {
            t.Def = t.OriginalDef;
            t.OriginalDef = null;
        }
        // 回手洗白时一并清掉离场时的旧定义：
        // 那张卡已经回到手牌、重新成为「一张普通的手牌」，
        // 再留着 LastFieldDef 会让它在下次离场时拿旧卡面的能力去响应事件。
        t.LastFieldDef = null;

        // 旧的召唤引用同样不能留：回手后再上场是另一条生命周期，
        // 残留的 $created 会让 Super-Evolve 的 them 指向上一次对局里的实体。
        t.LastCreatedRefs.Clear();

        // 回到手牌就是「一张普通手牌」：之前受过什么影响不该继续挂着，
        // 否则溯源面板会显示早已失效的来源。
        t.EffectHistory.Clear();

        var def = t.Def;
        t.Attack = def?.BaseAttack ?? t.Attack;
        t.Life = def?.BaseLife ?? t.Life;
        t.MaxLife = t.Life;
        t.Cost = def?.BaseCost ?? t.Cost;
        t.IsEvolved = false;
        t.IsSuperEvolved = false;
        t.IsSilenced = false;
        t.TempModifiers.Clear();

        // 回手要「洗白为印刷值」：临时获得的关键词、带参关键词的数值、
        // 临时计数器全部清掉，再按卡面重新装配常驻部分。
        // 之前只清了数值，于是回手再打出可以白拿一次守护。
        t.Keywords.Clear();
        t.KeywordValues.Clear();
        t.Counters.Clear();
        if (def == null) return;

        // 回手也清空一切已获得的授予能力与「谢幕曲已移除」标记（76-c 第 5 章）。
        if (_world is Battle.BattleState bsG)
        {
            bsG.ClearGrantedAbilities(t);
            bsG.MountInnateProperties(t);
        }
        t.LastWordsRemoved = false;
        // 兵种、裸关键词、初始计数器、PassiveAura 装配、Countdown ——
        // 与 CreateEntity / TransformEntity 共用同一个装配点，
        // 三处各写一份必然漂移（回手后关键词没了、吟唱没恢复都是这么来的）。
    }

    /// <summary>
    /// 第 5 步：亡者召还上场的随从**额外附加 Departed（死者）兵种标签**。
    ///
    /// GameSchema.Trait 是 GetOrRegister，首次调用即完成注册，
    /// 所以不需要在 Lexicon / GameSchema 里预先声明这个兵种。
    /// </summary>
    private void TagDeparted(RuntimeEntity e)
    {
        var schema = (_world as Battle.BattleState)?.Schema ?? Schema;
        var departed = schema.Trait("亡者");
        if (!e.Traits.Contains(departed)) e.Traits.Add(departed);
    }

    private bool DoSpawn(SpawnCardNode n, VmScope scope)
    {
        // 只有 Count == null 才默认 1；写了动态表达式算出 0 就是 0 个，
        // 不能被 Math.Max(1, …) 强行变成 1 个（174 号文档 四.9）。
        int count = n.Count != null ? _eval.EvalNum(n.Count, scope) : 1;
        if (count <= 0) return false;
        var created = new List<RuntimeEntity>();

        // 卡池检索 / 发现必须在循环外一次性处理：
        // 放进 for 里会变成「先挂起选 1 张」，Y>1 时第二张永远轮不到。
        if (n.SourceKind == CardSourceKind.PoolQuery)
        {
            SpawnFromPool(n, scope, count, created);
            if (Pending != null) return true;      // 已挂起等玩家从 X 张里选 Y 张
            scope.Bind(n.OutSlot, created);
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            RuntimeEntity e = null;
            if (n.SourceKind == CardSourceKind.CopyOfTarget)
            {
                var refs = _eval.ResolveRef(n.CopyFrom, scope);
                if (refs.Count == 0) break;

                // "summon an exact copy of each" —— 必须遍历 refs 全部复制，
                // 只取 refs[0] 会让「各自复制一份」变成「只复制第一个」。
                int owner2 = scope.ResolvePlayer(n.ToOwner);
                int times = n.Count != null ? _eval.EvalNum(n.Count, scope) : 1;

                // "an exact copy of each" 在编译期把 Count 设为 1，靠遍历 refs
                // 每个生成 1 份；"summon 2 exact copies of this card" 的 Count=2、
                // refs.Count=1，则复制 2 份。所以直接按 times 即可（174 号文档 2.4）。
                int perRef = times;
                var made = new List<RuntimeEntity>();
                foreach (var r in refs)
                {
                    for (int k = 0; k < perRef; k++)
                    {
                        var one = _world is Battle.BattleState bs2
                            // 直接传 addDepartedTrait：让 SpawnCopy 在拍 EnteredThisMatch 快照**之前**
                            // 就带上亡者兵种，而不是等返回后再补 —— 补的话快照里永远没有亡者。
                            ? bs2.SpawnCopy(r, n.ToZone, owner2, n.IsExactCopy, n.AddDepartedTrait)
                            : _world.SpawnCopy(r, n.ToZone, owner2,
                                               addDepartedTrait: n.AddDepartedTrait);
                        if (one != null) made.Add(one);
                    }
                }
                // 内层的 perRef 已经把「该复制几份」全部处理完了，
                // 外层这个 for (i < count) 必须立刻跳出：
                //   10333110 "summon 2 exact copies of $self"（count=2, refs.Count=1）
                //   若继续跑外层，就是 2 次 × 2 份 = 4 个复制体。
                // 同时必须直接 break —— 不能再落到底部的 created.Add(e)，
                // 否则 made[0] 会被加第 2 次（B 组 10172320 会给第 1 个复制体贴两遍谢幕曲）。
                foreach (var m in made) created.Add(m);
                e = null;
                break;
            }
            else if (n.SourceKind == CardSourceKind.CopyOfPrevious)
            {
                // "Summon a Megalorca. If you're in Overflow, summon 2 instead."
                // 这句没有卡名，宾语就是上一句召唤的那张卡。
                // 之前这里没有任何分支，SpawnCard 收到 CardId=0，产出空实体。
                int prevId = scope.LastSpawnedCardId;
                // scope 里没有时回退到实体上的持久记录（跨时机第二句跑在新 scope 里）。
                if (prevId <= 0 && scope.Source.HasValue)
                {
                    var srcLive0 = _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial);
                    if (srcLive0 != null) prevId = srcLive0.LastSpawnedCardId;
                }
                if (prevId <= 0) break;
                e = _world.SpawnCard(prevId, n.ToZone, scope.ResolvePlayer(n.ToOwner));
            }
            else if (n.IsCrest)
            {
                // 纹章走自己的集成点：同名唯一与区域上限都在 GainCrest 里处理，
                // 而且失败时安静返回 null（不进墓地、不触发事件）。
                //
                // 必须把主人卡内嵌的 CrestDef 一并传下去：那才是带 Abilities 的定义。
                // 只传名字的话，GainCrest 只能现造空壳，纹章于是「拿得到但没有能力」。
                if (_world is Battle.BattleState bsc)
                {
                    // "Give yourself and your opponent Crest: Corruption"（10453310）
                    // 解析出的 ToOwner 是 Any —— 必须给**双方各**发一次。
                    // 早先只按 ResolvePlayer(Any) 发一次（落到自己），对手拿不到纹章。
                    if (n.ToOwner == ZoneScope.Any)
                    {
                        for (int pid = 0; pid < 2; pid++)
                        {
                            var one = bsc.GainCrest(n.CardId, n.CardName, pid, n.CrestDef);
                            if (one != null) created.Add(one);
                        }
                        e = created.Count > 0 ? created[^1] : null;
                    }
                    else
                    {
                        e = bsc.GainCrest(n.CardId, n.CardName, scope.ResolvePlayer(n.ToOwner), n.CrestDef);
                    }
                }
                else e = null;
            }
            else
            {
                e = _world.SpawnCard(n.CardId, n.ToZone, scope.ResolvePlayer(n.ToOwner));
                if (e != null)
                {
                    scope.LastSpawnedCardId = n.CardId;
                    if (scope.Source.HasValue)
                    {
                        var srcLive1 = _world.GetLive(scope.Source.Value.EntityId, scope.Source.Value.EntrySerial);
                        if (srcLive1 != null) srcLive1.LastSpawnedCardId = n.CardId;
                    }
                }
            }
            if (e != null) created.Add(e);
        }
        // ---- $created 的写入策略 ----
        //
        // 连续两个 SpawnCardNode 来自同一个句子：
        //   "Summon an Enhanced Puppet and Puppet."
        //   "Enhance (7): Give them Storm."
        // 第二个会覆盖第一个的 $created，于是 Give them 只作用到第二只。
        //
        // 判据：上一条指令也是 SpawnCardNode。此时**追加**而不是覆盖。
        var frame = _frames?.Count > 0 ? _frames[^1] : null;
        bool consecutive = frame != null && frame.Ip > 0
                           && frame.Ip - 1 < frame.Code.Count
                           && frame.Code[frame.Ip - 1] is SpawnCardNode;

        if (consecutive)
        {
            var merged = new List<RuntimeEntity>(scope.Read(n.OutSlot).Count);
            foreach (var r in scope.Read(n.OutSlot))
            {
                var prev = _world.GetLive(r.EntityId, r.EntrySerial);
                if (prev != null) merged.Add(prev);
            }
            merged.AddRange(created);
            scope.Bind(n.OutSlot, merged);
        }
        else
        {
            scope.Bind(n.OutSlot, created);
        }

        // 无论单次还是连续召唤，都把当前 $created 槽位同步到源实体上。
        //
        // 早先这段只写在 else 分支里，于是「Summon A and B」的第二只
        // 不会进 LastCreatedRefs —— 后续 "Give them Storm" 少喂一只。
        // 跨能力域的 them/it 回指（Evolve 召唤 -> Super-Evolve 强化）也依赖它。
        if (scope.Source != null && n.OutSlot == TargetSpec.SlotCreated)
        {
            var srcRef = scope.Source.Value;
            var carrier = _world.GetLive(srcRef.EntityId, srcRef.EntrySerial);
            if (carrier != null)
            {
                carrier.LastCreatedRefs.Clear();
                carrier.LastCreatedRefs.AddRange(scope.Read(n.OutSlot));
            }
        }

        // 往牌组生成卡牌后必须洗牌：
        // 「时空掌握者」进化时往牌组洗入 10 张，不洗就全垫在牌库底端。
        if (n.ToZone == ZoneRef.Deck && created.Count > 0 && _world is Battle.BattleState bsDeck)
            bsDeck.ShuffleZone(ZoneRef.Deck, scope.ResolvePlayer(n.ToOwner));

        return false;
    }

    /// <summary>
    /// 整副牌库替换："Replace your deck with the Apocalypse Deck"。
    ///
    /// 牌组名必须能在 DeckDefTable 里解析；解析不到就抛错。
    /// 这里不静默装空牌库 —— 空牌库会让玩家下一回合直接 DECK_OUT 判负，
    /// 一个"看起来成功"的失败比报错危险得多。
    /// </summary>
    /// <summary>
    /// 把目标的牌库标记为「含胜利的卡牌」。
    ///
    /// 影之诗规则：牌库抽空时判负；若牌库里是胜利的卡牌，该次抽牌改判胜利。
    /// 这不是「把某张卡改成另一张真实卡牌」——胜利卡没有卡号也没有卡面，
    /// 它只是玩家身上的一个状态位，所以不能走 DoTransform。
    /// </summary>
    private bool DoMarkVictoryCard(MarkVictoryCardNode n, VmScope scope)
    {
        int pid = scope.ResolvePlayer(n.Owner);
        _world.SetVictoryCard(pid);
        return false;
    }

    private bool DoReplaceDeck(ReplaceDeckNode n, VmScope scope)
    {
        int pid = scope.ResolvePlayer(n.Owner);
        var cardIds = DeckDefs.Expand(n.DeckName);
        if (cardIds == null)
            throw new VmFaultException("未定义的具名牌组: [" + n.DeckName + "]");

        _world.ReplaceDeck(pid, n.DeckName, cardIds);
        return false;
    }

    private bool DoEvolve(EvolveNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;
        foreach (var t in targets)
        {
            RecordEffect(t, scope, "进化");
            // 已经进化过的随从不再进化：重复执行 "Evolve this follower"
            // 会无限叠属性（+2/+2 每跑一遍加一次）。
            if (t.IsEvolved) continue;

            // 进化加成与手动进化完全一致（TurnRunner.Evolve）：
            // 普通 +2/+2、超进化 +3/+3，Attack / Life / MaxLife 三者同步。
            // 之前这里只翻标志不加属性 —— 卡面写着 Evolve 却还是 2/2。
            // 自动进化与手动进化必须用同一套加成 ——
            // 但加成值本身要能从卡池规则读，不能写死 3/2。
            var rules = (_world as Battle.BattleState)?.Rules ?? MatchRules.CreateStandard();
            int deltaAtk = n.IsSuperEvolve ? rules.SuperEvolveBonusAttack : rules.EvolveBonusAttack;
            int deltaLife = n.IsSuperEvolve ? rules.SuperEvolveBonusLife : rules.EvolveBonusLife;
            int delta = deltaAtk;
            t.Attack += deltaAtk;
            t.Life += deltaLife;
            t.MaxLife += deltaLife;

            if (n.IsSuperEvolve) { t.IsSuperEvolved = true; t.IsEvolved = true; } else t.IsEvolved = true;

            // 奥义槽：与手动进化同一套逻辑（TurnRunner.BumpSkyboundGauge），
            // 不另写一份 —— 两份实现必然漂移。
            // 规则原文「本卡在手时我方随从进化的次数」，自动进化同样计入。
            Battle.TurnRunner.BumpSkyboundGauge(_world, Schema, t.PlayerId);

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

        // 非槽位来源的进化目标要把实际进化到的实体写进 $selected，
        // 后续 "... and give it +1/+1" 才接得上（10564110 索菲娜）。
        // 槽位来源的目标（"evolve them"）不写 —— 那是消费者，不是生产者。
        //
        // 这段曾误粘在 DoRestore 末尾：那会把 $selected 覆写成主战者，
        // 让「先选敌方随从 → 回血 → 对它造成伤害」反手打自己脸上。
        if (n.Target != null && n.Target.SourceKind != TargetSourceKind.Slot && targets.Count > 0)
            scope.Bind(TargetSpec.SlotSelected, targets);

        return false;
    }

    private bool DoTransform(TransformNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;
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
                if (intoId > 0)
                {
                    _world.TransformEntity(targets[i], intoId);
                    // 变身成「复制集合」时要连实时状态一起拷：攻防、进化、关键词。
                    // 只换卡号会变成白板印刷属性（174 号文档 2.3）。
                    var srcLive = _world.GetLive(src.EntityId, src.EntrySerial);
                    if (srcLive != null)
                    {
                        targets[i].Attack = srcLive.Attack;
                        targets[i].Life = srcLive.Life;
                        targets[i].MaxLife = srcLive.MaxLife;
                        targets[i].IsEvolved = srcLive.IsEvolved;
                        targets[i].IsSuperEvolved = srcLive.IsSuperEvolved;
                        targets[i].Keywords.Clear();
                        foreach (var k in srcLive.Keywords) targets[i].Keywords.Add(k);
                    }
                }
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
    /// <summary>
    /// "未激活" 状态位掩码的计数器键。
    /// 位 i 置位 = 选项 i 已被**这个源实体**激活过。
    /// 放在实体计数器上（不是玩家级计数器）是刻意的：
    /// 场上同时有两张 Slaus 时，各自的激活进度互不干扰。
    /// </summary>
    /// 注意：**不能**存成 static readonly 缓存 —— SymbolId 的 Id 由 GameSchema 分配，
    /// 而世界侧的 GameSchema 与编译期 Lexicon 的 Schema 是两个实例，
    /// 缓存下来的 Id 到了世界里会指到别的计数器上（实测写进去的位掩码读不回来）。
    /// 必须以 CardVm.Schema（运行时由对战层注入的那一份）为准，每次现查。
    private static SymbolId<CounterTag> ActivationMaskKey => Schema.Counter("mode_activated_mask");

    /// <summary>
    /// 解析「未激活」状态的持有者 —— 即发动这条能力的那个实体。
    ///
    /// 取 Source 优先、Trigger 兜底：能力由卡自己印制，Source 就是它；
    /// 而 "Whenever an allied amulet is destroyed" 这类事件里 Trigger 是那个护符，
    /// 不能拿它当持有者，否则进度会记到别人的头上。
    /// 两者都没有实体（纯玩家级效果）时返回 null，调用方退化为「每次都算未激活」。
    /// </summary>
    private RuntimeEntity ResolveActivationOwner(VmScope scope)
    {
        var s = scope?.Source;
        var live = s.HasValue ? _world.GetLive(s.Value.EntityId, s.Value.EntrySerial) : null;
        if (live != null) return live;
        var t = scope?.Trigger;
        return t.HasValue ? _world.GetLive(t.Value.EntityId, t.Value.EntrySerial) : null;
    }

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
                // 帧栈是后进先出，必须倒序压入才能按「从旧到新」正序结算（174 号文档 四.10）。
                var list = items.Take(max).ToList();
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var s = scope.Derive();
                    s.Bind(TargetSpec.SlotItem, new[] { list[i] });
                    PushBody(n.Body, s);
                }
                return false;
            }
            case LoopKind.RepeatUntil:
            {
                // "Repeat until <条件>"：先跑一次，条件不成立就再跑。
                //
                // 用「条件节点 + 自引用」表达，而不是在这里写 C# 循环：
                // 循环体里的每个原子节点都可能挂起等玩家输入（Choose 目标），
                // 一旦挂起就必须能从中途恢复 —— 那是帧栈的职责，
                // 在 C# 里 for/while 一圈圈跑会把挂起状态丢掉。
                //
                // 终止性由 max（MaxIterations 的夹取值）保证：
                // 每进入一次就少一次机会，到 0 就不再递归。
                int budget = _repeatBudgets.TryGetValue(n, out var b) ? b : max;
                if (budget <= 0) { _repeatBudgets.Remove(n); return false; }

                // 条件已成立：停，并清掉本节点的预算记录。
                if (_eval.EvalBool(n.Until, scope)) { _repeatBudgets.Remove(n); return false; }

                _repeatBudgets[n] = budget - 1;

                // 跑一轮 Body，**并把 n 自己接在末尾** —— 这才构成帧栈级的尾递归。
                //
                // 只压 n.Body 是不够的：DoLoop 返回后主循环会 frame.Ip++ 跨过当前
                // LoopNode，而栈顶的 body 跑完就再也不会回到这里 ——
                // 结果是 Body 只执行 1 次循环就结束，且 _repeatBudgets 永久残留。
                var pass = new List<EffectNode>(n.Body) { n };
                PushBody(pass, scope.Derive());
                return false;
            }
            default:
                throw new VmFaultException("未知循环类型：" + n.Kind);
        }
    }

    /// <summary>RepeatUntil 的剩余次数（按节点实例记账，跨挂起可恢复）。</summary>
    private readonly Dictionary<LoopNode, int> _repeatBudgets = new();

    private bool DoBranch(BranchModeNode n, VmFrame frame)
    {
        var opts = n.Options;
        if (opts == null || opts.Count == 0) return false;
        // 下限允许 0：PickCount 可能是动态值（"发动 Y 个，Y 为此卡费用"），
        // 0 费时钳到 1 会凭空多发动一个能力。
        int pickRaw = _eval.EvalNum(n.PickCount, frame.Scope);
        int pick = Math.Clamp(pickRaw, 0, opts.Count);
        if (pick <= 0) return false;

        // 「Increase the number of Modes you can select by 1」的光环。
        // mode_count 早就在卡面上编译出来了（ActionParser:177），但运行时从没人读。
        // 只对 Choose 生效：Random / Sequence 的取数由卡面自己写死，不是玩家可选数。
        if (n.Kind == ModeDispatchKind.Choose)
        {
            var bonusKey = Schema.Counter("mode_count");
            int bonus = _world.ReadCounter(frame.Scope.ResolvePlayer(frame.Scope.Owner), bonusKey);
            if (bonus > 0) pick = Math.Clamp(pick + bonus, 1, opts.Count);
        }

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
                // 「按顺序发动以下 N 个能力」：只取前 N 项按顺序执行。
                // pick == 选项总数时即「Activate all of them instead」，此时全部执行；
                // pick < 总数时（官方卡面「按顺序发动以下1个能力」）只执行前 pick 项。
                // 早先无条件压入全部选项，导致 pick=1 的卡把 3 项全跑了。
                for (int i = pick - 1; i >= 0; i--)
                    PushBody(opts[i], DeriveForBranch(n, frame.Scope));
                return false;

            case ModeDispatchKind.RandomUnused:
            case ModeDispatchKind.SequenceUnused:
            {
                // 「随机 / 按序取一个**尚未激活过**的」。
                // 候选池 = 还没被这个源实体激活过的选项下标。
                // 全部激活过时**什么也不做**（不崩、不重来），这是卡面语义：
                // Slaus 只有 3 项，第 4 个回合没有可激活的了。
                var live = ResolveActivationOwner(frame.Scope);
                var unused = new List<int>();
                for (int i = 0; i < opts.Count; i++)
                {
                    int bit = 1 << i;
                    if (live != null && (_world.ReadEntityCounter(live, ActivationMaskKey) & bit) != 0) continue;
                    unused.Add(i);
                }
                if (unused.Count == 0) return false;

                var chosen = n.Kind == ModeDispatchKind.RandomUnused
                    ? unused.OrderBy(_ => _world.Rng.Next()).Take(pick).ToList()
                    : unused.Take(pick).ToList();

                // 先记账再执行：选项本身可能再触发同一能力（"Destroy this crest"），
                // 后置记账会让它从头再来一遍。
                if (live != null)
                {
                    int mask = _world.ReadEntityCounter(live, ActivationMaskKey);
                    foreach (var i in chosen) mask |= 1 << i;
                    _world.WriteEntityCounter(live, ActivationMaskKey, mask);
                }

                for (int k = chosen.Count - 1; k >= 0; k--)
                    PushBody(opts[chosen[k]], DeriveForBranch(n, frame.Scope));
                return false;
            }

            default:
            {
                // Choose：挂起等玩家。
                //
                // MinSelect 必须等于 MaxSelect：
                //   「Select 2 Modes to activate」要求选满 2 个，
                //   写死 1 会让默认策略只选 1 个，第 2 个模式永远不生效。
                //
                // mode_count 光环已经在上面统一并进 pick 了（本方法开头的 Choose 分支），
                // 这里**绝不能**再读一次 —— 重复累加会让杉那查的 +1 变成 +2。
                // MinSelect 必须等于 MaxSelect：「Select 2 Modes」要求选满 2 个。
                return Block(new PendingChoice
                {
                    Player = frame.Scope.Owner,
                    Kind = ChoiceKind.SelectMode,
                    Candidates = Enumerable.Range(0, opts.Count).ToList(),
                    MinSelect = pick,
                    MaxSelect = pick,
                    Slot = "$mode",
                    Prompt = "请选择模式",
                });
            }
        }
    }

    // ---------- 授予 / 复刻 / 宏 ----------

    private bool DoGrant(GrantAbilityNode n, VmFrame frame)
    {
        var scope = frame.Scope;
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;
        // Duration 必须一起传：不传就等于永久授予，
        // 「本回合赋予某能力」到了下回合依然生效。
        foreach (var t in targets)
        {
            RecordEffect(t, scope, "赋予能力");
            _world.GrantAbility(t, n.Granted, n.Duration);
        }
        return false;
    }

    /// <summary>
    /// 从外部卡池检索并生成。
    ///
    /// 关键点：**复用现成的布尔求值器**。候选卡的属性（费用/攻击/兵种/职业/关键词）
    /// 用一个临时探针实体喂给 EvalBool，于是「发现一张费用 ≤3 的机械随从」
    /// 这类条件不需要任何新语法、任何新求值逻辑。
    /// </summary>
    private void SpawnFromPool(SpawnCardNode n, VmScope scope, int pickCount, List<RuntimeEntity> created)
    {
        // 若 Where 里显式指定了 HasPackExpr（具体卡包范围），自动从全量总表取底座 ——
        // 这样哪怕指定的卡包没勾进当前卡池，只要装在硬盘上就能发现出来。
        bool chaos = (n.CopyFrom != null && n.CopyFrom.Zone == ZoneRef.GlobalPool)
                     || ContainsPackFilter(n.CopyFrom?.Where);
        var owner = scope.ResolvePlayer(n.ToOwner);

        var candidates = new List<CardDef>();
        if (_world is Battle.BattleState bst)
        {
            // 126 轮起 EnumerateDiscoverPool 已不再走混沌发现（放弃跨池）。
            // 但 **显式 pack: 点名**是另一回事：创作者明确写了 "pack.b:..."，
            // 该卡只要装在硬盘上就应该能被发现 —— 这不是混沌随机，是有意引用。
            IEnumerable<CardDef> pool = chaos && bst.Pool?.Catalog != null
                ? bst.Pool.Catalog.EnumerateChaosDiscoverPool(includeTokens: false)
                : bst.EnumerateDiscoverPool(chaos, includeTokens: false);
            foreach (var def in pool)
            {
                if (def == null) continue;
                if (def.Kind is not (CardKind.Follower or CardKind.Spell or CardKind.Amulet)) continue;
                candidates.Add(def);
            }
        }

        // 条件过滤：纯内存探针，$item 与 $self 都绑到它身上。
        if (n.CopyFrom?.Where != null
            && !(n.CopyFrom.Where is ConstBoolExpr cbTrue && cbTrue.Value)
            && candidates.Count > 0)
        {
            var filtered = new List<CardDef>();
            foreach (var def in candidates)
            {
                var probe = new RuntimeEntity
                {
                    EntityId = -1, EntrySerial = 1, Def = def, PlayerId = owner,
                    Zone = ZoneRef.None,
                    Attack = def.BaseAttack, Life = def.BaseLife, MaxLife = def.BaseLife,
                    Cost = def.BaseCost,
                };
                foreach (var tr in def.Traits ?? Array.Empty<SymbolId<TraitTag>>()) probe.Traits.Add(tr);
                if (_world is Battle.BattleState bsMount) bsMount.MountInnateProperties(probe);

                var sub = scope.Derive();
                var probeRef = new[] { EntityRef.From(probe) };
                sub.BindRefs(TargetSpec.SlotItem, probeRef);
                sub.BindRefs(TargetSpec.SlotSelf, probeRef);
                if (_eval.EvalBool(n.CopyFrom.Where, sub)) filtered.Add(def);
            }
            candidates = filtered;
        }

        if (candidates.Count == 0) return;

        bool chooseMode = n.CopyFrom != null && n.CopyFrom.Mode == SelectMode.Choose;
        if (!chooseMode)
        {
            // 随机生成 Y 张
            for (int i = 0; i < pickCount; i++)
            {
                var pick = candidates[_world.Rng.Next(candidates.Count)];
                var made = _world.SpawnCard(pick.CardId, n.ToZone, owner);
                if (made != null)
                {
                    scope.LastSpawnedCardId = pick.CardId;
                    created.Add(made);
                }
            }
            return;
        }

        // 发现模式：不放回抽样 X 张（1~5），玩家从中选 Y 张。
        int offerX = Math.Clamp(n.OfferCount > 0 ? n.OfferCount : 3, 1, 5);
        int actualOffer = Math.Min(offerX, candidates.Count);
        int actualPick = Math.Clamp(pickCount, 1, Math.Max(1, actualOffer));

        var shuffled = candidates.OrderBy(_ => _world.Rng.Next()).Take(actualOffer).ToList();
        var ids = shuffled.Select(c => c.CardId).ToList();

        var slot = string.IsNullOrEmpty(n.OutSlot) ? TargetSpec.SlotCreated : n.OutSlot;
        Block(new PendingChoice
        {
            Kind = ChoiceKind.DiscoverCard,
            Player = scope.Owner,
            Candidates = ids,
            MinSelect = actualPick,
            MaxSelect = actualPick,
            Slot = slot,
            Prompt = "discover|" + (int)n.ToZone + "|" + owner,
        });
    }

    /// <summary>Where 里是否显式指定了卡包范围。</summary>
    private static bool ContainsPackFilter(BoolExpr e) => e switch
    {
        HasPackExpr => true,
        AndExpr a => a.Items.Any(ContainsPackFilter),
        OrExpr o => o.Items.Any(ContainsPackFilter),
        NotExpr nt => ContainsPackFilter(nt.Inner),
        _ => false,
    };

    /// <summary>给资源计数器挂载能力（faith 等）。</summary>
    private bool DoGrantCounter(GrantCounterAbilityNode n, VmScope scope)
    {
        _world.GrantCounterAbility(scope.ResolvePlayer(n.Owner), n.Counter, n.Granted);

        // 挂上去的是**常驻修正**（如 "Increase the number of Modes you can select by 1."）时，
        // 它不会再有第二次触发机会 —— 只登记进表就永远不会执行。
        // 常驻修正的语义就是「立刻生效并一直生效」，所以这里当场跑一遍它的 Body。
        //
        // 例：Sham-Nacha 扣 10 信仰后给信仰挂这条，玩家的 mode_count 应当立即 +1，
        // 否则「信仰满 10 之后可以多选一个模式」永远不成立。
        if (n.Granted?.Trigger != null
            && n.Granted.Trigger.Event == CoreEvent.PassiveAura
            && n.Granted.Body != null && n.Granted.Body.Count > 0)
            PushBody(n.Granted.Body, scope.Derive());
        return false;
    }

    private bool DoInvoke(InvokeAbilityNode n, VmScope scope, VmFrame frame)
    {
        // 必须走 ResolveOrBlock：目标的选法可能是 Choose（「启动 1 个己方护符」），
        // 直接调 _eval.Resolve 在 Choose 模式下返回空列表 ——
        // 既不弹选择框、也不复刻任何东西，表现为「这张卡什么都没做」。
        var targets = ResolveOrBlock(NormalizeZoneWhere(n.Target), scope, TargetSpec.SlotSelected, frame);
        if (targets == null) return true;      // 已挂起，等待玩家选目标
        foreach (var t in targets)
        {
            if (t.Def?.Abilities == null) continue;

            // 必须遍历**全部**同事件能力，不能用 FirstOrDefault：
            // 一张卡可能有两条同事件能力（或通过 GrantAbility 追加一条），
            // 只复刻第一条等于漏掉其余。
            // 倒序遍历：PushBody 是 LIFO，正序压栈会让第 2 条能力先执行（174 号文档 四.11）。
            for (int ai = t.Def.Abilities.Count - 1; ai >= 0; ai--)
            {
                var ab = t.Def.Abilities[ai];
                if (ab?.Trigger == null || ab.Trigger.Event != n.Event) continue;
                // 只复刻目标自身的入场曲/进化等能力；旁观触发器（on_play on(other)）
                // 的事件类型可能相同，一起复刻会变成「每打一张牌就触发」。
                if (ab.Trigger.SubjectFilter != null) continue;
                // 防自递归：复刻同卡号的自身能力会无限压栈直到熔断。
                if (ReferenceEquals(ab, frame?.Ability)) continue;

                // 子作用域必须把 Source 与 $self 指向**被复刻的那个实体**：
                // 「启动 $selected 里的护符」时，护符能力里的 $self 应当是护符自己，
                // 而不是施法的那张卡。
                var sub = scope.Derive();
                sub.Source = EntityRef.From(t);
                sub.Bind(TargetSpec.SlotSelf, new[] { t });
                PushBody(ab.Body, sub);
            }
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

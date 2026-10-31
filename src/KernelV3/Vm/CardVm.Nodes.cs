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

        foreach (var t in targets)
        {
            // 必须在**每个目标上**求值：翻倍这类表达式的属性读取锚点是 $item，
            // 在循环外求值会让所有目标都取第一个候选的身材。
            var itemScope = scope.Derive();
            itemScope.Bind(TargetSpec.SlotItem, new[] { t });

            // null = 不修改该项。绝不能当成 0 ——
            // 「Set 攻击为 4」把生命也赋 0 会让敌方全场随从当场被打死。
            int atk = n.Attack != null ? _eval.EvalNum(n.Attack, itemScope) : 0;
            int life = n.Life != null ? _eval.EvalNum(n.Life, itemScope) : 0;

            switch (n.Op)
            {
                case StatModOp.Add:
                    if (n.Attack != null) t.Attack += atk;
                    if (n.Life != null)
                    {
                        t.Life += life;
                        // 生命上限必须同步：只加 Life 不加 MaxLife，
                        // 之后任何 RestoreLife 都会被 MaxLife 截断回去。
                        if (life > 0) t.MaxLife += life;
                        else if (life < 0 && t.Life < t.MaxLife) t.MaxLife = t.Life;
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
                // 费用减半向上取整（76-c 第 5 章第 11 条）：3 费 → 2 费，1 费 → 1 费。
                CostModOp.Halve => Math.Max(0, (t.Cost + 1) / 2),
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

    private bool DoMoveZone(MoveZoneNode n, VmScope scope)
    {
        // 能力抽牌必须走统一管线：自己 MoveEntity 会绕开 OnDraw 与抽空判负。
        if (IsPlainDraw(n))
        {
            scope.Bind(n.OutSlot, DrawThroughPipeline(n, scope));
            return false;
        }

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
                bool fromBoard = t.Zone == ZoneRef.Board;

                // 「X is the number of cards you returned」的计数写入点。
                // cards_returned_to_hand 的写入点在循环**之后**统一做（见下），
                // 这里不再逐个累加：
                //   "Do this 2 times: 'Return your hand to deck. Draw X cards.
                //    X is the number of cards you returned.'"
                // 累加会让第二轮读到第一轮的余数（5 → 10），一次抽 10 张。
                if (fromBoard && n.ToZone != ZoneRef.Board)
                    _world.EmitEvent(new VmEvent { Kind = CoreEvent.OnLeaveField, Subject = EntityRef.From(t) });
                _world.MoveEntity(t, n.ToZone, t.PlayerId);

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

        // 兵种、裸关键词、初始计数器、PassiveAura 装配、Countdown ——
        // 与 CreateEntity / TransformEntity 共用同一个装配点，
        // 三处各写一份必然漂移（回手后关键词没了、吟唱没恢复都是这么来的）。
        if (_world is Battle.BattleState bs) bs.MountInnateProperties(t);
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
        var departed = schema.Trait("Departed");
        if (!e.Traits.Contains(departed)) e.Traits.Add(departed);
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
                if (n.AddDepartedTrait && e != null) TagDeparted(e);
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
            else if (n.IsCrest)
            {
                // 纹章走自己的集成点：同名唯一与区域上限都在 GainCrest 里处理，
                // 而且失败时安静返回 null（不进墓地、不触发事件）。
                //
                // 必须把主人卡内嵌的 CrestDef 一并传下去：那才是带 Abilities 的定义。
                // 只传名字的话，GainCrest 只能现造空壳，纹章于是「拿得到但没有能力」。
                e = _world is Battle.BattleState bsc
                    ? bsc.GainCrest(n.CardId, n.CardName, scope.ResolvePlayer(n.ToOwner), n.CrestDef)
                    : null;
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
            // 已经进化过的随从不再进化：重复执行 "Evolve this follower"
            // 会无限叠属性（+2/+2 每跑一遍加一次）。
            if (t.IsEvolved) continue;

            // 进化加成与手动进化完全一致（TurnRunner.Evolve）：
            // 普通 +2/+2、超进化 +3/+3，Attack / Life / MaxLife 三者同步。
            // 之前这里只翻标志不加属性 —— 卡面写着 Evolve 却还是 2/2。
            int delta = n.IsSuperEvolve ? 3 : 2;
            t.Attack += delta;
            t.Life += delta;
            t.MaxLife += delta;

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
                foreach (var it in items.Take(max))
                {
                    var s = scope.Derive();
                    s.Bind(TargetSpec.SlotItem, new[] { it });
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
        int pick = Math.Clamp(_eval.EvalNum(n.PickCount, frame.Scope), 1, opts.Count);

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
                // 帧栈是后进先出，必须倒序压入才能正序执行
                for (int i = opts.Count - 1; i >= 0; i--)
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

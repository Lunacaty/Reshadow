using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

/// <summary>
/// 发动前预扫描器：把一张卡的自身能力 Body 从头走一遍，
/// 找出「在扣费之前就必须问玩家」的目标与模式。
///
/// 它**只读**：不修改 BattleState，不执行任何效果，
/// 只回答一个问题 —— 这次操作还需要玩家选什么。
///
/// 必须与运行时的挂起点严格对齐：
///   运行时走 Block() 的地方（DoSelect / ResolveOrBlock / DoBranch），
///   这里就要产出对应 Prompt；少了会让预选卡住，多了会让答案错位。
/// </summary>
public static class PreChoiceInspector
{
    public sealed class Result
    {
        /// <summary>需要玩家先做的选择；null 表示没有前置必选，可直接提交。</summary>
        public PendingChoice Prompt { get; init; }

        /// <summary>本次已消费掉的答案个数。</summary>
        public int Consumed { get; init; }
    }

    public static Result Inspect(
        BattleState state,
        RuntimeEntity card,
        PlayerActionKind kind,
        IReadOnlyList<IReadOnlyList<int>> answers)
    {
        if (state == null || card?.Def?.Abilities == null) return new Result();

        // 模拟出牌 / 进化**那一刻**的上下文，否则条件分支一律判错：
        //   · PlayCard：pp_at_play 尚未写入、combo 尚未 +1
        //     → Enhance / Combo 分支预扫时恒为假，该问的不问
        //   · Evolve / SuperEvolve：随从的 IsEvolved 还是 false
        //     → HasStateExpr(Evolved, Self) 恒假，进化分支被整段跳过
        // try/finally 严格还原，保证本方法只读、无副作用。
        var ppKey = state.Schema.Counter("pp");
        var ppAtPlayKey = state.Schema.Counter("pp_at_play");
        var comboKey = state.Schema.Counter("combo");
        int savedPpAtPlay = state.ReadCounter(card.PlayerId, ppAtPlayKey);
        int savedCombo = state.ReadCounter(card.PlayerId, comboKey);
        bool savedEv = card.IsEvolved, savedSep = card.IsSuperEvolved;

        try
        {
            if (kind == PlayerActionKind.PlayCard)
            {
                // 与 TurnRunner.PlayCard 保持同一真相：被封印爆能时 pp_at_play 写 0，
                // 否则预扫会把爆能分支当成可走，弹出根本不该出现的选目标框。
                // 加上后手已激活待消耗的 BonusPp，统一可用能量口径（178 号文档 1.1）
                int ppNow = state.ReadCounter(card.PlayerId, ppKey) + state.Players[card.PlayerId].BonusPp;
                bool suppressed = state.HasEffectiveKeyword(
                    card, state.Schema.Keyword("suppress_enhance"));
                int synthetic = suppressed ? 0 : ppNow;
                state.WriteCounter(card.PlayerId, ppAtPlayKey, synthetic);
                state.WriteCounter(card.PlayerId, comboKey, savedCombo + 1);
            }
            else if (kind == PlayerActionKind.Evolve) card.IsEvolved = true;
            else if (kind == PlayerActionKind.SuperEvolve)
            {
                card.IsEvolved = true;
                card.IsSuperEvolved = true;
            }

            var scope = ScopeOf(card);
            int consumed = 0;

            // 激奏 / 结晶：当前 PP 不够本体费用时，运行时走的是子卡定义。
            // 预扫必须跟着切，否则会按本体入场曲去问目标 ——
            // 玩家被问了、选了，真正打出的却是激奏版，答案全错位。
            var defToScan = card.Def;
            if (kind == PlayerActionKind.PlayCard && defToScan != null)
            {
                int ppNow = state.ReadCounter(card.PlayerId, ppKey);
                int bodyCost = defToScan.BaseCost;
                bool boardFull = state.Players[card.PlayerId].Board.Count
                                 (x => x?.Def != null
                                    && (x.Def.Kind == Ast.CardKind.Follower
                                        || x.Def.Kind == Ast.CardKind.Amulet))
                                 >= state.Rules.MaxBoardSize;

                bool bodyPlayable = ppNow >= bodyCost
                    && !(boardFull
                         && (defToScan.Kind == Ast.CardKind.Follower
                             || defToScan.Kind == Ast.CardKind.Amulet));

                if (!bodyPlayable)
                {
                    if (defToScan.AccelerateDef != null
                        && ppNow >= defToScan.AccelerateDef.BaseCost)
                        defToScan = defToScan.AccelerateDef;
                    else if (defToScan.CrystallizeDef != null
                             && ppNow >= defToScan.CrystallizeDef.BaseCost && !boardFull)
                        defToScan = defToScan.CrystallizeDef;
                }
            }

            foreach (var ab in defToScan.Abilities)
            {
                if (ab?.Trigger == null || ab.Body == null) continue;

                // 只认**自身能力**：SubjectFilter 非空的是旁观监听
                // （「每当你打出战利品卡时…」的 Trigger.Event 也是 OnPlay），
                // 把它当自身入场曲预扫会凭空多问一次。
                if (ab.Trigger.SubjectFilter != null
                    && !(ab.Trigger.SubjectFilter.SourceKind == TargetSourceKind.Slot
                         && ab.Trigger.SubjectFilter.Slot == TargetSpec.SlotSelf))
                    continue;

                bool trigMatch = kind switch
                {
                    PlayerActionKind.Evolve => ab.Trigger.Event == CoreEvent.OnEvolve,
                    // 超进化同时触发 OnEvolve 与 OnSuperEvolve
                    PlayerActionKind.SuperEvolve => ab.Trigger.Event == CoreEvent.OnEvolve
                                                 || ab.Trigger.Event == CoreEvent.OnSuperEvolve,
                    PlayerActionKind.EngageAmulet => ab.Trigger.Event == CoreEvent.OnEngage,
                    _ => ab.Trigger.Event == CoreEvent.OnPlay,
                };
                if (!trigMatch) continue;

                // 能力级条件也要用同一个作用域 —— 它可能引用 $selected / $chooseK。
                if (!EvalCond(state, card, ab.Condition, scope)) continue;

                var prompt = Walk(state, card, scope, ab.Body, answers, ref consumed);
                if (prompt != null) return new Result { Prompt = prompt, Consumed = consumed };
            }

            return new Result { Consumed = consumed };
        }
        finally
        {
            state.Players[card.PlayerId].Counters[ppAtPlayKey.Id] = savedPpAtPlay;
            state.Players[card.PlayerId].Counters[comboKey.Id] = savedCombo;
            card.IsEvolved = savedEv;
            card.IsSuperEvolved = savedSep;
        }
    }

    private static PendingChoice Walk(
        BattleState state, RuntimeEntity card, VmScope scope,
        IReadOnlyList<EffectNode> body,
        IReadOnlyList<IReadOnlyList<int>> answers,
        ref int consumed,
        bool afterIrreversible = false)
    {
        foreach (var node in body)
        {
            // 不可逆节点（抽牌 / 发现 / 随机 / 循环）之后能不能预选，要分两种：
            //
            //   · 模式抉择（BranchModeNode Choose）—— **可以**跨越。
            //     选项列表与前置动作的结果无关，玩家在声明打出/进化时就该先选好
            //     （174 号文档 BUG 4 要的正是这一点：不能因为前面有抽牌就放弃预选）。
            //
            //   · 目标选择（Select / Choose 目标）—— **不可以**跨越。
            //     候选池取决于抽到了什么、随机打死了谁；越过前置节点替他选等于盲选。
            //
            // 这里**不能**提前截胡返回提示：SubmitPreActionChoice 每收一个答案都会
            // 从头重跑 Inspect，提前返回的分支若不消费 answers / 不递增 consumed，
            // 就会把同一个弹窗无限重弹（UI 点一次弹一次，自动预选跑满 500 次抛异常）。
            // 正确做法是打标记继续往后走，让模式分支自己走正常的答案消费逻辑。
            if (IsIrreversible(node))
            {
                if (node is LoopNode) return null;   // 循环体内外无法在预选期静态展开
                afterIrreversible = true;
                continue;
            }

            switch (node)
            {
                case ConditionalNode cond:
                {
                    bool take = EvalCond(state, card, cond.Condition, scope);
                    var branch = take ? cond.Then : cond.Else;
                    if (branch == null || branch.Count == 0) continue;
                    var sub = Walk(state, card, scope, branch, answers, ref consumed, afterIrreversible);
                    if (sub != null) return sub;
                    continue;
                }

                case BranchModeNode bm when bm.Kind == ModeDispatchKind.Choose:
                {
                    int n = bm.Options?.Count ?? 0;
                    if (n == 0) continue;

                    if (consumed < answers.Count)
                    {
                        consumed++;
                        var picked = answers[consumed - 1];
                        foreach (var idx in picked)
                        {
                            if (idx < 0 || idx >= n) continue;
                            var sub = Walk(state, card, scope, bm.Options[idx], answers, ref consumed, afterIrreversible);
                            if (sub != null) return sub;
                        }
                        continue;
                    }

                    return NewModePrompt(state, card, bm);
                }

                default:
                {
                    // 与运行时完全对齐：所有带 Choose 目标的动作节点都要问。
                    // 只认 SelectTargetNode 会漏掉「对 1 个敌方随从造成 2 点伤害」
                    // 这类 —— 它们编译成单条 DealDamageNode，走 ResolveOrBlock。
                    var spec = ChooseTargetOf(node);
                    if (spec == null || spec.Mode != SelectMode.Choose) continue;
                    if (spec.SourceKind == TargetSourceKind.Slot) continue;

                    // 抽牌 / 随机伤害之后的选目标不能预选：候选池取决于
                    // 前置动作的结果，必须留到运行期结算完再弹窗。
                    if (afterIrreversible) return null;

                    var ids = ResolveCands(state, card, spec, scope);
                    if (ids.Count == 0) continue;   // 无合法目标：运行时也直接跳过

                    // SelectTargetNode 的槽位在 OutSlot 上，不在 spec.Slot 上；
                    // 最大可选数量取 spec.Count（钳到实际候选数），不能写死 1。
                    string slotName = node is SelectTargetNode stn && !string.IsNullOrEmpty(stn.OutSlot)
                        ? stn.OutSlot
                        : spec.Slot;
                    int maxPick = 1;
                    if (spec.Count != null)
                    {
                        try { maxPick = new VmEval(state).EvalNum(spec.Count, scope); }
                        catch { maxPick = 1; }
                    }
                    maxPick = Math.Clamp(maxPick, 1, ids.Count);

                    if (consumed < answers.Count)
                    {
                        consumed++;
                        // append 跟着节点走：连选两次时第二次要追加到 $selected。
                        bool appendSel = node is SelectTargetNode stnApp && stnApp.AppendToSlot;
                        BindAnswer(state, scope, slotName, answers[consumed - 1], appendSel);
                        continue;
                    }

                    return NewPrompt(card, ChoiceKind.SelectTarget, ids, 1, maxPick,
                                     slotName, "选择目标");
                }
            }
        }

        return null;
    }

    /// <summary>构造模式抉择的预选提示（含「可选模式数 +1」光环）。</summary>
    private static PendingChoice NewModePrompt(BattleState state, RuntimeEntity card, BranchModeNode bm)
    {
        int n = bm.Options?.Count ?? 0;
        if (n == 0) return null;
        var cands = Enumerable.Range(0, n).ToList();

        int pick = 1;
        if (bm.PickCount != null)
        {
            try { pick = new VmEval(state).EvalNum(bm.PickCount, ScopeOf(card)); }
            catch { pick = 1; }
        }

        // 加上「Increase the number of Modes you can select by 1」的玩家光环值。
        // 漏了它，获得该增益后预选弹窗的 MaxSelect 仍是 1，玩家根本没法选第 2 个模式。
        int bonus = 0;
        try { bonus = state.ReadCounter(card.PlayerId, state.Schema.Counter("mode_count")); }
        catch { bonus = 0; }
        int actualPick = Math.Clamp(pick + Math.Max(0, bonus), 1, n);

        // MinSelect 必须等于 MaxSelect：要求选 N 个模式时玩家必须选满 N 个。
        return NewPrompt(card, ChoiceKind.SelectMode, cands, actualPick,
                         actualPick, "$mode", "选择模式");
    }

    /// <summary>把玩家选好的实体 id 绑定进探针作用域，供后续条件求值使用。</summary>
    private static void BindAnswer(BattleState state, VmScope scope, string slot,
                                   IReadOnlyList<int> picked, bool append = false)
    {
        if (scope == null || picked == null || picked.Count == 0) return;
        var list = new List<EntityRef>();
        foreach (var id in picked)
        {
            var live = state.GetLive(id, 0);
            if (live == null)
            {
                // EntrySerial 可能不匹配，按 id 扫一遍在场实体
                foreach (var p in new[] { state.Players[0], state.Players[1] })
                {
                    live = p.Board.FirstOrDefault(x => x.EntityId == id)
                        ?? p.Hand.FirstOrDefault(x => x.EntityId == id);
                    if (live != null) break;
                }
            }
            if (live != null) list.Add(EntityRef.From(live));
        }
        if (list.Count > 0)
        {
            // 预选阶段同样要写序数槽位：
            //   后续动作引用 "the second selected follower" 时，
            //   预选阶段就必须已经知道它是第几次选择，
            //   否则预选与实跑会算出不同的目标集合。
            // BindSelection 内部同时维护 $selected（追加语义由 append 决定）。
            // append 必须跟着节点走：
            //   "Select an allied and enemy follower" 连选两次，第二次要追加，
            //   写死 false 会让 $selected 只剩最后一次的结果。
            scope.BindSelection(slot ?? TargetSpec.SlotSelected, list, append);
        }
    }

    private static VmScope ScopeOf(RuntimeEntity card) => new()
    {
        Owner = ZoneScope.Self,
        Controller = card.PlayerId,
        Source = EntityRef.From(card),
        // 进化 / 入场能力里的 $trigger 指向本卡自己。
        // 少了它，带 $trigger 的分支条件在预选阶段全部误判为 false（174 号文档 BUG4）。
        Trigger = EntityRef.From(card),
    };

    private static PendingChoice NewPrompt(
        RuntimeEntity card, ChoiceKind kind,
        IReadOnlyList<int> cands, int min, int max, string slot, string text)
        => new()
        {
            Player = ZoneScope.Self,
            PlayerId = card.PlayerId,
            Kind = kind,
            Candidates = cands,
            MinSelect = min,
            MaxSelect = max,
            Slot = slot ?? TargetSpec.SlotSelected,
            CanCancel = true,
            Prompt = text,
        };

    /// <summary>该节点是否会让玩家看到不可逆信息。</summary>
    private static bool IsIrreversible(EffectNode node)
    {
        switch (node)
        {
            case LoopNode:
                return true;

            // 随机选模式：玩家看不到具体选了哪些，之后的选择不能反悔
            case BranchModeNode bm:
                return bm.Kind == ModeDispatchKind.Random
                    || bm.Kind == ModeDispatchKind.RandomUnused;

            // 抽牌是「牌库 → 手牌」的移区，不是 MacroNode("draw")
            case MoveZoneNode mv:
                return mv.ToZone == ZoneRef.Hand && mv.Target != null
                       && mv.Target.SourceKind == TargetSourceKind.Zone
                       && mv.Target.Zone == ZoneRef.Deck;

            // 「发现」是带 PoolQuery 的生成，玩家已看到候选
            case SpawnCardNode sp:
                return sp.SourceKind == CardSourceKind.PoolQuery;

            default:
                // 随机目标（"deal 3 damage to a random enemy follower"）
                // 也是不可逆信息：结果一旦定下就不能装作没问过。
                var t = ChooseTargetOf(node);
                if (t != null && t.Mode == SelectMode.Random) return true;
                return false;
        }
    }

    /// <summary>取出该节点作用的目标（与 TurnRunner.GetChooseTarget 保持一致）。</summary>
    private static TargetSpec ChooseTargetOf(EffectNode n) => n switch
    {
        SelectTargetNode st => st.Target,
        DealDamageNode d => d.Target,
        RestoreLifeNode r => r.Target,
        MoveZoneNode m => m.Target,
        ModifyStatsNode s => s.Target,
        ModifyKeywordNode k => k.Target,
        ModifyCostNode c => c.Target,
        TransformNode t => t.Target,
        EvolveNode e => e.Target,
        GrantAbilityNode g => g.Target,
        _ => null,
    };

    private static List<int> ResolveCands(BattleState state, RuntimeEntity card, TargetSpec spec,
                                          VmScope scope = null)
    {
        var eval = new VmEval(state);
        // 必须沿用 Walk 传下来的作用域：
        // 预选第 2 个目标时，spec.Where 里可能带着 "other than $choose1"，
        // 每次都新建空作用域会让第 1 个已选目标不被排除出候选列表。
        var sc = scope ?? ScopeOf(card);

        var ambush = state.Schema.Keyword("ambush");
        var aura = state.Schema.Keyword("aura");
        var ids = new List<int>();

        foreach (var r in eval.ResolveRef(spec, sc))
        {
            var live = state.GetLive(r.EntityId, r.EntrySerial);
            if (live == null) continue;
            if (live.Zone == ZoneRef.Board)
            {
                if (state.IsProtectedAmulet(live)) continue;
                // 必须走 HasEffectiveKeyword：潜行/灵气可能是光环授予的。
                if (live.PlayerId != card.PlayerId
                    && (state.HasEffectiveKeyword(live, ambush)
                        || state.HasEffectiveKeyword(live, aura)))
                    continue;
            }
            ids.Add(live.EntityId);
        }
        return ids;
    }

    private static bool EvalCond(BattleState state, RuntimeEntity card, BoolExpr cond,
                                 VmScope scope = null)
    {
        if (cond == null) return true;
        // 同样要沿用 Walk 的作用域：
        //   "Select an allied follower or amulet. If you selected an allied amulet,
        //    select an enemy follower ..."
        // 在空作用域上查 $selected 恒为 false，分支内的第 2 个预选框根本弹不出来。
        try { return new VmEval(state).EvalBool(cond, scope ?? ScopeOf(card)); }
        catch { return true; }
    }
}


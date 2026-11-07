using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 对局流程（Match Runner）—— 76-c 第 2.1 节。
//
// 之前只有零组件：BattleState / TurnRunner / CombatResolver。
// 没有东西负责「开局 → 换牌 → 回合循环 → 胜负」，后果是：
//   · 主战者从来没被创建，Leader 一直是 null，
//     Winner() 的「主战者生命归零判负」永远不触发
//   · 牌组从来没被初始化，SetupFaith() 扫不到任何东西
//   · 先后手、起手 4 张、换牌、洗牌 全部缺失
// ============================================================================

public enum MatchPhase
{
    NotStarted = 0,
    Mulligan,
    Playing,
    Finished,
}

public sealed class MatchRunner
{
    /// <summary>主战者卡 ID 偏移量，避免与真实卡冲突。</summary>
    public const int LeaderCardIdBase = 990000;

    private readonly BattleState _state;
    private readonly GameSchema _schema;
    private readonly TurnRunner _turns;
    private readonly CardVm _vm;

    /// <summary>发动前预选会话；null 表示不在预选中。</summary>
    private PreActionSession _preAction;
    private readonly CombatResolver _combat;

    public MatchRunner(BattleState state, GameSchema schema = null)
    {
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _schema = schema ?? state.Schema;
        _turns = new TurnRunner(_state, _schema);
        _combat = new CombatResolver(_state, _schema);
        _vm = new CardVm(_state);
        CardVm.Schema = _schema;
    }

    public MatchPhase Phase { get; private set; } = MatchPhase.NotStarted;
    public int ActivePlayer => _state.ActivePlayer;
    public int TurnNumber => _state.TurnNumber;

    /// <summary>胜负：0/1 胜方，-1 未结束，-2 平局。</summary>
    public int Winner() => _turns.Winner();

    // ---------------------------------------------------------------- 开局

    /// <summary>
    /// 开局（R-2.1）：
    ///   1. 双方主战者 Life = MaxLife = 20，pp = max_pp = 0，ep = sep = 2
    ///   2. 建牌组并洗牌
    ///   3. 扫描信仰（拥有信仰能力的卡 → 纹章区生成信仰实体）
    ///   4. 双方各抽 4 张起手
    /// 完成后停在 Mulligan 阶段，等待 ConfirmMulligan()。
    /// </summary>
    /// <summary>
    /// 用「双方各自的生效规则」开局 —— 支持非对称（如一方带 +10 血构筑卡）。
    ///
    /// 与 Start 的区别：主战者生命、起手抽牌、初始进化点都按**每一方自己**的
    /// EffectiveDeckProfile 设置，而不是共用一份全局规则。
    /// </summary>
    public void StartWithProfiles(
        int firstPlayer,
        Packs.ConstructedDeck deck0, Packs.EffectiveDeckProfile prof0,
        Packs.ConstructedDeck deck1, Packs.EffectiveDeckProfile prof1)
    {
        if (deck0 == null || deck1 == null)
            throw new ArgumentNullException("构筑不能为空");
        if (prof0 == null || prof1 == null)
            throw new ArgumentNullException("生效规则不能为空");
        if (!prof0.IsValid)
            throw new InvalidOperationException("玩家 0 构筑不合法：" + string.Join("; ", prof0.Errors));
        if (!prof1.IsValid)
            throw new InvalidOperationException("玩家 1 构筑不合法：" + string.Join("; ", prof1.Errors));

        // 内置衍生卡必须先就位，否则效果召唤 Fairy / 神器会造出 Def == null 的空壳。
        _state.RegisterBuiltinTokens();

        CreateLeader(0, prof0.LeaderInitialLife, prof0.LeaderMaxLife, deck0.LeaderClan);
        CreateLeader(1, prof1.LeaderInitialLife, prof1.LeaderMaxLife, deck1.LeaderClan);

        InitCountersWithBonus(0, prof0, _schema);
        InitCountersWithBonus(1, prof1, _schema);

        FillDeck(0, deck0.ExpandToCardIds());
        FillDeck(1, deck1.ExpandToCardIds());
        ShuffleDeck(0);
        ShuffleDeck(1);

        _state.SetupFaith();

        _state.DrawCards(0, Math.Max(0, prof0.StartingHandSize));
        _state.DrawCards(1, Math.Max(0, prof1.StartingHandSize));

        _state.ActivePlayer = firstPlayer;
        _state.FirstPlayer = firstPlayer;
        _state.TurnNumber = 0;
        Phase = MatchPhase.Mulligan;

        _state.EmitEvent(new VmEvent { Kind = CoreEvent.BattleStart });
        _vm?.StepUntilBlocked();
    }

    /// <summary>按某一方的生效规则初始化计数器（含 EP/SEP 增量）。</summary>
    private void InitCountersWithBonus(int playerId, Packs.EffectiveDeckProfile prof, GameSchema schema)
    {
        var rules = _state.Rules;
        _state.WriteCounter(playerId, schema.Counter("pp"), 0);
        _state.WriteCounter(playerId, schema.Counter("max_pp"), rules.InitialMaxPp);
        int targetEp = Math.Max(0, rules.InitialEp(playerId, _state.FirstPlayer) + prof.InitialEpBonus);
        int targetSep = Math.Max(0, rules.InitialSep(playerId, _state.FirstPlayer) + prof.InitialSepBonus);

        // 先拓宽上限再写：WriteCounter 会按 CounterLimits 钳制，
        // 而 BattleState 构造时把 ep/sep 上限设成了 MaxEpCap(=2)。
        // 不拓宽的话 deck_mod ep(+1) 写进去当场被钳回 2 —— 构筑卡 100% 失效。
        if (schema.CounterLimits.TryGetValue("ep", out var epLim) && targetEp > epLim.max)
            schema.CounterLimits["ep"] = (epLim.min, targetEp);
        if (schema.CounterLimits.TryGetValue("sep", out var sepLim) && targetSep > sepLim.max)
            schema.CounterLimits["sep"] = (sepLim.min, targetSep);

        // 记下「解锁时该发多少」，但开局一律置 0：
        // 原版规则里解锁前 EP/SEP 恒为 0，任何能力都无法提前获得。
        var p0 = _state.Players[playerId];
        p0.InitialEpToGrant = targetEp;
        p0.InitialSepToGrant = targetSep;
        p0.EpUnlocked = false;
        p0.SepUnlocked = false;
        _state.Players[playerId].Counters[schema.Counter("ep").Id] = 0;
        _state.Players[playerId].Counters[schema.Counter("sep").Id] = 0;
        _state.WriteCounter(playerId, schema.Counter("shadow"), 0);
        _state.WriteCounter(playerId, schema.Counter("combo"), 0);
        _state.WriteCounter(playerId, schema.Counter("rally"), 0);
        _state.WriteCounter(playerId, schema.Counter("evolve_count"), 0);
        InitPoolCounters(playerId, schema);
    }

    /// <summary>
    /// 用「局外构筑 + 卡池环境」开局。
    ///
    /// 构筑在这里被**展开**成卡号序列，再由 FillDeck 生成局内的 40 个实体 ——
    /// 此后无论洗牌、塞衍生卡、换整副牌库，改的都是 PlayerState.Deck，
    /// 局外那份构筑一字不动。
    /// </summary>
    public void StartWithConstructedDecks(
        Packs.CardPoolRuntime pool, int firstPlayer,
        Packs.ConstructedDeck deck0, Packs.ConstructedDeck deck1)
    {
        if (pool == null) throw new ArgumentNullException(nameof(pool));

        // 必须走 DeckRuleResolver 算出各自的生效规则 ——
        // 直接用 pool.Rules 会忽略牌组里的 DeckBuildModifier，
        // 「扩容 +20」「主战者生命 +10」「额外职业」全部不生效。
        var view = pool.ToCardPoolView();
        var prof0 = Packs.DeckRuleResolver.ResolveAndValidate(deck0, view);
        var prof1 = Packs.DeckRuleResolver.ResolveAndValidate(deck1, view);

        if (!prof0.IsValid || !prof1.IsValid)
        {
            var all = new List<string>(prof0.Errors);
            all.AddRange(prof1.Errors);
            throw new InvalidOperationException("构筑不合法：" + string.Join("; ", all));
        }

        // 本卡池的具名牌组表必须挂上，否则「替换为启示录牌组」会找不到定义。
        // 外部可能用裸 new BattleState() 建 runner —— 这里补挂卡池视图与卡片，
        // 否则发现类效果会因为 state.Pool == null 而恒为空。
        _state.Pool ??= view;
        foreach (var c in pool.AllCards.Values) _state.RegisterCard(c);

        CardVm.DeckDefs = pool.DeckDefs;
        // 转发给 StartWithProfiles：两个入口共用同一条开局路径才不会漂移。
        StartWithProfiles(firstPlayer, deck0, prof0, deck1, prof1);
    }

    public void Start(int firstPlayer, IReadOnlyList<int> deck0, IReadOnlyList<int> deck1,
                      int leaderLife = 20)
    {
        if (firstPlayer is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(firstPlayer));

        // 内置衍生卡必须先就位：效果召唤 Fairy / 神器、换 Apocalypse 牌库、
        // 生成土之印与线索护符都要读 CardDef，缺一个就会造出 Def == null 的空壳。
        _state.RegisterBuiltinTokens();

        // 主战者初始生命：显式传入的 leaderLife 优先，否则用规则里的值。
        int life = leaderLife > 0 ? leaderLife : _state.Rules.LeaderInitialLife;
        int maxLife = Math.Max(life, _state.Rules.LeaderMaxLife);
        CreateLeader(0, life, maxLife);
        CreateLeader(1, life, maxLife);

        InitCounters(0);
        InitCounters(1);

        FillDeck(0, deck0);
        FillDeck(1, deck1);
        ShuffleDeck(0);
        ShuffleDeck(1);

        // 信仰必须在抽牌之前生成（它扫牌组内容）
        _state.SetupFaith();

        _state.DrawCards(0, _state.Rules.StartingHandSize);
        _state.DrawCards(1, _state.Rules.StartingHandSize);

        _state.ActivePlayer = firstPlayer;
        _state.FirstPlayer = firstPlayer;
        _state.TurnNumber = 0;
        Phase = MatchPhase.Mulligan;

        // 对局开始事件。「At the start of the match」类能力靠它。
        // 放在最后发：此时双方主战者、计数器、牌库、信仰都已就位，
        // 监听方读到的是一副完整的开局状态。
        _state.EmitEvent(new VmEvent { Kind = CoreEvent.BattleStart });
        _vm?.StepUntilBlocked();
    }

    private void CreateLeader(int playerId, int life) => CreateLeader(playerId, life, life);

    /// <summary>建主战者：初始生命与生命上限分开指定（构筑卡可以只加其一）。</summary>
    private void CreateLeader(int playerId, int life, int maxLife, string leaderClan = null)
    {
        int cardId = LeaderCardIdBase + playerId;
        if (_state.CardDefOf(cardId) == null)
        {
            _state.RegisterCard(new CardDef
            {
                CardId = cardId,
                Name = "Leader" + playerId,
                Kind = CardKind.Leader,
                BaseLife = life,
                BaseAttack = 0,
                BaseCost = 0,
                // 主战者的职业要写进 Def：『发现本职业卡牌』(clan: self)
                // 是靠读我方主战者的 Clan 来判定的。
                Clan = string.IsNullOrEmpty(leaderClan)
                    ? SymbolId<ClanTag>.Invalid : _schema.Clan(leaderClan),
            });
        }
        var e = _state.CreateEntity(cardId, ZoneRef.Leader, playerId);
        e.Life = life;
        // 上限必须取 maxLife：只写 life 会把「初始 20、上限 30」的自定义规则压成 20。
        e.MaxLife = Math.Max(life, maxLife);
        _state.Players[playerId].Leader = e;
    }

    private void InitCounters(int playerId)
    {
        // 初始各项走规则集：DIY 卡池可以给不同的起始能量/进化点。
        var rules = _state.Rules;
        _state.WriteCounter(playerId, _schema.Counter("pp"), 0);
        _state.WriteCounter(playerId, _schema.Counter("max_pp"), rules.InitialMaxPp);
        // 同上：记下解锁时的发放量，开局置 0。
        var p1 = _state.Players[playerId];
        p1.InitialEpToGrant = rules.InitialEp(playerId, _state.FirstPlayer);
        p1.InitialSepToGrant = rules.InitialSep(playerId, _state.FirstPlayer);
        p1.EpUnlocked = false;
        p1.SepUnlocked = false;
        _state.Players[playerId].Counters[_schema.Counter("ep").Id] = 0;
        _state.Players[playerId].Counters[_schema.Counter("sep").Id] = 0;
        _state.WriteCounter(playerId, _schema.Counter("shadow"), 0);
        _state.WriteCounter(playerId, _schema.Counter("combo"), 0);
        _state.WriteCounter(playerId, _schema.Counter("rally"), 0);
        _state.WriteCounter(playerId, _schema.Counter("evolve_count"), 0);
        InitPoolCounters(playerId, _schema);
    }


    /// <summary>
    /// 按卡池配置写入自定义主战者计数器的初始值（165 续·专题二）。
    ///
    /// 少了这一步，卡池里定义的计数器开局是 0 而不是 Initial，
    /// 玩家在卡池管理器里填的初值永远不生效，且没有任何提示。
    /// 内置计数器由上面几行逐个写死，这里只处理卡池自定义的那批。
    /// </summary>
    private void InitPoolCounters(int playerId, GameSchema schema)
    {
        var defs = _state.PoolCounterDefs;
        if (defs == null || defs.Count == 0) return;
        foreach (var ctrDef in defs)
        {
            if (ctrDef == null || ctrDef.IsBuiltin || string.IsNullOrWhiteSpace(ctrDef.Name)) continue;
            var key = schema.Counter(ctrDef.Name);
            if (!key.IsValid) continue;
            _state.WriteCounter(playerId, key, ctrDef.Initial);

            // 把自定义计数器的事件驱动规则挂到该玩家名下（179 号文档 C6 环节 2）。
            // CardVm.FlushNextTrigger 会直接扫双方 Leader 承载的计数器能力，
            // 因此玩家空场时这些规则同样生效。
            foreach (var ab in ctrDef.RulesAbilities ?? Array.Empty<Ast.AbilityDef>())
                if (ab != null) _state.GrantCounterAbility(playerId, key, ab);
        }
    }

    private void FillDeck(int playerId, IReadOnlyList<int> cardIds)
    {
        if (cardIds == null) return;
        foreach (var id in cardIds)
            _state.SpawnCard(id, ZoneRef.Deck, playerId);
    }

    /// <summary>Fisher-Yates：用世界随机源，联机时由服务端播种保证可复现。</summary>
    private void ShuffleDeck(int playerId)
    {
        var deck = _state.Players[playerId].Deck;
        for (int i = deck.Count - 1; i > 0; i--)
        {
            int j = _state.Rng.Next(i + 1);
            (deck[i], deck[j]) = (deck[j], deck[i]);
        }
    }

    // ---------------------------------------------------------------- 换牌

    /// <summary>
    /// 换牌（R-2.1 第 3 步）：先抽等量新牌，再把换掉的牌洗回牌组。
    /// 两人都换完后调 ConfirmMulligan()。
    /// </summary>
    public void Mulligan(int playerId, IReadOnlyList<RuntimeEntity> returning)
    {
        if (Phase != MatchPhase.Mulligan) return;
        if (returning == null || returning.Count == 0) return;

        var p = _state.Players[playerId];
        var back = new List<RuntimeEntity>();

        foreach (var e in returning)
        {
            if (e == null || !p.Hand.Contains(e)) continue;
            p.Hand.Remove(e);
            back.Add(e);
        }
        if (back.Count == 0) return;

        _state.DrawCards(playerId, back.Count);

        foreach (var e in back)
        {
            e.Zone = ZoneRef.Deck;
            e.EntrySerial++;
            p.Deck.Add(e);
        }
        ShuffleDeck(playerId);
    }

    /// <summary>两人换牌均完成后进入对局，并执行先手的第 1 回合。</summary>
    public void ConfirmMulligan()
    {
        if (Phase != MatchPhase.Mulligan) return;
        Phase = MatchPhase.Playing;
        _turns.StartTurn(_state.ActivePlayer, _vm);
        DrainVm();
        CheckFinished();
    }

    // ---------------------------------------------------------------- 对局循环

    /// <summary>
    /// 结束当前回合，交给对手。
    ///
    /// 关键：回合末能力可能弹出手选目标（TurnEnd 挂起）。此时**绝不能**
    /// 同步往下开对手回合 —— 那会在玩家的选择还没消解时就把 ActivePlayer
    /// 改掉、给对手涨费、把对手的回合初触发器塞进队列。
    /// 开下一回合的动作必须排进续体，等这批事件彻底结算完再执行。
    /// </summary>
    public void EndTurn()
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return;


        int cur = _state.ActivePlayer;
        _turns.EndTurn(cur, _vm);
        if (CheckFinished()) return;

        _vm.EnqueueContinuation(() =>
        {
            if (CheckFinished()) return;
            _turns.StartTurn(1 - cur, _vm);
        });
        DrainVm();
        CheckFinished();
    }

    private bool CheckFinished()
    {
        if (_turns.Winner() == -1) return false;
        Phase = MatchPhase.Finished;
        return true;
    }

    // ---------------------------------------------------------------- 交互状态

    public BattleState State => _state;
    public CardVm Vm => _vm;
    public TurnRunner Turns => _turns;
    public CombatResolver Combat => _combat;

    /// <summary>当前挂起的选择（无挂起时为 null）。</summary>
    /// <summary>当前挂起的选择：预选优先，其次才是 VM 结算中的挂起。</summary>
    public PendingChoice PendingChoice => _preAction?.CurrentPrompt ?? _vm?.Pending;

    /// <summary>
    /// 是否在等玩家输入。
    ///
    /// 预选阶段由 MatchRunner 自己持有会话，**完全不碰 _vm.Pending** ——
    /// 因为 Block() 会重建 PendingChoice，外部设进去的值会被覆盖。
    /// </summary>
    public bool IsWaitingForInput
        => _preAction != null || (_vm != null && _vm.Pending != null);

    /// <summary>当前的发动前预选会话；null 表示不在预选中。</summary>
    public PreActionSession PreAction => _preAction;

    /// <summary>
    /// 取消发动前预选：卡退回手牌，PP/EP 零消耗。
    /// 结算中必选（已抽牌/已发现）不允许取消。
    /// </summary>
    public bool CancelPendingChoice()
    {
        if (_preAction == null) return false;
        _preAction = null;
        return true;
    }

    /// <summary>
    /// 自动选择策略。
    ///
    /// 为 null（默认）时，挂起后必须由外部调用 SubmitChoice —— UI 走这条路径。
    /// 非 null 时 DrainVm 会自己完成选择并继续推进，供自动化回归与 Bot 对战使用。
    /// </summary>
    public IChoicePolicy AutoChoicePolicy { get; set; }

    /// <summary>把 VM 推到「完成」或「需要外部输入」为止；有策略时自动消解挂起。</summary>
    private void DrainVm()
    {
        int guard = 0;
        while (true)
        {
            // 发动前预选阶段：没有自动策略就停下等 UI；
            // 有策略（Bot / 回归）就自动答完并提交，否则整局会卡死在预选上。
            if (_preAction != null && _preAction.CurrentPrompt != null)
            {
                if (AutoChoicePolicy == null) return;
                if (++guard > 500) throw new InvalidOperationException("自动预选死循环");
                var autoChosen = AutoChoicePolicy.Select(_state, _preAction.CurrentPrompt);
                SubmitPreActionChoice(autoChosen);
                continue;
            }

            var status = _vm.StepUntilBlocked();
            if (CheckFinished()) return;
            if (status != VmStatus.WaitingForInput || _vm.Pending == null) return;
            if (AutoChoicePolicy == null) return;   // 交给外部
            if (++guard > 500) throw new InvalidOperationException("自动选择死循环");
            var chosen = AutoChoicePolicy.Select(_state, _vm.Pending);
            _vm.SubmitChoice(_vm.Pending.ChoiceId, chosen);
        }
    }

    /// <summary>外部提交一次选择，然后继续推进。</summary>
    public bool SubmitChoice(int choiceId, IReadOnlyList<int> selectedIds)
    {
        // 发动前预选：答案交给会话，卡还没上场、VM 还没开始跑。
        if (_preAction != null)
        {
            SubmitPreActionChoice(selectedIds);
            return !CheckFinished();
        }

        if (_vm.Pending == null || _vm.Pending.ChoiceId != choiceId) return false;
        _vm.SubmitChoice(choiceId, selectedIds);
        DrainVm();
        return !CheckFinished();
    }

    // ---------------------------------------------------------------- 操作代理

    // 所有主动操作都先过一道「有挂起就不许动」的闸门。
    // 缺这道闸门，调用方可以在选择框还没消解时插入别的动作，
    // 于是同一个玩家的两个动作交错执行，状态机彻底乱掉。

    public bool PlayCard(RuntimeEntity card)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;

        // 先校验这次出牌本身合法，再做预选 ——
        // 否则只有 1 费时把 8 费带指向入场曲的随从拖出来，
        // 也会进入瞄准预选，选完目标才因费用不足打不出。
        if (!_turns.CanPlayCard(_state.ActivePlayer, card, out _)) return false;

        // 发动前预选：先问目标/模式，问完才真正出牌。
        if (!TryStartPreAction(PlayerActionKind.PlayCard, card)) return false;
        if (_preAction != null)
        {
            // 已挂起等预选。Bot / 回归设了自动策略时，这里要顺手把它答完，
            // 否则调用方看到「有挂起」就停手，整局卡死在预选上。
            DrainVm();
            return true;
        }

        bool ok = _turns.PlayCard(_state.ActivePlayer, card, _vm);
        DrainVm();
        return ok;
    }

    /// <summary>
    /// 尝试为一次主动操作发起预选。
    ///
    /// 返回 false 表示这张卡根本没有前置必选，调用方按常规流程继续；
    /// 返回 true 表示答案已备齐（或没有必选），可以提交。
    /// 发起后若 _preAction 非空，说明正在等玩家选，本次操作挂起。
    /// </summary>
    private bool TryStartPreAction(PlayerActionKind kind, RuntimeEntity subject)
    {
        if (subject?.Def?.Abilities == null) return true;

        var probe = PreChoiceInspector.Inspect(_state, subject, kind,
                                               Array.Empty<IReadOnlyList<int>>());
        if (probe.Prompt == null) return true;   // 没有前置必选

        _preAction = new PreActionSession
        {
            ActionKind = kind,
            PlayerId = subject.PlayerId,
            Subject = subject,
            CurrentPrompt = probe.Prompt,
        };
        return true;
    }

    /// <summary>
    /// 提交一次预选答案。
    ///
    /// 答完的答案写进**发起操作那张卡**的 PreCommittedChoices，
    /// 再真正执行出牌/进化；CardVm 在 StepUntilBlocked 里遇到挂起时会
    /// 从作用域上的同一队列里取值当场消解，不再二次弹窗。
    /// </summary>
    public bool SubmitPreActionChoice(IReadOnlyList<int> chosen)
    {
        if (_preAction == null) return false;
        _preAction.Answers.Add(chosen ?? Array.Empty<int>());
        _preAction.CurrentPrompt = null;

        var again = PreChoiceInspector.Inspect(_state, _preAction.Subject,
                                               _preAction.ActionKind, _preAction.Answers);
        if (again.Prompt != null)
        {
            _preAction.CurrentPrompt = again.Prompt;
            return true;   // 还有下一处必选
        }

        var session = _preAction;
        _preAction = null;

        // 答案交给发起操作的那张卡
        session.Subject.PreCommittedChoices = new Queue<IReadOnlyList<int>>();
        foreach (var a in session.Answers)
            session.Subject.PreCommittedChoices.Enqueue(a);

        bool ok;
        switch (session.ActionKind)
        {
            case PlayerActionKind.Evolve:
                ok = _turns.Evolve(session.PlayerId, session.Subject, false, _vm);
                break;
            case PlayerActionKind.SuperEvolve:
                ok = _turns.Evolve(session.PlayerId, session.Subject, true, _vm);
                break;
            case PlayerActionKind.EngageAmulet:
                ok = _turns.EngageAmulet(session.PlayerId, session.Subject, _vm);
                break;
            default:
                ok = _turns.PlayCard(session.PlayerId, session.Subject, _vm);
                break;
        }
        DrainVm();
        return ok;
    }

    public bool Evolve(RuntimeEntity e, bool super)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;

        // 先校验这次进化本身合法，再做预选 ——
        // 否则 EP 不够的随从也会弹预选框。
        bool can = super ? _turns.CanSuperEvolve(_state.ActivePlayer, e)
                         : _turns.CanEvolve(_state.ActivePlayer, e);
        if (!can || e == null || e.Zone != ZoneRef.Board
            || e.PlayerId != _state.ActivePlayer || e.IsEvolved) return false;

        // 「进化时：选择 1 个随从破坏」也要先选后进化，与出牌同理。
        var actKind = super ? PlayerActionKind.SuperEvolve : PlayerActionKind.Evolve;
        if (!TryStartPreAction(actKind, e)) return false;
        if (_preAction != null) { DrainVm(); return true; }

        bool ok = _turns.Evolve(_state.ActivePlayer, e, super, _vm);
        DrainVm();
        return ok;
    }

    public bool EngageAmulet(RuntimeEntity a, int cost)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;
        bool ok = _turns.EngageAmulet(_state.ActivePlayer, a, cost, _vm);
        DrainVm();
        return ok;
    }

    /// <summary>以卡面声明的启动费启动护符。</summary>
    public bool EngageAmulet(RuntimeEntity a)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;

        // 费用不够就不该弹预选框。
        if (_turns.CanEngageAmulet(_state.ActivePlayer, a, out _) == false) return false;

        // 启动式能力的指向性同样要「先选后启动」。
        if (!TryStartPreAction(PlayerActionKind.EngageAmulet, a)) return false;
        if (_preAction != null) { DrainVm(); return true; }

        bool ok = _turns.EngageAmulet(_state.ActivePlayer, a, _vm);
        DrainVm();
        return ok;
    }

    public bool FuseCards(RuntimeEntity b, IReadOnlyList<RuntimeEntity> m)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;
        bool ok = _turns.FuseCards(_state.ActivePlayer, b, m, _vm);
        DrainVm();
        return ok;
    }


    // ---------------------------------------------------------------- 手牌呈现状态（转发 TurnRunner）

    /// <summary>计算一张手牌在当前 PP 下的显示费用与可打出模式（决定手牌光圈）。</summary>
    public (TurnRunner.HandPlayMode Mode, int DisplayCost) EvaluateHandCardPresentation(
        int playerId, Vm.RuntimeEntity card, bool isMyActiveTurn)
        => _turns.EvaluateHandCardPresentation(playerId, card, isMyActiveTurn);

    public bool UseExtraPp()
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;
        bool ok = _turns.UseExtraPp(_state.ActivePlayer);
        DrainVm();
        return ok;
    }

    public void Attack(RuntimeEntity attacker, RuntimeEntity defender)
    {
        if (Phase != MatchPhase.Playing || IsWaitingForInput) return;
        _combat.ExecuteCombat(attacker, defender, _vm);
        DrainVm();
    }

    // ---------------------------------------------------------------- 合法动作枚举

    /// <summary>
    /// 枚举当前回合玩家的全部合法操作（76-c 2.3 的六类主阶段动作）。
    ///
    /// 有挂起选择时返回空列表：必须先消解选择，才谈得上「下一步能做什么」。
    /// 这是后端能自己跑完整对局的前提 —— 没有它，Bot 无处下手。
    /// </summary>
    public List<PlayerAction> GetLegalActions()
    {
        var list = new List<PlayerAction>();
        if (Phase != MatchPhase.Playing) return list;
        if (IsWaitingForInput) return list;

        int p = _state.ActivePlayer;
        var me = _state.Players[p];
        var foe = _state.Players[1 - p];

        // 1. 出牌
        foreach (var card in me.Hand.ToList())
        {
            if (card?.Def == null) continue;
            if (_turns.CanPlayCard(p, card, out _))
                list.Add(new PlayerAction { Kind = PlayerActionKind.PlayCard, Subject = card });
        }

        // 2. 启动护符
        foreach (var a in me.Board.ToList())
        {
            if (a?.Def == null) continue;
            if (_turns.CanEngageAmulet(p, a, out _))
                list.Add(new PlayerAction { Kind = PlayerActionKind.EngageAmulet, Subject = a });
        }

        // 3. 融合：主体 × 每张可用材料各产出一条（单选材料，够覆盖绝大多数卡）
        foreach (var b in me.Hand.ToList())
        {
            if (b?.Def == null || !b.Def.HasFuse) continue;
            foreach (var m in me.Hand.ToList())
            {
                if (ReferenceEquals(m, b)) continue;
                if (!_turns.CanFuseCards(p, b, new[] { m }, out _)) continue;
                list.Add(new PlayerAction
                {
                    Kind = PlayerActionKind.FuseCards,
                    Subject = b,
                    Materials = new[] { m },
                });
            }
        }

        // 4. 进化 / 超进化
        // 必须**逐个随从**调用带 unit 的重载。
        // 用无参版做外层开关的话，玩家 EP = 0 时整块被跳过 ——
        // 哪怕随从拿到了 free_evolve，界面上也不会出现进化按钮。
        foreach (var e in me.Board.ToList())
        {
            if (e?.Def == null || e.Def.Kind != Ast.CardKind.Follower) continue;
            if (e.IsEvolved) continue;
            if (_turns.CanEvolve(p, e))
                list.Add(new PlayerAction { Kind = PlayerActionKind.Evolve, Subject = e });
            if (_turns.CanSuperEvolve(p, e))
                list.Add(new PlayerAction { Kind = PlayerActionKind.SuperEvolve, Subject = e });
        }

        // 5. 额外能量点
        if (_turns.CanUseExtraPp(p))
            list.Add(new PlayerAction { Kind = PlayerActionKind.UseExtraPp });

        // 6. 攻击
        foreach (var atk in me.Board.ToList())
        {
            if (atk?.Def == null || atk.Def.Kind != Ast.CardKind.Follower) continue;
            foreach (var def in foe.Board.ToList())
            {
                if (def?.Def == null) continue;
                if (_combat.CanAttack(atk, def, out _))
                    list.Add(new PlayerAction { Kind = PlayerActionKind.Attack, Subject = atk, Target = def });
            }
            if (foe.Leader != null && _combat.CanAttack(atk, foe.Leader, out _))
                list.Add(new PlayerAction { Kind = PlayerActionKind.Attack, Subject = atk, Target = foe.Leader });
        }

        // 7. 结束回合永远合法
        list.Add(new PlayerAction { Kind = PlayerActionKind.EndTurn });
        return list;
    }

    /// <summary>执行一个枚举出来的动作。</summary>
    public bool Execute(PlayerAction a)
    {
        if (a == null) return false;
        switch (a.Kind)
        {
            case PlayerActionKind.PlayCard: return PlayCard(a.Subject);
            case PlayerActionKind.EngageAmulet: return EngageAmulet(a.Subject);
            case PlayerActionKind.FuseCards: return FuseCards(a.Subject, a.Materials);
            case PlayerActionKind.Evolve: return Evolve(a.Subject, false);
            case PlayerActionKind.SuperEvolve: return Evolve(a.Subject, true);
            case PlayerActionKind.UseExtraPp: return UseExtraPp();
            case PlayerActionKind.Attack: Attack(a.Subject, a.Target); return true;
            case PlayerActionKind.EndTurn: EndTurn(); return true;
            default: return false;
        }
    }
}

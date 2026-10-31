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
    public void Start(int firstPlayer, IReadOnlyList<int> deck0, IReadOnlyList<int> deck1,
                      int leaderLife = 20)
    {
        if (firstPlayer is not (0 or 1))
            throw new ArgumentOutOfRangeException(nameof(firstPlayer));

        // 内置衍生卡必须先就位：效果召唤 Fairy / 神器、换 Apocalypse 牌库、
        // 生成土之印与线索护符都要读 CardDef，缺一个就会造出 Def == null 的空壳。
        _state.RegisterBuiltinTokens();

        CreateLeader(0, leaderLife);
        CreateLeader(1, leaderLife);

        InitCounters(0);
        InitCounters(1);

        FillDeck(0, deck0);
        FillDeck(1, deck1);
        ShuffleDeck(0);
        ShuffleDeck(1);

        // 信仰必须在抽牌之前生成（它扫牌组内容）
        _state.SetupFaith();

        _state.DrawCards(0, 4);
        _state.DrawCards(1, 4);

        _state.ActivePlayer = firstPlayer;
        _state.TurnNumber = 0;
        Phase = MatchPhase.Mulligan;

        // 对局开始事件。「At the start of the match」类能力靠它。
        // 放在最后发：此时双方主战者、计数器、牌库、信仰都已就位，
        // 监听方读到的是一副完整的开局状态。
        _state.EmitEvent(new VmEvent { Kind = CoreEvent.BattleStart });
        _vm?.StepUntilBlocked();
    }

    private void CreateLeader(int playerId, int life)
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
            });
        }
        var e = _state.CreateEntity(cardId, ZoneRef.Leader, playerId);
        e.Life = life;
        e.MaxLife = life;
        _state.Players[playerId].Leader = e;
    }

    private void InitCounters(int playerId)
    {
        _state.WriteCounter(playerId, _schema.Counter("pp"), 0);
        _state.WriteCounter(playerId, _schema.Counter("max_pp"), 0);
        _state.WriteCounter(playerId, _schema.Counter("ep"), 2);
        _state.WriteCounter(playerId, _schema.Counter("sep"), 2);
        _state.WriteCounter(playerId, _schema.Counter("shadow"), 0);
        _state.WriteCounter(playerId, _schema.Counter("combo"), 0);
        _state.WriteCounter(playerId, _schema.Counter("rally"), 0);
        _state.WriteCounter(playerId, _schema.Counter("evolve_count"), 0);
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
    public PendingChoice PendingChoice => _vm?.Pending;

    /// <summary>VM 是否正在等玩家做选择。此时禁止任何主动操作。</summary>
    public bool IsWaitingForInput => _vm != null && _vm.Pending != null;

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
        if (IsWaitingForInput) return false;
        bool ok = _turns.PlayCard(_state.ActivePlayer, card, _vm);
        DrainVm();
        return ok;
    }

    public bool Evolve(RuntimeEntity e, bool super)
    {
        if (IsWaitingForInput) return false;
        bool ok = _turns.Evolve(_state.ActivePlayer, e, super, _vm);
        DrainVm();
        return ok;
    }

    public bool EngageAmulet(RuntimeEntity a, int cost)
    {
        if (IsWaitingForInput) return false;
        bool ok = _turns.EngageAmulet(_state.ActivePlayer, a, cost, _vm);
        DrainVm();
        return ok;
    }

    /// <summary>以卡面声明的启动费启动护符。</summary>
    public bool EngageAmulet(RuntimeEntity a)
    {
        if (IsWaitingForInput) return false;
        bool ok = _turns.EngageAmulet(_state.ActivePlayer, a, _vm);
        DrainVm();
        return ok;
    }

    public bool FuseCards(RuntimeEntity b, IReadOnlyList<RuntimeEntity> m)
    {
        if (IsWaitingForInput) return false;
        bool ok = _turns.FuseCards(_state.ActivePlayer, b, m, _vm);
        DrainVm();
        return ok;
    }

    public bool UseExtraPp()
    {
        if (IsWaitingForInput) return false;
        bool ok = _turns.UseExtraPp(_state.ActivePlayer);
        DrainVm();
        return ok;
    }

    public void Attack(RuntimeEntity attacker, RuntimeEntity defender)
    {
        if (IsWaitingForInput) return;
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
        bool canEv = _turns.CanEvolve(p);
        bool canSep = _turns.CanSuperEvolve(p);
        if (canEv || canSep)
        {
            foreach (var e in me.Board.ToList())
            {
                if (e?.Def == null || e.Def.Kind != Ast.CardKind.Follower) continue;
                if (e.IsEvolved) continue;
                if (canEv) list.Add(new PlayerAction { Kind = PlayerActionKind.Evolve, Subject = e });
                if (canSep) list.Add(new PlayerAction { Kind = PlayerActionKind.SuperEvolve, Subject = e });
            }
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

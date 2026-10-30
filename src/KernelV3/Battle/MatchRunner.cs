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
        CheckFinished();
    }

    // ---------------------------------------------------------------- 对局循环

    /// <summary>结束当前回合，交给对手。</summary>
    public void EndTurn()
    {
        if (Phase != MatchPhase.Playing) return;
        int cur = _state.ActivePlayer;
        _turns.EndTurn(cur, _vm);
        if (CheckFinished()) return;
        _turns.StartTurn(1 - cur, _vm);
        CheckFinished();
    }

    private bool CheckFinished()
    {
        if (_turns.Winner() == -1) return false;
        Phase = MatchPhase.Finished;
        return true;
    }

    // ---------------------------------------------------------------- 操作代理

    public bool PlayCard(RuntimeEntity card) => _turns.PlayCard(_state.ActivePlayer, card, _vm);
    public bool Evolve(RuntimeEntity e, bool super) => _turns.Evolve(_state.ActivePlayer, e, super, _vm);
    public bool EngageAmulet(RuntimeEntity a, int cost) => _turns.EngageAmulet(_state.ActivePlayer, a, cost, _vm);
    public bool FuseCards(RuntimeEntity b, IReadOnlyList<RuntimeEntity> m)
        => _turns.FuseCards(_state.ActivePlayer, b, m, _vm);
    public bool UseExtraPp() => _turns.UseExtraPp(_state.ActivePlayer);

    public void Attack(RuntimeEntity attacker, RuntimeEntity defender)
        => _combat.ExecuteCombat(attacker, defender, _vm);
}

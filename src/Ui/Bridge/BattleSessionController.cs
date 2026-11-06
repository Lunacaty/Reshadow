using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.Ui.Bridge;

/// <summary>
/// 战斗桌面的纯 C# 控制器（零 Godot 依赖，可在无头工程里直接单测）。
///
/// 职责边界：
///   · 只做「把 UI 意图翻译成 MatchRunner 调用」与「把状态翻译成 UI 可读的值」；
///   · 不画任何东西、不碰任何节点；
///   · 所有规则判定一律问 MatchRunner / BattleState，自己不猜。
///
/// 一旦这里 using Godot，Regress.BattleUi 就再也编不过 —— 这是硬边界。
/// </summary>
public sealed class BattleSessionController
{
    public GameSessionStore Store { get; }
    public BattleState State { get; }
    public MatchRunner Runner { get; }
    public GameSchema Schema => State.Schema;

    /// <summary>我方玩家 id（本地视角）。</summary>
    public int MyPlayerId { get; }

    public BattleSessionController(GameSessionStore store, ConstructedDeck myDeck,
                                   ConstructedDeck foeDeck, int seed = 0,
                                   bool startInMulligan = false)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Store.Initialize();

        var pool = Store.GetPoolRuntime(myDeck.PoolId)
                   ?? throw new InvalidOperationException("找不到卡池：" + myDeck.PoolId);

        MyPlayerId = 0;

        State = pool.CreateBattleState(seed);
        Runner = new MatchRunner(State, State.Schema);

        // 关键：**不设** AutoChoicePolicy。
        // 之前为了让第二轮先跑起来，设成 FirstLegalChoicePolicy 让 VM 替玩家
        // 自动乱选第 1 个目标 —— 那样 SelectTarget / SelectMode / Discover
        // 永远不会挂起，第三轮的交互层就无从谈起。
        // 现在玩家自己的选择真正挂起，由 PendingChoiceOverlay 接管；
        // 只有 AI 对手的选择才走 ResolveAiPendingIfAny 自动消解。
        Runner.AutoChoicePolicy = null;

        Runner.StartWithConstructedDecks(pool, 0, myDeck, foeDeck ?? myDeck);

        if (startInMulligan)
            ResolveAiPendingIfAny();
        else
            ConfirmPlayerMulligan(Array.Empty<RuntimeEntity>());
    }


    // ---------------------------------------------------------------- 联机镜像

    /// <summary>
    /// 客机模式：把权威端下发的裁剪快照铺到本地 State 上。
    ///
    /// 本地 State 只是**只读镜像**，不是真相源。所有数值都来自快照，
    /// 所以改本地内存毫无意义 —— 下一包快照推进来就覆盖回去了。
    /// 玩家操作也不在这里结算，一律经 RemoteBattleClient 上行给权威端。
    ///
    /// 隐藏卡（IsHidden / CardId == -1）用一张占位卡面填充：
    /// 客机的内存里根本不存在对手的真实卡号，这是防读牌的最后一道。
    /// </summary>
    /// <summary>远端权威端推过来的挂起选择（客机模式专用）。</summary>
    public PendingChoice RemotePendingChoice { get; private set; }

    /// <summary>
    /// 当前生效的挂起选择：客机优先读远端快照带来的那一份。
    /// UI 一律读这个属性，不直接碰 Runner.PendingChoice ——
    /// 客机本地根本不跑 VM，Runner 里永远是空的。
    /// </summary>
    public PendingChoice EffectivePendingChoice => RemotePendingChoice ?? Runner.PendingChoice;


    public void ApplyRemoteSnapshot(ShadowverseFan.KernelV3.Net.NetStateSnapshot snap)
    {
        if (snap == null || State == null) return;

        // ★ 视角归一化：本地 UI 永远以 0 = 我方（屏幕下方）、1 = 敌方（屏幕上方）。
        // 本机在权威端若是 Player 1（客机），则把权威端的 1 映射到本地 0。
        // EntityId 一律保持原样 —— 客机上行 PlayCard(entityId) 仍旧精确命中。
        int me = snap.ViewerPlayerId is 0 or 1 ? snap.ViewerPlayerId : 0;
        int MapPid(int serverPid) => serverPid < 0 ? serverPid : (serverPid == me ? 0 : 1);

        State.ActivePlayer = MapPid(snap.ActivePlayer);
        State.TurnNumber = snap.TurnNumber;

        for (int serverPid = 0; serverPid < 2 && serverPid < snap.Players.Length; serverPid++)
        {
            var dto = snap.Players[serverPid];
            if (dto == null) continue;
            int localPid = MapPid(serverPid);
            var p = State.Players[localPid];

            p.Deck.Clear();
            p.Hand.Clear();
            p.Board.Clear();
            p.CrestArea.Clear();
            p.Cemetery.Clear();
            p.Banished.Clear();

            // 牌库只发张数：补等量的占位，让界面上的张数正确，
            // 但没有任何真实卡号可读。
            for (int i = 0; i < dto.DeckCount; i++) p.Deck.Add(PlaceholderEntity(localPid, ZoneRef.Deck));
            for (int i = 0; i < dto.CemeteryCount; i++) p.Cemetery.Add(PlaceholderEntity(localPid, ZoneRef.Cemetery));
            for (int i = 0; i < dto.BanishedCount; i++) p.Banished.Add(PlaceholderEntity(localPid, ZoneRef.Banished));

            if (dto.Leader != null)
                p.Leader = MaterializeEntity(dto.Leader, localPid, ZoneRef.Leader) ?? p.Leader;

            foreach (var e in dto.Board)
            {
                var made = MaterializeEntity(e, localPid, ZoneRef.Board);
                if (made != null) p.Board.Add(made);
            }
            foreach (var e in dto.Crests)
            {
                var made = MaterializeEntity(e, localPid, ZoneRef.CrestArea);
                if (made != null) p.CrestArea.Add(made);
            }
            foreach (var e in dto.Hand)
            {
                var made = MaterializeEntity(e, localPid, ZoneRef.Hand);
                if (made != null) p.Hand.Add(made);
            }

            p.EpUnlocked = dto.EpUnlocked;
            p.SepUnlocked = dto.SepUnlocked;
            p.ManualEvolvedThisTurn = dto.ManualEvolvedThisTurn;
            p.UsedEarlyExtraPp = dto.UsedEarlyExtraPp;
            p.UsedLateExtraPp = dto.UsedLateExtraPp;
            p.UsedExtraPpThisTurn = dto.UsedExtraPpThisTurn;

            foreach (var kv in dto.Counters ?? new System.Collections.Generic.Dictionary<string, int>())
                State.WriteCounter(localPid, State.Schema.Counter(kv.Key), kv.Value);
        }

        // 还原挂起选择：客机打出一张需要选目标/选模式/发现的牌时，
        // 权威端把 Pending 发过来，不还原就永远弹不出选择框。
        if (snap.Pending != null)
        {
            System.Enum.TryParse<ChoiceKind>(snap.Pending.Kind, true, out var ck);
            RemotePendingChoice = new PendingChoice
            {
                ChoiceId = snap.Pending.ChoiceId,
                PlayerId = MapPid(snap.Pending.PlayerId),
                Kind = ck,
                MinSelect = snap.Pending.MinSelect,
                MaxSelect = snap.Pending.MaxSelect,
                CanCancel = snap.Pending.CanCancel,
                Prompt = snap.Pending.Prompt ?? "",
                Candidates = snap.Pending.Candidates ?? new System.Collections.Generic.List<int>(),
            };
        }
        else
        {
            RemotePendingChoice = null;
        }

        // ---- 还原增量事件日志 ----
        //
        // 房主端把本回合的战场事件打包进 snap.NewEvents 下发；客机若不还原，
        // 本地 State.EventLog 永远为空，BattleScreenRoot.ConsumeNewEventsToVfx()
        // 每次都读到 0 条 —— 攻击冲锋、伤害飘字、受击抖动、死亡碎裂、
        // 超进化砸地在客机端全部瘫痪，只剩属性僵硬跳变。
        if (snap.NewEvents != null && snap.NewEvents.Count > 0)
        {
            foreach (var ne in snap.NewEvents)
            {
                if (!Enum.TryParse<CoreEvent>(ne.Kind, out var kind)) continue;
                int mappedPid = MapPid(ne.PlayerId);

                State.EventLog.Add(new VmEvent
                {
                    Kind = kind,
                    PlayerId = mappedPid,
                    Subject = FindLiveRef(ne.SubjectEntityId),
                    Object = FindLiveRef(ne.ObjectEntityId),
                    Amount = ne.Amount,
                    SecondaryAmount = ne.SecondaryAmount,
                    CounterName = ne.CounterName ?? "",
                    KnockedBackBySuperEvolve = ne.KnockedBackBySuperEvolve,
                });
            }
        }
    }

    /// <summary>
    /// 按 EntityId 在双方的手牌/场上/主战者里找回活体引用。
    /// 已被破坏或消失的实体找不回来，此时返回 null —— 飘字等演出仍靠
    /// Amount / Kind 驱动，不依赖主体能否解析。
    /// </summary>
    private EntityRef? FindLiveRef(int entityId)
    {
        if (entityId < 0) return null;
        foreach (var p in State.Players)
        {
            if (p == null) continue;
            var live = p.Board.FirstOrDefault(x => x.EntityId == entityId)
                    ?? p.Hand.FirstOrDefault(x => x.EntityId == entityId);
            if (live != null) return EntityRef.From(live);
            if (p.Leader != null && p.Leader.EntityId == entityId)
                return EntityRef.From(p.Leader);
        }
        return null;
    }

    /// <summary>隐藏卡：没有卡号，用一张中性占位立绘撑住手牌张数。</summary>
    private RuntimeEntity PlaceholderEntity(int playerId, ZoneRef zone)
    {
        return new RuntimeEntity
        {
            EntityId = -9000 - playerId,
            EntrySerial = 0,
            Def = null,
            PlayerId = playerId,
            Zone = zone,
            Cost = 0,
            Attack = 0,
            Life = 1,
            MaxLife = 1,
        };
    }

    private RuntimeEntity MaterializeEntity(ShadowverseFan.KernelV3.Net.NetEntityDto d, int playerId, ZoneRef zone)
    {
        if (d == null) return null;
        if (d.IsHidden || d.CardId <= 0) return PlaceholderEntity(playerId, zone);

        var def = State.CardDefOf(d.CardId);
        if (def == null) return PlaceholderEntity(playerId, zone);

        var e = new RuntimeEntity
        {
            EntityId = d.EntityId,
            EntrySerial = d.EntrySerial,
            Def = def,
            PlayerId = playerId,
            Zone = zone,
            Cost = d.Cost,
            Attack = d.Attack,
            Life = d.Life,
            MaxLife = d.MaxLife,
            IsEvolved = d.IsEvolved,
            IsSuperEvolved = d.IsSuperEvolved,
            IsSilenced = d.IsSilenced,
            IsRevealed = d.IsRevealed,
            EnteredTurn = d.EnteredTurn,
            AttacksThisTurn = d.AttacksThisTurn,
            AttackedThisTurn = d.AttackedThisTurn,
            EngagedThisTurn = d.EngagedThisTurn,
            FusedThisTurn = d.FusedThisTurn,
            IsFaith = d.IsFaith,
        };

        // 关键词 / 兵种 / 计数器：快照里已经发过来了，不还原的话
        // 客机画面上看不到守护外框、必杀图标，也看不到护符的吟唱倒计时。
        foreach (var kw in d.Keywords ?? new System.Collections.Generic.List<string>())
            if (!string.IsNullOrEmpty(kw)) e.Keywords.Add(State.Schema.Keyword(kw));
        foreach (var tr in d.Traits ?? new System.Collections.Generic.List<string>())
            if (!string.IsNullOrEmpty(tr)) e.Traits.Add(State.Schema.Trait(tr));
        foreach (var kv in d.Counters ?? new System.Collections.Generic.Dictionary<string, int>())
            if (!string.IsNullOrEmpty(kv.Key)) e.Counters[State.Schema.Counter(kv.Key)] = kv.Value;

        return e;
    }

    // ---------------------------------------------------------------- 换牌

    public bool IsInMulligan => Runner.Phase == MatchPhase.Mulligan;

    /// <summary>
    /// 确认我方起手换牌，同时替 AI 对手换掉高费牌，然后正式进入第 1 回合。
    /// </summary>
    public void ConfirmPlayerMulligan(IReadOnlyList<RuntimeEntity> myReturningCards)
    {
        if (Runner.Phase != MatchPhase.Mulligan) return;

        if (myReturningCards != null && myReturningCards.Count > 0)
            Runner.Mulligan(MyPlayerId, myReturningCards);

        // AI 的简单策略：换掉费用 >= 5 的卡。
        int foeId = 1 - MyPlayerId;
        var foeReturn = Hand(foeId).Where(c => c.Cost >= 5).ToList();
        if (foeReturn.Count > 0)
            Runner.Mulligan(foeId, foeReturn);

        Runner.ConfirmMulligan();
        ResolveAiPendingIfAny();
    }

    /// <summary>
    /// 若当前挂起的选择属于 AI 对手，自动替它完成；
    /// 属于我方则**保持挂起**，交给 UI。
    /// </summary>
    public void ResolveAiPendingIfAny()
    {
        int guard = 0;
        while (Runner.IsWaitingForInput && Runner.PendingChoice != null)
        {
            if (Runner.PendingChoice.PlayerId == MyPlayerId) return;
            if (++guard > 200) break;

            var chosen = FirstLegalChoicePolicy.Instance.Select(State, Runner.PendingChoice);
            Runner.SubmitChoice(Runner.PendingChoice.ChoiceId, chosen);
        }
    }

    /// <summary>当前挂起的 BranchModeNode（供 SelectMode 弹窗渲染每个选项的中文描述）。</summary>
    public BranchModeNode GetPendingBranchModeNode()
    {
        if (Runner.PendingChoice?.Kind != ChoiceKind.SelectMode) return null;

        // 发动前预选阶段 VM 还没开始跑、没有任何帧，
        // 此时要从这张卡的 AST 里把那个 BranchModeNode 找回来。
        if (Runner.PreAction?.Subject != null)
        {
            foreach (var ab in Runner.PreAction.Subject.Def?.Abilities ??
                                new System.Collections.Generic.List<ShadowverseFan.KernelV3.Ast.AbilityDef>())
            {
                if (ab?.Body == null) continue;
                foreach (var n in ab.Body)
                    if (n is BranchModeNode bm && bm.Kind == ModeDispatchKind.Choose) return bm;
            }
            return null;
        }

        var frames = Runner.Vm?.Frames;
        if (frames == null || frames.Count == 0) return null;
        var top = frames[^1];
        return top.Ip < top.Code.Count ? top.Code[top.Ip] as BranchModeNode : null;
    }

    /// <summary>当前挂起选择的来源实体（弹窗左侧展示「是哪张卡发起的」）。</summary>
    /// <summary>当前挂起是否可被玩家撤回（预选阶段才可）。</summary>
    public bool CanCancelPending => Runner.PendingChoice?.CanCancel == true;

    /// <summary>取消发动前预选：卡退回手牌、PP/EP 零消耗。</summary>
    public bool CancelPendingChoice() => Runner.CancelPendingChoice();

    public RuntimeEntity GetPendingSourceEntity()
    {
        // 预选阶段 VM 还没有帧，来源就是 PreAction.Subject
        if (Runner.PreAction?.Subject != null) return Runner.PreAction.Subject;

        var frames = Runner.Vm?.Frames;
        if (frames == null || frames.Count == 0) return null;
        var srcRef = frames[^1].Scope?.Source;
        if (srcRef == null) return null;
        return State.GetLive(srcRef.Value.EntityId, srcRef.Value.EntrySerial);
    }

    /// <summary>
    /// 推进 AI 对手回合一步（UI 定时器每 0.35s 调一次，让玩家看清对手在做什么）。
    /// 返回 true = 执行了一个动作；false = 已回到我方回合或对局结束。
    /// </summary>
    public bool StepOpponentTurn()
    {
        ResolveAiPendingIfAny();
        if (IsFinished || IsMyTurn || IsWaitingForInput) return false;

        var actions = Runner.GetLegalActions();
        if (actions.Count == 0)
        {
            Runner.EndTurn();
            ResolveAiPendingIfAny();
            return false;
        }

        // 优先级：额外PP → 出牌 → 进化 → 启动护符 → 攻击 → 结束回合
        var pick = actions.FirstOrDefault(a => a.Kind == PlayerActionKind.UseExtraPp)
                ?? actions.FirstOrDefault(a => a.Kind == PlayerActionKind.PlayCard)
                ?? actions.FirstOrDefault(a => a.Kind is PlayerActionKind.Evolve or PlayerActionKind.SuperEvolve)
                ?? actions.FirstOrDefault(a => a.Kind == PlayerActionKind.EngageAmulet)
                ?? actions.FirstOrDefault(a => a.Kind == PlayerActionKind.Attack)
                ?? actions.FirstOrDefault(a => a.Kind == PlayerActionKind.EndTurn);

        if (pick == null) return false;
        Runner.Execute(pick);
        ResolveAiPendingIfAny();
        return true;
    }

    // ---------------------------------------------------------------- 回合状态

    public bool IsMyTurn => State.ActivePlayer == MyPlayerId;
    // 客机模式本地不跑 VM，Runner 里永远不是「等待输入」；
    // 真正在等选目标是权威端，靠远端快照的 Pending 体现。
    public bool IsWaitingForInput => RemotePendingChoice != null || Runner.IsWaitingForInput;
    public bool IsFinished => Runner.Phase == MatchPhase.Finished;
    public int Winner => Runner.Winner();

    // ---------------------------------------------------------------- 计数读取

    public int ReadCounter(int playerId, string counterName)
    {
        if (string.IsNullOrEmpty(counterName)) return 0;
        try
        {
            var key = Schema.Counter(counterName);
            // 必须走 BattleState.ReadCounter，不能直接查字典：
            // 那里有进化/超进化解锁守卫，未解锁时恒读 0。
            return State.ReadCounter(playerId, key);
        }
        catch
        {
            return 0;
        }
    }

    public IReadOnlyList<RuntimeEntity> Hand(int playerId)
        => State.EnumerateZone(ZoneRef.Hand, playerId).ToList();

    public IReadOnlyList<RuntimeEntity> Board(int playerId)
        => State.EnumerateZone(ZoneRef.Board, playerId).ToList();

    public IReadOnlyList<RuntimeEntity> Crests(int playerId)
        => State.EnumerateZone(ZoneRef.CrestArea, playerId).ToList();

    public RuntimeEntity Leader(int playerId) => State.Players[playerId].Leader;

    // ---------------------------------------------------------------- 手牌

    public bool TryPlayCard(RuntimeEntity card, out string reason)
    {
        reason = "";
        if (card == null) { reason = "卡不存在"; return false; }
        if (!Runner.PlayCard(card))
        {
            reason = "无法打出（费用不足、时机不对或该卡不可用）";
            return false;
        }
        InvalidateLegalActions();
        ResolveAiPendingIfAny();
        return true;
    }

    public bool CanPlayCard(RuntimeEntity card)
    {
        if (card == null) return false;
        if (!IsMyTurn || IsWaitingForInput) return false;
        return GetLegalActionsCached().Any(a =>
            a.Kind == PlayerActionKind.PlayCard && a.Subject?.EntityId == card.EntityId);
    }

    // ---------------------------------------------------------------- 攻击

    public bool TryAttack(RuntimeEntity attacker, RuntimeEntity defender, out string reason)
    {
        reason = "";
        if (attacker == null || defender == null) { reason = "目标无效"; return false; }
        // Attack 是 void：先确认「这一对 (attacker, defender) 在合法动作里」，
        // 再调用。否则调用会被内部守卫静默吞掉，UI 却以为打出去了。
        bool legal = GetLegalActionsCached().Any(a =>
            a.Kind == PlayerActionKind.Attack
            && a.Subject?.EntityId == attacker.EntityId
            && a.Target?.EntityId == defender.EntityId);

        if (!legal)
        {
            reason = "无法攻击（未就绪、目标不合法或非我方回合）";
            return false;
        }

        Runner.Attack(attacker, defender);
        ResolveAiPendingIfAny();
        return true;
    }

    /// <summary>该随从本回合能否攻击（决定是否画绿/黄光环）。</summary>
    public bool CanAttackWith(RuntimeEntity unit)
    {
        if (unit == null) return false;
        if (!IsMyTurn || IsWaitingForInput) return false;
        return GetLegalActionsCached().Any(a =>
            a.Kind == PlayerActionKind.Attack && a.Subject?.EntityId == unit.EntityId);
    }

    /// <summary>当前合法攻击目标集合（用于拖拽时点亮红色锁定圈）。</summary>
    public HashSet<long> LegalAttackTargets(RuntimeEntity attacker)
    {
        var set = new HashSet<long>();
        if (attacker == null) return set;
        foreach (var a in GetLegalActionsCached())
        {
            if (a.Kind != PlayerActionKind.Attack) continue;
            if (a.Subject?.EntityId != attacker.EntityId) continue;
            if (a.Target != null) set.Add(a.Target.EntityId);
        }
        return set;
    }

    // ---------------------------------------------------------------- 进化 / 超进化

    public bool CanEvolveUnit(RuntimeEntity e)
        => e != null && e.Zone == ZoneRef.Board
           && !e.IsEvolved && !e.IsSuperEvolved
           && GetLegalActionsCached().Any(a =>
                a.Kind == PlayerActionKind.Evolve && a.Subject?.EntityId == e.EntityId);

    public bool CanSuperEvolveUnit(RuntimeEntity e)
        => e != null && e.Zone == ZoneRef.Board
           && !e.IsEvolved && !e.IsSuperEvolved
           && GetLegalActionsCached().Any(a =>
                a.Kind == PlayerActionKind.SuperEvolve && a.Subject?.EntityId == e.EntityId);

    public bool TryEvolve(RuntimeEntity e, bool super)
    {
        if (e == null) return false;
        bool ok = Runner.Evolve(e, super);
        if (ok) { InvalidateLegalActions(); ResolveAiPendingIfAny(); }
        return ok;
    }

    // ---------------------------------------------------------------- 启动 / 融合

    public bool CanEngageUnit(RuntimeEntity e, out int cost)
    {
        cost = 0;
        if (e == null || e.Zone != ZoneRef.Board) return false;
        return GetLegalActionsCached().Any(a =>
            a.Kind == PlayerActionKind.EngageAmulet && a.Subject?.EntityId == e.EntityId);
    }

    public bool TryEngage(RuntimeEntity e)
    {
        if (e == null) return false;
        bool ok = Runner.EngageAmulet(e);
        if (ok) { InvalidateLegalActions(); ResolveAiPendingIfAny(); }
        return ok;
    }

    public bool CanStartFuse(RuntimeEntity card)
    {
        if (card == null || card.Zone != ZoneRef.Hand) return false;
        return GetLegalActionsCached().Any(a =>
            a.Kind == PlayerActionKind.FuseCards && a.Subject?.EntityId == card.EntityId);
    }

    // ---- 融合选材模式（状态保存在控制器里，UI 只读） ----
    public bool IsInFuseMode { get; private set; }
    public RuntimeEntity FuseBaseCard { get; private set; }
    public List<RuntimeEntity> SelectedFuseMaterials { get; } = new();

    public void StartFuseMode(RuntimeEntity baseCard)
    {
        if (!CanStartFuse(baseCard)) return;
        IsInFuseMode = true;
        FuseBaseCard = baseCard;
        SelectedFuseMaterials.Clear();
    }

    public void ToggleFuseMaterial(RuntimeEntity mat)
    {
        if (!IsInFuseMode || mat == null || ReferenceEquals(mat, FuseBaseCard)) return;
        if (SelectedFuseMaterials.Any(m => m.EntityId == mat.EntityId))
            SelectedFuseMaterials.RemoveAll(m => m.EntityId == mat.EntityId);
        else
            SelectedFuseMaterials.Add(mat);
    }

    public bool ConfirmFuse()
    {
        if (!IsInFuseMode || FuseBaseCard == null) return false;
        bool ok = Runner.FuseCards(FuseBaseCard, SelectedFuseMaterials.ToList());
        if (ok) { CancelFuseMode(); ResolveAiPendingIfAny(); }
        return ok;
    }

    public void CancelFuseMode()
    {
        IsInFuseMode = false;
        FuseBaseCard = null;
        SelectedFuseMaterials.Clear();
    }

    // ---------------------------------------------------------------- 回合推进

    public void EndTurn()
    {
        if (IsWaitingForInput) return;

        // 只能结束自己的回合。
        //
        // 少了这道守卫，对面回合时点结束按钮也会照常执行：
        // 内核看到 ActivePlayer 是对方，于是「结束对方的回合、开始我方的回合」，
        // 表现出来就是玩家能替 AI 结束它的回合。
        if (!IsMyTurn) return;

        Runner.EndTurn();
        ResolveAiPendingIfAny();
    }

    // ---------------------------------------------------------------- 规则求值缓存

    private List<PlayerAction> _cachedLegalActions;
    private int _cachedEventCount = -1;
    private int _cachedTurn = -1;
    private int _cachedActivePlayer = -1;
    private int _cachedPp = -1;

    /// <summary>
    /// 带版本脏标记的合法动作查询。
    ///
    /// MatchRunner.GetLegalActions 每次都要对所有手牌求法术目标、
    /// 两两组合求融合材料、对全场随从跑 CanAttack（内含光环扫描）。
    /// 而一次界面刷新里 CanPlayCard / CanAttackWith / CanEvolveUnit …
    /// 会被调用十几遍 —— 全在裸调它。缓存后单帧规则求值开销降一个数量级。
    ///
    /// 失效条件挑「局面真的变了」的四个信号：事件数、回合数、当前玩家、PP。
    /// 挂起选择时一律重算：那意味着局面刚被推进到待输入点。
    /// </summary>
    private List<PlayerAction> GetLegalActionsCached()
    {
        int evCnt = State.EventLog.Count;
        int turn = State.TurnNumber;
        int ap = State.ActivePlayer;
        int pp = ReadCounter(ap, "pp");

        if (_cachedLegalActions == null
            || _cachedEventCount != evCnt
            || _cachedTurn != turn
            || _cachedActivePlayer != ap
            || _cachedPp != pp
            || Runner.IsWaitingForInput)
        {
            _cachedLegalActions = Runner.GetLegalActions();
            _cachedEventCount = evCnt;
            _cachedTurn = turn;
            _cachedActivePlayer = ap;
            _cachedPp = pp;
        }
        return _cachedLegalActions;
    }

    /// <summary>动作已执行：立刻作废缓存，避免读到上一帧的旧结论。</summary>
    private void InvalidateLegalActions() => _cachedLegalActions = null;

    public bool CanUseExtraPp()
        => GetLegalActionsCached().Any(a => a.Kind == PlayerActionKind.UseExtraPp);

    public bool TryUseExtraPp()
    {
        bool ok = Runner.UseExtraPp();
        if (ok) ResolveAiPendingIfAny();
        return ok;
    }

    // ---------------------------------------------------------------- Extra PP

    /// <summary>该玩家是否是后手（只有后手才有 Extra PP 框）。</summary>
    /// 注意 State.FirstPlayer 是 int（玩家 id），不是 PlayerState 对象 ——
    /// 早先拿它跟 PlayerState 比 Equals，永远不等，于是两边都被判成后手。
    public bool IsSecondPlayer(int playerId)
        => playerId != State.FirstPlayer;

    /// <summary>
    /// 后手的 Extra PP 当前是否处于「耀金可用态」。
    ///
    /// 规则：1~5 回合看 UsedEarlyExtraPp；第 6 回合起刷新，看 UsedLateExtraPp。
    /// 这不是 UI 自定义规则 —— 是内核实际使用的判定，这里只是暴露给界面。
    /// </summary>
    public bool IsExtraPpAvailableInPhase(int playerId)
    {
        if (!State.Rules.EnableExtraPpForSecondPlayer) return false;
        if (!IsSecondPlayer(playerId)) return false;

        var p = State.Players[playerId];
        bool early = State.TurnNumber <= State.Rules.ExtraPpEarlyPhaseLastTurn;
        return early ? !p.UsedEarlyExtraPp : !p.UsedLateExtraPp;
    }

    // ---------------------------------------------------------------- 拖拽判定

    /// <summary>手牌区上沿：松手位置高于这条线才算「拖到战场」。</summary>
    /// <summary>
    /// 出牌判定线的基准值（720p 下）。
    ///
    /// 真正的判定必须用 HandZoneTopYFor(视口高) —— 手牌区锚在窗口底部，
    /// 判定线也必须跟着走。窗口拉到 1080 时手牌在 Y≈950，
    /// 却要拖到 Y&lt;500（屏幕上半截）才能打出，手感完全错乱。
    /// 这个常量保留给无视口上下文的调用方与既有门禁。
    /// </summary>
    public const float HandZoneTopY = 500f;

    /// <summary>出牌判定线距窗口底部的恒定距离（720 - 500 = 220）。</summary>
    public const float HandZoneBottomGap = 220f;

    /// <summary>按实际视口高度算出的出牌判定线。</summary>
    public static float HandZoneTopYFor(float viewportHeight)
        => viewportHeight > 0f ? viewportHeight - HandZoneBottomGap : HandZoneTopY;

    /// <summary>
    /// 拖拽松手时是否应当打出这张牌。
    ///
    /// 抽成纯函数是为了可测：真实判定发生在 Godot 的 _Input 里，
    /// 而事件没法在无头工程里模拟 —— 但「松手位置 + 卡是否可打」这段
    /// 逻辑是可以完全确定的，把它留在这里就能被门禁覆盖。
    /// </summary>
    public static bool ShouldPlayOnRelease(float releaseGlobalY, bool isDraggingCard)
        => isDraggingCard && releaseGlobalY < HandZoneTopY;

    /// <summary>
    /// 攻击松手时该打谁：先判主战者，再判随从。
    /// 命中框以矩形描述，调用方传入实际屏幕矩形。
    /// </summary>
    public RuntimeEntity ResolveAttackTarget(
        RuntimeEntity attacker,
        (Rect2Like rect, RuntimeEntity entity)[] candidates,
        float releaseX, float releaseY)
    {
        if (attacker == null) return null;
        var legal = LegalAttackTargets(attacker);

        foreach (var (rect, entity) in candidates)
        {
            if (entity == null) continue;
            if (!legal.Contains(entity.EntityId)) continue;
            if (releaseX >= rect.X && releaseX <= rect.X + rect.W
                && releaseY >= rect.Y && releaseY <= rect.Y + rect.H)
                return entity;
        }
        return null;
    }

    // ---------------------------------------------------------------- 挂起选择

    public bool HasPendingChoice => Runner.IsWaitingForInput;

    public void SubmitChoice(int choiceId, params int[] selected)
    {
        Runner.SubmitChoice(choiceId, selected);
        // 玩家做完选择后，若接下来轮到 AI 的选择就替它消解；
        // 若又轮到我方则保持挂起继续等 UI。
        ResolveAiPendingIfAny();
    }
}

/// <summary>卡面上的能力类型标记（由「红色书页 / 绿色旗子 / 空」三态驱动）。</summary>
public enum UnitAbilityBadge
{
    /// <summary>无谢幕曲也无时机能力 —— 底部留空，不画图标。</summary>
    None,
    /// <summary>有谢幕曲（Last Words）—— 红色书页图标。</summary>
    LastWords,
    /// <summary>有时机能力（在 XX 时）—— 绿色旗帜图标。</summary>
    Triggered,
}

/// <summary>从 CardDef 判定该显示哪种底部标记。</summary>
public static class UnitAbilityBadgeResolver
{
    public static UnitAbilityBadge Resolve(CardDef def)
    {
        if (def?.Abilities == null || def.Abilities.Count == 0) return UnitAbilityBadge.None;

        bool hasLastWords = false;
        bool hasTriggered = false;

        foreach (var ab in def.Abilities)
        {
            if (ab?.Trigger == null) continue;
            var ev = ab.Trigger.Event;

            // (1) 谢幕曲 -> 红色书页
            if (ev == CoreEvent.OnDestroy)
            {
                hasLastWords = true;
                continue;
            }

            // (2) 明确排除：这些不是「在场上持续监听的时机能力」。
            //     入场曲用完就没了，PassiveAura 是常驻关键词，
            //     OnEngage 是玩家主动启动，BattleStart 是开局一次性。
            //     把这几类画成绿旗，会让每张普通随从底部都亮一个图标。
            if (ev is CoreEvent.OnPlay or CoreEvent.PassiveAura
                   or CoreEvent.OnEngage or CoreEvent.BattleStart)
                continue;

            // (3) 手动进化冒号头也即时结算，不常驻监听
            if ((ev is CoreEvent.OnEvolve or CoreEvent.OnSuperEvolve) && ab.Trigger.ManualEvolveOnly)
                continue;

            // (4) 必须在场上才监听的才算
            if (ab.Trigger.ActiveZone != ActiveZoneScope.Board)
                continue;

            hasTriggered = true;
        }

        if (hasLastWords) return UnitAbilityBadge.LastWords;
        if (hasTriggered) return UnitAbilityBadge.Triggered;
        return UnitAbilityBadge.None;
    }

    /// <summary>该能力是否会在触发时发出一圈光效（时机能力）。</summary>
    public static bool FlashesOnTrigger(UnitAbilityBadge badge)
        => badge == UnitAbilityBadge.Triggered;
}

/// <summary>与 Godot.Rect2 字段兼容的最小矩形（避免 Bridge 层引用 Godot）。</summary>
public readonly struct Rect2Like
{
    public readonly float X, Y, W, H;
    public Rect2Like(float x, float y, float w, float h) { X = x; Y = y; W = w; H = h; }
}

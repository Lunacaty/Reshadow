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
    public bool IsWaitingForInput => Runner.IsWaitingForInput;
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
        ResolveAiPendingIfAny();
        return true;
    }

    public bool CanPlayCard(RuntimeEntity card)
    {
        if (card == null) return false;
        if (!IsMyTurn || IsWaitingForInput) return false;
        return Runner.GetLegalActions().Any(a =>
            a.Kind == PlayerActionKind.PlayCard && a.Subject?.EntityId == card.EntityId);
    }

    // ---------------------------------------------------------------- 攻击

    public bool TryAttack(RuntimeEntity attacker, RuntimeEntity defender, out string reason)
    {
        reason = "";
        if (attacker == null || defender == null) { reason = "目标无效"; return false; }
        // Attack 是 void：先确认「这一对 (attacker, defender) 在合法动作里」，
        // 再调用。否则调用会被内部守卫静默吞掉，UI 却以为打出去了。
        bool legal = Runner.GetLegalActions().Any(a =>
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
        return Runner.GetLegalActions().Any(a =>
            a.Kind == PlayerActionKind.Attack && a.Subject?.EntityId == unit.EntityId);
    }

    /// <summary>当前合法攻击目标集合（用于拖拽时点亮红色锁定圈）。</summary>
    public HashSet<long> LegalAttackTargets(RuntimeEntity attacker)
    {
        var set = new HashSet<long>();
        if (attacker == null) return set;
        foreach (var a in Runner.GetLegalActions())
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
           && Runner.GetLegalActions().Any(a =>
                a.Kind == PlayerActionKind.Evolve && a.Subject?.EntityId == e.EntityId);

    public bool CanSuperEvolveUnit(RuntimeEntity e)
        => e != null && e.Zone == ZoneRef.Board
           && !e.IsEvolved && !e.IsSuperEvolved
           && Runner.GetLegalActions().Any(a =>
                a.Kind == PlayerActionKind.SuperEvolve && a.Subject?.EntityId == e.EntityId);

    public bool TryEvolve(RuntimeEntity e, bool super)
    {
        if (e == null) return false;
        bool ok = Runner.Evolve(e, super);
        if (ok) ResolveAiPendingIfAny();
        return ok;
    }

    // ---------------------------------------------------------------- 启动 / 融合

    public bool CanEngageUnit(RuntimeEntity e, out int cost)
    {
        cost = 0;
        if (e == null || e.Zone != ZoneRef.Board) return false;
        return Runner.GetLegalActions().Any(a =>
            a.Kind == PlayerActionKind.EngageAmulet && a.Subject?.EntityId == e.EntityId);
    }

    public bool TryEngage(RuntimeEntity e)
    {
        if (e == null) return false;
        bool ok = Runner.EngageAmulet(e);
        if (ok) ResolveAiPendingIfAny();
        return ok;
    }

    public bool CanStartFuse(RuntimeEntity card)
    {
        if (card == null || card.Zone != ZoneRef.Hand) return false;
        return Runner.GetLegalActions().Any(a =>
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

    public bool CanUseExtraPp()
        => Runner.GetLegalActions().Any(a => a.Kind == PlayerActionKind.UseExtraPp);

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

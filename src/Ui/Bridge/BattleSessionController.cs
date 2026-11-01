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
                                   ConstructedDeck foeDeck, int seed = 0)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
        Store.Initialize();

        var pool = Store.GetPoolRuntime(myDeck.PoolId)
                   ?? throw new InvalidOperationException("找不到卡池：" + myDeck.PoolId);

        State = pool.CreateBattleState(seed);
        Runner = new MatchRunner(State, State.Schema);
        Runner.AutoChoicePolicy = FirstLegalChoicePolicy.Instance;

        Runner.StartWithConstructedDecks(pool, 0, myDeck, foeDeck ?? myDeck);
        Runner.ConfirmMulligan();

        MyPlayerId = 0;
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
            return State.Players[playerId].Counters.TryGetValue(key.Id, out int v) ? v : 0;
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
        return Runner.Evolve(e, super);
    }

    // ---------------------------------------------------------------- 启动 / 融合

    public bool CanEngageUnit(RuntimeEntity e, out int cost)
    {
        cost = 0;
        if (e == null || e.Zone != ZoneRef.Board) return false;
        return Runner.GetLegalActions().Any(a =>
            a.Kind == PlayerActionKind.EngageAmulet && a.Subject?.EntityId == e.EntityId);
    }

    public bool TryEngage(RuntimeEntity e) => e != null && Runner.EngageAmulet(e);

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
        if (ok) CancelFuseMode();
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
        Runner.EndTurn();
    }

    public bool CanUseExtraPp()
        => Runner.GetLegalActions().Any(a => a.Kind == PlayerActionKind.UseExtraPp);

    public bool TryUseExtraPp() => Runner.UseExtraPp();

    // ---------------------------------------------------------------- 挂起选择

    public bool HasPendingChoice => Runner.IsWaitingForInput;

    public void SubmitChoice(int choiceId, params int[] selected)
        => Runner.SubmitChoice(choiceId, selected);
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
            if (ab.Trigger.Event == CoreEvent.OnDestroy || ab.Trigger.Event == CoreEvent.OnBanish)
                hasLastWords = true;
            else
                hasTriggered = true;
        }

        // 谢幕曲优先：官方卡面上两种同时存在时，左下是红色书页、右上是绿旗，
        // 我们只有一个槽位，取信息量更大的谢幕曲（它决定「这张卡死后还会做事」）。
        if (hasLastWords) return UnitAbilityBadge.LastWords;
        if (hasTriggered) return UnitAbilityBadge.Triggered;
        return UnitAbilityBadge.None;
    }

    /// <summary>该能力是否会在触发时发出一圈光效（时机能力）。</summary>
    public static bool FlashesOnTrigger(UnitAbilityBadge badge)
        => badge == UnitAbilityBadge.Triggered;
}
using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Battle;

// ============================================================================
// 攻击合法性校验 + 战斗五步时序
//
// 这一层把「随从乱打人」的错误挡在门外：
//   · 召唤失调的随从不能攻击
//   · 突进/进化只能打随从，不能打脸
//   · 有守护时只能打守护
//   · 潜伏/威慑永远不能被选为目标
// ============================================================================

public sealed class CombatResolver
{
    private readonly BattleState _state;
    private readonly GameSchema _schema;

    public CombatResolver(BattleState state, GameSchema schema)
    {
        _state = state;
        _schema = schema ?? state.Schema;
    }

    private bool Has(RuntimeEntity e, string keyword)
        => e.Keywords.Contains(_schema.Keyword(keyword));

    /// <summary>攻击是否合法。返回 false 时 reason 说明原因。</summary>
    public bool CanAttack(RuntimeEntity attacker, RuntimeEntity defender, out string reason)
    {
        reason = null;
        if (attacker == null) { reason = "攻击者为空"; return false; }
        if (attacker.Zone != ZoneRef.Board) { reason = "攻击者不在场上"; return false; }
        if (attacker.Attack <= 0) { reason = "攻击力为 0"; return false; }
        if (defender == null) { reason = "目标为空"; return false; }

        bool isLeaderTarget = defender.Zone == ZoneRef.Leader;
        if (!isLeaderTarget && defender.Zone != ZoneRef.Board) { reason = "目标不在场上"; return false; }
        if (defender.PlayerId == attacker.PlayerId) { reason = "不能攻击自己方的目标"; return false; }

        // 攻击次数上限
        int maxAttacks = 1;
        var maxKey = _schema.Keyword("max_attacks_per_turn");
        if (attacker.KeywordValues.TryGetValue(maxKey, out var mv)) maxAttacks = mv;
        if (attacker.AttacksThisTurn >= maxAttacks) { reason = "本回合攻击次数已用尽"; return false; }

        // 召唤失调
        if (attacker.EnteredTurn >= _state.TurnNumber)
        {
            bool storm = Has(attacker, "storm");
            bool rush = Has(attacker, "rush") || attacker.IsEvolved || attacker.IsSuperEvolved;
            if (!storm && !rush) { reason = "召唤失调，且没有疾走或突进"; return false; }
            if (!storm && isLeaderTarget) { reason = "突进/进化当回合不能攻击主战者"; return false; }
        }

        // 目标过滤
        if (!isLeaderTarget)
        {
            if (Has(defender, "ambush") || Has(defender, "intimidate"))
            { reason = "潜伏/威慑的随从不能被选为目标"; return false; }
        }

        // 守护
        bool ignoresWard = Has(attacker, "ignores ward");
        if (!ignoresWard)
        {
            var guards = _state.EnumerateZone(ZoneRef.Board, defender.PlayerId)
                .Where(e => Has(e, "ward") && !Has(e, "ambush"))
                .ToList();
            if (guards.Count > 0)
            {
                if (isLeaderTarget) { reason = "敌方有守护随从，不能攻击主战者"; return false; }
                if (!guards.Any(g => g.EntityId == defender.EntityId))
                { reason = "敌方有守护随从，只能攻击守护"; return false; }
            }
        }

        return true;
    }

    /// <summary>战斗五步时序：宣告 -> OnAttack -> 伤害 -> OnDamaged -> SBA。</summary>
    public void ExecuteCombat(RuntimeEntity attacker, RuntimeEntity defender, CardVm vm)
    {
        if (!CanAttack(attacker, defender, out var reason))
            throw new InvalidOperationException("非法攻击：" + reason);

        attacker.AttacksThisTurn++;
        attacker.AttackedThisTurn = true;
        attacker.AttackedLeaderLastTurn = defender.Zone == ZoneRef.Leader;

        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnAttack, Subject = EntityRef.From(attacker) });

        int atk = attacker.Attack;
        if (defender.Zone == ZoneRef.Leader)
        {
            ApplyDamage(defender, atk, attacker.PlayerId, vm);
        }
        else
        {
            ApplyDamage(defender, atk, attacker.PlayerId, vm);
            ApplyDamage(attacker, defender.Attack, defender.PlayerId, vm);

            if (attacker.IsSuperEvolved && defender.Life <= 0)
            {
                var foe = _state.Players[defender.PlayerId].Leader;
                if (foe != null) ApplyDamage(foe, 1, attacker.PlayerId, vm);
            }
        }

        _state.ProcessStateBasedActions();
    }

    /// <summary>
    /// 统一伤害管线：超进化免伤 -> 易伤 -> 伤害上限 -> 屏障 -> 扣血 -> OnDamaged。
    /// </summary>
    public void ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId, CardVm vm = null)
    {
        if (target == null || amount <= 0) return;

        if (target.IsSuperEvolved && _state.ActivePlayer == target.PlayerId) return;

        var bonusKey = _schema.Keyword("damage_taken_bonus");
        if (target.KeywordValues.TryGetValue(bonusKey, out var bonus)) amount += bonus;

        var capKey = _schema.Keyword("damage_cap");
        if (target.KeywordValues.TryGetValue(capKey, out var cap)) amount = Math.Min(amount, cap);

        if (amount <= 0) return;

        var barrierKey = _schema.Keyword("barrier");
        if (target.Keywords.Contains(barrierKey))
        {
            target.Keywords.Remove(barrierKey);
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnDamaged, Subject = EntityRef.From(target), Amount = 0 });
            return;
        }

        target.Life -= amount;

        vm?.EnqueueEvent(new VmEvent
        {
            Kind = CoreEvent.OnDamaged,
            Subject = EntityRef.From(target),
            Amount = amount,
        });
    }
}

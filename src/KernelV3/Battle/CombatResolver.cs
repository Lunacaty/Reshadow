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

        // 目标过滤（官方术语表：潜行 / 威慑 / 灵气）
        if (!isLeaderTarget)
        {
            if (Has(defender, "ambush"))    { reason = "潜行(Ambush)的随从不能被攻击"; return false; }
            if (Has(defender, "intimidate")){ reason = "威慑(Intimidate)的随从不能被随从攻击"; return false; }
            // 灵气(aura) 不在这里：它只阻止被对手能力选中，
            // 随从仍然可以正常攻击它。写在这里会让灵气随从无法被打。
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
    /// <summary>
    /// 攻击结算 12 步（R-5.2）。
    /// 每一步都要先检查双方是否还在场，离场则跳过后续的交战与伤害交换。
    /// </summary>
    public void ExecuteCombat(RuntimeEntity attacker, RuntimeEntity defender, CardVm vm)
    {
        if (!CanAttack(attacker, defender, out var reason))
            throw new InvalidOperationException("非法攻击：" + reason);

        // 1. 攻击宣言：计数与潜行解除
        attacker.AttacksThisTurn++;
        attacker.AttackedThisTurn = true;
        attacker.AttackedLeaderLastTurn = defender.Zone == ZoneRef.Leader;
        var ambushKw = _schema.Keyword("ambush");
        if (attacker.Keywords.Contains(ambushKw)) attacker.Keywords.Remove(ambushKw);

        // 2. 攻击方【攻撃時】/【交戦時】
        vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnAttack, Subject = EntityRef.From(attacker) });

        bool isFollowerTarget = defender.Zone == ZoneRef.Board;

        // 3. 被攻击方【交戦時】（只在攻击随从时）
        if (isFollowerTarget)
        {
            vm?.EnqueueEvent(new VmEvent
            {
                Kind = CoreEvent.OnClash,
                Subject = EntityRef.From(attacker),
                Object = EntityRef.From(defender),
            });

            // 若攻击方在第 2 步就用能力破坏了对方，对方的【交戦時】不触发
            vm?.StepUntilBlocked();
        }

        _state.ProcessStateBasedActions();

        // 4. 超进化溢出 1 点（第 2 步用能力破坏对方随从时）
        if (attacker.IsSuperEvolved && isFollowerTarget && defender.Zone != ZoneRef.Board)
            OverflowDamage(attacker, defender);

        // 5/6. 双方因【攻撃時】/【交戦時】诱发的效果
        vm?.StepUntilBlocked();

        if (attacker.Zone != ZoneRef.Board) { _state.ProcessStateBasedActions(); return; }

        int atk = attacker.Attack;
        bool defenderHasBane = isFollowerTarget && Has(defender, "bane");
        bool attackerHasBane = Has(attacker, "bane");

        // 7. 攻击方造成伤害
        if (defender.Zone == ZoneRef.Leader)
        {
            ApplyDamage(defender, atk, attacker.PlayerId, vm);
            ApplyDrain(attacker, atk, vm);
        }
        else if (defender.Zone == ZoneRef.Board)
        {
            // 8. 被攻击方反击
            ApplyDamage(defender, atk, attacker.PlayerId, vm);
            ApplyDrain(attacker, atk, vm);
            ApplyDamage(attacker, defender.Attack, defender.PlayerId, vm);
            ApplyDrain(defender, defender.Attack, vm);

            // 9. 双方体力 <= 0 的破坏；必杀（Bane）即使 0 伤害也破坏
            if (attackerHasBane && defender.Zone == ZoneRef.Board) _state.DestroyEntity(defender);
            if (defenderHasBane && attacker.Zone == ZoneRef.Board) _state.DestroyEntity(attacker);
        }

        _state.ProcessStateBasedActions();

        // 10. 超进化溢出 1 点（交战破坏对方随从时）
        if (attacker.IsSuperEvolved && isFollowerTarget && defender.Zone != ZoneRef.Board)
            OverflowDamage(attacker, defender);

        // 11/12. 被破坏随从的谢幕曲等诱发效果
        vm?.StepUntilBlocked();
        _state.ProcessStateBasedActions();
    }

    private void OverflowDamage(RuntimeEntity attacker, RuntimeEntity defender)
    {
        var foe = _state.Players[defender.PlayerId].Leader;
        if (foe != null) ApplyDamage(foe, 1, attacker.PlayerId);
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

    /// <summary>
    /// 虹吸(Drain)：通过攻击造成伤害时，主战者回复等量生命。
    /// 官方术语表原文：通过攻击造成伤害时，将回复主战者与该伤害等量的生命值。
    /// </summary>
    private void ApplyDrain(RuntimeEntity source, int amount, CardVm vm)
    {
        if (!Has(source, "drain") || amount <= 0) return;
        var leader = _state.Players[source.PlayerId].Leader;
        if (leader == null) return;
        int before = leader.Life;
        leader.Life = Math.Min(leader.MaxLife, leader.Life + amount);
        int healed = leader.Life - before;
        if (healed > 0)
            vm?.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnHealed, Subject = EntityRef.From(leader), Amount = healed });
    }
}

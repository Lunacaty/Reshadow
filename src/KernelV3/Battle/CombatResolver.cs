using ShadowverseFan.KernelV3.Ast;
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

    /// <summary>
    /// 该实体此刻是否有效持有某个关键词。
    ///
    /// 必须走 BattleState.HasEffectiveKeyword 而不是直接查 Keywords：
    /// 「当本卡在场时，我方所有其他随从获得【守护】」这类光环授予的词条
    /// 根本不在目标实体的 Keywords 里，直接查会让守护彻底失效。
    /// </summary>
    private bool Has(RuntimeEntity e, string keyword)
        => _state.HasEffectiveKeyword(e, _schema.Keyword(keyword));

    /// <summary>攻击是否合法。返回 false 时 reason 说明原因。</summary>
    public bool CanAttack(RuntimeEntity attacker, RuntimeEntity defender, out string reason)
    {
        reason = null;
        if (attacker == null) { reason = "攻击者为空"; return false; }
        if (attacker.Zone != ZoneRef.Board) { reason = "攻击者不在场上"; return false; }
        if (attacker.Attack <= 0) { reason = "攻击力为 0"; return false; }

        // 「不能攻击」关键词（cannot_attack）。
        // 卡面写 Can't attack 的随从之前照样能打人。
        if (Has(attacker, "cannot_attack")) { reason = "该随从不能攻击"; return false; }
        if (defender == null) { reason = "目标为空"; return false; }

        bool isLeaderTarget = defender.Zone == ZoneRef.Leader;
        if (!isLeaderTarget && defender.Zone != ZoneRef.Board) { reason = "目标不在场上"; return false; }
        if (defender.PlayerId == attacker.PlayerId) { reason = "不能攻击自己方的目标"; return false; }

        // 护符没有攻击力和生命值，不能被攻击，也不会被打（76-c 1.1）。
        // 少了这道守卫，护符的 Life 会被打成负数并永久留在场上：
        // ProcessStateBasedActions 按 Kind 跳过非随从，没人回收它。
        if (!isLeaderTarget && defender.Def != null && defender.Def.Kind != Ast.CardKind.Follower)
        { reason = "护符不能被攻击"; return false; }

        // 「Can't attack leaders」只封主战者；随从照打（76-c 3.1 第 1 条）。
        // 「Can't attack followers」是对称的另一半。
        if (isLeaderTarget && Has(attacker, "cannot_attack_leaders"))
        { reason = "该随从不能攻击主战者"; return false; }
        if (!isLeaderTarget && Has(attacker, "cannot_attack_followers"))
        { reason = "该随从不能攻击随从"; return false; }

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
            // 守护与威慑同时拥有时「守护失效」（76-c 3.1 / 76-d Phase 5 第 5 条）：
            // 威慑随从本来就不能被随从攻击，若它还参与守护过滤，
            // 就会出现「只能打守护」与「守护不能被打」同时成立 —— 玩家一手都打不出。
            var guards = _state.EnumerateZone(ZoneRef.Board, defender.PlayerId)
                .Where(e => Has(e, "ward") && !Has(e, "ambush") && !Has(e, "intimidate"))
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
        // Object 必须带上被攻击方：「每当 X 攻击随从/主战者时」
        // 的 SecondaryFilter 就是靠它判断目标类型的，缺了它谓词恒假。
        EmitOrEnqueue(vm, new VmEvent
        {
            Kind = CoreEvent.OnAttack,
            Subject = EntityRef.From(attacker),
            Object = EntityRef.From(defender),
        });

        bool isFollowerTarget = defender.Zone == ZoneRef.Board;

        // 3. 交战（Clash）——**双方各触发一次**。
        // 事件主体永远是「这张卡自己」，Object 永远是对面的那一方，
        // 于是 $self 指自己、$opposing 指对面，两侧的 Clash: 都写得通。
        if (isFollowerTarget)
        {
            // 攻击方的交戦時
            EmitOrEnqueue(vm, new VmEvent
            {
                Kind = CoreEvent.OnClash,
                Subject = EntityRef.From(attacker),
                Object = EntityRef.From(defender),
            });
            vm?.StepUntilBlocked();
            _state.ProcessStateBasedActions(attacker != null && attacker.IsSuperEvolved);

            // 被攻击方的交戦時。只有双方都还在场才触发：
            // 若攻击方在第 2 步就用能力破坏了对方，对方已经不在场，跳过。
            //
            // 这一步**也必须进续体**：攻击方的 Strike:/Clash: 可能弹出手选目标，
            // 上面那次 StepUntilBlocked 会挂起。此时玩家还没选、防守方还活着，
            // 直接往下走就会把防守方的 Clash 提前塞进队列 —— 等玩家选完把防守方
            // 打死后，它的 Clash 依然会诈尸执行。
            if (vm != null)
            {
                vm.EnqueueContinuation(() =>
                {
                    if (attacker.Zone != ZoneRef.Board || defender.Zone != ZoneRef.Board) return;
                    // 必须走 EmitOrEnqueue：这条是**被攻击方**的交战事件。
                    // 漏改会绕过 EventLog，前端读不到防守方的 Clash，
                    // 「交战时」的防守方演出与能力触发全都不响应。
                    EmitOrEnqueue(vm, new VmEvent
                    {
                        Kind = CoreEvent.OnClash,
                        Subject = EntityRef.From(defender),
                        Object = EntityRef.From(attacker),
                        PlayerId = defender.PlayerId,
                    });
                });
            }
            else if (attacker.Zone == ZoneRef.Board && defender.Zone == ZoneRef.Board)
            {
                EmitOrEnqueue(vm, new VmEvent
                {
                    Kind = CoreEvent.OnClash,
                    Subject = EntityRef.From(defender),
                    Object = EntityRef.From(attacker),
                });
            }
        }

        _state.ProcessStateBasedActions(attacker != null && attacker.IsSuperEvolved);

        // ── 第 4～12 步：战斗结算主体 ──
        //
        // 必须排进宿主续体：第 2/3 步的【攻撃時】/【交戦時】能力可能弹出手选目标，
        // VM 会挂起等玩家输入。若这之后仍写成同步代码，玩家还没选，
        // 双方伤害就已经交换、Bane 就已经判死 —— 战斗时序被整个跳过。
        if (vm != null)
        {
            vm.EnqueueContinuation(() => ResolveCombatBody(attacker, defender, vm, isFollowerTarget));
            vm.StepUntilBlocked();
        }
        else
        {
            ResolveCombatBody(attacker, defender, vm, isFollowerTarget);
        }
    }

    /// <summary>战斗第 4～12 步：能力溢出伤害 → 伤害交换 → Bane 判死 → 谢幕曲。</summary>
    private void ResolveCombatBody(RuntimeEntity attacker, RuntimeEntity defender, CardVm vm,
                                   bool isFollowerTarget)
    {
        if (attacker == null) return;

        // 第 2～3 步的能力可能已经把某一方带走，主体结算前必须重新确认。
        if (attacker.Zone != ZoneRef.Board) { _state.ProcessStateBasedActions(); return; }

        // 溢出伤害只结算一次：第 4 步（能力击杀）与第 10 步（交战击杀）
        // 用同一个判据「defender.Zone != Board」，若不设闸门，
        // 能力击杀会在第 4 步打 1 点、第 10 步再打 1 点（实测 20→18）。
        bool overflowPaid = false;

        // 4. 超进化溢出 1 点（第 2 步用能力破坏对方随从时）
        if (attacker.IsSuperEvolved && isFollowerTarget && defender.Zone != ZoneRef.Board)
        {
            OverflowDamage(attacker, defender, vm);
            overflowPaid = true;
        }

        // 5/6. 双方因【攻撃時】/【交戦時】诱发的效果
        vm?.StepUntilBlocked();

        if (attacker.Zone != ZoneRef.Board) { _state.ProcessStateBasedActions(); return; }

        int atk = attacker.Attack;
        bool defenderHasBane = isFollowerTarget && Has(defender, "bane");
        bool attackerHasBane = Has(attacker, "bane");

        // 7. 攻击方造成伤害
        if (defender.Zone == ZoneRef.Leader)
        {
            // 必须标成 Combat：否则主战者的「不受战斗伤害 / 战斗减伤 / 上限」
            // 对随从平 A 完全失效，反而被「不受能力伤害」莫名挡住。
            int dealt = ApplyDamage(defender, atk, attacker.PlayerId, vm,
                                    Ast.DamageSourceMask.Combat);
            ApplyDrain(attacker, dealt, vm);
        }
        else if (defender.Zone == ZoneRef.Board)
        {
            // 屏障必须在伤害管线之前取快照：ApplyDamage 命中屏障时会当场把
            // barrier 关键词删掉，之后再 Has() 永远为 false，
            // 于是「被屏障抵消」与「真的造成 0 点伤害」无从区分，Bane 直接破坏对方。
            bool defenderHadBarrier = Has(defender, "barrier");
            // 攻击方的屏障同样要取快照：反击伤害打进来时它会被当场删掉。
            bool attackerHadBarrier = Has(attacker, "barrier");

            // 8. 被攻击方反击
            int dealtToDefender = ApplyDamage(defender, atk, attacker.PlayerId, vm,
                                               Ast.DamageSourceMask.Combat);
            ApplyDrain(attacker, dealtToDefender, vm);
            int dealtToAttacker = ApplyDamage(attacker, defender.Attack, defender.PlayerId, vm,
                                               Ast.DamageSourceMask.Combat);
            // 【虹吸 Drain】只在「主动发动攻击造成伤害」时回血。
            // 被攻击方的反击不触发它自己的 Drain（官方定义）。
            // 之前这里也调了 ApplyDrain(defender, ...)，
            // 于是带 Drain 的随从被打时也会回血。

            // 9. 双方体力 <= 0 的破坏；必杀（Bane）即使 0 伤害也破坏。
            // 例外：伤害被屏障 Barrier 抵消时， Bane 不生效（R-6.1）。
            bool blockedByBarrier = dealtToDefender == 0 && defenderHadBarrier;
            if (attackerHasBane && !blockedByBarrier && defender.Zone == ZoneRef.Board)
                _state.DestroyEntity(defender, byRule: false, bySuperEvolved: attacker.IsSuperEvolved);
            // 反击的 Bane 同样被屏障抵消（R-6.1 对称适用）。
            bool blockedAttackerByBarrier = dealtToAttacker == 0 && attackerHadBarrier;
            if (defenderHasBane && !blockedAttackerByBarrier && attacker.Zone == ZoneRef.Board)
                _state.DestroyEntity(attacker, byRule: false, bySuperEvolved: false);
        }

        _state.ProcessStateBasedActions(attacker != null && attacker.IsSuperEvolved);

        // 10. 超进化溢出 1 点（交战破坏对方随从时）
        if (!overflowPaid && attacker.IsSuperEvolved && isFollowerTarget && defender.Zone != ZoneRef.Board)
            OverflowDamage(attacker, defender, vm);

        // 11/12. 被破坏随从的谢幕曲等诱发效果
        // 顺序铁律（R-3.2 第 11/12 步）：
        //   先结算攻击方（当前回合玩家）在第 9 步入列的谢幕曲，
        //   再结算被攻击方（非当前回合玩家）的同类能力。
        // 既然队列已按「主客场 + 旧→新」排序，这里只需在
        // 每个待机能力结算前确保它仍在正确的主客场序上。
        vm?.StepUntilBlocked();
        _state.ProcessStateBasedActions(attacker != null && attacker.IsSuperEvolved);

        // 两轮收尾：第一轮解决破坏时新产生的待机能力，
        // 第二轮解决那些能力再次引发的破坏（例如谢幕曲再造成伤害）。
        // 不做无限循环：最多两轮，避免互相触发无休止。
        vm?.StepUntilBlocked();
        _state.ProcessStateBasedActions(attacker != null && attacker.IsSuperEvolved);
    }

    /// <summary>
    /// 超进化溢出 1 点。必须把 vm 传下去：3 参版 ApplyDamage 不派发 OnDamaged，
    /// 于是溢出伤害不会触犯「每当受到伤害时」类能力，也不会计入 Wrath。
    /// </summary>
    private void OverflowDamage(RuntimeEntity attacker, RuntimeEntity defender, CardVm vm)
    {
        // 飞脸伤害走规则：DIY 卡池可以设成 0（不飞脸）或 3。
        int dmg = _state.Rules.SuperEvolveOverflowDamage;
        if (dmg <= 0) return;
        var foe = _state.Players[defender.PlayerId].Leader;
        if (foe != null) ApplyDamage(foe, dmg, attacker.PlayerId, vm);
    }

    /// <summary>
    /// 统一伤害管线：超进化免伤 -> 易伤 -> 伤害上限 -> 屏障 -> 扣血 -> OnDamaged。
    /// </summary>
    /// <summary>统一伤害管线。返回本次**实际扣减**的生命值（被屏障抵消时为 0）。</summary>
    /// <summary>
    /// 事件出口：优先走 _state.EmitEvent（同时写入 EventLog 并转发给 VM）。
    ///
    /// 早先这里全是 vm?.EnqueueEvent —— 绕过了 EventLog，
    /// 前端读不到任何战斗事件，所以「伤害飘字、攻击冲撞、
    /// 吸血回复」全部是空气。
    /// </summary>
    private void EmitOrEnqueue(CardVm vm, VmEvent ev)
    {
        if (ev == null) return;
        if (_state.TriggerSink != null || vm == null) _state.EmitEvent(ev);
        else { _state.EventLog.Add(ev); vm.EnqueueEvent(ev); }
    }

    public int ApplyDamage(RuntimeEntity target, int amount, int sourcePlayerId,
                           CardVm vm = null,
                           DamageSourceMask source = DamageSourceMask.Ability)
    {
        if (target == null || amount <= 0) return 0;

        if (target.IsSuperEvolved && _state.ActivePlayer == target.PlayerId) return 0;

        // ---- 绝对免疫：按伤害来源精确拦截 ----
        //
        // 「不受任何伤害」永远生效；
        // 「不受战斗伤害」「不受能力伤害」只拦对应来源。
        // immune_effects 蕴含免疫能力伤害，但战斗伤害照常。
        var kwImmuneAll = _schema.Keyword("immune_damage");
        var kwImmuneCombat = _schema.Keyword("immune_combat_damage");
        var kwImmuneAbility = _schema.Keyword("immune_ability_damage");
        var kwImmuneEffects = _schema.Keyword("immune_effects");
        var kwImmuneEffectsXS = _schema.Keyword("immune_effects_except_stats");

        if (target.Keywords.Contains(kwImmuneAll)) return 0;
        if ((source & DamageSourceMask.Combat) != 0
            && target.Keywords.Contains(kwImmuneCombat)) return 0;
        if ((source & DamageSourceMask.Ability) != 0
            && (target.Keywords.Contains(kwImmuneAbility)
                || target.Keywords.Contains(kwImmuneEffects)
                || target.Keywords.Contains(kwImmuneEffectsXS))) return 0;

        // ---- 动态伤害修正（光环 + 挂载）----
        //
        // 先算「造成伤害」侧（由来源实体决定），再算「受到伤害」侧（由目标决定）。
        // 全部实时扫描，不缓存 —— 光环源离场或被沉默后立刻失效。
        amount = _state.ApplyDealtModifiers(sourcePlayerId, target, amount, source);
        amount = _state.ApplyTakenModifiers(target, amount, source);
        if (amount <= 0) return 0;

        var bonusKey = _schema.Keyword("damage_taken_bonus");
        if (target.KeywordValues.TryGetValue(bonusKey, out var bonus)) amount += bonus;

        var capKey = _schema.Keyword("damage_cap");
        if (target.KeywordValues.TryGetValue(capKey, out var cap)) amount = Math.Min(amount, cap);

        if (amount <= 0) return 0;

        var barrierKey = _schema.Keyword("barrier");
        if (target.Keywords.Contains(barrierKey))
        {
            target.Keywords.Remove(barrierKey);
            // 这里只入队、不写 EventLog：能力伤害经 BattleState.ApplyDamage 时
            // 由那个包装器统一 EmitEvent（含日志）。两条路各发一次就成了双份。
            EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnDamaged, Subject = EntityRef.From(target), Amount = 0 });
            return 0;
        }

        target.Life -= amount;

        // 血怒（Wrath）：我方**主战者**每受到一次伤害就 +1。
        // 随从受伤不计 —— 卡面写的是「your leader」受到伤害的次数。
        if (target.Zone == ZoneRef.Leader)
        {
            var wrathKey = _schema.Counter("wrath");
            _state.WriteCounter(target.PlayerId, wrathKey,
                _state.ReadCounter(target.PlayerId, wrathKey) + 1);
        }

        // 同上：入队由 vm 通道走，日志由 BattleState.ApplyDamage 补。
        // 两边都发会让「当受到伤害时」触发两次（实测 hand+2 而不是 +1）。
        EmitOrEnqueue(vm, new VmEvent
        {
            Kind = CoreEvent.OnDamaged,
            Subject = EntityRef.From(target),
            Amount = amount,
        });
        return amount;
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
            EmitOrEnqueue(vm, new VmEvent { Kind = CoreEvent.OnHealed, Subject = EntityRef.From(leader), Amount = healed });
    }
}

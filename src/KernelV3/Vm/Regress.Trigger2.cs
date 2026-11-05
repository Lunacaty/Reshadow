using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// trigger2 · 触发头「is damaged」与 other/another 的排除主体
//
//   缺陷 1（严重）：Whenever X **is damaged** 不编译。
//     MatchEventPredicate 只认 "takes damage"。is damaged 在 ConditionCompiler 里
//     有分支，但那是**条件**路径，不是事件头路径 —— 事件头这里直接不认，
//     整句在位置 0 报「无法解析的动作 / 停在 [Whenever]」，abil=0。
//     两种写法是同一事件，必须等价，且都不能破坏 "takes damage but isn't destroyed"。
//
//   缺陷 2（严重）：other / another 排除错了实体。
//     TargetParser 两处 NotSameEntityExpr 都写死 ExcludeSlot=$self。
//     在触发上下文里 other 应排除**事件主体**（$trigger），而不是监听者自己。
//     实测（基线）：「Whenever an allied follower enters the field,
//     deal 1 damage to all other followers」→ 刚进场的那个掉血，监听者反而没事。
//
//   缺陷 2 的**反向半边**同样必须钉住（这是最容易改坏的地方）：
//     · 普通动作上下文 "Deal 2 damage to all other allied followers"
//       仍排除 $self（施放者自己不掉血）；
//     · 触发器从句的**主语** "Whenever another allied follower enters the field"
//       仍排除 $self —— 主语里的 another 是「谁算数」的判据，
//       若被改成排除事件主体，过滤条件恒假，这条能力永不触发。
//
//   夹具命名一律用 Trg2 前缀：Regress 是 partial 类，
//   Tg2Card / Tg2State / Tg2Lexicon 已被 Regress.Tg2.cs 占用。
// ============================================================================
public static partial class Regress
{
    public static string RunTrigger2()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(Trg2Lexicon());

        // ── 1. "is damaged" 事件头可编译，且编译成 OnDamaged ──
        {
            var r = comp.Compile("Whenever this follower is damaged, give this follower +1/+0.");
            var ev = r.Abilities.FirstOrDefault()?.Trigger?.Event;
            Chk("trg2-1 Whenever X is damaged 事件头可编译（OnDamaged）",
                r.Ok && r.Abilities.Count == 1 && ev == CoreEvent.OnDamaged,
                $"ok={r.Ok} abil={r.Abilities.Count} ev={ev} errs={string.Join("|", r.Errors)}");
        }

        // ── 2. "is damaged" 真跑生效：受伤后 +1/+0 ──
        {
            var (ok, detail) = Trg2IsDamagedFires(comp);
            Chk("trg2-2 is damaged 触发后真的 +1/+0（伤害前 2，伤害后 3）", ok, detail);
        }

        // ── 3. 既有 "takes damage" 不退化 ──
        {
            var r = comp.Compile("Whenever this follower takes damage, give this follower +1/+0.");
            var ev = r.Abilities.FirstOrDefault()?.Trigger?.Event;
            var (ok, detail) = Trg2TakesDamageFires(comp);
            Chk("trg2-3 takes damage 仍正常（不退化）",
                r.Ok && r.Abilities.Count == 1 && ev == CoreEvent.OnDamaged && ok,
                $"ok={r.Ok} abil={r.Abilities.Count} ev={ev} {detail}");
        }

        // ── 4. 两种写法必须编译成等价的事件与主体 ──
        {
            var a = comp.Compile("Whenever this follower is damaged, draw a card.");
            var b = comp.Compile("Whenever this follower takes damage, draw a card.");
            string pa = a.Abilities.Count > 0 ? AstCanonicalPrinter.PrintAbilityDef(a.Abilities[0]) : "";
            string pb = b.Abilities.Count > 0 ? AstCanonicalPrinter.PrintAbilityDef(b.Abilities[0]) : "";
            Chk("trg2-4 is damaged 与 takes damage 编译结果逐字等价",
                a.Ok && b.Ok && pa.Length > 0 && pa == pb,
                $"isDamaged=[{pa}] takes=[{pb}]");
        }

        // ── 5. 触发上下文：other 排除事件主体（进场者不掉血）──
        {
            var (ok, detail) = Trg2OtherExcludesTrigger(comp);
            Chk("trg2-5 触发上下文 other 排除事件主体（入场者不掉血）", ok, detail);
        }

        // ── 6. 普通动作上下文：another 仍排除 $self ──
        {
            var (ok, detail) = Trg2AnotherExcludesSelfInAction(comp);
            Chk("trg2-6 普通动作上下文 another 仍排除 $self（不打自己）", ok, detail);
        }

        // ── 7. 触发器从句主语里的 another 仍排除 $self ──
        {
            var (ok, detail) = Trg2AnotherSubjectExcludesSelf(comp);
            Chk("trg2-7 触发器主语 another 仍排除 $self（监听者自己不算）", ok, detail);
        }

        log.AppendLine("OK " + pass + "/" + total);
        return "=== 回归 Trigger2 " + pass + "/" + total + " ===" + Environment.NewLine + log;
    }

    // ---------- 探针 ----------

    /// <summary>
    /// "is damaged" 真跑：监听者挨 1 点，+1/+0 必须落到它身上。
    /// 走 BattleState.ApplyDamage（能力伤害管线）而不是直接发事件，
    /// 这样屏障 / 超进化免伤等前置判定也一并走真实路径。
    /// </summary>
    private static (bool ok, string detail) Trg2IsDamagedFires(EnglishCompiler comp)
    {
        var st = Trg2State(7301, out _, out var vm);
        var r = comp.Compile("Whenever this follower is damaged, give this follower +1/+0.");
        st.RegisterCard(Trg2Card(700, r.Abilities, atk: 2, life: 5));
        var self = st.SpawnCard(700, ZoneRef.Board, 0);
        int before = self.Attack;
        st.ApplyDamage(self, 1, 1);
        vm.StepUntilBlocked();
        int after = self.Attack;
        return (r.Ok && before == 2 && after == 3,
                $"ok={r.Ok} atk {before} -> {after}（期望 2 -> 3） life={self.Life}");
    }

    /// <summary>既有路径的对照：takes damage 必须仍然跑通。</summary>
    private static (bool ok, string detail) Trg2TakesDamageFires(EnglishCompiler comp)
    {
        var st = Trg2State(7302, out _, out var vm);
        var r = comp.Compile("Whenever this follower takes damage, give this follower +1/+0.");
        st.RegisterCard(Trg2Card(701, r.Abilities, atk: 2, life: 5));
        var self = st.SpawnCard(701, ZoneRef.Board, 0);
        int before = self.Attack;
        st.ApplyDamage(self, 1, 1);
        vm.StepUntilBlocked();
        int after = self.Attack;
        return (before == 2 && after == 3, $"atk {before} -> {after}（期望 2 -> 3）");
    }

    /// <summary>
    /// 触发上下文：监听者（1/9）+ 旁观者（1/5）+ 刚进场的（1/5）。
    /// "对 all other followers 造成 1 点"结算后：
    ///   进场者 5 → 5（它是事件主体，必须被排除）
    ///   监听者 9 → 8（它不是事件主体，要挨打）
    ///   旁观者 5 → 4（同上）
    /// 基线的错误正好相反：进场者 5 → 4，监听者 9 → 9。
    /// </summary>
    private static (bool ok, string detail) Trg2OtherExcludesTrigger(EnglishCompiler comp)
    {
        var st = Trg2State(7303, out _, out var vm);
        var r = comp.Compile(
            "Whenever an allied follower enters the field, deal 1 damage to all other followers.");
        st.RegisterCard(Trg2Card(710, r.Abilities, atk: 1, life: 9));   // 监听者
        st.RegisterCard(Trg2Card(711, null, atk: 1, life: 5));          // 旁观者
        st.RegisterCard(Trg2Card(712, null, atk: 1, life: 5));          // 刚进场者

        var watcher = st.SpawnCard(710, ZoneRef.Board, 0);
        var bystander = st.SpawnCard(711, ZoneRef.Board, 0);
        var entrant = st.SpawnCard(712, ZoneRef.Board, 0);

        // 布置用的 SpawnCard 也会发 OnEnterField；那不是本用例要测的那一次。
        vm.DropPendingTriggers();
        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(entrant) });
        vm.StepUntilBlocked();

        bool ok = r.Ok && entrant.Life == 5 && watcher.Life == 8 && bystander.Life == 4;
        return (ok,
                $"ok={r.Ok} entrant={entrant.Life}(期望5) watcher={watcher.Life}(期望8) bystander={bystander.Life}(期望4)");
    }

    /// <summary>
    /// 普通动作上下文："Fanfare: Deal 2 damage to all other allied followers"
    /// 我方场上放施放者 + 一张旁观者。all 口径下 another/other 排除 $self，
    /// 所以施放者自己不能掉血，旁观者必须掉 2。
    /// </summary>
    private static (bool ok, string detail) Trg2AnotherExcludesSelfInAction(EnglishCompiler comp)
    {
        var st = Trg2State(7304, out _, out var vm);
        var r = comp.Compile("Fanfare: Deal 2 damage to all other allied followers.");
        st.RegisterCard(Trg2Card(720, r.Abilities, atk: 1, life: 5));   // 施放者（Fanfare 来源）
        st.RegisterCard(Trg2Card(721, null, atk: 1, life: 5));          // 旁观者

        var caster = st.SpawnCard(720, ZoneRef.Board, 0);
        var bystander = st.SpawnCard(721, ZoneRef.Board, 0);
        int casterLife = caster.Life;

        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnPlay, Subject = EntityRef.From(caster) });
        vm.StepUntilBlocked();

        bool ok = r.Ok && bystander.Life == 3 && caster.Life == casterLife;
        return (ok, $"ok={r.Ok} caster={caster.Life}(期望 {casterLife} 不变) bystander={bystander.Life}(期望3)");
    }

    /// <summary>
    /// 触发器主语里的 another："Whenever another allied follower enters the field, give it +1/+0"
    /// 主语里的 another 是「谁算数」的判据，必须仍排除 $self；
    /// 若被错误地改成排除事件主体，过滤条件恒假，能力永不触发。
    /// 做法：先编译一句触发上下文的效果体，把 DefaultPronounSlot 的 $trigger
    /// 残留状态复现出来（同一次 Compile 调用会重置，跨调用不会），再编译这一句。
    /// </summary>
    private static (bool ok, string detail) Trg2AnotherSubjectExcludesSelf(EnglishCompiler comp)
    {
        comp.Compile("Whenever an allied follower enters the field, deal 1 damage to all other followers.");
        var r = comp.Compile("Whenever another allied follower enters the field, give it +1/+0.");
        var subj = r.Abilities.FirstOrDefault()?.Trigger?.SubjectFilter;

        string slot = Trg2ExcludeSlotOf(subj);
        bool subjectUsesSelf = slot == TargetSpec.SlotSelf;

        var st = Trg2State(7305, out _, out var vm);
        st.RegisterCard(Trg2Card(730, r.Abilities, atk: 1, life: 5));   // 监听者
        st.RegisterCard(Trg2Card(731, null, atk: 1, life: 5));          // 另一个随从
        var watcher = st.SpawnCard(730, ZoneRef.Board, 0);
        var other = st.SpawnCard(731, ZoneRef.Board, 0);
        int otherAtkBefore = other.Attack;

        // 布置用的 SpawnCard 也会发 OnEnterField；那不是本用例要测的那一次。
        vm.DropPendingTriggers();
        vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.OnEnterField, Subject = EntityRef.From(other) });
        vm.StepUntilBlocked();
        int otherAtkAfter = other.Attack;

        bool ok = r.Ok && subjectUsesSelf && otherAtkAfter == 2;
        return (ok,
                $"ok={r.Ok} excludeSlot={slot}(期望{TargetSpec.SlotSelf}) otherAtk {otherAtkBefore}->{otherAtkAfter}(期望1->2) watcherAtk={watcher.Attack}");
    }

    /// <summary>从主体过滤器的 Where 里取出 NotSameEntityExpr 的排除槽位。</summary>
    private static string Trg2ExcludeSlotOf(TargetSpec t)
    {
        var w = t?.Where;
        if (w is NotSameEntityExpr ns) return ns.ExcludeSlot;
        if (w is AndExpr and)
        {
            foreach (var item in and.Items)
                if (item is NotSameEntityExpr n2) return n2.ExcludeSlot;
        }
        return "(none)";
    }

    // ---------- 夹具 ----------

    private static CardDef Trg2Card(int cardId, IReadOnlyList<AbilityDef> abilities,
                                    CardKind kind = CardKind.Follower, int atk = 1, int life = 1) => new()
    {
        CardId = cardId, Name = "R" + cardId, Kind = kind,
        BaseAttack = atk, BaseLife = life, BaseCost = 1,
        Abilities = abilities ?? Array.Empty<AbilityDef>(),
    };

    private static BattleState Trg2State(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new BattleState(seed: seed);
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 20; l0.MaxLife = 20; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 20; l1.MaxLife = 20; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        vm.CurrentTurnOwner = 0;
        return st;
    }

    private static Lexicon Trg2Lexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 事件触发链路（BattleState.EmitEvent → CardVm 触发队列）的回归用例
//
// 收口前：EmitEvent 只做 EventLog.Add，与 VM 的触发队列之间没有任何连接。
//   于是「每当抽牌时」对所有非显式 EnqueueEvent 的抽牌一律失明
//   （包括回合初的第 8 步、包括能力抽牌），
//   「当受到伤害时」对能力伤害失明，法术打出时手牌监听卡的主体过滤也找不到它。
//
// 收口后：IVmWorld.TriggerSink 是唯一转发通道 ——
//   世界先写 EventLog，再把**同一条**事件交给 CardVm 入队；
//   显式 EnqueueEvent 的那几处（入场曲 / 攻撃时 / 回合头 / 进化）走另一条路，
//   两条路没有交集，所以不存在「既转发又重复入队」的双重触发。
//
// 边界：转发只入队，不推进结算；结算时机仍由 StepUntilBlocked 独占，
//   递归由它的步数上限（_faultBudget = 5000）兜住。
// ============================================================================
public static partial class Regress
{
    public static string RunTrigger()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildTriggerLexicon();
        var comp = new EnglishCompiler(lex);

        // ── 1. 回合初抽牌触发「每当抽牌时」──
        // 这是最典型的失明点：TurnRunner.StartTurn 第 8 步走 _state.DrawCards，
        // 事件只进 EventLog，触发器一次都收不到。
        {
            var st = NewTriggerState(seed: 410, out var tr, out var vm);
            var watch = comp.Compile("Whenever you draw a card, deal 1 damage to the enemy leader.");
            st.RegisterCard(TriggerCard(501, watch.Abilities, CardKind.Follower, 1, 1));
            st.RegisterCard(TriggerCard(502, null, CardKind.Follower, 1, 1));
            st.SpawnCard(501, ZoneRef.Board, 0);
            st.SpawnCard(502, ZoneRef.Deck, 0);
            st.SpawnCard(502, ZoneRef.Deck, 0);
            st.ClearEvents();

            tr.StartTurn(0, vm);

            int enemy = st.Players[1].Leader.Life;
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            Check("trigger-1 回合初抽牌触发「每当抽牌时」（20 → 19）",
                  enemy == 19 && onDraw == 1,
                  $"enemyLeader={enemy} onDraw={onDraw}");
        }

        // ── 2. 能力抽牌同样触发，且抽 2 张就响应 2 次 ──
        // 与 trigger-1 走同一条 IVmWorld.DrawCards 管线，只是入口在 VM 侧。
        {
            var st = NewTriggerState(seed: 411, out var tr, out var vm);
            var watch = comp.Compile("Whenever you draw a card, deal 1 damage to the enemy leader.");
            var draw2 = comp.Compile("Fanfare: Draw 2 cards.");
            st.RegisterCard(TriggerCard(501, watch.Abilities, CardKind.Follower, 1, 1));
            st.RegisterCard(TriggerCard(503, draw2.Abilities, CardKind.Follower, 1, 1));
            st.RegisterCard(TriggerCard(502, null, CardKind.Follower, 1, 1));
            st.SpawnCard(501, ZoneRef.Board, 0);
            for (int i = 0; i < 6; i++) st.SpawnCard(502, ZoneRef.Deck, 0);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(503, ZoneRef.Hand, 0);
            st.ClearEvents();

            tr.PlayCard(0, c, vm);

            int enemy = st.Players[1].Leader.Life;
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            Check("trigger-2 能力抽 2 张触发 2 次（20 → 18）",
                  enemy == 18 && onDraw == 2 && hand == 2,
                  $"enemyLeader={enemy} onDraw={onDraw} hand={hand}");
        }

        // ── 3. 不双重触发：入场曲的显式 EnqueueEvent 不会被转发通道再入队一次 ──
        // 判据是数字：单次结算为 18；若转发把 OnPlay 又入队一次就是 16。
        {
            var st = NewTriggerState(seed: 412, out var tr, out var vm);
            var r = comp.Compile("Fanfare: Deal 2 damage to the enemy leader.");
            st.RegisterCard(TriggerCard(504, r.Abilities, CardKind.Follower, 1, 1));
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(504, ZoneRef.Hand, 0);
            st.ClearEvents();

            tr.PlayCard(0, c, vm);

            int enemy = st.Players[1].Leader.Life;
            int onPlay = st.EventLog.Count(e => e.Kind == CoreEvent.OnPlay);
            Check("trigger-3 入场曲不双重触发（20 → 18，不是 16）",
                  enemy == 18 && onPlay <= 1,
                  $"enemyLeader={enemy}（16 = OnPlay 被转发通道重复入队一次）" +
                  $" onPlayInLog={onPlay}");
        }

        // ── 4. 能力伤害发 OnDamaged，且监听卡对一次 2 点伤害只响应 1 次 ──
        {
            var st = NewTriggerState(seed: 413, out var tr, out var vm);
            var watch = comp.Compile("Whenever an allied follower takes damage, draw a card.");
            st.RegisterCard(TriggerCard(505, watch.Abilities, CardKind.Follower, 1, 8));
            st.RegisterCard(TriggerCard(502, null, CardKind.Follower, 1, 1));
            for (int i = 0; i < 6; i++) st.SpawnCard(502, ZoneRef.Deck, 0);
            var target = st.SpawnCard(505, ZoneRef.Board, 0);
            st.ClearEvents();

            st.ApplyDamage(target, 2, 1);
            vm.StepUntilBlocked();

            int onDamaged = st.EventLog.Count(e => e.Kind == CoreEvent.OnDamaged);
            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            Check("trigger-4 能力伤害发 OnDamaged，监听卡只响应 1 次",
                  onDamaged == 1 && hand == 1 && target.Life == 6,
                  $"onDamaged={onDamaged} hand={hand} life={target.Life}");
        }

        // ── 5. 法术打出时手牌监听卡能命中（与随从路径一致）──
        // 收口前：随从路径 15、法术路径 20 —— 法术结算时它还在手牌区，
        // 主体过滤按 Board 解析必然找不到它。
        {
            int PlayPath(CardKind kind)
            {
                var st = NewTriggerState(seed: 414, out var tr, out var vm);
                var byHand = comp.Compile(
                    "Activates in hand. Whenever you play a card, deal 5 damage to the enemy leader.");
                st.RegisterCard(TriggerCard(506, byHand.Abilities, CardKind.Spell, 1, 1));
                st.RegisterCard(TriggerCard(507, null, kind, 1, 1));
                st.WriteCounter(0, st.Schema.Counter("pp"), 10);
                st.SpawnCard(506, ZoneRef.Hand, 0);
                var c = st.SpawnCard(507, ZoneRef.Hand, 0);
                tr.PlayCard(0, c, vm);
                return st.Players[1].Leader.Life;
            }

            int follower = PlayPath(CardKind.Follower);
            int spell = PlayPath(CardKind.Spell);
            Check("trigger-5 法术打出时手牌监听卡同样命中（随从/法术两条路径均 20 → 15）",
                  follower == 15 && spell == 15,
                  $"followerPath={follower} spellPath={spell}");
        }

        // ── 6. 不误触发：无关事件不得让手牌监听卡响应 ──
        // 泛化主体解析只放宽「哪一次打出算数」，不放宽「什么事件算数」。
        {
            var st = NewTriggerState(seed: 415, out var tr, out var vm);
            var byHand = comp.Compile(
                "Activates in hand. Whenever you play a card, deal 5 damage to the enemy leader.");
            st.RegisterCard(TriggerCard(506, byHand.Abilities, CardKind.Spell, 1, 1));
            st.SpawnCard(506, ZoneRef.Hand, 0);
            st.ClearEvents();

            vm.EnqueueEvent(new VmEvent { Kind = CoreEvent.TurnEnd });
            vm.StepUntilBlocked();
            int afterTurnEnd = st.Players[1].Leader.Life;

            // 同一张卡换个事件仍然不该响应：抽牌不是打出
            st.SpawnCard(502, ZoneRef.Deck, 0);
            st.DrawCards(0, 1);
            vm.StepUntilBlocked();
            int afterDraw = st.Players[1].Leader.Life;

            Check("trigger-6 手牌监听卡不因 TurnEnd / OnDraw 误触发",
                  afterTurnEnd == 20 && afterDraw == 20,
                  $"afterTurnEnd={afterTurnEnd} afterDraw={afterDraw}");
        }

        // ── 7. 无递归风暴：伤害 → OnDamaged → 再造成伤害，必须在有限步内收束 ──
        // 两个分支都验证：
        //   (a) 正常收束（随从被自己的连锁打死，事件自然停）
        //   (b) 血量远超步数上限时由 _faultBudget 截断，而不是栈溢出
        {
            var st = NewTriggerState(seed: 416, out var tr, out var vm);
            var loop = comp.Compile("Whenever an allied follower takes damage, deal 1 damage to this follower.");
            st.RegisterCard(TriggerCard(508, loop.Abilities, CardKind.Follower, 1, 40));
            var a = st.SpawnCard(508, ZoneRef.Board, 0);
            var b = st.SpawnCard(508, ZoneRef.Board, 0);
            st.ClearEvents();

            // 点燃自激链：1 点伤害 → OnDamaged → 每张监听卡再打自己 1 点 → …
            st.ApplyDamage(a, 1, 1);
            var status = vm.StepUntilBlocked();
            // 正常收尾现在是 Completed（Idle 保留给「从未开始」）。
            bool settled = status == VmStatus.Completed || status == VmStatus.Idle || status == VmStatus.Faulted;
            bool stopped = a.Life == 0 || vm.FaultMessage == "触发链超过步数上限";
            Check("trigger-7 自激伤害链在有限步内收束（不栈溢出）",
                  settled && stopped && vm.Status != VmStatus.Running,
                  $"status={status} fault='{vm.FaultMessage}' lifeA={a.Life} lifeB={b.Life} " +
                  $"steps={vm.StepsExecuted} events={st.EventLog.Count}");

            var st2 = NewTriggerState(seed: 417, out var tr2, out var vm2);
            st2.RegisterCard(TriggerCard(508, loop.Abilities, CardKind.Follower, 1, 20000));
            var big = st2.SpawnCard(508, ZoneRef.Board, 0);
            st2.SpawnCard(508, ZoneRef.Board, 0);
            st2.ClearEvents();
            st2.ApplyDamage(big, 1, 1);
            var status2 = vm2.StepUntilBlocked();
            // 两个熔断点都合法：主循环的「执行步数超过上限」与触发队列的
            // 「触发链超过步数上限」。自激链先撞哪一个取决于每步执行了多少个节点，
            // 断言写成「任一熔断点生效」即可 —— 这里要守的是「有硬截断」，
            // 不是「恰好走某个分支」。
            bool hardStopped = vm2.FaultMessage == "触发链超过步数上限"
                            || vm2.FaultMessage == "执行步数超过上限";
            Check("trigger-7b 自激链超出步数上限时被硬截断（Faulted，不是无限循环）",
                  status2 == VmStatus.Faulted && hardStopped && big.Life > 0,
                  $"status={status2} fault='{vm2.FaultMessage}' life={big.Life} steps={vm2.StepsExecuted}");
        }

        // ── 8. EventLog 照常记录：ClearEvents 语义不变，转发不是它的替代品 ──
        {
            var st = NewTriggerState(seed: 418, out var tr, out var vm);
            st.RegisterCard(TriggerCard(502, null, CardKind.Follower, 1, 1));
            st.SpawnCard(502, ZoneRef.Deck, 0);
            st.SpawnCard(502, ZoneRef.Deck, 0);
            st.ClearEvents();
            int afterClear = st.EventLog.Count;

            st.DrawCards(0, 2);
            int onDraw = st.EventLog.Count(e => e.Kind == CoreEvent.OnDraw);
            Check("trigger-8 ClearEvents 语义不变、OnDraw 仍逐张入日志",
                  afterClear == 0 && onDraw == 2,
                  $"afterClear={afterClear} onDraw={onDraw}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return log.ToString().TrimEnd() + Environment.NewLine;
    }

    private static CardDef TriggerCard(int cardId, IReadOnlyList<AbilityDef> abilities,
                                       CardKind kind, int atk, int life) => new()
    {
        CardId = cardId, Name = "T" + cardId, Kind = kind,
        BaseAttack = atk, BaseLife = life, BaseCost = 1,
        // CreateEntity 会遍历 Def.Abilities，null 会直接炸；无能力的卡给空数组。
        Abilities = abilities ?? Array.Empty<AbilityDef>(),
    };

    private static Battle.BattleState NewTriggerState(int seed, out TurnRunner tr, out CardVm vm)
    {
        var st = new Battle.BattleState(seed: seed);
        tr = new TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 20 });
        st.Players[0].Leader = st.CreateEntity(99, ZoneRef.Leader, 0);
        st.Players[1].Leader = st.CreateEntity(99, ZoneRef.Leader, 1);
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        return st;
    }

    private static Lexicon BuildTriggerLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

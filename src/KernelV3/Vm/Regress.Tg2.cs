using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// tg2 · 触发链路的两个实测缺口
//
//   缺口 1（致命）：谢幕曲永不触发。
//     DestroyEntity 的顺序是「Detach -> EntrySerial++ -> PlaceInZone(Cemetery) -> EmitEvent」，
//     等 OnDestroy 送达时卡**已经在墓地**。而 FlushNextTrigger 只扫 Crest/Board/ExArea/Hand/Deck，
//     即便扫到，MatchesTrigger 也要求 owner.Zone == Board。两条一起断，全卡池谢幕曲都是白板。
//     102 项验收里的 b-规则4 只断言 EventLog 里有 OnDestroy、没断言效果执行，所以一直是绿的。
//
//   缺口 2：主体过滤的归属判断。
//
//     ★ 措辞纠正（反射逐字段实测）：enemy / allied 的归属**本来就是对的** ——
//       "an enemy follower" → Owner=Opponent（只认敌方随从，我方随从受伤不响应）
//       "an allied follower" → Owner=Self（只认我方随从）
//       这两条不是缺陷，本轮的回归只负责把它们钉住，防止以后改坏。
//
//     真正的、唯一的缺陷是**裸 "a follower"（无归属）编译出 Owner=Self**，
//     于是「每当随从受到伤害」只认我方、漏掉敌方 —— 是**少触发、偏保守**，不是反向错误。
//     不能在这里修：无归属的 "a follower" 与 "an allied follower" 编译出的 TargetSpec
//     逐字段完全相同（kind=Zone owner=Self zone=Board mode=Choose count=1
//     where=HasKindExpr omitted=None slot='' cardId=0），归属信息在编译期就已经丢失；
//     而 VM 侧是确定性函数，同一输入不可能分出两种输出。
//
//     >>> 已知缺口（待办）：裸 "a follower" 的归属。
//     >>> 修复前提是 TargetParser 在 TargetSpec 上留下「owner 是否被显式写出」的标记
//     >>> （新增 bool OwnerExplicit，或 OmittedFields |= TargetField.Owner），
//     >>> 再由 CardVm.SubjectMatches 把「未显式」按 Any 判。
//     >>> 本轮不修：TargetParser.cs 正被另一分支并发修改，避开合并冲突。
//     >>> 因此下面**没有**「无归属对双方响应」这条用例（不写成通过，避免留下假绿）。
// ============================================================================
public static partial class Regress
{
    public static string RunTg2()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(Tg2Lexicon());
        var lw = comp.Compile("Last Words: Deal 3 damage to the enemy leader.");
        var fanDestroy = comp.Compile("Fanfare: Destroy an enemy follower.");
        var fanPlusLw = comp.Compile(
            "Fanfare: Deal 1 damage to the enemy leader. Last Words: Deal 3 damage to the enemy leader.");
        var lwDraw = comp.Compile("Last Words: Draw a card.");

        // ── 1. 直接 DestroyEntity 触发谢幕曲 ──
        {
            var st = Tg2State(7100, out _, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            var e = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();

            st.DestroyEntity(e);
            vm.StepUntilBlocked();

            int enemy = st.Players[1].Leader.Life;
            Chk("tg2-1 能力破坏触发谢幕曲（20 → 17）", enemy == 17,
                $"enemyLeader={enemy}（20 = 谢幕曲完全没执行） zone={e.Zone}");
        }

        // ── 2. 能力破坏（Fanfare: Destroy an enemy follower）触发谢幕曲 ──
        //
        // 玩家关系（复核时最易误判，写清楚）：
        //   玩家 0（我方，当前回合）= 打出 Fanfare 的随从持有者；
        //   玩家 1（敌方）        = 被破坏的谢幕曲随从的持有者。
        // 谢幕曲写的是 "Deal 3 damage to the **enemy** leader" ——
        //   enemy 是**相对谢幕曲持有者**而言，即玩家 1 的敌方 = **玩家 0**。
        // 所以正确结果是**玩家 0 的主战者** 20 → 17，玩家 1 的主战者保持 20。
        // 这不是打错目标，是「能力归属跟随卡的主人、不跟随回合玩家」。
        {
            var st = Tg2State(7101, out var tr, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            st.RegisterCard(Tg2Card(601, fanDestroy.Abilities));
            var victim = st.SpawnCard(600, ZoneRef.Board, 1);
            st.WriteCounter(0, st.Schema.Counter("pp"), 10);
            var c = st.SpawnCard(601, ZoneRef.Hand, 0);
            st.ClearEvents();

            tr.PlayCard(0, c, vm);

            int p0 = st.Players[0].Leader.Life;
            int p1 = st.Players[1].Leader.Life;
            Chk("tg2-2 能力破坏触发谢幕曲（谢幕曲持有者的敌方主战者 20 → 17）",
                p0 == 17 && p1 == 20 && victim.Zone == ZoneRef.Cemetery,
                $"p0Leader={p0}（谢幕曲属玩家1，其 enemy 即玩家0）p1Leader={p1}（应保持 20）" +
                $" victimZone={victim.Zone}（20 = 谢幕曲没执行）");
        }

        // ── 3. 战斗破坏触发谢幕曲（攻守两个方向各一次） ──
        {
            int Fight(int lwOwner, int seed)
            {
                var st = Tg2State(seed, out _, out var vm);
                st.RegisterCard(Tg2Card(600, lw.Abilities));
                st.RegisterCard(Tg2Card(602, null, atk: 5, life: 5));
                var victim = st.SpawnCard(600, ZoneRef.Board, lwOwner);
                int attackerSide = 1 - lwOwner;
                var attacker = st.SpawnCard(602, ZoneRef.Board, attackerSide);
                attacker.EnteredTurn = st.TurnNumber - 1;
                st.ActivePlayer = attackerSide;
                vm.CurrentTurnOwner = attackerSide;
                st.ClearEvents();

                new CombatResolver(st, st.Schema).ExecuteCombat(attacker, victim, vm);
                vm.StepUntilBlocked();
                // 谢幕曲的 "enemy leader" —— 对谢幕曲持有者而言是对方主战者
                return st.Players[1 - lwOwner].Leader.Life;
            }

            int a = Fight(1, 7102);
            int b = Fight(0, 7103);
            Chk("tg2-3 战斗破坏触发谢幕曲（两个方向均 20 → 17）", a == 17 && b == 17,
                $"我方谢幕曲被打死={b} 敌方谢幕曲被打死={a}（20 = 没触发）");
        }

        // ── 4. 只执行一次：事件一条、效果一次 ──
        {
            var st = Tg2State(7104, out _, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            var e = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();

            st.DestroyEntity(e);
            vm.StepUntilBlocked();

            int dealt = 20 - st.Players[1].Leader.Life;
            int onDestroy = st.EventLog.Count(x => x.Kind == CoreEvent.OnDestroy);
            Chk("tg2-4 谢幕曲只执行一次（伤害 3、OnDestroy 1 条）", dealt == 3 && onDestroy == 1,
                $"dealt={dealt}（6 = 重复触发） onDestroyInLog={onDestroy}（2 = 事件重复）");
        }

        // ── 5. 非谢幕曲能力不因进墓地而复活 ──
        // 同一张卡同时有 Fanfare 与 Last Words：只有 Last Words 生效（-3，不是 -4）。
        {
            var st = Tg2State(7105, out _, out var vm);
            st.RegisterCard(Tg2Card(603, fanPlusLw.Abilities));
            var e = st.SpawnCard(603, ZoneRef.Board, 0);
            st.ClearEvents();

            st.DestroyEntity(e);
            vm.StepUntilBlocked();

            int dealt = 20 - st.Players[1].Leader.Life;
            Chk("tg2-5 墓地里的卡不复活非谢幕曲能力（只 -3）", dealt == 3,
                $"dealt={dealt}（4 = Fanfare 也跟着执行了） zone={e.Zone}");
        }

        // ── 6. 放逐 / 回手 / 变形一律不触发谢幕曲 ──
        {
            var st = Tg2State(7106, out _, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            var banished = st.SpawnCard(600, ZoneRef.Board, 0);
            var bounced = st.SpawnCard(600, ZoneRef.Board, 0);
            var transformed = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();

            st.BanishEntity(banished);
            st.MoveEntity(bounced, ZoneRef.Hand, 0);
            st.TransformEntity(transformed, 600);
            vm.StepUntilBlocked();

            int enemy = st.Players[1].Leader.Life;
            Chk("tg2-6 放逐 / 回手 / 变形不触发谢幕曲", enemy == 20,
                $"enemyLeader={enemy}（17 = 离场被误判成破坏） zones=" +
                $"{banished.Zone}/{bounced.Zone}/{transformed.Zone}");
        }

        // ── 7. 墓地里的卡不响应其它事件（仅离场语义允许在墓地判定） ──
        {
            var st = Tg2State(7107, out _, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            st.RegisterCard(Tg2Card(604, null));
            var dead = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();
            st.DestroyEntity(dead);
            vm.StepUntilBlocked();
            int afterDestroy = st.Players[1].Leader.Life;

            // (a) 墓地里的卡收到 OnDamaged 不得响应
            var alive = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();
            st.ApplyDamage(alive, 1, 1);
            vm.StepUntilBlocked();
            int afterDamaged = st.Players[1].Leader.Life;

            // (b) 另一张卡死亡，不得把墓地里那张的谢幕曲再触发一次
            var other = st.SpawnCard(604, ZoneRef.Board, 0);
            st.ClearEvents();
            st.DestroyEntity(other);
            vm.StepUntilBlocked();
            int afterOtherDeath = st.Players[1].Leader.Life;

            Chk("tg2-7 谢幕曲只在「自己离场」时判定，不因他人死亡或受伤复活",
                afterDestroy == 17 && afterDamaged == 17 && afterOtherDeath == 17,
                $"afterDestroy={afterDestroy} afterDamaged={afterDamaged} afterOtherDeath={afterOtherDeath}");
        }

        // ── 8. 同一条能力不会因「先扫描到墓地」而对同一事件响应两次 ──
        // 用亡者召还再破坏来验证：第二次破坏必须重新触发（serial 不同），第一次的引用不得重放。
        {
            var st = Tg2State(7108, out _, out var vm);
            st.RegisterCard(Tg2Card(600, lw.Abilities));
            var e = st.SpawnCard(600, ZoneRef.Board, 0);
            st.ClearEvents();
            st.DestroyEntity(e);
            vm.StepUntilBlocked();
            int afterFirst = st.Players[1].Leader.Life;

            st.MoveEntity(e, ZoneRef.Board, 0);   // 亡者召还
            st.ClearEvents();
            st.DestroyEntity(e);
            vm.StepUntilBlocked();
            int afterSecond = st.Players[1].Leader.Life;

            Chk("tg2-8 亡者召还后再破坏重新触发（17 → 14），旧引用不重放",
                afterFirst == 17 && afterSecond == 14,
                $"afterFirst={afterFirst} afterSecond={afterSecond}（17 = 第二次没触发；11 = 旧引用被重放）");
        }

        // ── 9. 谢幕曲不是「只认伤害」的特例：抽牌类同样生效 ──
        {
            var st = Tg2State(7109, out _, out var vm);
            st.RegisterCard(Tg2Card(605, lwDraw.Abilities));
            st.RegisterCard(Tg2Card(612, null, atk: 0, life: 1));
            for (int i = 0; i < 4; i++) st.SpawnCard(612, ZoneRef.Deck, 0);
            var e = st.SpawnCard(605, ZoneRef.Board, 0);
            st.ClearEvents();

            st.DestroyEntity(e);
            vm.StepUntilBlocked();

            int hand = st.EnumerateZone(ZoneRef.Hand, 0).Count();
            Chk("tg2-9 Last Words: Draw a card 同样生效（手牌 +1）", hand == 1,
                $"hand={hand}（0 = 谢幕曲仍是白板）");
        }

        // ── 10. 主体归属：enemy follower 只认敌方随从 ──
        {
            bool Responds(string text, int damagedSide, int seed)
            {
                var r = comp.Compile(text);
                var st = Tg2State(seed, out _, out var vm);
                st.RegisterCard(Tg2Card(610, r.Abilities, atk: 0, life: 5));
                st.RegisterCard(Tg2Card(611, null, atk: 0, life: 5));
                st.RegisterCard(Tg2Card(612, null, atk: 0, life: 1));
                st.SpawnCard(610, ZoneRef.Board, 0);
                for (int i = 0; i < 4; i++) st.SpawnCard(612, ZoneRef.Deck, 0);
                var tgt = st.SpawnCard(611, ZoneRef.Board, damagedSide);
                st.ClearEvents();

                st.ApplyDamage(tgt, 2, 1 - damagedSide);
                vm.StepUntilBlocked();
                return st.EnumerateZone(ZoneRef.Hand, 0).Count() == 1;
            }

            bool onEnemy = Responds("Whenever an enemy follower takes damage, draw a card.", 1, 7110);
            bool onAlly = Responds("Whenever an enemy follower takes damage, draw a card.", 0, 7111);
            Chk("tg2-10 Whenever an enemy follower takes damage 只认敌方随从",
                onEnemy && !onAlly,
                $"敌方受伤={onEnemy}（False = 归属判断失效） 我方受伤={onAlly}（True = 误触发）");
        }

        // ── 11. 主体归属：allied follower 只认我方随从 ──
        {
            bool Responds(string text, int damagedSide, int seed)
            {
                var r = comp.Compile(text);
                var st = Tg2State(seed, out _, out var vm);
                st.RegisterCard(Tg2Card(610, r.Abilities, atk: 0, life: 5));
                st.RegisterCard(Tg2Card(611, null, atk: 0, life: 5));
                st.RegisterCard(Tg2Card(612, null, atk: 0, life: 1));
                st.SpawnCard(610, ZoneRef.Board, 0);
                for (int i = 0; i < 4; i++) st.SpawnCard(612, ZoneRef.Deck, 0);
                var tgt = st.SpawnCard(611, ZoneRef.Board, damagedSide);
                st.ClearEvents();

                st.ApplyDamage(tgt, 2, 1 - damagedSide);
                vm.StepUntilBlocked();
                return st.EnumerateZone(ZoneRef.Hand, 0).Count() == 1;
            }

            bool onAlly = Responds("Whenever an allied follower takes damage, draw a card.", 0, 7112);
            bool onEnemy = Responds("Whenever an allied follower takes damage, draw a card.", 1, 7113);
            Chk("tg2-11 Whenever an allied follower takes damage 只认我方随从",
                onAlly && !onEnemy,
                $"我方受伤={onAlly} 敌方受伤={onEnemy}（True = 敌我混淆）");
        }

        // ── 12. 「Whenever you play a card」的主体豁免不被本次改动破坏 ──
        // 手牌监听卡对法术打出必须仍然命中恰好 1 次（法术结算时它还在手牌区）。
        {
            int PlayCard(bool spell)
            {
                var st = Tg2State(7114 + (spell ? 1 : 0), out var tr, out var vm);
                var byHand = comp.Compile(
                    "Activates in hand. Whenever you play a card, deal 5 damage to the enemy leader.");
                st.RegisterCard(Tg2Card(620, byHand.Abilities, CardKind.Spell));
                st.RegisterCard(Tg2Card(621, null, spell ? CardKind.Spell : CardKind.Follower, atk: 1, life: 1));
                st.WriteCounter(0, st.Schema.Counter("pp"), 10);
                st.SpawnCard(620, ZoneRef.Hand, 0);
                var c = st.SpawnCard(621, ZoneRef.Hand, 0);
                st.ClearEvents();

                tr.PlayCard(0, c, vm);
                return 20 - st.Players[1].Leader.Life;
            }

            int followerPath = PlayCard(false);
            int spellPath = PlayCard(true);
            Chk("tg2-12 Whenever you play a card 仍恰好响应 1 次（随从/法术两条路径）",
                followerPath == 5 && spellPath == 5,
                $"followerPath={followerPath}（0 = 豁免失效；10 = 双重触发） spellPath={spellPath}");
        }

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Tg2 " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    // ---------- 公共设施 ----------

    private static CardDef Tg2Card(int cardId, IReadOnlyList<AbilityDef> abilities,
                                   CardKind kind = CardKind.Follower, int atk = 1, int life = 1) => new()
    {
        CardId = cardId, Name = "T" + cardId, Kind = kind,
        BaseAttack = atk, BaseLife = life, BaseCost = 1,
        Abilities = abilities ?? Array.Empty<AbilityDef>(),
    };

    private static BattleState Tg2State(int seed, out TurnRunner tr, out CardVm vm)
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

    private static Lexicon Tg2Lexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }
}

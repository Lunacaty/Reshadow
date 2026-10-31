using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 召唤复制体回归：exact copy 与 copy 的区分（76-Gemini 意见 c · 第五章动作 9）
//
// 官方术语表把「复制」拆成两种，语义完全不同：
//   an exact copy of X —— 复制体继承源目标**当前**的攻击力 / 当前生命 / 生命上限 /
//      当前费用 / 已获得的关键词与授予能力 / 已进化与超进化状态；
//      但**不继承**「本回合已攻击 / 已启动」：复制体是一次全新入场。
//   a copy of X（无 exact）—— 只是把那张卡的**印刷白板**再拿出来一份。
//
// 缺陷两处，各自都能单独让卡面失真：
//   C1 编译期：ParseSpawnCore 的两个分支都只置 isCopy，IsExactCopy 恒 false ——
//      「exact」这个词被吃掉，两种句子编出同一棵 AST。
//   C2 运行期：SpawnCopy 只把 Attack / Life / Cost 传进 CreateEntity，
//      MaxLife 被写成 Life（7/6 的复制体上限也只有 6），
//      关键词 / 进化状态 / 特性 / 授予能力一个都没搬过去。
//
// 因此每条断言都分两层咬：AST 上看 IsExactCopy，运行期上看实体字段，
// 最后再从编译到执行整条链跑一遍 —— 只比对 AST 抓不到 C2，只看实体抓不到 C1。
// ============================================================================
public static partial class Regress
{
    public static string RunCopy()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(CoreLexicon());

        // ---------- C1 编译期：exact 必须落进 AST ----------
        {
            var rExact = comp.Compile("Fanfare: Summon an exact copy of an enemy follower.");
            var spExact = rExact.Abilities.SelectMany(a => a.Body).OfType<SpawnCardNode>().FirstOrDefault();
            Chk("copy-1 编译 an exact copy of → IsExactCopy=true",
                rExact.Ok && spExact != null && spExact.SourceKind == CardSourceKind.CopyOfTarget
                    && spExact.IsExactCopy,
                "ok=" + rExact.Ok + " kind=" + (spExact?.SourceKind.ToString() ?? "none")
                    + " exact=" + (spExact?.IsExactCopy.ToString() ?? "none")
                    + " err=" + string.Join(";", rExact.Errors));

            var rPlain = comp.Compile("Fanfare: Summon a copy of an enemy follower.");
            var spPlain = rPlain.Abilities.SelectMany(a => a.Body).OfType<SpawnCardNode>().FirstOrDefault();
            Chk("copy-2 编译 a copy of → IsExactCopy=false",
                rPlain.Ok && spPlain != null && spPlain.SourceKind == CardSourceKind.CopyOfTarget
                    && !spPlain.IsExactCopy,
                "ok=" + rPlain.Ok + " kind=" + (spPlain?.SourceKind.ToString() ?? "none")
                    + " exact=" + (spPlain?.IsExactCopy.ToString() ?? "none")
                    + " err=" + string.Join(";", rPlain.Errors));

            // 数量写法同样要认 exact："2 exact copies of it"。
            var rTwo = comp.Compile("Fanfare: Summon 2 exact copies of an enemy follower.");
            var spTwo = rTwo.Abilities.SelectMany(a => a.Body).OfType<SpawnCardNode>().FirstOrDefault();
            Chk("copy-3 编译 2 exact copies of → IsExactCopy=true",
                rTwo.Ok && spTwo != null && spTwo.IsExactCopy,
                "ok=" + rTwo.Ok + " exact=" + (spTwo?.IsExactCopy.ToString() ?? "none")
                    + " err=" + string.Join(";", rTwo.Errors));

            // 真实卡池里最主流的写法是 "of each"（Artifact 系一排卡），
            // 不能只认 "of it" / "of an enemy follower"。
            var rEach = comp.Compile("Fanfare: Summon an exact copy of each.");
            var spEach = rEach.Abilities.SelectMany(a => a.Body).OfType<SpawnCardNode>().FirstOrDefault();
            Chk("copy-3b 编译 an exact copy of each → IsExactCopy=true",
                rEach.Ok && spEach != null && spEach.IsExactCopy,
                "ok=" + rEach.Ok + " exact=" + (spEach?.IsExactCopy.ToString() ?? "none")
                    + " err=" + string.Join(";", rEach.Errors));
        }

        // ---------- C2 运行期：exact copy 完整继承状态 ----------
        {
            var st = CopyState(3100);
            var src = MakeLoadedSource(st, out _);
            var ex = st.SpawnCopy(EntityRef.From(src), ZoneRef.Board, 0, true);

            bool stats = ex != null && ex.Attack == 7 && ex.Life == 6
                                  && ex.MaxLife == 9 && ex.Cost == 4;
            Chk("copy-4 exact copy 继承 7/6、MaxLife 9、Cost 4",
                stats,
                ex == null ? "没生成"
                    : ex.Attack + "/" + ex.Life + " max=" + ex.MaxLife + " cost=" + ex.Cost);

            bool kw = ex != null && ex.Keywords.Contains(st.Schema.Keyword("ward"));
            bool evo = ex != null && ex.IsEvolved;
            bool trait = ex != null && ex.Traits.Contains(st.Schema.Trait("artifact"));
            bool granted = ex != null && st.GrantedAbilities.Any(g => ReferenceEquals(g.e, ex));
            Chk("copy-5 exact copy 继承 ward / 进化 / 特性 / 授予能力",
                kw && evo && trait && granted,
                "ward=" + kw + " evolved=" + evo + " trait=" + trait + " granted=" + granted);
        }

        // ---------- C2 运行期：普通 copy 取印刷白板 ----------
        {
            var st = CopyState(3101);
            var src = MakeLoadedSource(st, out _);
            var plain = st.SpawnCopy(EntityRef.From(src), ZoneRef.Board, 0, false);

            bool stats = plain != null && plain.Attack == 2 && plain.Life == 2
                                        && plain.MaxLife == 2 && plain.Cost == 3;
            bool bare = plain != null && !plain.Keywords.Contains(st.Schema.Keyword("ward"))
                        && !plain.IsEvolved
                        && !st.GrantedAbilities.Any(g => ReferenceEquals(g.e, plain));
            Chk("copy-6 普通 copy 是白板 2/2、MaxLife 2、Cost 3、无 ward / 未进化 / 无授予",
                stats && bare,
                plain == null ? "没生成"
                    : plain.Attack + "/" + plain.Life + " max=" + plain.MaxLife
                      + " cost=" + plain.Cost
                      + " bare=" + bare);
        }

        // ---------- exact copy 不能被 CreateEntity 的「0 表示用印刷值」吞掉 ----------
        {
            // 一个被削成 0 攻的随从，exact copy 也必须还是 0 攻。
            // CreateEntity(atk: 0) 的语义是「没给」，会把印刷攻击力装回去。
            var st = CopyState(3105);
            var src = MakeLoadedSource(st, out _);
            src.Attack = 0;
            var ex = st.SpawnCopy(EntityRef.From(src), ZoneRef.Board, 0, true);
            Chk("copy-6b exact copy 保留 0 攻击力（不被印刷值顶回去）",
                ex != null && ex.Attack == 0 && ex.Life == 6 && ex.MaxLife == 9,
                ex == null ? "没生成" : ex.Attack + "/" + ex.Life + " max=" + ex.MaxLife);
        }

        // ---------- 两者都不继承「本回合已攻击 / 已启动」，且是新入场 ----------
        {
            var st = CopyState(3102);
            var src = MakeLoadedSource(st, out _);
            src.AttackedThisTurn = true;
            src.AttacksThisTurn = 2;
            src.EngagedThisTurn = true;
            src.EnteredTurn = st.TurnNumber - 3;

            var ex = st.SpawnCopy(EntityRef.From(src), ZoneRef.Board, 0, true);
            var plain = st.SpawnCopy(EntityRef.From(src), ZoneRef.Board, 0, false);
            bool fresh = ex != null && plain != null
                         && !ex.AttackedThisTurn && ex.AttacksThisTurn == 0 && !ex.EngagedThisTurn
                         && !plain.AttackedThisTurn && plain.AttacksThisTurn == 0 && !plain.EngagedThisTurn;
            bool entered = ex != null && plain != null
                           && ex.EnteredTurn == st.TurnNumber && plain.EnteredTurn == st.TurnNumber;
            Chk("copy-7 两种复制都不继承已攻击 / 已启动，EnteredTurn 盖当前回合",
                fresh && entered,
                "exact=" + (ex == null ? "无" : ex.AttackedThisTurn + "/" + ex.AttacksThisTurn
                              + "/" + ex.EngagedThisTurn + "/E" + ex.EnteredTurn)
                + " plain=" + (plain == null ? "无" : plain.AttackedThisTurn + "/" + plain.AttacksThisTurn
                              + "/" + plain.EngagedThisTurn + "/E" + plain.EnteredTurn)
                + " TurnNumber=" + st.TurnNumber);

            // 召唤失调必须真的生效：当回合的复制体不能打主战者。
            var cr = new CombatResolver(st, st.Schema);
            bool canExact = cr.CanAttack(ex, st.Players[1].Leader, out var r1);
            bool canPlain = cr.CanAttack(plain, st.Players[1].Leader, out var r2);
            Chk("copy-8 复制体当回合不能攻击（召唤失调）",
                !canExact && !canPlain,
                "exact=" + canExact + "(" + r1 + ") plain=" + canPlain + "(" + r2 + ")");
        }

        // ---------- 端到端：编译 → VM 执行 ----------
        {
            var rExact = comp.Compile("Fanfare: Summon an exact copy of this follower.");
            var st = CopyState(3103);
            var src = MakeLoadedSource(st, out _);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            vm.CurrentTurnOwner = 0;

            var body = rExact.Abilities.SelectMany(a => a.Body).ToList();
            vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) });
            vm.StepUntilBlocked();

            var copy = st.EnumerateZone(ZoneRef.Board, 0).FirstOrDefault(x => !ReferenceEquals(x, src));
            bool ok = rExact.Ok && copy != null
                      && copy.Attack == 7 && copy.Life == 6 && copy.MaxLife == 9
                      && copy.Keywords.Contains(st.Schema.Keyword("ward"))
                      && copy.IsEvolved
                      && copy.EnteredTurn == st.TurnNumber;
            Chk("copy-9 端到端 exact copy：VM 执行后复制体 7/6 max9 ward 已进化",
                ok,
                "ok=" + rExact.Ok + " "
                    + (copy == null ? "没生成"
                        : copy.Attack + "/" + copy.Life + " max=" + copy.MaxLife
                          + " ward=" + copy.Keywords.Contains(st.Schema.Keyword("ward"))
                          + " evo=" + copy.IsEvolved + " entered=" + copy.EnteredTurn)
                    + " err=" + string.Join(";", rExact.Errors));
        }
        {
            var rPlain = comp.Compile("Fanfare: Summon a copy of this follower.");
            var st = CopyState(3104);
            var src = MakeLoadedSource(st, out _);
            var vm = new CardVm(st);
            CardVm.Schema = st.Schema;
            vm.CurrentTurnOwner = 0;

            var body = rPlain.Abilities.SelectMany(a => a.Body).ToList();
            vm.PushBody(body, new VmScope { Owner = ZoneScope.Self, Controller = 0, Source = EntityRef.From(src) });
            vm.StepUntilBlocked();

            var copy = st.EnumerateZone(ZoneRef.Board, 0).FirstOrDefault(x => !ReferenceEquals(x, src));
            bool ok = rPlain.Ok && copy != null
                      && copy.Attack == 2 && copy.Life == 2 && copy.MaxLife == 2
                      && !copy.Keywords.Contains(st.Schema.Keyword("ward"))
                      && !copy.IsEvolved
                      && copy.EnteredTurn == st.TurnNumber;
            Chk("copy-10 端到端普通 copy：VM 执行后复制体是白板 2/2",
                ok,
                "ok=" + rPlain.Ok + " "
                    + (copy == null ? "没生成"
                        : copy.Attack + "/" + copy.Life + " max=" + copy.MaxLife
                          + " ward=" + copy.Keywords.Contains(st.Schema.Keyword("ward"))
                          + " evo=" + copy.IsEvolved + " entered=" + copy.EnteredTurn)
                    + " err=" + string.Join(";", rPlain.Errors));
        }

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Copy " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    // ---------- 公共设施 ----------

    private static BattleState CopyState(int seed)
    {
        var st = new BattleState(seed: seed);
        st.RegisterCard(new CardDef
        {
            CardId = 500, Name = "Loaded", Kind = CardKind.Follower,
            BaseAttack = 2, BaseLife = 2, BaseCost = 3,
            Abilities = Array.Empty<AbilityDef>(),
        });
        st.RegisterCard(new CardDef
        {
            CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 21,
            Abilities = Array.Empty<AbilityDef>(),
        });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 21; l0.MaxLife = 21;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 21; l1.MaxLife = 21;
        st.Players[0].Leader = l0;
        st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        return st;
    }

    /// <summary>印刷值 2/2/2 的随从，被改造成「当前 7/6（上限 9）、费用 4、带 ward、已进化、有特性、被授予一条能力」。</summary>
    private static RuntimeEntity MakeLoadedSource(BattleState st, out AbilityDef grant)
    {
        var src = st.SpawnCard(500, ZoneRef.Board, 0);
        src.Attack = 7;
        src.Life = 6;
        src.MaxLife = 9;
        src.Cost = 4;
        src.IsEvolved = true;
        src.Keywords.Add(st.Schema.Keyword("ward"));
        src.Traits.Add(st.Schema.Trait("artifact"));

        grant = new AbilityDef
        {
            Trigger = new TriggerSpec { Event = CoreEvent.PassiveAura },
            Condition = BoolExpr.True,
            Costs = Array.Empty<CostSpec>(),
            Body = new List<EffectNode>(),
        };
        st.GrantAbility(src, grant);
        return src;
    }
}

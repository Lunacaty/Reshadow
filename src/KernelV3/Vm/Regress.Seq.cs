using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 模式引导句的多种语义回归
//
// 缺陷：卡面里几种**互不相同**的模式引导句被压成同一种。
//
//   "Select a Mode to activate."                          玩家手选     -> Choose
//   "Activate 2 random abilities from the following."     随机取 2     -> Random
//   "activate an ability in sequence from the following." 按序全自动   -> Sequence
//   "... that hasn't been activated yet ..."              跳过已激活的 -> *Unused
//
// 收口前 ExtractModePick 只认 random 一个维度，于是 "in sequence" 落成 Choose：
// 卡面明写「按 1→2→3 自动全部发动」，运行时却弹窗等玩家手选。
// City of Babelon (10703210) 与 Omerio (10964120) 两张实卡因此完全不是卡面语义。
//
// 本回归钉住四件事：
//   1. 引导句分别编译出对应 Kind（不串味）
//   2. Sequence 运行时不挂起、按 1→2→3 正序全部执行
//   3. Choose / Random 两条既有路径不退化
//   4. "未激活" 是**源实体**上的持久位掩码：跨回合不重复、耗尽后不崩、实例间隔离
//
// ★ 观测口径：计数器一律从 **AST 的 SymbolId** 读，不按名字重新查 Schema。
//   SymbolId.Id 由 GameSchema 分配，编译器用自己的 Lexicon.Schema，
//   世界用自己的 Schema，两个实例的自定义计数器 Id 并不保证一致
//   （实测 "gamma" 在两份表里落在不同槽位，按名字读会读成 0）。
//   生产代码本来就走 AST 里的 Id，回归必须用同一口径，否则测的是假象。
// ============================================================================
public static partial class Regress
{
    public static string RunSeq()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var lex = BuildSeqLexicon();
        var comp = new EnglishCompiler(lex);

        // ── S1 "in sequence" 必须编译成 Sequence，不是 Choose ──
        {
            var r = comp.Compile(
                "At the end of your turn, activate an ability in sequence from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var bm = ModeBranch(r);
            Chk("seq-1 \"in sequence\" 编译成 Sequence（收口前是 Choose）",
                r.Ok && bm != null && bm.Kind == ModeDispatchKind.Sequence && bm.Options.Count == 3,
                bm == null ? "no BranchModeNode" : $"kind={bm.Kind} opts={bm.Options.Count} err={r.Errors.Count}");
        }

        // ── S2 Sequence 运行时：不挂起，1→2→3 全部按序执行 ──
        {
            var r = comp.Compile(
                "At the end of your turn, activate an ability in sequence from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var ctrs = OptionCounters(r);
            var w = NewSeqWorld(seed: 700);
            var vm = new CardVm(w);
            var src = SpawnModeCard(w, 501, r.Abilities);
            var order = new List<string>();
            w.OnCounterWrite = (name, val) => order.Add(name);

            Fire(w, vm, src, CoreEvent.TurnEnd);

            bool notBlocked = vm.Pending == null && vm.Status != VmStatus.WaitingForInput;
            int fired = SumCounters(w, ctrs);
            string seq = string.Join(",", order);
            Chk("seq-2 Sequence 运行时不弹选择、按 1→2→3 全部执行",
                r.Ok && notBlocked && fired == 3 && seq == "alpha,beta,gamma",
                $"blocked={!notBlocked} 计数={fired}/3 order=[{seq}]");
        }

        // ── S3 Choose 不退化：仍然挂起等玩家，选中的那项才执行 ──
        {
            var r = comp.Compile(
                "At the end of your turn, Select a Mode to activate. 1. Gain 1 alpha. 2. Gain 1 beta.");
            var bm = ModeBranch(r);
            var ctrs = OptionCounters(r);
            var w = NewSeqWorld(seed: 701);
            var vm = new CardVm(w);
            var src = SpawnModeCard(w, 502, r.Abilities);

            Fire(w, vm, src, CoreEvent.TurnEnd);
            bool blocked = vm.Pending != null && vm.Status == VmStatus.WaitingForInput;
            bool nothingYet = SumCounters(w, ctrs) == 0;

            if (blocked)
            {
                vm.SubmitChoice(vm.Pending.ChoiceId, new[] { 1 });
                vm.StepUntilBlocked();
            }
            bool secondOnly = ctrs.Count == 2
                              && w.ReadCounter(0, ctrs[1]) == 1
                              && w.ReadCounter(0, ctrs[0]) == 0;

            Chk("seq-3 Select a Mode 仍是 Choose 且会挂起（不退化）",
                r.Ok && bm != null && bm.Kind == ModeDispatchKind.Choose
                && blocked && nothingYet && secondOnly,
                $"kind={bm?.Kind} blocked={blocked} nothingYet={nothingYet} secondOnly={secondOnly}");
        }

        // ── S4 Random 不退化：随机恰好取 PickCount 项 ──
        {
            var r = comp.Compile(
                "At the end of your turn, Activate 2 random abilities from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var bm = ModeBranch(r);
            var ctrs = OptionCounters(r);

            int firedTotal = 0;
            for (int seed = 0; seed < 8; seed++)
            {
                var w = NewSeqWorld(seed: 800 + seed);
                var vm = new CardVm(w);
                var src = SpawnModeCard(w, 503, r.Abilities);
                Fire(w, vm, src, CoreEvent.TurnEnd);
                firedTotal += SumCounters(w, ctrs);
            }
            Chk("seq-4 Activate N random abilities 仍是 Random，恰好取 2 项",
                r.Ok && bm != null && bm.Kind == ModeDispatchKind.Random && firedTotal == 16,
                $"kind={bm?.Kind} 8 次合计={firedTotal}（期望 8×2=16）");
        }

        // ── S5 未激活：RandomUnused 每回合一个，跨回合不重复 ──
        {
            var r = comp.Compile(
                "At the start of your turn, activate a random ability that hasn't been activated yet from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var bm = ModeBranch(r);
            var ctrs = OptionCounters(r);
            var w = NewSeqWorld(seed: 702);
            var vm = new CardVm(w);
            var src = SpawnModeCard(w, 504, r.Abilities);
            int maskKey = ActivityMaskId(w);

            var perTurn = new List<string>();
            for (int turn = 0; turn < 3; turn++)
            {
                int before = SumCounters(w, ctrs);
                Fire(w, vm, src, CoreEvent.TurnStart);
                perTurn.Add((SumCounters(w, ctrs) - before).ToString());
            }
            int afterThree = SumCounters(w, ctrs);

            bool crashed = false;
            int afterFour = afterThree;
            try
            {
                Fire(w, vm, src, CoreEvent.TurnStart);
                afterFour = SumCounters(w, ctrs);
            }
            catch (Exception ex) { crashed = true; log.AppendLine("      异常: " + ex.Message); }

            int mask = ReadMask(w, src, maskKey);
            Chk("seq-5 hasn't been activated yet 编译成 RandomUnused，3 回合各 1 项且不重复",
                r.Ok && bm != null && bm.Kind == ModeDispatchKind.RandomUnused
                && string.Join(",", perTurn) == "1,1,1"
                && afterThree == 3 && !crashed && afterFour == 3 && mask == 0b111,
                $"kind={bm?.Kind} 每回合=[{string.Join(",", perTurn)}] 3回合后={afterThree} " +
                $"第4次后={afterFour} crashed={crashed} mask={Convert.ToString(mask, 2)}");
        }

        // ── S6 未激活 + Sequence：按序取「下一个」，不是一次全跑 ──
        {
            var r = comp.Compile(
                "Whenever an allied amulet is destroyed, activate an ability that hasn't been activated yet in sequence from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var bm = ModeBranch(r);
            var ctrs = OptionCounters(r);
            var w = NewSeqWorld(seed: 703);
            var vm = new CardVm(w);
            var src = SpawnModeCard(w, 505, r.Abilities);

            var fired = new List<string>();
            for (int i = 0; i < 4; i++)
            {
                int before = SumCounters(w, ctrs);
                Fire(w, vm, src, CoreEvent.OnDestroy);
                fired.Add((SumCounters(w, ctrs) - before).ToString());
            }
            bool oneEach = string.Join(",", fired) == "1,1,1,0"
                           && ctrs.All(c => w.ReadCounter(0, c) == 1);

            Chk("seq-6 未激活+in sequence 每次只取下一个，耗尽后不动",
                r.Ok && bm != null && bm.Kind == ModeDispatchKind.SequenceUnused && oneEach,
                $"kind={bm?.Kind} 每次激活数=[{string.Join(",", fired)}]（期望 1,1,1,0）");
        }

        // ── S7 未激活进度挂在**源实体**上：同卡两个实例互不干扰 ──
        {
            var r = comp.Compile(
                "At the start of your turn, activate a random ability that hasn't been activated yet from the following. " +
                "1. Gain 1 alpha. 2. Gain 1 beta. 3. Gain 1 gamma.");
            var ctrs = OptionCounters(r);
            var w = NewSeqWorld(seed: 704);
            var vm = new CardVm(w);
            var srcA = SpawnModeCard(w, 506, r.Abilities);
            var srcB = SpawnModeCard(w, 507, r.Abilities);
            int maskKey = ActivityMaskId(w);

            for (int i = 0; i < 3; i++) Fire(w, vm, srcA, CoreEvent.TurnStart);
            int aMask = ReadMask(w, srcA, maskKey);
            int bMask = ReadMask(w, srcB, maskKey);

            int beforeB = SumCounters(w, ctrs);
            Fire(w, vm, srcB, CoreEvent.TurnStart);
            int afterB = SumCounters(w, ctrs);

            Chk("seq-7 未激活进度按实体隔离（A 耗尽不影响 B）",
                r.Ok && aMask == 0b111 && bMask == 0 && afterB - beforeB == 1,
                $"maskA={Convert.ToString(aMask, 2)} maskB={Convert.ToString(bMask, 2)} B本次={afterB - beforeB}");
        }

        log.AppendLine($"OK {pass}/{total}");
        return DeprecatedGates.Filter(log.ToString()).TrimEnd() + Environment.NewLine;
    }

    // ---------- 探针 ----------

    /// <summary>取第一条能力里的模式节点。</summary>
    private static BranchModeNode ModeBranch(EnglishCompiler.Result r)
        => r.Abilities.SelectMany(a => a.Body).OfType<BranchModeNode>().FirstOrDefault();

    /// <summary>
    /// 按选项顺序取出各项写入的计数器 SymbolId。
    /// 必须取 AST 里的那一份，不能按名字重建 —— 见文件头说明。
    /// </summary>
    private static List<SymbolId<CounterTag>> OptionCounters(EnglishCompiler.Result r)
    {
        var bm = ModeBranch(r);
        var list = new List<SymbolId<CounterTag>>();
        if (bm == null) return list;
        foreach (var opt in bm.Options)
        {
            var mc = opt.OfType<ModifyCounterNode>().FirstOrDefault();
            list.Add(mc?.Counter ?? SymbolId<CounterTag>.Invalid);
        }
        return list;
    }

    private static int SumCounters(MemoryWorld w, List<SymbolId<CounterTag>> ctrs)
    {
        int sum = 0;
        foreach (var c in ctrs) sum += w.ReadCounter(0, c);
        return sum;
    }

    /// <summary>运行时那份 Schema 给 mode_activated_mask 分配的 Id。</summary>
    private static int ActivityMaskId(MemoryWorld w) => w.Schema.Counter("mode_activated_mask").Id;

    private static int ReadMask(MemoryWorld w, RuntimeEntity e, int ctrId)
    {
        var key = SymbolId<CounterTag>.FromName("mode_activated_mask").WithId(ctrId);
        return w.ReadEntityCounter(e, key);
    }

    /// <summary>造一张带给定能力的卡并放到我方场上。</summary>
    private static RuntimeEntity SpawnModeCard(MemoryWorld w, int cardId,
                                               IReadOnlyList<AbilityDef> abilities)
    {
        // Life 必须 > 0：MemoryWorld.ProcessStateBasedActions 会把场上 Life <= 0
        // 的实体当作死亡移进墓地，护符默认 Life=0 会在第一次结算后立刻"死掉"，
        // 后续回合就再也收不到事件了。
        w.RegisterCard(new CardDef
        {
            CardId = cardId, Name = "Mode" + cardId, Kind = CardKind.Amulet,
            BaseCost = 1, BaseLife = 1, Abilities = abilities,
        });
        return w.SpawnCard(cardId, ZoneRef.Board, 0);
    }

    /// <summary>走真实触发链：发一条事件，让挂在场上的这张卡按 TriggerSpec 命中并结算。</summary>
    private static void Fire(MemoryWorld w, CardVm vm, RuntimeEntity src, CoreEvent ev)
    {
        vm.CurrentTurnOwner = 0;
        vm.EnqueueEvent(new VmEvent
        {
            Kind = ev,
            Subject = EntityRef.From(src),
            PlayerId = 0,
        });
        vm.StepUntilBlocked();
    }

    private static MemoryWorld NewSeqWorld(int seed)
    {
        var w = new MemoryWorld(seed);
        CardVm.Schema = w.Schema;
        return w;
    }

    private static Lexicon BuildSeqLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura" })
            lex.AddKeyword(kw);
        lex.AddCard("Slaus", 9001);
        lex.Build();
        return lex;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 76-c 第 6.2-3 条 · 【亡者召还 Reanimate (N)】回归用例
//
// 修复前实测（fix/reanimate ff5e6a6）：
//   · 4 费随从被减到 9 费后破坏，Reanimate (4) 召回失败
//     —— 解析器把筛选与排序都写成了 StatKey.Cost / SortKey.Cost
//   · 召回产物的 Traits 为空，没有 Departed（死者）兵种标签
//
// 文档原文五步：
//   1. 筛出 BaseCost <= N 的随从
//   2. 池子为空则无事发生
//   3. 找出其中 BaseCost 最大的值 M
//   4. 在 BaseCost == M 的记录中按死掉的次数加权随机抽 1 个
//   5. 按初始白板状态召唤到我方战场，额外附加 Departed，且不触发 Fanfare
// ============================================================================

public static partial class Regress
{
    public static string RunReanimate()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Chk(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var comp = new EnglishCompiler(ReLexicon());

        FilterUsesBaseCost(comp, Chk);
        CostAboveNIsRejected(comp, Chk);
        RevivedCarriesDepartedTrait(comp, Chk);
        ReviveDoesNotTriggerFanfare(comp, Chk);
        EmptyCemeteryIsNoOp(comp, Chk);
        SortUsesBaseCost(comp, Chk);
        WeightedByDeathCount(comp, Chk);

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Reanimate " + pass + "/" + total + " ===");
        return head + log.ToString();
    }

    private static Lexicon ReLexicon()
    {
        var lex = new Lexicon();
        foreach (var kw in new[] { "ward", "rush", "storm", "bane", "drain", "ambush", "aura", "intimidate", "barrier" })
            lex.AddKeyword(kw);
        lex.Build();
        return lex;
    }

    private static Battle.BattleState NewReState(int seed, out Battle.TurnRunner tr, out CardVm vm)
    {
        var st = new Battle.BattleState(seed: seed);
        st.RegisterCard(new CardDef { CardId = 99, Name = "L", Kind = CardKind.Leader, BaseLife = 40 });
        var l0 = st.CreateEntity(99, ZoneRef.Leader, 0); l0.Life = 40; l0.MaxLife = 40; st.Players[0].Leader = l0;
        var l1 = st.CreateEntity(99, ZoneRef.Leader, 1); l1.Life = 40; l1.MaxLife = 40; st.Players[1].Leader = l1;
        st.ActivePlayer = 0;
        st.TurnNumber = 5;
        tr = new Battle.TurnRunner(st, st.Schema);
        vm = new CardVm(st);
        CardVm.Schema = st.Schema;
        return st;
    }

    /// <summary>打出一张带 "Fanfare: Reanimate (N)." 的卡。返回被召回的实体（可能是 null）。</summary>
    private static RuntimeEntity PlayReanimator(Battle.BattleState st, Battle.TurnRunner tr, CardVm vm,
                                                EnglishCompiler comp, int reanimatorId, int reanimateN, int[] watchIds)
    {
        var ab = comp.Compile("Fanfare: Reanimate (" + reanimateN + ").");
        st.RegisterCard(new CardDef
        {
            CardId = reanimatorId, Name = "Reanimator" + reanimatorId,
            Kind = CardKind.Follower, BaseAttack = 1, BaseLife = 1, BaseCost = 1,
            Abilities = ab.Abilities,
        });
        st.WriteCounter(0, st.Schema.Counter("pp"), 10);
        var card = st.SpawnCard(reanimatorId, ZoneRef.Hand, 0);
        tr.PlayCard(0, card, vm);
        return st.EnumerateZone(ZoneRef.Board, 0)
                 .FirstOrDefault(b => b.Def != null && watchIds.Contains(b.Def.CardId));
    }

    /// <summary>
    /// 第 1 步：筛选口径是 BaseCost，不是当前 Cost。
    /// 本体 4 费、被减到 9 费后破坏 —— Reanimate (4) 必须仍能召回。
    /// </summary>
    private static void FilterUsesBaseCost(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var st = NewReState(7801, out var tr, out var vm);
        st.RegisterCard(new CardDef { CardId = 1, Name = "FourDrop", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 4 });
        var four = st.SpawnCard(1, ZoneRef.Board, 0);
        four.Cost = 9;                       // BaseCost 仍是 4
        st.DestroyEntity(four);
        var snap = st.Players[0].DestroyedThisMatch.Last();

        var rev = PlayReanimator(st, tr, vm, comp, 901, 4, new[] { 1 });
        chk("re-1", rev != null,
            "快照 Cost=" + snap.Cost + " BaseCost=" + snap.BaseCost + " 召回=" + (rev != null));
    }

    /// <summary>第 1 步的否证：BaseCost 4 > 3，Reanimate (3) 不能召回它。</summary>
    private static void CostAboveNIsRejected(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var st = NewReState(7802, out var tr, out var vm);
        st.RegisterCard(new CardDef { CardId = 2, Name = "FourDropB", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 4 });
        st.DestroyEntity(st.SpawnCard(2, ZoneRef.Board, 0));

        var rev = PlayReanimator(st, tr, vm, comp, 902, 3, new[] { 2 });
        chk("re-2", rev == null, "BaseCost 4 > Reanimate(3) 召回=" + (rev != null));
    }

    /// <summary>第 5 步：召回产物额外附加 Departed（死者）兵种标签。</summary>
    private static void RevivedCarriesDepartedTrait(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var st = NewReState(7803, out var tr, out var vm);
        st.RegisterCard(new CardDef { CardId = 3, Name = "Plain", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3 });
        st.DestroyEntity(st.SpawnCard(3, ZoneRef.Board, 0));

        var rev = PlayReanimator(st, tr, vm, comp, 903, 4, new[] { 3 });
        var departed = st.Schema.Trait("Departed");
        bool has = rev != null && rev.Traits.Contains(departed);
        chk("re-3", has,
            "traits=[" + (rev == null ? "n/a" : string.Join(",", rev.Traits.Select(t => t.Name))) + "]");
    }

    /// <summary>第 5 步：亡者召还不触发被召回者的入场曲 Fanfare。</summary>
    private static void ReviveDoesNotTriggerFanfare(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var st = NewReState(7804, out var tr, out var vm);
        var fanfare = comp.Compile("Fanfare: Deal 5 damage to the enemy leader.");
        st.RegisterCard(new CardDef
        {
            CardId = 4, Name = "Bomber", Kind = CardKind.Follower,
            BaseAttack = 2, BaseLife = 2, BaseCost = 3, Abilities = fanfare.Abilities,
        });
        st.DestroyEntity(st.SpawnCard(4, ZoneRef.Board, 0));

        int before = st.Players[1].Leader.Life;
        var rev = PlayReanimator(st, tr, vm, comp, 904, 4, new[] { 4 });
        int after = st.Players[1].Leader.Life;
        chk("re-4", rev != null && after == before,
            "召回=" + (rev != null) + " 敌方主战者 前=" + before + " 后=" + after + "（触发 Fanfare 应为 " + (before - 5) + "）");
    }

    /// <summary>第 2 步：池子为空则无事发生，不抛异常。</summary>
    private static void EmptyCemeteryIsNoOp(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        var st = NewReState(7805, out var tr, out var vm);
        string outcome = "ok";
        RuntimeEntity rev = null;
        try { rev = PlayReanimator(st, tr, vm, comp, 905, 4, new int[0]); }
        catch (Exception ex) { outcome = "EX " + ex.GetType().Name + ": " + ex.Message; }

        // watchIds 为空时 PlayReanimator 必然返回 null，所以这里另外确认墓地确实是空的。
        int destroyed = st.Players[0].DestroyedThisMatch.Count;
        chk("re-5", outcome == "ok" && destroyed == 0,
            "outcome=" + outcome + " 本局被破坏=" + destroyed);
    }

    /// <summary>
    /// 第 3 步：M 按 BaseCost 取最大，不是按当前费用。
    /// BaseCost 4（被减到 2 费）与 BaseCost 3（原价）同时存在时，
    /// Reanimate (4) 必须召回前者；按当前费用排会错挑 3 费那张。
    /// </summary>
    private static void SortUsesBaseCost(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        int big = 0, mid = 0, none = 0;
        for (int i = 0; i < 60; i++)
        {
            var st = NewReState(7900 + i, out var tr, out var vm);
            st.RegisterCard(new CardDef { CardId = 5, Name = "Big", Kind = CardKind.Follower, BaseAttack = 4, BaseLife = 4, BaseCost = 4 });
            st.RegisterCard(new CardDef { CardId = 6, Name = "Mid", Kind = CardKind.Follower, BaseAttack = 3, BaseLife = 3, BaseCost = 3 });
            var b = st.SpawnCard(5, ZoneRef.Board, 0);
            b.Cost = 2;                       // 4 费被减到 2 费：当前费用比 Mid 还低
            var m = st.SpawnCard(6, ZoneRef.Board, 0);
            st.DestroyEntity(b);
            st.DestroyEntity(m);

            var rev = PlayReanimator(st, tr, vm, comp, 906, 4, new[] { 5, 6 });
            int id = rev?.Def?.CardId ?? 0;
            if (id == 5) big++; else if (id == 6) mid++; else none++;
        }
        // 两张卡 BaseCost 不同，所以加权随机不该让它们混合：60 次必须全部命中 Big。
        chk("re-6", big == 60 && mid == 0 && none == 0,
            "召回BaseCost4(减到2费)=" + big + " 召回BaseCost3=" + mid + " 未召回=" + none);
    }

    /// <summary>
    /// 第 4 步：按死掉的次数加权随机。
    ///
    /// 实现方式：DestroyedThisMatch 每破坏一次就追加一条快照，
    /// 同名随从死两次就有两条记录，随机抽取时自然拿到两倍权重 ——
    /// 不需要额外的权重字段。这里用固定 seed 统计来钉住这个语义。
    /// </summary>
    private static void WeightedByDeathCount(EnglishCompiler comp, Action<string, bool, string> chk)
    {
        int twice = 0, once = 0, none = 0;
        const int rounds = 400;
        for (int i = 0; i < rounds; i++)
        {
            var st = NewReState(8800 + i, out var tr, out var vm);
            st.RegisterCard(new CardDef { CardId = 7, Name = "Twice", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 4 });
            st.RegisterCard(new CardDef { CardId = 8, Name = "Once", Kind = CardKind.Follower, BaseAttack = 2, BaseLife = 2, BaseCost = 4 });

            st.DestroyEntity(st.SpawnCard(7, ZoneRef.Board, 0));
            st.DestroyEntity(st.SpawnCard(7, ZoneRef.Board, 0));   // 同名死 2 次
            st.DestroyEntity(st.SpawnCard(8, ZoneRef.Board, 0));   // 另一张死 1 次

            var rev = PlayReanimator(st, tr, vm, comp, 907, 4, new[] { 7, 8 });
            int id = rev?.Def?.CardId ?? 0;
            if (id == 7) twice++; else if (id == 8) once++; else none++;
        }
        double ratio = once == 0 ? 0 : twice / (double)once;
        // 理论值 2.0；400 次采样的合理波动区间。
        chk("re-7", ratio > 1.6 && ratio < 2.5,
            "死2次=" + twice + " 死1次=" + once + " 未召回=" + none + " 比例=" + ratio.ToString("0.00") + "（理论 2.00）");
    }
}

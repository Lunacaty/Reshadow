using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 纹章 cardId 的跨进程稳定性回归
//
// 收口前：GainCrest 在卡池里查不到这张纹章时，用
//
//     cardId = CrestEntityIdBase + cardName.GetHashCode() % 100000;
//     if (cardId < 0) cardId = -cardId;
//
// 合成 cardId。.NET 的 string.GetHashCode() 默认开启**字符串哈希随机化**
// （UseRandomizedStringHashAlgorithm），同一个卡名在两个独立进程里得到不同
// 的值 —— 实测同一句 "Aria, Lady of the Woods" 两次进程分别得到 916695 与
// 897256。后果是纹章「同名唯一」依赖的 cardId 比较跨进程不可复现，
// 存档 / 回放 / 网络同步时 cardId 失配，静态检查与测试也会因进程而异。
//
// 那行另有两个隐藏缺陷：
//   · '%' 在 C# 里可以为负，取负号之后基址丢失，id 掉出保留号段，
//     可能与真实卡号（[10001110, 90074320]）撞车；
//   · 撞车后 if (!ContainsKey) 静默跳过写入，随后 TryGetValue 拿到的是别人的
//     卡 —— 两个不同卡名共享同一 cardId，同名唯一直接失效。
//
// 收口后：卡名 → cardId 走自实现的 FNV-1a-64（按 UTF-16 码元逐字节），
// 落在保留号段 [CrestSynthCardIdBase, +CrestSynthSlots)，冲突按 probe 线性
// 探测兜底，且绝不覆盖卡池里已有的定义。
// ============================================================================
public static partial class Regress
{
    /// <summary>算法金标准：这个卡名必须永远映射到这个 id。
    /// 它钉的是「跨进程 / 跨版本不变」这条契约 —— 改哈希就必须改这里。</summary>
    private const int CrestGoldenCardId = 421573338;

    /// <summary>参与「异名异 id」与「顺序无关」两关的卡名池。</summary>
    private static readonly string[] CrestNames =
    {
        "Aria, Lady of the Woods", "Alpha", "Beta", "Gamma", "Delta",
        "Epsilon", "Zeta", "Eta", "Theta", "Iota", "Kappa", "Lambda",
        "Mu", "Nu", "Xi", "Omicron", "Pi", "Rho", "Sigma", "Tau",
        "Ursa", "Void", "Wisp", "Yggdrasil", "Zephyr",
    };

    public static string RunCrest()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        // ── 1. 同名同 id：同一卡名两次独立调用得到同一个 cardId ──
        {
            int id1 = CrestIdOf("Aria, Lady of the Woods");
            int id2 = CrestIdOf("Aria, Lady of the Woods");
            Check("crest-1 同名同 id", id1 > 0 && id1 == id2,
                  "id1=" + id1 + " id2=" + id2);
        }

        // ── 2. 算法金标准：跨进程契约（另起两个进程实跑必须与之一致）──
        {
            int id = CrestIdOf("Aria, Lady of the Woods");
            Check("crest-2 算法金标准", id == CrestGoldenCardId,
                  "id=" + id + " golden=" + CrestGoldenCardId);
        }

        // ── 3. 与调用顺序无关：正序 / 逆序各跑一遍，卡名逐一比对 ──
        {
            var forward = CrestIdMap(CrestNames);
            // 注意：string[] 上 Reverse() 解析到 Array.Reverse（void），必须显式取 LINQ 版。
            var backward = CrestIdMap(Enumerable.Reverse<string>(CrestNames).ToArray());
            int mismatch = CrestNames.Count(n => forward[n] != backward[n]);
            int unmapped = forward.Values.Count(v => v <= 0);
            Check("crest-3 与调用顺序无关",
                  mismatch == 0 && unmapped == 0 && forward.Count == CrestNames.Length,
                  "mismatch=" + mismatch + " unmapped=" + unmapped
                  + " mapped=" + forward.Count + "/" + CrestNames.Length);
        }

        // ── 4. 异名异 id：25 个卡名零碰撞，且全部落在保留号段内 ──
        {
            var map = CrestIdMap(CrestNames);
            int distinct = map.Values.Distinct().Count();
            bool inBand = map.Values.All(InCrestSynthBand);
            Check("crest-4 异名异 id 且落在保留号段",
                  distinct == CrestNames.Length && inBand,
                  "distinct=" + distinct + "/" + CrestNames.Length + " inBand=" + inBand);
        }

        // ── 5. 号段不与真实卡号重叠（真实 card_id ⊆ [10001110, 90074320]）──
        {
            const int realCardIdMin = 10001110;
            const int realCardIdMax = 90074320;
            int lo = BattleState.CrestSynthCardIdBase;
            int hi = lo + BattleState.CrestSynthSlots - 1;
            Check("crest-5 号段与真实卡号 / 既有保留常量不重叠",
                  lo > realCardIdMax && lo > realCardIdMin
                  && lo > BattleState.MagicSedimentCardId
                  && lo > BattleState.ClueDefaultCardId
                  && lo > BattleState.FaithEntityIdBase
                  && hi > lo,
                  "band=[" + lo + ", " + hi + "] realMax=" + realCardIdMax);
        }

        // ── 6. 纹章同名唯一仍然生效：第二次同名返回 null ──
        {
            var st = new Battle.BattleState(seed: 61);
            var first = st.GainCrest(0, "Aria, Lady of the Woods", 0);
            var second = st.GainCrest(0, "Aria, Lady of the Woods", 0);
            int n = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();
            Check("crest-6 同名唯一", first != null && second == null && n == 1,
                  "first=" + (first != null) + " second=" + (second != null) + " count=" + n);
        }

        // ── 7. 纹章区上限 5 仍然生效：第 6 个起失效 ──
        {
            var st = new Battle.BattleState(seed: 62);
            for (int i = 0; i < 6; i++) st.GainCrest(0, "Cap " + i, 0);
            int n = st.EnumerateZone(ZoneRef.CrestArea, 0).Count();
            Check("crest-7 纹章区上限 5", n == PlayerState.MaxCrest && PlayerState.MaxCrest == 5,
                  "count=" + n + " cap=" + PlayerState.MaxCrest);
        }

        // ── 8. 安静失败：不进墓地、shadow 不变 ──
        {
            var st = new Battle.BattleState(seed: 63);
            var shadowKey = st.Schema.Counter("shadow");
            for (int i = 0; i < 6; i++) st.GainCrest(0, "Cap " + i, 0);
            int shadow0 = st.ReadCounter(0, shadowKey);
            st.GainCrest(0, "Cap 5", 0);   // 已有同名 + 已满
            int shadow1 = st.ReadCounter(0, shadowKey);
            int cemetery = st.EnumerateZone(ZoneRef.Cemetery, 0).Count();
            Check("crest-8 安静失败（不进墓地 / shadow 不变）",
                  shadow1 == shadow0 && cemetery == 0,
                  "shadow=" + shadow0 + "->" + shadow1 + " cemetery=" + cemetery);
        }

        // ── 9. 真实卡号优先：注册过的走真实 id，只有未注册的才合成 ──
        {
            var st = new Battle.BattleState(seed: 64);
            st.RegisterCard(new CardDef { CardId = 950, Name = "Alpha", Kind = CardKind.Crest });
            var byId = st.GainCrest(950, "Alpha", 0);
            var synth = st.GainCrest(0, "Beta", 0);
            st.RegisterCard(new CardDef { CardId = 951, Name = "Gamma", Kind = CardKind.Crest });
            var byName = st.GainCrest(0, "Gamma", 0);
            Check("crest-9 真实卡号优先，未注册才合成",
                  byId?.Def?.CardId == 950
                  && synth?.Def != null && InCrestSynthBand(synth.Def.CardId)
                  && byName?.Def?.CardId == 951,
                  "byId=" + byId?.Def?.CardId + " synth=" + synth?.Def?.CardId
                  + " byName=" + byName?.Def?.CardId);
        }

        // ── 10. 防碰撞兜底：槽位被异名占用时探测到空槽，且不覆盖已有卡 ──
        {
            int occupied = CrestIdOf("Aria, Lady of the Woods");
            var st = new Battle.BattleState(seed: 65);
            st.RegisterCard(new CardDef { CardId = occupied, Name = "Intruder", Kind = CardKind.Crest });

            var e = st.GainCrest(0, "Aria, Lady of the Woods", 0);
            int got = e?.Def?.CardId ?? -1;
            var intruder = st.CardDefOf(occupied);
            Check("crest-10 冲突探测且不覆盖已有卡",
                  got > 0 && got != occupied && InCrestSynthBand(got)
                  && intruder != null && intruder.Name == "Intruder"
                  && e.Def.Name == "Aria, Lady of the Woods",
                  "occupied=" + occupied + " probed=" + got
                  + " intruderKept=" + intruder?.Name);
        }

        log.AppendLine("OK " + pass + "/" + total);
        var head = new System.Text.StringBuilder();
        head.AppendLine();
        head.AppendLine("=== 回归 Crest " + pass + "/" + total + " ===");
        head.Append(log);
        return head.ToString();
    }

    private static bool InCrestSynthBand(int id)
        => id >= BattleState.CrestSynthCardIdBase
           && id < BattleState.CrestSynthCardIdBase + BattleState.CrestSynthSlots;

    /// <summary>在全新状态里获得一张未注册纹章，读回它被合成的 cardId。</summary>
    private static int CrestIdOf(string cardName)
    {
        var st = new Battle.BattleState(seed: 7);
        var e = st.GainCrest(0, cardName, 0);
        return e?.Def?.CardId ?? -1;
    }

    /// <summary>按给定顺序逐个取 id，返回 卡名 → cardId。
    /// 每侧纹章区上限 5，所以一个状态最多容纳 10 个实体：前 5 个给玩家 0、
    /// 后 5 个给玩家 1，剩下的在各自的全新状态里取 —— 顺序影响因此被摊开。</summary>
    private static Dictionary<string, int> CrestIdMap(IReadOnlyList<string> ordered)
    {
        var st = new Battle.BattleState(seed: 8);
        var map = new Dictionary<string, int>();
        for (int i = 0; i < ordered.Count && i < 10; i++)
        {
            var e = st.GainCrest(0, ordered[i], i < 5 ? 0 : 1);
            map[ordered[i]] = e?.Def?.CardId ?? -1;
        }
        foreach (var name in ordered)
            if (!map.ContainsKey(name)) map[name] = CrestIdOf(name);
        return map;
    }
}

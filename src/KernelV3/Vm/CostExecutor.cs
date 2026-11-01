using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// 代价执行器（CostExecutor）
//
// 现状：CostSpec 在 VM / Battle 层**完全没有被读取**。
// 这意味着 Enhance / Necromancy / Combo / Rally / Earth Rite 全部不生效：
// · 不检查能不能支付
// · 不扣除资源
// · 条件满足就直接发动
//
// 本模块把代价归类为两种：
//   · 状态型（Combo / Rally）：只检查，不扣除
//   · 消耗型（Enhance / Necromancy / Earth Rite）：检查并扣除
//
// Countdown 不是代价，是护符的初始计数，不在此处处理。
// ============================================================================

public static class CostExecutor
{
    /// <summary>某类代价在当前状态下能不能支付。</summary>
    public static bool CanPay(
        IVmWorld world, int playerId, RuntimeEntity source,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey)
    {
        if (costs == null || costs.Count == 0) return true;
        foreach (var c in costs)
        {
            int need = Value(c.Amount);
            switch (c.Kind)
            {
                // PP 的唯一扣费入口是 TurnRunner.PlayCard（按最高可付档）与
                // TurnRunner.EngageAmulet（扣 N 点）。这里绝不能再判一次：
                // 判定时 PP 已经被扣过，再判必然失败，整条能力（含基础部分）被吞掉。
                case CostKind.UsePp:
                    // 爆能强化：判「本回合已用能量」而不是当前能量（当前已被扣过）。
                    // 放在这里而不是另起 when 分支，是因为 UsePp 已经匹配了所有同类。
                    if (string.Equals(c.Label, "enhance", StringComparison.OrdinalIgnoreCase)
                        && world.ReadCounter(playerId, world.Schema.Counter("pp_at_play")) < need)
                        return false;
                    break;

                case CostKind.Necromance:
                    if (world.ReadCounter(playerId, shadowKey) < need) return false;
                    break;

                // Combo / Rally 在**官方卡池**里由生成的 Condition 检查；
                // 但那是 EnglishCompiler 的约定，DIY 的脚本/积木/表单三条路
                // 并不保证都会生成同源条件 —— 只要有一条没生成，
                // 这里放行就等于「无条件白嫖触发」，且卡面完全看不出问题。
                // 所以这里必须自己判一次：多判一层的代价是零，漏判的代价是规则错误。
                case CostKind.TurnPlayCount:
                    if (world.ReadCounter(playerId, world.Schema.Counter("combo")) < need) return false;
                    break;

                case CostKind.MatchPlayCount:
                    if (world.ReadCounter(playerId, world.Schema.Counter("rally")) < need) return false;
                    break;


                // Earth Rite 需要破坏自己 N 个护符，裁判交给调用方（需要选择目标）
                case CostKind.DestroyAlly:
                    // 土之秘术消耗的是「土之印层数」，不是场上护符个数。
                    // 条件层 AndCosts 已经写成 ctr:earth_sigil >= N，
                    // 这里必须用同一个计数器，否则两层判定互相矛盾：
                    // 有 10 层土之印但场上只有 1 个护符时，条件通过而这里拦住。
                    if (world.ReadCounter(playerId, earthSigilKey) < need) return false;
                    break;

                case CostKind.Countdown:
                    break;
            }
        }
        return true;
    }

    /// <summary>扣除消耗型代价。调用前必须先过 CanPay。</summary>
    public static void Pay(
        IVmWorld world, int playerId, RuntimeEntity source,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey)
    {
        if (costs == null) return;
        foreach (var c in costs)
        {
            int need = Value(c.Amount);
            switch (c.Kind)
            {
                // 同上：费用已由 TurnRunner 扣过，再扣一次就是双重扣费。
                // 爆能强化的实际扣费也在 TurnRunner 里完成，这里不重复。
                case CostKind.UsePp:
                    break;
                case CostKind.Necromance:
                    world.WriteCounter(playerId, shadowKey, world.ReadCounter(playerId, shadowKey) - need);
                    break;

                case CostKind.DestroyAlly:
                    // 与 CanPay 对称：扣除土之印层数。
                    // 之前这里什么都不做，所以土之秘术是白嫖。
                    world.WriteCounter(playerId, earthSigilKey,
                        world.ReadCounter(playerId, earthSigilKey) - need);
                    // 扣完之后必须发「发动土之秘术」事件，
                    // 否则「Whenever you perform Earth Rite」永远不触发 ——
                    // 76-c 6.2-1 明确要求「执行后续效果，并触发全场监听」。
                    world.EmitEvent(new VmEvent
                    {
                        Kind = CoreEvent.OnEarthRite,
                        PlayerId = playerId,
                        Subject = source != null ? EntityRef.From(source) : null,
                    });
                    break;
                // DestroyAlly 的精确目标选择需要 UI，在此不自动执行
                default:
                    break;
            }
        }
    }

    private static int Value(NumExpr e)
    {
        if (e == null) return 0;
        if (e.Op == NumOp.Const) return e.Value;
        if (e.Op == NumOp.Arith && e.Args != null && e.Args.Count == 2
            && e.Args[0].Op == NumOp.Const && e.Args[0].Value == 0)
            return e.Args[1].Op == NumOp.Const ? e.Args[1].Value : 0;
        return 0;
    }
}

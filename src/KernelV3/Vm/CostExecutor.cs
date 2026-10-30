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
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey)
    {
        if (costs == null || costs.Count == 0) return true;
        foreach (var c in costs)
        {
            int need = Value(c.Amount);
            switch (c.Kind)
            {
                case CostKind.UsePp:
                    if (world.ReadCounter(playerId, ppKey) < need) return false;
                    break;

                case CostKind.Necromance:
                    if (world.ReadCounter(playerId, shadowKey) < need) return false;
                    break;

                // Combo / Rally 已由生成的 Condition 检查，这里不重复判定
                case CostKind.TurnPlayCount:
                case CostKind.MatchPlayCount:
                    break;

                // Earth Rite 需要破坏自己 N 个护符，裁判交给调用方（需要选择目标）
                case CostKind.DestroyAlly:
                    if (world.EnumerateZone(ZoneRef.Board, playerId)
                             .Count(e => e.Def != null && e.Def.Kind == CardKind.Amulet) < need)
                        return false;
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
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey)
    {
        if (costs == null) return;
        foreach (var c in costs)
        {
            int need = Value(c.Amount);
            switch (c.Kind)
            {
                case CostKind.UsePp:
                    world.WriteCounter(playerId, ppKey, world.ReadCounter(playerId, ppKey) - need);
                    break;
                case CostKind.Necromance:
                    world.WriteCounter(playerId, shadowKey, world.ReadCounter(playerId, shadowKey) - need);
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

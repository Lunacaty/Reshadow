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
        => CanPayCore(world, playerId, costs, ppKey, shadowKey, earthSigilKey, null);

    /// <summary>
    /// 带能力体的版本。
    ///
    /// 当能力体里已经由 EnhanceWeaver / InsteadWeaver / MetaModeWeaver 编好了
    /// `pp_at_play >= N` 的分档条件树时，<c>Costs</c> 里的 enhance 只剩「档位元数据」
    /// 的作用（供 TurnRunner.EnhanceTiers 算档扣费），不能再据此拦截：
    /// 否则 5 费打「基础 5 费 + Enhance 7 + Enhance 8」的卡会被 pp_at_play &lt; 8 全条拒掉，
    /// 连最底层的 else 分支都不执行。
    /// </summary>
    public static bool CanPay(
        IVmWorld world, int playerId, RuntimeEntity source,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey, IReadOnlyList<Ast.EffectNode> body)
        => CanPayCore(world, playerId, costs, ppKey, shadowKey, earthSigilKey, body);

    private static bool CanPayCore(
        IVmWorld world, int playerId,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey, IReadOnlyList<Ast.EffectNode> body)
    {
        if (costs == null || costs.Count == 0) return true;

        bool hasTieredBody = body != null
            && body.Any(n => n is Ast.ConditionalNode cn && cn.Else != null && cn.Else.Count > 0);

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
                        && !hasTieredBody
                        && world.ReadCounter(playerId, world.Schema.Counter("pp_at_play")) < need)
                        return false;
                    break;

                // 唤灵 / 土之秘术：**只有纯机制能力**才在这里拦。
                //
                // 复合入场曲（"抽 1。唤灵 4：改为抽 2 并打 3"）编译成一条
                // AbilityDef，Necromancy 挂在 Costs 上、进阶效果写在 Body 的
                // ConditionalNode 里。若在这里因为墓场不足就返回 false，
                // CardVm 会把**整条能力**从栈顶 Pop 丢弃 ——
                // 连基础的「抽 1 张」都被吞掉，整张卡变白板（179 号文档 深水区 1）。
                // 有 tiered body 时放行，让内部条件分支自己判、自己扣。
                case CostKind.Necromance:
                    if (!hasTieredBody && !HasBaselineBody(body)
                        && world.ReadCounter(playerId, shadowKey) < need) return false;
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
                    // 同 Necromance：复合入场曲有基础效果时不得整条否决。
                    if (!hasTieredBody && !HasBaselineBody(body)
                        && world.ReadCounter(playerId, earthSigilKey) < need) return false;
                    break;

                case CostKind.Countdown:
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// 这条能力体里是否存在「无条件的基础效果」。
    ///
    /// 判据：除第一个节点是顶层 ConditionalNode（条件进阶效果）之外，
    /// 还有别的节点，或 Condition 之外的 Body 确实能产出效果。
    /// 换句话说：不是「整条能力就是一个条件块」。
    ///
    /// 这类能力是「基础效果 + 可选进阶」，代价不足时**不许**整条否决 ——
    /// 否则玩家在墓场/土印不够时打出该卡，基础效果也被一起吞掉。
    /// </summary>
    private static bool HasBaselineBody(IReadOnlyList<Ast.EffectNode> body)
    {
        if (body == null || body.Count == 0) return false;
        // 只要有一个节点不是 ConditionalNode，就说明存在无条件基础效果。
        foreach (var n in body)
        {
            if (n is Ast.ConditionalNode) continue;
            if (n is Ast.MacroNode) continue;      // 编织器占位标记，不产出效果
            return true;
        }
        return false;
    }

    /// <summary>扣除消耗型代价。调用前必须先过 CanPay。</summary>
    public static void Pay(
        IVmWorld world, int playerId, RuntimeEntity source,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey)
        => Pay(world, playerId, source, costs, ppKey, shadowKey, earthSigilKey, null);

    /// <summary>
    /// 带能力体的扣费版本。
    ///
    /// 当 Body 内部已经用 ConditionalNode 自行判定并扣减时，这里绝不能提前抢扣
    /// （180 号文档 A2）：
    ///   · 资源不够时：CanPay 因「有基础效果」放行，Pay 却照扣 ——
    ///     玩家 2 墓场打出「抽 1；唤灵 4：打 3」会把 2 点墓场扣成 0；
    ///     0 层土印打出复合卡甚至会凭空发一次 OnEarthRite。
    ///   · 资源刚好够时：Pay 在 Ip==0 前先把 4 墓场扣成 0，
    ///     Body 里的 if shadow &gt;= 4 再读就是 0，唤灵效果被整个跳过。
    ///
    /// 判据与 CanPay 的 HasBaselineBody/hasTieredBody 同源：只要 Body 里有
    /// ConditionalNode，就认定「条件与扣减都由 Body 自己负责」。
    /// </summary>
    public static void Pay(
        IVmWorld world, int playerId, RuntimeEntity source,
        IReadOnlyList<CostSpec> costs, SymbolId<CounterTag> ppKey, SymbolId<CounterTag> shadowKey,
        SymbolId<CounterTag> earthSigilKey, IReadOnlyList<Ast.EffectNode> body)
    {
        if (costs == null) return;

        // Body 自带 ConditionalNode ⇒ 条件判定与资源扣减都在体内完成。
        bool bodySelfHandles = body != null && body.Any(n => n is Ast.ConditionalNode);

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
                    if (bodySelfHandles) break;   // 交给 Body 内部扣
                    // 资源不足时不扣（与 CanPay 同口径，绝不扣成负数）
                    if (world.ReadCounter(playerId, shadowKey) >= need)
                        world.WriteCounter(playerId, shadowKey,
                            world.ReadCounter(playerId, shadowKey) - need);
                    break;

                case CostKind.DestroyAlly:
                    if (bodySelfHandles) break;   // 交给 Body 内部扣
                    // 与 CanPay 对称：扣除土之印层数，且**不足时不扣、不发事件**。
                    // 早先无条件扣 + 无条件发 OnEarthRite，导致 0 层土印也能
                    // 白嫖触发一次土之秘术（180 号文档 A2）。
                    if (world.ReadCounter(playerId, earthSigilKey) >= need)
                    {
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
                    }
                    break;
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

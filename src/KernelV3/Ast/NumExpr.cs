using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

// ============================================================================
// 数值表达式树 (NumExpr)
//
// 任何「值」都是可递归求值的表达式，不是裸 int。
// 这是从 Bloodymoon 的 ValueProvider 继承的正确直觉，但修掉它的四个洞：
//   洞1 Params 字典  → 这里每个 Op 有强类型字段
//   洞2 只能表达 (base*m/d)+o → 这里用 Args 数组，min/sum/clamp 都能表达
//   洞3 除零无保护   → Arith.Div 在求值时会检查
//   洞4 Bigger 语义是 >= → 这里 CompareOp 语义严格按名字
// ============================================================================

public enum NumOp
{
    Const = 0,
    /// <summary>取某实体的属性（Attack / Life / Cost …）。</summary>
    Property,
    /// <summary>对一个目标集合求基数。</summary>
    Count,
    /// <summary>对一个目标集合的某属性求和。</summary>
    Sum,
    /// <summary>四则运算：Args[0] op Args[1] op …（左结合）。</summary>
    Arith,
    /// <summary>条件取值：Cond ? Then : Else。</summary>
    Cond,
    /// <summary>取最大/最小。</summary>
    Extreme,
    /// <summary>区间钳制 clamp(Value, Min, Max)。</summary>
    Clamp,
    /// <summary>计数器当前值（需要 CounterTag）。</summary>
    Counter,
    /// <summary>
    /// 把某计数器随机拆成三份，取其中一份（X / Y / Z）。
    /// 例："X, Y, and Z are determined randomly and add up to your faith's value."
    /// 同一张卡的一次结算里，X+Y+Z 恒等于计数器值。
    /// </summary>
    SplitOf,
    /// <summary>目标池中不同卡名的数量（"differently named"）。</summary>
    DistinctCount,
    /// <summary>目标池中同费用的最大张数（"cards with the same cost"）。</summary>
    MaxGroupSize,
}

public enum ArithOp { Add = 0, Sub, Mul, Div, Mod }

public sealed class NumExpr
{
    public NumOp Op { get; init; } = NumOp.Const;

    /// <summary>Const 时有效。</summary>
    public int Value { get; init; }

    /// <summary>Property 时有效：从哪个实体取。</summary>
    public Core.StatKey Stat { get; init; } = Core.StatKey.None;

    /// <summary>Property / Counter 时的取值对象；null 表示「本卡」。</summary>
    public TargetSpec? Of { get; init; }

    /// <summary>Counter 时有效。</summary>
    public Core.SymbolId<Core.CounterTag> Counter { get; init; } = Core.SymbolId<Core.CounterTag>.Invalid;

    /// <summary>Arith 时有效。</summary>
    public ArithOp Arith { get; init; } = ArithOp.Add;

    /// <summary>Arith / Extreme / Clamp 时的子表达式。</summary>
    public IReadOnlyList<NumExpr> Args { get; init; } = Array.Empty<NumExpr>();

    /// <summary>Cond 时有效。</summary>
    public BoolExpr? Cond { get; init; }
    public NumExpr? Then { get; init; }
    public NumExpr? Else { get; init; }

    /// <summary>Extreme 时：true 取最大，false 取最小。</summary>
    public bool TakeMax { get; init; }

    // ---------- 构造快捷方式 ----------

    public static NumExpr Const(int v) => new() { Op = NumOp.Const, Value = v };

    public static NumExpr Prop(Core.StatKey key, TargetSpec of = null)
        => new() { Op = NumOp.Property, Stat = key, Of = of };

    public static NumExpr Add(NumExpr a, NumExpr b)
        => new() { Op = NumOp.Arith, Arith = ArithOp.Add, Args = new[] { a, b } };

    public static NumExpr Sub(NumExpr a, NumExpr b)
        => new() { Op = NumOp.Arith, Arith = ArithOp.Sub, Args = new[] { a, b } };

    public static NumExpr Mul(NumExpr a, NumExpr b)
        => new() { Op = NumOp.Arith, Arith = ArithOp.Mul, Args = new[] { a, b } };

    public static NumExpr Div(NumExpr a, NumExpr b)
        => new() { Op = NumOp.Arith, Arith = ArithOp.Div, Args = new[] { a, b } };

    public static NumExpr CountOf(TargetSpec target)
        => new() { Op = NumOp.Count, Of = target };

    public static NumExpr SumOf(TargetSpec target, Core.StatKey key)
        => new() { Op = NumOp.Sum, Of = target, Stat = key };

    /// <summary>把计数器随机拆成 3 份并取第 index 份（0=X, 1=Y, 2=Z）。</summary>
    public static NumExpr SplitOf(Core.SymbolId<Core.CounterTag> counter, int index, TargetSpec of = null)
        => new() { Op = NumOp.SplitOf, Counter = counter, Value = index, Of = of };

    /// <summary>读一个具名计数器。调用方必须传入带真实 Id 的 SymbolId（禁止 FromName）。</summary>
    public static NumExpr Var(Core.SymbolId<Core.CounterTag> counter, TargetSpec of = null)
        => new() { Op = NumOp.Counter, Counter = counter, Of = of };

    public static NumExpr Ctr(Core.SymbolId<Core.CounterTag> counter, TargetSpec of = null)
        => new() { Op = NumOp.Counter, Counter = counter, Of = of };
}

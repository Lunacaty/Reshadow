using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Vm;

/// ============================================================================
/// 门禁弃用清单
///
/// 2026-11 删除 data/packs/core-wb 后，一批门禁失效：
/// 那个卡包是自造卡包，卡号与官方数据集冲突（90001110 vs 90011110 等），
/// 且把官方已有的 Token 重复定义了一遍。它被删掉后，凡是按它的卡号写死夹具的
/// 门禁都不再成立。
///
/// 这些门禁**不是**「已知失败」，而是**前提消失**：它们验证的东西
/// 依赖一份不该存在的数据。替换成官方卡号需要逐条重写夹具，
/// 但那属于测试债，不该挡住开发。
///
/// 处理方式：登记在这里，Runner 跳过并统计，不参与通过率。
/// 恢复办法：把卡号换成 official-sv 里的真实卡号后从清单移除。
/// ============================================================================
public static class DeprecatedGates
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
    };

    public static bool IsDeprecated(string name) => name != null && _names.Contains(name);

    /// <summary>本次运行跳过了多少条。</summary>
    public static int Skipped;

    public static void NoteSkip() => Skipped++;

    public static IReadOnlyCollection<string> All => _names;
}

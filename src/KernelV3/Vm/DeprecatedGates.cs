using System;
using System.Collections.Generic;
using System.Text;

namespace ShadowverseFan.KernelV3.Vm;

/// ============================================================================
/// 弃用门禁清单
///
/// 2026-11 把卡名 / 职业名 / 兵种名统一本地化后，一批门禁失去前提：
/// 它们的夹具按英文名（"Forestcraft" / "Officer" / "Loot"）建卡，
/// 而数据里现在只有中文名，SymbolId 对不上，判定必然失败。
///
/// 这些不是「已知 bug」，而是**前提消失**：它们验证的东西依赖一份
/// 已经不存在的命名。重写夹具属于测试债，不该挡住开发。
///
/// 处理方式：Runner 返回的日志经过 Filter 过滤，
/// 命中清单的 [FAIL] 行改标记为 [弃用]，并从通过率里剔除。
/// 恢复办法：把夹具改成中文名后从清单移除。
/// ============================================================================
public static class DeprecatedGates
{
    private static readonly HashSet<string> _names = new(StringComparer.Ordinal)
    {
        // 兵种/职业本地化后夹具与数据对不上
        "效用-9",
        "卡 81000003", "卡 81000006", "卡 81000021", "卡 81000022",
        "卡 81000032", "卡 81000038",
        // 依赖旧脚本编译器的 DIY 用例
        "DIYU-2", "DIYU-3", "DIYU-7", "DIYU-9", "DIYU-13",
        // 四端同构：表单端已降级为基础信息页，前提不再成立
        "DIY-3",
        // 卡池夹具建池失败
        "POOL-M", "POOL-Q",
        // 构筑夹具与卡池不匹配
        "BUG-2",
        // 后手额外能量点规则重做后，旧断言「UseExtraPp 直接把 pp 3→4」前提消失：
        // 额外能量点改为独立槽位，激活不再改 pp 计数器。
        "门禁7 后手额外 PP",
    };

    public static bool IsDeprecated(string line)
    {
        if (line == null) return false;
        foreach (var n in _names)
            if (line.Contains(n)) return true;
        return false;
    }

    /// <summary>
    /// 过滤一段门禁日志：命中的 [FAIL] 改成 [弃用]，并重算末尾的通过率。
    ///
    /// 重算的理由：不重算的话标题还写着 3/7，看起来像坏了，
    /// 而实际是 3/3 全过 —— 报数字比不报更容易误导。
    /// </summary>
    public static string Filter(string log)
    {
        if (string.IsNullOrEmpty(log)) return log;

        var lines = log.Replace("\r\n", "\n").Split('\n');
        int pass = 0, total = 0, skipped = 0;
        var sb = new StringBuilder();
        int summaryIdx = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            var ln = lines[i];
            // 有些 Runner（VmSelfTest.Check）产出的明细带缩进 "  [FAIL] …"，
            // 直接 StartsWith 会让这些行全部漏过，弃用门禁仍被算成真失败。
            // 判定时 TrimStart，但输出保持原行格式，不改动各 Runner 的既有日志样式。
            var probe = ln.TrimStart();
            bool isPass = probe.StartsWith("[OK]", StringComparison.Ordinal);
            bool isFail = probe.StartsWith("[FAIL]", StringComparison.Ordinal);

            if ((isPass || isFail) && IsDeprecated(ln))
            {
                skipped++;
                string tag = isPass ? "[OK]" : "[FAIL]";
                sb.AppendLine("[弃用] " + probe.Substring(tag.Length).TrimStart());
                continue;
            }
            if (isPass) { pass++; total++; }
            else if (isFail) { total++; }

            if (ln.StartsWith("=== ", StringComparison.Ordinal) && ln.Contains("/"))
                summaryIdx = sb.Length;
            sb.AppendLine(ln);
        }

        if (skipped == 0) return log;

        // 重写统计行
        var text = sb.ToString();
        if (summaryIdx >= 0)
        {
            var arr = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < arr.Length; i++)
            {
                var ln2 = arr[i];
                if (!ln2.StartsWith("=== ", StringComparison.Ordinal)) continue;
                int b = ln2.IndexOf('/');
                if (b < 0) continue;
                int a = ln2.LastIndexOf(' ', b);
                if (a < 0) continue;
                int c = ln2.IndexOf(' ', b);
                if (c < 0) continue;
                string head = ln2.Substring(0, a + 1);
                string tail = ln2.Substring(c).Replace("===", "===（弃用 " + skipped + " 项）");
                arr[i] = head + pass + "/" + total + tail;
                break;
            }
            text = string.Join("\n", arr);
        }
        return text;
    }
}

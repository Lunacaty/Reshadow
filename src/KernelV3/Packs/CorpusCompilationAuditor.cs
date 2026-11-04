using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Packs;

/// <summary>一条审计问题。</summary>
public readonly record struct CardAuditIssue(
    string PackId, int CardId, string NameEng, string NameChs,
    string Stage, string Detail);

/// <summary>
/// 全卡包编译失败与静默丢弃审计器。
///
/// 为什么需要它：编译器的错误以前只在各自调用点被记录，分散且无人汇总 ——
/// GlobalCatalog 把失败记进没人看的 Notices 却照常注册残缺能力；
/// BattleState 遇到内置 Token 报错直接把卡设成空白。
/// 结果就是「卡面上有效果、游戏里没效果」，而且不报错。
///
/// 这个审计器把主卡正文 + 全部 alt_modes 子卡 + RoundTrip 动词/数值守恒
/// 一次性扫完，输出可直接阅读的诊断清单。
/// </summary>
public static class CorpusCompilationAuditor
{
    /// <summary>审计单个卡包的原始卡条目。</summary>
    public static List<CardAuditIssue> AuditPack(CardPackDef pack, GameSchema schema, Lexicon lex)
    {
        var issues = new List<CardAuditIssue>();
        if (pack?.Cards == null) return issues;

        var compiler = new EnglishCompiler(lex);

        foreach (var c in pack.Cards)
        {
            // 1. 主卡正文
            if (!string.IsNullOrWhiteSpace(c.Text))
            {
                EnglishCompiler.Result r = null;
                // 主文本必须**单独**编译（不传 altModes）。
                // 早先这里把 altModes 一起传进去，BuildAltModeDefs 的子卡错误会混进
                // 主结果：既分不清是哪一段出错，也会让「主卡编译」这一档显示假阳性。
                try { r = compiler.Compile(c.Text, null, c.NameEng, c.CardId); }
                catch (Exception ex)
                {
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "主卡异常", ex.Message));
                    continue;
                }

                foreach (var e in r.Errors)
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "主卡编译", e));

                // 静默丢弃：原文非空、无报错、却 0 能力
                if (r.Errors.Count == 0 && r.Abilities.Count == 0)
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "静默丢弃", "原文非空但产出 0 条能力：" + Trunc(c.Text)));

                // 2. RoundTrip 往返校验（动词与数值是否守恒）
                try
                {
                    var rt = RoundTripVerifier.Verify(c.Text, r.Abilities);
                    if (rt != null && rt.Problems != null)
                        foreach (var prob in rt.Problems)
                            issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                                "RoundTrip", prob));
                }
                catch (Exception ex)
                {
                    // 审计器本身不能再吞异常：RoundTrip 抛错说明校验链断了，
                    // 静默忽略会让「本轮零问题」变成假绿。
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "RoundTrip异常", ex.Message));
                }
            }

            // 3. alt_modes 子卡（纹章 / 信仰 / 激奏 / 结晶）
            foreach (var alt in c.AltModes ?? new List<AltModeRawEntry>())
            {
                string typeEng = alt.TypeEng, textEng = alt.TextEng;
                if (string.IsNullOrWhiteSpace(textEng)) continue;
                EnglishCompiler.Result sr = null;
                try { sr = new EnglishCompiler(lex).Compile(textEng); }
                catch (Exception ex)
                {
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "子卡[" + typeEng + "]异常", ex.Message));
                    continue;
                }
                foreach (var e in sr.Errors)
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "子卡[" + typeEng + "]", e));
                if (sr.Errors.Count == 0 && sr.Abilities.Count == 0)
                    issues.Add(new CardAuditIssue(pack.PackId, c.CardId, c.NameEng, c.NameChs,
                        "子卡静默丢弃[" + typeEng + "]", "原文非空但 0 能力：" + Trunc(textEng)));
            }
        }
        return issues;
    }

    private static string Trunc(string s)
        => s == null ? "" : (s.Length > 100 ? s.Substring(0, 100) + "…" : s);

    /// <summary>把审计结果格式化成可直接阅读的报告。</summary>
    public static string FormatReport(IReadOnlyList<CardAuditIssue> issues)
    {
        var sb = new StringBuilder();
        int cards = issues.Select(i => i.CardId).Distinct().Count();
        sb.AppendLine("=== 全卡包编译审计报告（" + issues.Count + " 处问题，涉及 " + cards + " 张卡）===");
        if (issues.Count == 0) sb.AppendLine("（无问题）");

        foreach (var g in issues.GroupBy(i => (i.PackId, i.CardId, i.NameEng, i.NameChs)))
        {
            sb.AppendLine();
            sb.AppendLine("[" + g.Key.PackId + "] " + g.Key.CardId
                          + " 《" + g.Key.NameChs + " / " + g.Key.NameEng + "》");
            foreach (var item in g)
                sb.AppendLine("  - [" + item.Stage + "] " + item.Detail);
        }
        return sb.ToString();
    }
}

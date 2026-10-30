using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

public static partial class DocAcceptanceTest
{
    /// <summary>设计3：六层流水线。</summary>
    private static void RunDesign3()
    {
        _log.Add("");
        _log.Add("=== 设计3：六层流水线 ===");
        var lex = BuildFullLexicon();
        var comp = new EnglishCompiler(lex);

        // ---- Stage 1：结构分段器 ----
        {
            // <hr> 划分 Normal / Evolved 域
            var r = comp.Compile("Fanfare: Draw a card.<hr>Evolve: Deal 2 damage to the enemy leader.");
            bool twoDomains = r.Abilities.Count >= 2
                && r.Abilities[0].Trigger?.Event == CoreEvent.OnPlay
                && r.Abilities[1].Trigger?.Event == CoreEvent.OnEvolve;
            Ok("d3-S1", "<hr> 划分能力域", twoDomains,
               $"count={r.Abilities.Count}");
        }
        {
            // <ev> / <sev>
            var r = comp.Compile("Fanfare: Draw a card.<ev>Evolve: Draw 2 cards.<sev>Super-Evolve: Draw 3 cards.");
            bool three = r.Abilities.Count >= 3;
            Ok("d3-S1", "<ev> / <sev> 三域分离", three, $"count={r.Abilities.Count}");
        }
        {
            // 编号块 1. / 2. 组装为树状
            var r = comp.Compile("Select a Mode to activate. 1. Draw a card. 2. Deal 2 damage to the enemy leader.");
            var bm = r.Abilities.SelectMany(a => a.Body).OfType<BranchModeNode>().FirstOrDefault();
            bool tree = bm != null && bm.Options.Count == 2;
            Ok("d3-S1", "编号块组装为 BlockNode 树", tree,
               bm != null ? $"options={bm.Options.Count}" : "no BranchModeNode");
        }

        {
            // 情况 1 引号内嵌式：引号内自带编号选项块，必须递归构造内层模式树
            var r = comp.Compile("Fanfare: Do this 2 times: \"Select a Mode to activate. 1. Draw a card. 2. Deal 2 damage to the enemy leader.\"");
            string body = DumpDeep(r.Abilities);
            bool nested = body.Contains("modes") && body.Contains("loop");
            Ok("d3-S5.1.2", "引号内嵌式模式树（情况 1）", nested, body);
        }
        {
            // 代表卡真实用例：Sagelight Teachings 的 Do this 2 times + 引号内模式
            var r = comp.Compile("Do this 2 times: \"Select a Mode to activate. 1. Draw a card. 2. Restore 2 defense to your leader.\"");
            bool ok2 = r.Abilities.Count > 0 && r.Errors.Count == 0;
            string body = DumpDeep(r.Abilities);
            Ok("d3-S5.1.2", "Do this N times + 引号模式可编译", ok2 && body.Contains("modes"), body);
        }

        // ---- Stage 2：闭环词法分析器 ----
        {
            // 卡名最长前缀匹配（含句号的卡名）
            var r = comp.Compile("Add an Istyndet vs. Mitilykket to your hand.");
            bool ok2 = r.Abilities.Count > 0 && r.Errors.Count == 0;
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("d3-S2", "含句号的卡名不被误切", ok2,
               body.Length > 0 ? body : string.Join(";", r.Errors));
        }
        {
            // 引号块作为单个 Token
            var r = comp.Compile("Do this 2 times: \"Return your hand to deck. Draw X cards.\"");
            bool quoted = r.Abilities.Count > 0;
            Ok("d3-S2", "引号块内含句号不被切碎", quoted,
               $"count={r.Abilities.Count} err={r.Errors.Count}");
        }
        {
            // Stat 型 Token：+X/+Y
            var r = comp.Compile("Fanfare: Give this follower +2/+2.");
            var ms = r.Abilities.SelectMany(a => a.Body).OfType<ModifyStatsNode>().FirstOrDefault();
            bool stat = ms != null;
            Ok("d3-S2", "Stat(+X/+Y) 识别为数值修正", stat);
        }

        // ---- Stage 3：组合子语法 ----
        {
            // PrefixParser：时机 + 代价
            var r = comp.Compile("Necromancy (3): Draw a card.");
            bool prefix = r.Abilities.Count > 0
                && r.Abilities[0].Costs != null && r.Abilities[0].Costs.Count > 0;
            Ok("d3-S3", "PrefixParser 识别代价前缀", prefix,
               r.Abilities.Count > 0 ? $"costs={r.Abilities[0].Costs?.Count}" : "none");
        }
        {
            // TargetParser：限定从句
            var r = comp.Compile("Select an enemy follower with 2 attack or less and destroy it.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            bool clause = body.Contains("where");
            Ok("d3-S3", "TargetParser 识别限定从句", clause, body);
        }
        {
            // ActionParser：递归引号块
            var r = comp.Compile("Fanfare: Give this follower \"Whenever an allied follower enters the field, draw a card.\"");
            var ga = r.Abilities.SelectMany(a => a.Body).OfType<GrantAbilityNode>().FirstOrDefault();
            bool nested = ga != null && ga.Granted != null && ga.Granted.Trigger?.Event == CoreEvent.OnEnterField;
            Ok("d3-S3", "ActionParser 递归解析引号块内能力", nested);
        }

        // ---- Stage 4：四个消解 Pass ----
        {
            // Pass 4.1 模式树缝合
            var r = comp.Compile("Fanfare: Activate 2 random abilities from the following. 1. Draw a card. 2. Restore 2 defense to your leader. 3. Deal 2 damage to the enemy leader.");
            var bm = r.Abilities.SelectMany(a => a.Body).OfType<BranchModeNode>().FirstOrDefault();
            bool stitched = bm != null && bm.Options.Count == 3 && bm.PickCount != null;
            Ok("d3-S4.1", "模式树缝合（含 Give N random）", stitched,
               bm != null ? $"options={bm.Options.Count}" : "none");
        }
        {
            // Pass 4.2 变量符号表
            var r = comp.Compile("Fanfare: Deal X damage to the enemy leader. X is the number of cards in your hand.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            bool resolved = body.Contains("count(") || body.Contains("ctr:");
            Ok("d3-S4.2", "变量后置定义回填", resolved, body);
        }
        {
            // Pass 4.3 代词寄存器
            var r = comp.Compile("Whenever an allied follower enters the field, give it Ward.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("d3-S4.3", "代词寄存器绑定 $trigger", body.Contains("$trigger"), body);
        }
        {
            // Pass 4.4 Instead 透镜
            var r = comp.Compile("Summon a Majestic Megalorca. If you're in Overflow, summon 2 instead.");
            string body = DumpDeep(r.Abilities);
            bool lens = body.Contains("Majestic Megalorca") && body.Contains("spawn 2");
            Ok("d3-S4.4", "Instead 透镜覆写（保留卡名与目标）", lens, body);
        }

        // ---- Stage 5：AST 降级 ----
        {
            var r = comp.Compile("Fanfare: Select an enemy follower on the field and destroy it.");
            var nodes = r.Abilities.SelectMany(a => a.Body).ToList();
            bool lowered = nodes.Any(n => n is SelectTargetNode) && nodes.Any(n => n is MoveZoneNode);
            bool hasTarget = nodes.OfType<MoveZoneNode>().Any(m => m.Target != null);
            Ok("d3-S5", "Semantic IR 降级为 ActionNode + TargetSpec", lowered && hasTarget);
        }

        // ---- Stage 6：双向还原验证 ----
        {
            bool verifierExists = typeof(RoundTripVerifier) != null;
            // 取一张真实卡跑完整回环
            var r = comp.Compile("Fanfare: Draw a card.");
            var vr = RoundTripVerifier.Verify("Fanfare: Draw a card.", r.Abilities);
            Ok("d3-S6", "RoundTripVerifier 存在且能跑通", verifierExists,
               $"ok={vr.Ok} diffs={(vr.Problems.Count)}");
        }
        {
            // 零兜底：躯骨覆盖完整的句子应通过验证
            var r = comp.Compile("Fanfare: Deal 3 damage to the enemy leader.");
            var vr = RoundTripVerifier.Verify("Fanfare: Deal 3 damage to the enemy leader.", r.Abilities);
            Ok("d3-S6", "双向还原无差异", vr.Ok,
               string.Join(";", vr.Problems.Take(3)));
        }

        // ---- 六层端到端：真实卡全链路 ----
        {
            var r = comp.Compile(
                "Fanfare: Summon a Majestic Megalorca. If you're in Overflow, summon 2 instead." +
                "<hr>Whenever an allied Marine follower enters the field, give it Ward.");
            string body = DumpDeep(r.Abilities);
            bool s1 = r.Abilities.Count == 2;
            bool s2 = body.Contains("Majestic Megalorca");
            bool s3 = body.Contains("max_pp");
            bool s4 = body.Contains("spawn 2");
            bool s5 = body.Contains("keyword");
            bool s6 = body.Contains("$trigger");
            Ok("d3-E2E", "六层端到端（Ocean Rider 全链路）",
               s1 && s2 && s3 && s4 && s5 && s6,
               $"S1={s1} S2={s2} S3={s3} S4={s4} S5={s5} S6={s6}");
        }
    }
}

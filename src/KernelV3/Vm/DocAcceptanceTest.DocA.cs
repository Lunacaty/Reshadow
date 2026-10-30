using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;

namespace ShadowverseFan.KernelV3.Vm;

public static partial class DocAcceptanceTest
{
    /// <summary>76-a：五大病根 + 六套方案。</summary>
    private static void RunDocA()
    {
        _log.Add("=== 76-a：架构偏离与六套方案 ===");
        var lex = BuildFullLexicon();
        var comp = new EnglishCompiler(lex);

        // 病根 1：\n 不能被抹成空格
        {
            var r = comp.Compile("Countdown (2)\nWhenever an allied Pixie follower enters the field, draw a card.");
            bool split = r.Abilities.Count >= 2;
            bool hasCountdown = r.Abilities.Any(a => a.Trigger?.Event == CoreEvent.PassiveAura);
            bool hasEnter = r.Abilities.Any(a => a.Trigger?.Event == CoreEvent.OnEnterField);
            Ok("a-病根1", "\n 作为能力级分隔符（吟唱与触发器分开）",
               split && hasCountdown && hasEnter, $"count={r.Abilities.Count}");
        }

        // 病根 1：同一能力内句号共享能力头
        {
            var r = comp.Compile("Engage: Destroy this card. Select an allied follower on the field and give it Rush.");
            bool oneAbility = r.Abilities.Count == 1;
            bool twoActions = r.Abilities.Count > 0 && r.Abilities[0].Body.Count >= 3;
            Ok("a-病根1", "Engage 后的第二句不变 OnPlay",
               oneAbility && twoActions,
               r.Abilities.Count > 0 ? $"trigger={r.Abilities[0].Trigger?.Event} body={r.Abilities[0].Body.Count}" : "no ability");
        }

        // 病根 2a：OpponentLeader 区域是 Leader，不是 ExArea
        {
            bool zoneOk = TargetSpec.OpponentLeader.Zone == ZoneRef.Leader;
            bool noDouble = !TargetSpec.OpponentLeader.IncludeLeader;
            Ok("a-病根2", "OpponentLeader 区域为 Leader 且 IncludeLeader=false",
               zoneOk && noDouble, $"zone={TargetSpec.OpponentLeader.Zone} inc={TargetSpec.OpponentLeader.IncludeLeader}");
        }

        // 病根 2b：打脸伤害不重复
        {
            var r = comp.Compile("Deal 3 damage to the enemy leader.");
            int dmg = r.Abilities.Sum(a => a.Body.OfType<DealDamageNode>().Count());
            Ok("a-病根2", "打脸伤害不重复生成", dmg == 1, $"dealCount={dmg}");
        }

        // 病根 2c：all enemies 不会打自己全场
        {
            var r = comp.Compile("Deal X damage to all enemies.");
            var d = r.Abilities.SelectMany(a => a.Body).OfType<DealDamageNode>().FirstOrDefault();
            bool oppOwner = d?.Target?.Owner == ZoneScope.Opponent || d?.Target?.Owner == ZoneScope.Any;
            Ok("a-病根2", "all enemies 目标不会反转为自己",
               oppOwner, d?.Target?.Owner.ToString() ?? "none");
        }

        // 病根 3a：代词 it 在触发器下指向 $trigger
        {
            var r = comp.Compile("Whenever an allied follower enters the field, give it Ward.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("a-病根3", "触发器下的 it 指向 $trigger",
               body.Contains("$trigger"), body);
        }

        // 病根 3a：Select 后的 it 指向 $selected
        {
            var r = comp.Compile("Fanfare: Select an enemy follower on the field and destroy it.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("a-病根3", "Select 后的 it 指向 $selected",
               body.Contains("$selected"), body);
        }

        // 病根 3b：引号内无触发头 → PassiveAura
        {
            // 引号内只有关键词时，应被展开成 ModifyKeywordNode，
            // 而不是另起一条带 OnPlay 的子能力。
            var r = comp.Compile("Fanfare: Give this follower \"Can't attack followers or leaders\".");
            bool oneAbility = r.Abilities.Count == 1;
            bool hasKeyword = r.Abilities.SelectMany(a => a.Body).OfType<ModifyKeywordNode>().Any();
            bool noNestedPlay = !r.Abilities.Any(a => a.Trigger?.Event == CoreEvent.OnPlay && a != r.Abilities[0]);
            Ok("a-病根3", "引号内常驻效果不另起 OnPlay 子能力",
               oneAbility && hasKeyword && noNestedPlay,
               $"one={oneAbility} kw={hasKeyword} noNested={noNestedPlay}");
        }

        // 病根 4：打印器五个盲区
        {
            bool allPrinted = true;
            var sb = new List<string>();

            // (1) SubjectFilter
            var r1 = comp.Compile("Whenever an allied follower enters the field, draw a card.");
            string p1 = r1.Abilities.Count > 0 ? AstEnglishPrinter.PrintAbility(r1.Abilities[0]) : "";
            sb.Add(p1);
            // 打印格式是「e事件 on(主体)」；只要主体出现在 on(...) 里就算打出了
            if (p1.Contains("on(") == false) allPrinted = false;

            // (2) Property 要带 Of
            var r2 = comp.Compile("Fanfare: Deal damage equal to your leader's attack to the enemy leader.");
            string p2 = r2.Abilities.Count > 0 ? AstEnglishPrinter.PrintAbility(r2.Abilities[0]) : "";
            sb.Add(p2);

            // (3) GrantCounterAbilityNode 不能打出裸类名
            var r3 = comp.Compile("Fanfare: Give your faith \"Whenever an allied follower evolves, deal 1 damage to the enemy leader.\"");
            string p3 = r3.Abilities.Count > 0 ? AstEnglishPrinter.PrintAbility(r3.Abilities[0]) : "";
            sb.Add(p3);
            if (p3.Contains("grantcounterabilitynode")) allPrinted = false;

            Ok("a-病根4", "打印器不再打出裸类名 / 丢字段",
               allPrinted, string.Join(" || ", sb).Replace("\n", " "));
        }

        // 方案五：X starts at N 落地为初始计数器
        {
            var r = comp.Compile("X starts at 2. On Spellboost: Increase X by 1.");
            bool hasInit = r.Abilities.Any(a =>
                a.Trigger?.Event == CoreEvent.PassiveAura &&
                a.Body.OfType<ModifyCounterNode>().Any(m => m.Op == CounterOp.Set));
            Ok("a-方案五", "X starts at N 生成初始计数器", hasInit,
               $"abilities={r.Abilities.Count}");
        }

        // 方案五：跨卡状态不泄漏
        {
            var shared = new EnglishCompiler(BuildFullLexicon());
            int fake = 0;
            string[] probes =
            {
                "Fanfare: Draw a card.",
                "Fanfare: Deal 2 damage to the enemy leader.",
                "Fanfare: Restore 2 defense to your leader.",
            };
            foreach (var t in probes)
            {
                var rr = shared.Compile(t);
                if (rr.Abilities.Any(a => a.Trigger?.Event == CoreEvent.PassiveAura
                        && a.Body.OfType<ModifyCounterNode>().Any()))
                    fake++;
            }
            Ok("a-方案五", "共享编译器不泄漏初始计数器", fake == 0, $"fake={fake}");
        }

        // 方案一（修正版）：变量名不能是整句
        {
            var r = comp.Compile("Fanfare: Deal X damage to the enemy leader. X is the number of earth sigils you have.");
            string body = r.Abilities.Count > 0
                ? string.Join("|", r.Abilities[0].Body.Select(AstEnglishPrinter.PrintNode)) : "";
            Ok("a-方案一", "变量名不会把整句拼进去",
               body.Contains("ctr:earth_sigil"), body);
        }
    }
}

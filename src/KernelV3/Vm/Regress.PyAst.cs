using System;
using System.Collections.Generic;
using System.Linq;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.KernelV3.Packs;

namespace ShadowverseFan.KernelV3.Vm;

// ============================================================================
// PyAST DSL 双向闭环门禁（204 号文档第五节第 3 步 / 206 号文档收尾）
//
// 为什么必须有这道门禁：
//   AstCanonicalPrinter 与 AstCanonicalParser 是一对自定义序列化器。
//   任何一侧单独改动都会让它们漂移，而漂移后的症状是「打印得出来、
//   解析得回去、但字段已经丢了」—— 只有逐字节比对两次输出才抓得到。
//
// 判据（比 StructurallyEqual 更严）：
//   卡 -> 打印 -> 解析 -> 再打印，两段文本必须**字节级完全相等**。
//   文本相等意味着两侧对每一个 AST 字段的理解都一致，
//   而不只是「主要字段碰巧一样」。
// ============================================================================
public static partial class Regress
{
    public static string RunPyAst()
    {
        int pass = 0, total = 0;
        var log = new System.Text.StringBuilder();

        void Check(string name, bool ok, string detail)
        {
            total++;
            if (ok) pass++;
            log.AppendLine((ok ? "[OK]   " : "[FAIL] ") + name + "  —— " + detail);
        }

        var schema = GameSchema.CreateDefault();
        var lex = new Lexicon(schema);
        var root = CardPackLoader.ResolvePacksRoot();
        if (root == null) return "[FAIL] pyast：找不到 data/packs\n";
        var catalog = GlobalCatalog.LoadAll(root);
        if (catalog.AllCards.Count == 0) return "[FAIL] pyast：卡包总表为空\n";
        foreach (var c in catalog.AllCards.Values)
        {
            try { lex.AddCard(c.Name, c.CardId); } catch { }
        }
        lex.Build();

        var parser = new AstCanonicalParser(schema, lex);
        int ok = 0, totalCards = 0;
        var firstBad = new List<string>();

        foreach (var card in catalog.AllCards.Values.OrderBy(x => x.CardId))
        {
            totalCards++;
            string t1;
            try { t1 = AstCanonicalPrinter.PrintCard(card); }
            catch (Exception ex)
            {
                if (firstBad.Count < 5) firstBad.Add(card.CardId + " 打印异常：" + ex.Message);
                continue;
            }

            var back = parser.ParseCard(t1);
            if (parser.Errors.Count > 0)
            {
                if (firstBad.Count < 5) firstBad.Add(card.CardId + " 解析错误：" + parser.Errors[0]);
                parser.Errors.Clear();
                continue;
            }

            string t2;
            try { t2 = AstCanonicalPrinter.PrintCard(back); }
            catch (Exception ex)
            {
                if (firstBad.Count < 5) firstBad.Add(card.CardId + " 重打印异常：" + ex.Message);
                continue;
            }

            if (string.Equals(t1, t2, StringComparison.Ordinal)) ok++;
            else
            {
                if (firstBad.Count < 5)
                {
                    var l1 = t1.Split('\n');
                    var l2 = t2.Split('\n');
                    string diffLine = "";
                    for (int k = 0; k < Math.Min(l1.Length, l2.Length); k++)
                    {
                        if (l1[k] != l2[k])
                        {
                            diffLine = " 原|" + l1[k].Trim() + "| 回|" + l2[k].Trim() + "|";
                            break;
                        }
                    }
                    firstBad.Add(card.CardId + " 文本不等" + diffLine);
                }
            }
        }

        Check("pyast-1 全量卡往返：打印 → 解析 → 再打印，文本必须字节级相等",
              ok == totalCards && totalCards > 0,
              ok + "/" + totalCards + (firstBad.Count > 0 ? " | " + string.Join(" ;; ", firstBad) : ""));

        // ---- 2. 关键字段的显式往返（防止只靠整体文本碰巧相等）----
        // 这 6 个字段是 204 号文档点名的「静默丢字段」高危项，
        // 单独断言它们能被打印并被解析回来。
        {
            var probe = AstCanonicalPrinter.PrintCard(new Ast.CardDef
            {
                CardId = 1, Name = "Probe", Kind = CardKind.Follower,
                BaseCost = 1, BaseAttack = 1, BaseLife = 1,
                Abilities = new List<AbilityDef>
                {
                    new AbilityDef
                    {
                        Trigger = new TriggerSpec { Event = CoreEvent.OnPlay, IsFanfare = true },
                        Condition = BoolExpr.True,
                        Body = new List<EffectNode>
                        {
                            new SelectTargetNode
                            {
                                Target = new TargetSpec
                                {
                                    SourceKind = TargetSourceKind.Zone, Owner = ZoneScope.Opponent,
                                    Zone = ZoneRef.Board, Mode = SelectMode.Choose, Count = NumExpr.Const(2),
                                },
                                OutSlot = TargetSpec.SlotSelected,
                                AppendToSlot = true,
                            },
                            new MoveZoneNode
                            {
                                Target = TargetSpec.Self, ToZone = ZoneRef.Cemetery,
                                IsDestroy = true, OutSlot = TargetSpec.SlotCreated,
                            },
                        },
                    },
                },
                Traits = new List<SymbolId<TraitTag>>(),
            });

            var parsed = parser.ParseCard(probe);
            parser.Errors.Clear();
            var sel = parsed?.Abilities?.FirstOrDefault()?.Body?.OfType<SelectTargetNode>().FirstOrDefault();
            var mv = parsed?.Abilities?.FirstOrDefault()?.Body?.OfType<MoveZoneNode>().FirstOrDefault();

            bool appendOk = sel != null && sel.AppendToSlot;
            bool destroyOk = mv != null && mv.IsDestroy;
            bool fanfareOk = parsed?.Abilities?.FirstOrDefault()?.Trigger?.IsFanfare == true;

            Check("pyast-2 IsFanfare / AppendToSlot / IsDestroy 三个高危字段可往返",
                  appendOk && destroyOk && fanfareOk,
                  "append=" + appendOk + " destroy=" + destroyOk + " fanfare=" + fanfareOk);
        }


        // ---- 3. DIY 接入口（204 号文档第五节第 2 步）----
        // 从 PyAST 文本编译一张卡，再导出，文本必须与输入一致。
        {
            var wsRoot = CardPackLoader.ResolvePacksRoot();
            var wsCatalog = wsRoot == null ? null : GlobalCatalog.LoadAll(wsRoot);
            bool diyOk = false;
            string diyDetail = "未执行";
            if (wsCatalog != null && wsCatalog.AllCards.Count > 0)
            {
                var probeCard = wsCatalog.AllCards.Values.OrderBy(x => x.CardId).First();
                string dsl = AstCanonicalPrinter.PrintCard(probeCard);

                // 用一个独立 Schema 的 DIY 工作区，模拟真实的自定义卡流程
                var diyWs = Diy.DiyWorkspace.Create(1);
                var made = diyWs.CompileFromCanonicalAst(dsl, out var diyErrors);
                if (made == null)
                {
                    diyDetail = "编译失败：" + (diyErrors.FirstOrDefault() ?? "null");
                }
                else
                {
                    string again = diyWs.ExportCanonicalAst(made);
                    diyOk = string.Equals(dsl, again, StringComparison.Ordinal);
                    diyDetail = diyOk ? "卡 " + probeCard.CardId + " 往返一致"
                                      : "导出文本不一致";
                }
            }
            Check("pyast-3 DIY CompileFromCanonicalAst ↔ ExportCanonicalAst 往返", diyOk, diyDetail);
        }


        // ---- 4. 手写 DSL 的 6 类高危分支（206 号验收报告）----
        // 这些分支官方卡池走不到，只有人类手写 PyAST 才会踩。
        // 每条都曾经会静默退化（代价变 UsePp / 随机生成变发现 / 条件消失），
        // 而卡面文字完全看不出，所以必须逐个断言。
        {
            var caseParser = new AstCanonicalParser(schema, lex);

            bool CostKindOf(string dsl, out CostKind k)
            {
                caseParser.Errors.Clear();
                var abs = caseParser.ParseAbilities(dsl);
                k = CostKind.None;
                var c0 = abs?.SelectMany(a => a.Costs ?? Array.Empty<CostSpec>()).FirstOrDefault();
                if (c0 != null) k = c0.Kind;
                return caseParser.Errors.Count == 0 && c0 != null;
            }

            bool necroOk = CostKindOf("fanfare [necromancy(6)]:\n    draw 1", out var k1) && k1 == CostKind.Necromance;
            bool comboOk = CostKindOf("fanfare [combo(3)]:\n    draw 1", out var k2) && k2 == CostKind.TurnPlayCount;
            bool rallyOk = CostKindOf("fanfare [rally(10)]:\n    draw 1", out var k3) && k3 == CostKind.MatchPlayCount;
            Check("pyast-4 代价枚举：necromancy / combo / rally 不退化成 UsePp",
                  necroOk && comboOk && rallyOk,
                  "necro=" + k1 + " combo=" + k2 + " rally=" + k3);

            // counter ... on <target> 后跟 = / fill
            caseParser.Errors.Clear();
            var setAbs = caseParser.ParseAbilities("counter countdown on $self = 4");
            var setNode = setAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<ModifyCounterNode>().FirstOrDefault();
            bool setOk = caseParser.Errors.Count == 0 && setNode != null && setNode.Op == CounterOp.Set;

            // pool_spawn 必须是 Random，discover 必须是 Choose
            caseParser.Errors.Clear();
            var poolAbs = caseParser.ParseAbilities("pool_spawn 1 from self:cardpool");
            var poolNode = poolAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<SpawnCardNode>().FirstOrDefault();
            bool poolOk = poolNode?.CopyFrom != null && poolNode.CopyFrom.Mode == SelectMode.Random;

            caseParser.Errors.Clear();
            var discAbs = caseParser.ParseAbilities("discover 1 from self:cardpool");
            var discNode = discAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<SpawnCardNode>().FirstOrDefault();
            bool discOk = discNode?.CopyFrom != null && discNode.CopyFrom.Mode == SelectMode.Choose;

            // spawn_copy 的 owner / +departed
            caseParser.Errors.Clear();
            var copyAbs = caseParser.ParseAbilities("spawn_copy 2 of $self to Board [owner=Opponent] +departed");
            var copyNode = copyAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<SpawnCardNode>().FirstOrDefault();
            bool copyOk = caseParser.Errors.Count == 0 && copyNode != null
                          && copyNode.ToOwner == ZoneScope.Opponent && copyNode.AddDepartedTrait;

            Check("pyast-5 counter= / pool_spawn / discover / spawn_copy 后缀",
                  setOk && poolOk && discOk && copyOk,
                  "set=" + setOk + " poolRandom=" + poolOk + " discChoose=" + discOk + " copyOwner=" + copyOk);

            // @if 套在任意节点上（旧实现只覆盖 2 种）
            caseParser.Errors.Clear();
            var ifAbs = caseParser.ParseAbilities("@if is Follower:\n    buff $self +1/+1");
            var cond = ifAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<ConditionalNode>().FirstOrDefault();
            bool ifOk = caseParser.Errors.Count == 0 && cond != null
                        && cond.Then != null && cond.Then.Count == 1;
            Check("pyast-6 @if 可套在任意节点上（WithIf 统一包 ConditionalNode）", ifOk,
                  ifOk ? "ok" : "未生成 ConditionalNode");
        }

        log.AppendLine();
        log.AppendLine("=== PyAST 门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }
}

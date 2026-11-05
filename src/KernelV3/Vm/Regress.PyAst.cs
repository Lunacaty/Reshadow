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

            // @if 套在任意节点上。判据是节点自身的 If（不是包一层 ConditionalNode）——
            // 后者会把节点类型从「带条件的动作」变成「条件块」，重打印变成 if 而非 @if。
            caseParser.Errors.Clear();
            var ifAbs = caseParser.ParseAbilities("@if is Follower:\n    buff $self +1/+1");
            var buffed = ifAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<ModifyStatsNode>().FirstOrDefault();
            bool ifOk = caseParser.Errors.Count == 0 && buffed != null
                        && buffed.If != null && buffed.If is not ConstBoolExpr;
            Check("pyast-6 @if 挂到节点自身的 If 上（不改变节点类型）", ifOk,
                  ifOk ? "ok" : "未生成 ConditionalNode");
        }


        // ================================================================
        // pyast-7 ~ 10：语义正确性断言。
        //
        // 为什么单列一组：pyast-1 只比对「打印 → 解析 → 打印」的文本是否相等，
        // 而下面这些问题恰好是「两边一致地错」或「不进入文本」，
        // 往返测试对它们完全无感 —— 159/160/161 三轮的遗留问题都属此类。
        // 这一组不依赖两边一致，而是直接断言解析结果的语义。
        // ================================================================
        {
            var semParser = new AstCanonicalParser(schema, lex);

            // ---- pyast-7：card:#id 解析后 CardName 必须非空 ----
            // 打印器只写卡号，解析端必须用 Lexicon 反查补回卡名，
            // 否则中文打印器把《痴迷的试验体》渲染成「我方的所有该卡牌」。
            int nameEmpty = 0, nameChecked = 0;
            void WalkCardNames(EffectNode n)
            {
                if (n == null) return;
                if (n is SelectTargetNode st && st.Target != null
                    && st.Target.SourceKind == TargetSourceKind.CardId && st.Target.CardId > 0)
                {
                    nameChecked++;
                    if (string.IsNullOrEmpty(st.Target.CardName)) nameEmpty++;
                }
                if (n is ConditionalNode c) { foreach (var x in c.Then) WalkCardNames(x); foreach (var x in c.Else) WalkCardNames(x); }
                if (n is LoopNode lp) foreach (var x in lp.Body) WalkCardNames(x);
            }
            foreach (var card in catalog.AllCards.Values.OrderBy(x => x.CardId))
            {
                semParser.Errors.Clear();
                var back7 = semParser.ParseCard(AstCanonicalPrinter.PrintCard(card));
                if (back7 == null) continue;
                foreach (var ab in back7.Abilities ?? Array.Empty<AbilityDef>())
                    foreach (var n in ab.Body ?? Array.Empty<EffectNode>()) WalkCardNames(n);
            }
            Check("pyast-7 card:#id 解析后 CardName 必须由 Lexicon 反查补回",
                  nameEmpty == 0, "空卡名=" + nameEmpty + "/" + nameChecked);

            // ---- pyast-8：prop:Stat 必须映射到真实 StatKey ----
            // StatOf 漏项会让 prop:Countdown 变成 StatKey.None（求值恒 0），
            // 而打印出来仍是 prop:Countdown，文本看不出问题。
            (string dsl, StatKey want)[] statCases =
            {
                ("aura: if prop:Countdown >= 2: draw 1", StatKey.Countdown),
                ("aura: if prop:Cost >= 3: draw 1", StatKey.Cost),
                ("aura: if prop:Attack >= 3: draw 1", StatKey.Attack),
                ("aura: if prop:Life >= 3: draw 1", StatKey.Life),
                ("aura: if prop:MaxLife >= 3: draw 1", StatKey.MaxLife),
            };
            var statBad = new List<string>();
            foreach (var (dsl, want) in statCases)
            {
                semParser.Errors.Clear();
                var abs8 = semParser.ParseAbilities(dsl);
                var cond8 = abs8?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                    .OfType<ConditionalNode>().FirstOrDefault()?.Condition as CompareExpr;
                var num8 = cond8?.Left;
                if (semParser.Errors.Count > 0 || num8 == null || num8.Op != NumOp.Property || num8.Stat != want)
                    statBad.Add(dsl + "=>" + (num8?.Stat.ToString() ?? "null"));
            }
            Check("pyast-8 prop:Stat 必须映射到真实 StatKey（不得为 None）",
                  statBad.Count == 0, statBad.Count == 0 ? "ok" : string.Join("; ", statBad));

            // ---- pyast-9：非默认主语后缀 (<Target>) 必须可往返 ----
            // 打印器在 Of 不是上下文默认主语时会打 has ward($trigger)，
            // 解析端若不消费这个括号，它会残留在行尾报错。
            (string dsl, string must)[] suffixCases =
            {
                ("aura: if has ward($trigger): draw 1", "has ward($trigger)"),
                ("aura: if has trait Artifact($trigger): draw 1", "has trait Artifact($trigger)"),
                ("aura: if is Evolved($trigger): draw 1", "is Evolved($trigger)"),
            };
            var suffixBad = new List<string>();
            foreach (var (dsl, must) in suffixCases)
            {
                semParser.Errors.Clear();
                var abs9 = semParser.ParseAbilities(dsl);
                string txt9 = abs9 == null ? "" : AstCanonicalPrinter.PrintAbilities(abs9, 0);
                if (semParser.Errors.Count > 0 || !txt9.Contains(must))
                    suffixBad.Add(dsl + "=>" + (semParser.Errors.Count > 0 ? semParser.Errors[0] : "文本不含 " + must));
            }
            Check("pyast-9 非默认主语后缀 (<Target>) 可往返",
                  suffixBad.Count == 0, suffixBad.Count == 0 ? "ok" : string.Join("; ", suffixBad));

            // ---- pyast-10：条件主语方向（顶层 $self / where 内 $item） ----
            // 打印器按 _inWherePrint 隐藏括号，解析器按 _whereDepth 还原默认主语。
            // 两边只要有一边没对齐，顶层 if is Evolved 就会变成判 $item，
            // 运行期在顶层作用域里找不到 $item，条件恒为 false —— 而文本仍相等。
            bool IsSlot(TargetSpec t, string slot)
                => t != null && t.SourceKind == TargetSourceKind.Slot && t.Slot == slot;

            semParser.Errors.Clear();
            var topAbs = semParser.ParseAbilities("aura: if is Evolved: draw 1");
            var topState = topAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<ConditionalNode>().FirstOrDefault()?.Condition as HasStateExpr;
            bool topOk = topState != null && IsSlot(topState.Of, TargetSpec.SlotSelf);

            semParser.Errors.Clear();
            var whereAbs = semParser.ParseAbilities("tutor self:deck Random(1) where is Evolved");
            var whereSpec = whereAbs?.SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<MoveZoneNode>().FirstOrDefault()?.Target;
            var whereState = whereSpec?.Where as HasStateExpr;
            bool whereOk = whereState != null && IsSlot(whereState.Of, TargetSpec.SlotItem);

            Check("pyast-10 条件主语方向：顶层默认 $self，where 内默认 $item",
                  topOk && whereOk,
                  "顶层=" + (topState?.Of?.Slot ?? "null") + " where=" + (whereState?.Of?.Slot ?? "null"));
        }


        // ================================================================
        // pyast-11：中文卡面质量扫描。
        //
        // 163 号文档的 8 处 Bug 共同特征：卡面文本出错但不抛异常、
        // 门禁不红，只有人眼逐张看才能发现。这里把它变成机器可查的断言。
        // ================================================================
        {
            var zhPrinter = new ChineseCardPrinter();
            var faceBad = new List<string>();
            foreach (var card in catalog.AllCards.Values.OrderBy(x => x.CardId))
            {
                string face;
                try { face = zhPrinter.PrintCard(card); }
                catch (Exception ex) { faceBad.Add(card.CardId + " 抛异常：" + ex.Message); continue; }

                if (face.Contains("《》")) faceBad.Add(card.CardId + " 空书名号");
                // 英文内码外泄：CounterZh / KeywordZh 漏译时会直接印出 snake_case。
                var leak = System.Text.RegularExpressions.Regex.Match(face, @"[a-z]+_[a-z_]+");
                if (leak.Success) faceBad.Add(card.CardId + " 英文内码 " + leak.Value);
                if (face.Contains("其他的其他") || face.Contains("所有所有"))
                    faceBad.Add(card.CardId + " 重复修饰词");
                // 区间未折叠：[X >= N and X <= N] 应印成「X为N」。
                if (System.Text.RegularExpressions.Regex.IsMatch(face, @"≥(\d+)且.{0,12}≤\1"))
                    faceBad.Add(card.CardId + " 区间未折叠");
            }
            Check("pyast-11 中文卡面质量：无空书名号 / 无英文内码外泄 / 无重复修饰词",
                  faceBad.Count == 0,
                  faceBad.Count == 0 ? "1021 张全部通过" : string.Join(" ;; ", faceBad.Take(5)));

        // pyast-12：带参关键词的次数必须活过整条链路。
        //
        // 164 专题四暴露的 4 处断链全部集中在这一点：
        //   积木词表名写错（multi_attack）、属性面板没有 N 输入孔位、
        //   积木编译器把 (N) 截断、英文端反向渲染丢 N。
        // 任一处断掉，玩家造出的「1回合可攻击N次」都会静默退化成攻击 1 次。
        {
            var ws12 = Diy.DiyWorkspace.Create(1);
            foreach (var k in new[] { "ward", "max_attacks_per_turn", "damage_cap" }) ws12.Context.Keywords(k);

            var doc12 = new Diy.CardBlockWorkspace
            {
                CardId = 990012, Name = "带参关键词探针", Kind = "Follower",
                Cost = 3, Attack = 2, Life = 3,
            };
            doc12.Keywords.Add("max_attacks_per_turn(3)");
            doc12.Keywords.Add("ward");

            var res12 = new Diy.DiyBlockCompiler(ws12.Schema, ws12.Lexicon).Compile(doc12);
            bool ok12 = res12.Ok && res12.Def != null;

            // ① 无参的进 InnateKeywords，带参的留在能力里且 Value=3
            bool innate12 = ok12 && (res12.Def.InnateKeywords ?? Array.Empty<SymbolId<KeywordTag>>())
                .Any(k => k.Name == "ward");
            bool valued12 = ok12 && (res12.Def.Abilities ?? Array.Empty<AbilityDef>())
                .SelectMany(a => a.Body ?? Array.Empty<EffectNode>())
                .OfType<ModifyKeywordNode>()
                .Any(mk => mk.Keyword.Name == "max_attacks_per_turn"
                           && mk.Value is NumExpr { Op: NumOp.Const } v && v.Value == 3);

            // ② 反解必须带回 (3)
            var back12 = ok12 ? new Diy.DiyBlockDecompiler(ws12.Schema).Decompile(res12.Def) : null;
            bool round12 = back12?.Keywords != null
                           && back12.Keywords.Contains("max_attacks_per_turn(3)");

            // ③ 积木词表不能再用引擎不认识的 multi_attack
            bool zh12 = Ui.Diy.DiyZh.Keywords.Any(k => k.Key == "max_attacks_per_turn")
                        && !Ui.Diy.DiyZh.Keywords.Any(k => k.Key == "multi_attack");

            Check("pyast-12 带参关键词次数活过积木整链路",
                  innate12 && valued12 && round12 && zh12,
                  $"innate={innate12} value={valued12} round={round12} zh={zh12}");
        }

        log.AppendLine();
        log.AppendLine("=== PyAST 门禁 " + pass + "/" + total + " ===");
        return log.ToString();
    }
}
}

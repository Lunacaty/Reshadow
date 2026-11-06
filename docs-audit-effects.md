# 效果执行全量审核清单

> 本轮为审计交付，未修改任何业务代码或卡包数据。三个审计域各自独立 harness，仓库业务文件 0 改动。

## 一、你报的「旧版本暴风破」

**确认为真的缺陷。** 10131320（第1弹旧版）与 10831310（第8弹复刻）卡面与 PyAST 逐字相同，同病。

卡面：「X起始为2。/【魔力增幅时】使本卡牌的X+1。/ 对敌方1个随从造成X点伤害。」

实测（引擎正规路径 `CreateEntity` + 真实 PyAST + 真实出牌管线）：

```
实体 X 初值 = 2   （aura 由 MountInnateProperties 写实体级，正确）
玩家 X       = 0
VmEval ctr:X (Source=卡) = 2   （读侧正确，VmEval.cs:141-161 优先读 Source 实体计数器）
连续施放两张法术后：ok=True、entitySB 1→2（魔力增幅计数正常涨）
                    但 entityX 恒 2、playerX=0、ctr:X 仍=2
deal ctr:X 期望 4，实际 2；敌方随从 Life 12→10（伤害冻结在初值 2，不随增幅增长）
```

**根因（三条并列，均已给行号）：**

1. `on_spellboost` 能力 `ActiveZone=Board`，而卡在手牌 → `CardVm.MatchesTrigger`(CardVm.cs:836-844) 挡掉事件。`ActsAsOnField`(CardVm.cs:790-799) 只豁免 OnPlay / OnFuse，没有 OnSpellboost。
2. `TurnRunner.BumpSpellboost`(TurnRunner.cs:979-996) 只写实体 `spellboost` 计数，第 995 行才发一个**不带 PlayerId** 的 `OnSpellboost` 事件，从不写 X。⇒ 引擎自己的魔力增幅计数涨了，卡面写的「X+1」能力没走 —— 这就是「无法使用」的体感来源。
3. `on_spellboost: counter X by 1` 编译成 `ModifyCounterNode.Target=null`（玩家级），即使触发也写玩家级 X；而初值写实体级、读取也从实体级读 → **写侧作用域错**。编译链缺口：`ActionParser.cs:2648-2662`（Increase X by 1）与 `EnglishCompiler.cs:2455-2470`（X starts at N）均未赋 Target。

**同族 7 张全部确认失效**（逐卡四数值证据见 `.auditA/REPORT.md`）：

| 卡号 | 名称 | 应有 X | 实际 |
|---|---|---|---|
| 10131320 | 暴风破 | 4 | 2 |
| 10831310 | 暴风破(复刻) | 4 | 2 |
| 10132130 | 玛纳利亚的学生·威廉 | 2 | 0 |
| 10133310 | 做作业啦！ | 2 | 0 |
| 10232310 | 混沌赤焰 | 2 | 0 |
| 10833310 | 钢铁的小憩 | 2 | 0 |
| 10834110 | 恩爱的大地·坦忒拉&拉缇卡 | 2 | 0 |

后 5 张的 `if ctr:X >= 5 / >= 10 / >= 20` 分支因此**永远不触发**。

**已排除的误判**（早期判断，已纠正，勿采信）：

- 「X 恒为 0 / 出牌被拦 / 伤害为 0」：实体 X 初值 2；敌方有随从时可正常打出；正规路径伤害 2。早期 harness 手工 new 实体没入 `_all`，`GetLive` 找不到 → 假象。
- 「读侧作用域撕裂」：读侧是对的，只有写侧错。
- 「X=0 导致 `HasLegalTargetForSpell` 拦下出牌」：该预检根本不读 X（TurnRunner.cs:792-872）。
- 「`ModifyCounterNode` 没有 Target 字段」：有（EffectNode.cs:299），只是这 7 张编译成 null。
- 「37 张 X 类卡全失效」：30 张编译成内联 `NumExpr`（count/prop/ctr）是对的，只有 7 张走 `ctr:X`。

## 二、比暴风破更大的一类：`count()` 算成 0（43 张）

**确认失效，影响面最大。** 卡面「X 为……的张数」但运行时 X=0。

端到端实测（`.auditC/harness10.out`）：10542310《日珥咆哮》场上 5 随从，打出后各随从 Life=8（伤害 0），应有 X=5 → Life=3；把 `HasKind.Of` 改成 `$item` 后 `EvalNum=5`。

根因：`AstCanonicalParser.cs:1863-1870/2062-2082` 把 `<Follower>` 提权成 `HasKindExpr(Of=$self)`；`CardVm.Nodes.cs:252-262` 的 `NormalizeZoneWhere` 只归一化**动作节点外层** TargetSpec；而 `VmEval.cs:68-75` 的 `NumOp.Count` 直接 `ResolveRef(e.Of)` 不归一化 → `HasKind(Of=$self)` 去读 Source 的卡种，Source 是 Spell/Amulet 时恒 false → Count=0。

另两例：10562210《穹顶护甲》应有 1 实际 0；10132120《奇迹女巫·爱蜜儿》应有 2 实际 4（Follower 主语时反过来多算）。

43 张全号（25 张含 where、18 张纯 `<Kind>`）见 `.auditC/REPORT.md` C-1 节。我独立复核卡包 `count(...<Kind>...)` 命中 42 张，与域C 的 43 差 1 张是统计口径，不影响定性。

## 三、其余确认失效

| # | 问题 | 卡数 | 根因 | 证据 |
|---|---|---|---|---|
| 3 | `ChineseCardPrinter` 把 `!=` 印成 `=`，卡面条件与执行相反 | 3 张（10331110 / 10332110 / 10333110「真理」系列） | `ChineseCardPrinter.cs:1676-1681` switch 缺 `CompareOp.Ne`，`_ => "="`；`AstCanonicalPrinter.cs:1039` 与 `VmEval.cs:363-372` 都正确 | `.auditC/harness6.out`：Cost=2 时 VM 不回血、卡面却写「若费用=2则回复」 |
| 4 | `prop:Life($selected)` 读绑定时快照旧值 | 1 张（10262310 神圣守护） | `VmEval.cs:57-66` 优先 `refsP[0].Snapshot`；同文件 `RefStat`(:577-582) 却是活体优先，两条路径不一致 | `.auditC/harness7.out`：+0/+1 后 live.Life=6，求值仍 5 |
| 5 | `Sequence(pick=1)` 把 3 个选项全跑了 | 2 张（10703210 巴别隆城 / 10964120 翼天的变貌·奥梅里欧） | `CardVm.Nodes.cs:1386-1390` 的 Sequence 无条件压入全部选项；官方卡面明确「按顺序发动以下**1**个能力」；`ChineseCardPrinter.cs:1228` 还印成「全部能力」 | `.auditA/evidence.txt` J 段：3 项全落地（随从12→10、主战者20→18、护符被破坏） |

**关于第 5 条的边界**（我亲自查了官方原文，避免误报）：卡包里 `Sequence(pick=N)` 共 11 张，但另外 9 张是「Select a Mode」+ 条件满足时「Activate all of them instead」，它们的「全部发动」是**正确实现**，不是缺陷。只有明确写「以下 1 个能力」的 2 张是失效。

## 四、疑似失效（当前卡池 0 实例，不计入实际故障）

域A 在 22 种 EffectNode 的 handler 里发现 6 处结构性缺口，但全卡池没有卡命中，所以**现在不会出故障**，属于未来新增卡时的隐患：

- `DealDamageNode.IsAbility` 被吞（DoDamage 不读 isAbility，一律按 Ability 伤害结算）
- `ModifyStatsNode` 限时 `Op=Set` 撤销值记 0
- `GrantCounterAbilityNode` 的 `FlushNextTrigger` 硬编码只认 faith
- `SpawnCardNode` 的 `CardName` 无解析 / `ToOwner=Any` 未展开
- `TransformNode.IntoCardName` 分支直接 `return false`
- `ModifyCounterNode.CounterOp.Fill` 与 Set 等价，未实现「填到上限」

另 `DamageModifierNode` / `RevealCardNode` / `MacroNode` 三种节点当前卡池出现数 0，只能标「无实例，无法判定执行正确性」，**不是已验证通过**。

域C 另有 2 类疑似：`NumOp.MaxGroupSize` 用活体池分组（2 张）、`AstCanonicalPrinter.PrintCard(withSubDefs=false)` 导致 87/983 张子卡定义在往返比对里丢失（属编译产物一致性风险，不影响运行时效果——游戏读的是 pack.json 里的完整文本）。

## 五、编译器缺口（确认，但影响的是「重建管线」而非玩家）

- 7 张 `ctr:X` 的 `Target=null`（见第一节，与运行时失效同一根因）
- 18 条 `on_spellboost:` 能力全部未写 `zone=Hand`（`PrefixParser.cs:360` 默认 Board）
- 用当前 `EnglishCompiler` 重新编译 983 张官方英文：126 张报错（其中 5 张带 X 句式，如 10324110 `Fuse: Loot cards`、11053310 `Clue amulet` 词表缺失）。**但这些卡的 pack.json PyAST 完好、往返 983/983 逐字节相等**，游戏读的是 pack.json，所以它们不是玩家实际遇到的失效，属于「重建/合并管线的词表缺口」，单独归类。

## 六、已验证正常（避免你误以为全盘皆坏）

- **卡包产物干净**：983 张全 `py_ast`、1930 个能力头、空 body 0、编译错误残留 0、TODO/Fallback 标记 0
- **PyAST 往返零信息损失**：`ParseCard→PrintCard` 与 `PrintCard→ParseCard→PrintCard` 均 983/983 逐字节相等，parse_error=0
- 11 类重点官方句式在权威 PyAST 里**全部有对应结构**：Once/turn 7/7 有 `max=1`；Can’t be 6/6；Give opponent Crest 4/4；Do this N times 27/27 有 `repeat`；Select a Mode 48/48 有 `modes`；Enhance 67、Engage 57、Fuse 18、when/whenever 从句、X is the number of 30 张内联 NumExpr
- `HasStateExpr.None` 恒假兜底：卡包与重新编译均 0 实例
- 22 种 EffectNode：22/22 有 handler，15 语义完整
- 带参关键词 15+4+1 张全部落 `grant_keyword` 能力体、0 张误入 `InnateKeywords`，VM 有读取点
- 37 张 X 类句式归类：A 卡面本卡牌的 X→ctr:X 7 张 / B→专用计数器 10 张 / C→内联 count 20 张 / D 丢失 0 张

## 七、分级汇总

| 级别 | 类别数 | 影响卡数 | 说明 |
|---|---|---|---|
| 确认失效 | 5 类 | 7 + 43 + 3 + 1 + 2 = 56 张（去重后少于 56） | 暴风破族、count 算 0、!= 印反、快照旧值、Sequence 全跑 |
| 疑似失效 | 8 类 | 当前卡池 0～2 张 | 结构性缺口 / 打印层风险，现在不发作 |
| 前提不成立 | 多组 | — | 空 body 的 countdown 单行、纯裸关键词 0 abilities、4 张 vanilla、早期 harness 假象 |
| 重建管线缺口 | 1 类 | 126 张 | pack.json 完好，非运行时失效 |

## 八、建议修法（本轮未落盘，等你回来定）

按性价比排序：

1. **`count()` 归一化**（影响 43 张）：`VmEval.ResolveRef` 对 `Count/Sum/DistinctCount/MaxGroupSize` 的 `e.Of` 也套 `NormalizeWhereForItem`。
2. **`ctr:X` 三件套**（影响 7 张 + 18 条能力）：编译期给 `Increase X by 1` / `X starts at N` 产出的 `ModifyCounterNode` 赋 `Target=TargetSpec.Self`；给 `on spellboost` 头设 `ActiveZone=Hand`（或 `MatchesTrigger` 加手牌豁免）；`BumpSpellboost` 发的事件带 `PlayerId`。
3. **`CompareOp.Ne` 打印**（3 张）：`ChineseCardPrinter.cs:1676-1681` 补 `Ne => "≠"/"不为"`。
4. **`prop` 活体优先**（1 张）：`NumOp.Property` 改用 `RefStat(refsP[0], e.Stat)`。
5. **`Sequence(pick=1)`**（2 张）：`DoBranch` 的 Sequence 按 `PickCount` 取前 N 项；同时修正 `ChineseCardPrinter.cs:1228` 的「全部能力」措辞。
6. 结构性的 6 条缺口可随新增卡再补，不急。

## 九、证据索引

| 域 | 报告 | harness 输出 |
|---|---|---|
| A（VM 节点执行 + 暴风破） | `.auditA/REPORT.md`(134行) | `.auditA/evidence.txt`(346行) + `out*.txt` |
| B（编译器/解析器） | `.auditB/REPORT.md`(63行) | `.auditB/harness_*.stdout` + `recompile.json` + `semantic_diffs.json` |
| C（数值/条件/目标 + 卡面） | `.auditC/REPORT.md`(147行) | `.auditC/harness*.out` + `classify37.cjs/out` |
| 协调员复核 | `.audit/Program.cs` | 本文所有「实测」均另跑或逐条核过行号 |

> 三个子代理都只在各自 `.auditX/` 下建 harness，`git status` 业务文件 0 改动、`pack.json` 未动。

---

## 十、修复结果（2026-11-06 提交 d3deb8a）

用户确认「全修」。5 类确认失效 + 2 条规则全部落地并逐项复验：

| # | 缺陷 | 修复 | 复验 |
|---|---|---|---|
| 1 | count() 算 0（43 张） | VmEval 的 Count/Sum/DistinctCount/MaxGroupSize 套 NormalizeZoneWhere | 10542310 count=5（改前 0） |
| 2 | ctr:X 写侧作用域（7 张+18 条） | 编译期补 Target=Self + 运行期 X/Y/Z 写实体兜底 + OnSpellboost 手牌豁免 | 暴风破 X 2→4、ctr:X 读 4 |
| 3 | != 印成 =（3 张） | ChineseCardPrinter 补 CompareOp.Ne => ≠ | 10331110 卡面含 ≠ |
| 4 | prop 读快照旧值（1 张） | NumOp.Property 改用 RefStat（活体优先） | 10262310 加 buff 后 5→6 |
| 5 | Sequence(pick=1) 全跑（2 张） | DoBranch 只取前 pick 项 + 卡面措辞同步 | 10703210 只发动第 1 项 |
| R1 | rally 规则 | 用户确认现有补偿设计正确，未改动 | 打出随从 +1、法术不变、任意进场方式 +1 |
| R2 | spellboost 规则 | 移到效果生效前 + 任何法术都发事件 + 不包括给自己 | 手牌卡 0→1、被打出的法术 0、事件顺序正确 |

**连带修正**：测试 81000034 的期望写错（用 Sequence(pick=1) 却期望执行 2 项，
沿用修复前行为）。官方日文「下記の能力から順に1つの能力が働く」= 1 个能力，已改为期望只发动第 1 项。

**未处理的疑似失效（当前卡池 0 实例，未修）**：见第四节 6 条结构性缺口、
C-5 MaxGroupSize、C-6 PrintCard(withSubDefs=false)。这些当前不影响任何卡。

**pack.json 未改动**：7 张 ctr:X 卡的运行时修复走「运行期写实体兜底」，
编译期修复（Target=Self）只影响未来重新编译/合并的产物。

## 十一、剩余条目补修（提交 e5a459c）

用户指示：④⑤ 不修，其余全修。

| 条目 | 处理 | 说明 |
|---|---|---|
| ① IsAbility 被吞 | **已修** | IsAbility 贯通 ApplyDamage → DamageSourceMask，免疫可按来源精确拦截 |
| ② 限时 Set 撤销值记 0 | **已修** | 改前抓原值快照，ExpireOn 对 Set 型按原值还原 |
| ③ FlushNextTrigger 硬编码 faith | **已修** | 改按计数器名匹配；信仰实体按定义持有 faith |
| ④ SpawnCard CardName / ToOwner=Any | 跳过（用户指定） | — |
| ⑤ TransformNode.IntoCardName | 跳过（用户指定） | — |
| ⑥ CounterOp.Fill 等价 Set | **已修** | 三处改为 Math.Max(cur, delta) |
| ⑦ MaxGroupSize 活体池 | **已修** | 改 ResolveRef + RefStat(StatKey.Cost) |
| ⑧ PrintCard(withSubDefs=false) | **确认为误报** | 见下 |

### ⑧ 是误报

实测：`withSubDefs=true` 往返 同构 1013 / 失真 0；`withSubDefs=false` 往返 同构 1013 / 失真 0；
pack 原文往返 同构 1013 / 失真 0。域C 报的「87 张失真」是它自己 harness 的口径问题，
产品无缺陷，不改。

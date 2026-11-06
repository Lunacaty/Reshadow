# 审计域A 最终报告 · VM 效果节点执行

## 0. 边界与证据
- harness: `E:/text/reshadow/game/.auditA/Program.cs` + `.auditA/auditA.csproj`（EnableDefaultCompileItems=false；-p:BaseIntermediateOutputPath=obj_auditA/ -p:BaseOutputPath=bin_auditA/），dotnet build 0 错误。
- 完整输出: `.auditA/evidence.txt`（并保留 out*.txt 中间输出）。
- PyAST 全部直接来自 `data/packs/official-sv/pack.json`（983 张，经 GlobalCatalog.LoadAll → AstCanonicalParser）；G/H/I/J 段标注的"合成节点"仅用于隔离探测 VM handler 语义，**不是**卡的复现代替品。
- 仓库业务文件 0 修改（git status 只有各域自建目录）。注意 `-p:BaseIntermediateOutputPath` 的相对路径被 ProjectReference 传播，在项目根生成了 `obj_auditA/`；未动 `.et/ .audit/`。
- 分级统计：**确认失效 2**（A-1 暴风破含旧版；A-2 ctr:X 全 7 张一族）、**疑似失效 6**（N-1..N-6，均为当前卡池 0 实例的结构性缺口）、**前提不成立 1**（P-1）。已验证正常 8 项另列。合计 9 条发现。

## 1. 暴风破 10131320 / 10831310 完整根因链

### 1.1 原始 PyAST（两卡逐字相同）
```
card 10131320 "暴风破" (Spell, clan=巫师, cost=1, rarity=1):
    on_spellboost:
        counter X by 1
    on_play:
        select opponent:board<Follower> Choose(1)
        deal ctr:X damage to $selected
    aura:
        counter X = 2
```
编译后（harness A 段实测）：
- `OnSpellboost / ModifyCounterNode{Counter=X, Op=Add, Delta=Const(1), Target=null, Owner=Self}`
- `OnPlay / SelectTargetNode{Mode=Choose, Zone=Board, Owner=Opponent, Where=HasKindExpr, OutSlot=$selected, Count=Const(1)} + DealDamageNode{Amount=NumOp.Counter(X), Target=Slot:$selected}`
- `PassiveAura / ModifyCounterNode{Counter=X, Op=Set, Delta=Const(2), Target=null}`
三条 Trigger.ActiveZone 实测均为 **Board**。10831310 与 10131320 卡面/PyAST 逐字相同 → 同病，非版本差异。

### 1.2 三条并列的确认失效（按协调员要求独立列出）

**B1【确认失效】OnSpellboost 能力的 ActiveZone=Board，手牌里的卡收不到事件。**
- 根因：`CardVm.MatchesTrigger` src/KernelV3/Vm/CardVm.cs:836-844、876 要求 `owner.Zone==Board`；`ActsAsOnField` CardVm.cs:790-799 只豁免 OnPlay / OnFuse；`BumpSpellboost` TurnRunner.cs:979-996 只写实体 spellboost 计数并 `EmitEvent(OnSpellboost)`，从不写 X。
- 数值（evidence F 段）：F0 `ActiveZone=Board / card.Zone=Hand / entityX=2 playerX=0`；F1 手牌原样发 `OnSpellboost(PlayerId=0)` → `entityX=2 playerX=0`（能力未入栈）；F2 把同一张卡 MoveEntity 到 Board 再发同一事件 → `playerX=1`（立刻执行，证明闸门就是区域）；F3 直接 PushAbility → `playerX 1→2`；F5 按 BumpSpellboost 实际写法发无 PlayerId 的事件 → `playerX=0`。CardVm.cs:890-897 的 `ev.PlayerId>=0 && !=owner.PlayerId` 不是主因（真正主因是区域）。

**B2【确认失效】on_spellboost 的 ModifyCounterNode(X) 增量根本没落到 X 上。**
- 交叉证据（evidence E 段，真实管线连打 10133310 与 10833310 两张法术）：`ok=True`、`target.entitySB=1→2`（魔力增幅计数正常涨）、但 `target.entityX` 恒为 2（暴风破）/0（其余）、`playerX=0`、`VmEval ctr:X` 仍为 2/0、`deal ctr:X` 求值 2/0（应为 4/2）。这排除了两个假象：不是施法失败（ok=True），也不只是区域闸门（entitySB 在涨）。
- entitySB 由谁写：`TurnRunner.BumpSpellboost` src/KernelV3/Battle/TurnRunner.cs:979-996，第 991-992 行 `e.Counters[sbKey] = cur + 1` 直接写实体 `spellboost` 计数器，与 on_spellboost **能力体**是两套独立机制；第 995 行才发 `OnSpellboost` 事件（且不带 PlayerId）。因此玩家看到「魔力增幅时 X+1」，实际只有引擎自己的 spellboost 计数涨、X 不涨 —— 这正是「无法使用」的体感来源。
- 写错作用域：`ModifyCounterNode.Target=null` → `DoModifyCounter` CardVm.Nodes.cs:662-671 走 `_world.WriteCounter` 玩家级；aura 的 `counter X = 2` 却由 `BattleState.MountInnateProperties` BattleState.cs:1248-1251 写进 `e.Counters` 实体级。`VmEval.EvalNum case NumOp.Counter` VmEval.cs:141-161 先读 `scope.Source` 实体计数器，命中即返回，否则才回落玩家级。**读侧是正确的**（与卡面「使本卡牌的 X+1」一致），错的是写侧。
- 数值：F2 `playerX=1, entityX=2`；H2 手工写 `playerX=7 (entityX=2)` 后 `VmEval ctr:X=2`（玩家级 7 被吞）；G1 `counter X=5(Target=null)` → `playerX=5, src.entityX=2`；G2 `counter X on $self=7` → `playerX=5, src.entityX=7`。

**A-2【确认失效】7 张 ctr:X 卡全族同病。** 每卡用真实 PyAST + 真实出牌管线，触发 2 次魔力增幅：

| 卡号 | 名称 | 实体X初值 | 触发后实体X | 触发后玩家X | entitySB | ctr:X求值 | 应有 | 判定 |
|---|---|---|---|---|---|---|---|---|
|10131320|暴风破|2|2|0|2|2|4|确认失效|
|10831310|暴风破(第8弹复刻)|2|2|0|2|2|4|确认失效，同病|
|10132130|玛纳利亚的学生·威廉|0|0|0|2|0|2|确认失效|
|10133310|做作业啦！|0|0|0|2|0|2|确认失效，`if ctr:X>=5` 分支死|
|10232310|混沌赤焰|0|0|0|2|0|2|确认失效|
|10833310|钢铁的小憩|0|0|0|2|0|2|确认失效，`if ctr:X>=5` 分支死|
|10834110|恩爱的大地·坦忒拉&拉缇卡|0|0|0|2|0|2|确认失效，>=10 / >=20 双分支死|

- 建议修法（不落盘）：on_spellboost 的 `ModifyCounterNode` 编译成 `Target=TargetSpec.Self`（或 `DoModifyCounter` 对 X/Y/Z 类卡级变量统一走 WriteEntityCounter），并把该能力 `ActiveZone` 设为 Hand / 在 MatchesTrigger 里给 OnSpellboost 手牌豁免；同时让 BumpSpellboost 发出的事件带 PlayerId。

### 1.3 【P-1 前提不成立】「X=0 导致 HasLegalTargetForSpell 拦下出牌」
`TurnRunner.HasLegalTargetForSpell` src/KernelV3/Battle/TurnRunner.cs:792-872 只看顶层 Choose 目标候选池，完全不读 ctr:X。实测：有敌方随从时 `HasLegalTargetForSpell=True / CanPlayCard=True reason=空`（7 张全部如此）；空场时 `False / reason="没有合法目标"`（正确规则，暴风破必须点 1 个敌方随从）。正规入口 `MatchRunner.PlayCard` → 选目标后 `foe.Life 12→10`（H2d），伤害是 **2** 而非 0，被冻结在初值、不随增幅增长。早期 harness 用 `TurnRunner.PlayCard` 直调出现「出牌 True 但 foe.Life 不变」是假象：自建 CardVm 抢注了 `BattleState.TriggerSink`（CardVm.cs:83），`EmitOrEnqueue`（TurnRunner.cs:1318-1323）在 TriggerSink != null 时只调 `_state.EmitEvent`，事件进了 MatchRunner 私有 VM 的队列。

### 1.4 OnPlay 的 zone=Board 是否影响法术打出：不影响（单列验证）
`ActsAsOnField` CardVm.cs:790-799 明确对「事件主体就是本卡 + OnPlay」豁免 Board 判定。H2b：`OnPlay ActiveZone=Board, card.Zone=Hand, Subject=本卡` → `Step=WaitingForInput pending=SelectTarget`，选完 `foe.Life 12→10`；H2c 主体=别的卡 → `pending=null`。无需修。

## 2. 22 种 EffectNode 覆盖表
主 switch `CardVm.Nodes.cs:55-81`，**22/22 全部有 case，0 缺失 handler**。统计口径：全卡池 1013 张卡 + 91 个嵌套子定义（crest/faith/accelerate/crystallize）递归遍历 2937 个节点实例。

| # | 节点 | switch行 | handler | 卡数 | 状态/证据 |
|---|---|---|---|---|---|
|1|SelectTargetNode|59|DoSelect:299|262|有·完整：Choose / AppendToSlot / 潜行灵气过滤 / 单候选自动绑定|
|2|DealDamageNode|60|DoDamage:363|320|**部分 N-1**：IsAbility 被吞|
|3|RestoreLifeNode|61|DoRestore:426|112|有·完整：FullRestore + MaxLife 钳制|
|4|ModifyStatsNode|62|DoModifyStats:443|147|**部分 N-2**：限时 Op=Set 撤销值记 0|
|5|ModifyCostNode|63|DoModifyCost:522|50|有·完整：Duration → TempModifiers(IsCostMod)|
|6|ModifyKeywordNode|64|DoModifyKeyword:555|167|有·完整：Grant/Remove/SilenceAll + Value + Duration|
|7|GrantAbilityNode|75|DoGrant:1454|11|有·完整：Duration 透传|
|8|GrantCounterAbilityNode|76|DoGrantCounter:1577|4|**部分 N-3**：FlushNextTrigger 硬编码 faith|
|9|SpawnCardNode|67|DoSpawn:964|431|**部分 N-4**：CardName 无解析 / ToOwner=Any 未展开|
|10|MoveZoneNode|66|DoMoveZone:749|324|有·完整：抽牌管线/破坏/弃牌/回手洗白/计数器|
|11|MarkVictoryCardNode|71|DoMarkVictoryCard:1132|1|有·完整：G8 实测 HasVictoryCard=True|
|12|TransformNode|70|DoTransform:1208|18|**部分 N-5**：IntoCardName 分支空转|
|13|EvolveNode|69|DoEvolve:1150|66|有·完整：+2/+2 / 超进化 / 奥义槽 / 事件|
|14|ModifyCounterNode|65|DoModifyCounter:616|125|**部分 A-2 + N-6**：Target 双路径都在(G1/G2)，但 X 编译成 Target=null 写错作用域；Fill 与 Set 等价|
|15|InvokeAbilityNode|77|DoInvoke:1594|61|有·完整：ResolveOrBlock + 遍历全部同事件能力 + 子作用域重定向|
|16|ConditionalNode|72|DoConditional:1285|171|有·完整：G7 实测 Then 执行|
|17|LoopNode|73|DoLoop:1292|29|有·完整（仅 Times 被使用）：全卡池 36 实例 Kind=Times=36；ForEach / RepeatUntil 有实现但 0 张卡命中，未实测。G6 Times=3 → X+3 正确|
|18|DamageModifierNode|58|DoDamageModifier:197|0|**当前卡池无实例，无法判定执行正确性**（合成节点探测仅证明框架可跑：G3 SourceMask=Combat → ApplyTaken(Ability,5)=5 / (Combat,5)=1；H2e Duration=ThisTurn 在 EndTurn 后 granted 1→0、ApplyTaken(9) 2→9）|
|19|RevealCardNode|57|DoReveal:228|0|**当前卡池无实例，无法判定执行正确性**（合成节点 G4：IsRevealed=True + OnCardRevealed）|
|20|BranchModeNode|74|DoBranch:1355|63|有·完整：五种 Kind 全实现 + mode_count 光环 + 实体级 mode_activated_mask。真实卡实测 J 段：10703210 `Sequence(pick=1)` 3 选项全跑（随从12→10、敌方主战者20→18、护符被破坏），但卡面渲染 ChineseCardPrinter.cs:1228 是「按顺序发动以下全部能力」，且 EffectNode.cs:346 注释明确 Sequence 全部执行 → **设计语义，非缺陷**。pick<选项数的真实卡共 3 张带 pick=1：10703210 / 10964120 / 11024120（其余 8 张是 pick=2/3 与 Choose 分支并列，正则跨分支计数所致）|
|21|ReplaceDeckNode|68|DoReplaceDeck:1139|3|有·完整：I 段 10104120 `DeckName="the Apocalypse Deck"`、DeckDefs.Has=True、Expand=10、牌库 36→10、无 fault|
|22|MacroNode|78|DoMacro:1630|0|**当前卡池无实例，无法判定执行正确性**（合成节点 G9：未定义宏 → Faulted + "机制宏未实现" + StateCorrupted=true，不静默吞）|

合计：handler 有 22 / 无 0；语义完整 15；部分 6；无实例无法判定 3（已在表中标注，不计入「通过」）。

## 3. 其余发现（逐条交付格式）

**N-1 DealDamageNode.IsAbility 被吞【疑似失效】**
- 原始 PyAST 语法 `deal ... combat`（AstCanonicalParser.cs:1208-1215）产出 IsAbility=false；I 段实测全卡池命中 **0 张**，当前无实际影响。
- 复现：I 段结构扫描 `IsAbility=false 卡数=0`；合成对照 `ApplyTaken(Combat,3)=3`。
- 根因：`DoDamage:396 → ApplyDamage(t,total,n.IsAbility,scope)` CardVm.Nodes.cs:406-424 收到 isAbility 后完全不读，直接 `_world.ApplyDamage` → `CombatResolver.ApplyDamage` 默认 `source=DamageSourceMask.Ability`（CombatResolver.cs:353）。
- 建议：把 isAbility 映射成 DamageSourceMask 传入统一伤害管线。

**N-2 ModifyStatsNode 限时 Op=Set【疑似失效】**
- I 段实测 `Op=Set 且 Duration!=Permanent 卡数=0`。
- 根因 CardVm.Nodes.cs:511-517：Set 分支把 TempModifiers.Attack/Life 记 0，撤销时无法还原被覆盖原值。
- 建议：Set 分支记录 before/after 差值。

**N-3 GrantCounterAbilityNode 只认 faith【疑似失效】**
- I 段实测 `Counter!=faith 卡数=0`（4 张全 faith）。
- 根因：DoGrantCounter:1579 按任意 counter 登记，`FlushNextTrigger` CardVm.cs:626-633 硬编码 `if (ctr.Name != "faith") continue`。
- 建议：按登记的 ctr 与能力 Trigger 精确匹配。

**N-4 SpawnCardNode CardName / ToOwner=Any 未展开【疑似失效】**
- I 段实测 `CardId=0 & CardName非空且非Crest/Copy = 0 张`、`ToOwner=Any且非Crest = 0 张`、`spawn_previous 真实卡 = 0 张`。
- 合成节点（非真实卡）：G10 Transform by name → `step=Completed, cardId 保持 10131320`；G11 Spawn CopyOfPrevious → `board0=0 created=0`（这是无前序卡号时的合理空结果，不当作失效）。
- 根因 CardVm.Nodes.cs:1030-1062：只有 IsCrest 分支处理 Any；普通 `SpawnCard` 直接 `scope.ResolvePlayer(Any)=-1`（VmScope.cs:49），CreateEntity 会抛。
- 建议：普通生成也按 CardName 查 CardDefOf，Any 生成按双方展开。

**N-5 TransformNode IntoCardName 分支空转【疑似失效】**
- I 段实测 `IntoCardId=0 且 CopySource=null 卡数=0`（18 张全带卡号或 CopySource），G10/H3 合成节点实测 `step=Completed / cardId 不变`。
- 根因 CardVm.Nodes.cs:1234-1235：注释写「交给 Battle 层按 CardName 解析」但只 `return false`。
- 建议：按 IntoCardName 查卡表取 CardId 后调 TransformEntity。

**N-6 ModifyCounterNode CounterOp.Fill 语义缺失【疑似失效】**
- I 段实测 `Op=Fill 卡数=0`。
- 根因 CardVm.Nodes.cs:630-631 / 666-667：`Fill => delta` 与 Set 完全等价，未实现「填到上限」。
- 建议：Fill 改为 max(cur, delta) 或读 CounterLimits 上限。

**已验证正常（避免误报）**：PassiveAura 初值挂载（MountInnateProperties 写实体 X）；HasLegalTargetForSpell 目标预检；OnPlay zone 豁免；DamageModifier 的 SourceMask/Duration（EndTurn 后撤销）；BranchMode Sequence 语义；ReplaceDeck；MarkVictoryCard；Macro 熔断；DoLoop.Times；DoConditional；GrantAbility/InvokeAbility 的 Duration 与作用域。

## 4. 至少 8 张真实卡抽样复现（12 张，全部直接加载 pack.json 的 PyAST）
覆盖：10131320、10831310（SelectTarget+DealDamage+ModifyCounter+PassiveAura）、10132130（DealDamage All + ctr:X + Evolve）、10133310（Conditional+Transform+MoveZone draw）、10232310（DealDamage split+ctr:X）、10833310（Conditional+Restore+recover_pp）、10834110（双 Conditional+SpawnCard）、10114120（Transform by cardId + aura/ModifyKeyword）、10304110（SpawnCard + crest 子定义 ReplaceDeck/MarkVictoryCard/discard/draw）、10104120（ReplaceDeck 实测牌库 36→10）、11061120（aura 带参关键词 + strike）、10123110（aura + strike/SpawnCard）。另结构性覆盖 10031110（BranchMode Choose+pay）、10114130（Loop Times+NumOp.Count）、10172320（SpawnCard CopyOfTarget+GrantAbility）、10061130（InvokeAbility）、10703210（真实 Sequence(pick=1)）。
抽样输出要点（evidence D/E/I/J 段）：暴风破两张 `entityX=2→2 / playerX=0 / ctr:X=2 / deal ctr:X 求值=2（应有4）`，正规路径 foe.Life 12→10；威廉/做作业啦/混沌赤焰/钢铁的小憩/坦忒拉 全为 `entityX=0→0 / playerX=0 / ctr:X 求值=0（应有2）`；10132130 与 10834110 的 deal/if 分支求值均为 0；10114120 等 5 张非 ctr:X 卡 `PlayCard=True / VM Completed / 无 fault`；10104120 ReplaceDeck 36→10；10703210 J 段 3 选项全落地（随从12→10、敌方主战者20→18、护符 Zone=Cemetery）。

## 5. 校准要点
- 域B 转述的「ModifyCounterNode 没有 Target 字段」**错误**：EffectNode.cs:299 有 `public TargetSpec Target { get; init; }`；DoModifyCounter 分实体/玩家两条路径（G1/G2 实测）。正确表述是「有 Target 字段，但暴风破的 PyAST 编译成 Target=null」。
- 基线「X 恒为 0、出牌被拦、伤害为 0」三点需修正：实体 X 初值 2（暴风破）/0（其余）；敌方有随从时可打出；正规路径伤害为 2（暴风破）而非 0。真实缺陷是 X 永不随魔力增幅增长。
- 未落盘任何业务修改，所有建议修法均为一句话。

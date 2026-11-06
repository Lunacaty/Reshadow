# 审计域B 编译器/解析器 终版报告
边界：仅新建 .auditB/；未修改任何仓库文件。构建命令：cd .auditB && dotnet build auditB.csproj -p:BaseIntermediateOutputPath=obj_auditB/ -p:BaseOutputPath=bin_auditB/（0 error）。
harness 源码：.auditB/Program.cs；证据：.auditB/harness_all.stdout / harness_micro.stdout / harness_semantic.stdout / harness_astaudit.stdout / harness_evalx.stdout / harness_fallback.stdout / recompile.json / semantic_diffs.json。

## 必查项1 卡包产物洁净度
983 卡全部 source_format=py_ast。1930 个能力头、空 body=0；TODO/FALLBACK/UNKNOWN/UNSUPPORTED/UNPARSED/COMPILE_ERROR 标记 0；pass/noop/None/[] 兜底 token 0。grep 的 21 条 "error" 全部是合法关键词 cannot_attack/cannot_destroy 的子串，非残留。
25 条解析后 body 为空的能力全部是 countdown(N) 单行声明（如 10011210、10603210），AstCanonicalPrinter.cs:190-200 有意打印成单行，前提不成立，不是空 body。
AstCanonicalParser.ParseCard(pack.text) 全 983 张 parse_errors=0、exception=0；PrintCard 与 pack.text 逐字节相等 983/983。

## 必查项2 官方英文句式→分支（权威口径 = pack.json PyAST；fresh = 用当前 EnglishCompiler 重新编译 sva_src.json 英文）
| 句式 | sva 卡数 | 真实英文原句 + 卡号 | 卡包实际 AST | 分支/结论 |
| Once on each of your turns | 7 | 10171140 "Once on each of your turns, when an allied Puppetry follower enters the field, give it Bane."；10314110 "Once on each of your turns, when an enemy follower is given - defense on the field, restore 1 defense to your leader." | 10171140 on_enter turn=Self max=1 on(self:board<Follower> All where has trait 人偶): grant_keyword bane to $trigger；10314110 on_statsmodified turn=Self max=1 on(opponent:board<Follower> All): restore 1 life to self:leader | PrefixParser.cs:305 TryConsumeFrequency 设 MaxTriggersPerTurn=1；AstCanonicalPrinter.cs:293 打印 max=1；7/7 落地 |
| Can't be ... | 6 | 10374110 "Can't be destroyed by abilities."；90071210 "Can't be played."；10273110 "give it Ward and \"Can't be destroyed by abilities.\"" | 10374110 keywords: [ward, cannot_destroy]；90071210 keywords: [cannot_be_played]；10273110 evolve: grant_keyword cannot_destroy to $selected | EnglishCompiler.cs:1023-1050 裸限制句→AddInnate；引号内变体走 grant_keyword；6/6 落地 |
| Give your opponent Crest | 4 | 10144110 "Super-Evolve: Give your opponent Crest: Burnite, Anathema of Flame."；10314110 "...Crest: Krulle, Heir to Unkilling." | 10144110/10314110 super_evolve: gain_crest #<id> [owner=Opponent]，并内嵌 crest 子定义 | 4/4 落地；CardVm.Nodes.cs:1053 GainCrest(scope.ResolvePlayer(n.ToOwner)) |
| whenever/when 从句 | when 137、whenever 94 | 10011210 "Whenever an allied Pixie follower enters the field, deal 1 damage to a random enemy follower." | 10011210 on_enter on(self:board<Follower> All where has trait 妖精): deal 1 damage to opponent:board<Follower> Random(1)；countdown(2) | PrefixParser.cs:518 TryConsumeWhenClause；pack 全量 AST 未见 is None |
| Do this N times | 27 | 10153120 "Evolve: Necromancy (4) - Do this 2 times: \"Deal 2 damage to a random enemy follower.\""；10212310 "Do this 1 time: ... Combo (3) - Do it 2 times instead." | 10153120 evolve [necromancy(4)]: repeat 2 times: ...；10212310 on_play: if ctr:combo >=3: repeat 2 times ... else: repeat 1 times ... | ActionParser.cs:2985-3060；27/27 有 repeat |
| modes Choose | 48 | 10031110 "Fanfare: Select a Mode to activate. 1. Spellboost your hand 2 times. 2. Earth Rite (1) - Give this follower +2/+2 and Ward." | 10031110 fanfare: modes Choose(pick=1): option 1/2 | ActionParser.cs:2169 + EnglishCompiler MergeOptionSegments；48/48 有 modes |
| Enhance | 67 | 10001110 "Enhance (4): Give this follower +3/+3." | on_play [enhance(4)]: if ctr:pp_at_play >= 4: buff $self +3/+3 | EnhanceWeaver.cs / EnglishCompiler.cs:264 |
| Engage | 57 | 10001210 "Engage: Destroy this card. Select an enemy follower ... remove Ward from it." | engage [engage(0)]: destroy $self; select ...; remove_keyword ward from $selected | PrefixParser.cs:360 HeaderTable |
| Fuse | 18 | 10213310 "Fuse: Forestcraft cards / Draw a card. If you've Fused to this card, draw 2 instead." | fuse: any:board All where has clan 精灵 / on_play: draw 1 / if ... | EnglishCompiler.cs:141-151 |
| X is the number of ... | 30 | 10111130 "Fanfare: Draw a card. Restore X defense to your leader. X is the number of cards in your hand."；10114130 "Give this follower +X/+X. X is the number of Pixie followers in your hand." | 10111130 restore count(self:hand All) life to self:leader；10114130 buff $self +count(self:hand<Follower> All where has trait 妖精)/+count(...) | EnglishCompiler.cs:2156 IsVariableDeclaration、:2380 TryBindVariableDeclaration、:2568 BuildVariableExpr |
兜底核验：BuildSimpleCondition 的 UnitState.None 兜底在 EnglishCompiler.cs:2125（仅 overflow/evolved/max pp 之外）；ConditionCompiler.cs:272/290 会跳过 None；TargetParser.cs:1417 无匹配返回 null（由 ActionParser 报错，不是静默目标）。对 983 张英文重新编译后实测 cards_with_HasStateExpr_None=0、instances=0；卡包 PyAST 实测 HasStateExpr(None)=0。两者均不存在运行时恒假条件。

## 必查项3 语义与卡面不符（按模式归类）
B1【确认缺陷 · 编译器语义缺口】7 张「本卡牌的 X」被编成 Target=null 玩家级计数器。
卡：10131320 暴风破、10132130 威廉、10133310 做作业啦！、10232310 混沌赤焰、10831310 暴风破、10833310 钢铁的小憩、10834110 坦忒拉&拉缇卡。
原始 PyAST（10131320）：on_spellboost: counter X by 1 / on_play: select ...; deal ctr:X damage to $selected / aura: counter X = 2。
复现（.auditB/harness_micro.stdout）：两个 ModifyCounterNode 均 counter=X, Target=null(player-level), Owner=Self；7/7 卡 counter_target_null=2 / counter_target_set=0。应有值：卡面「本卡牌的 X」应为实体级 Target=Self。
根因：ActionParser.cs:2648-2662 "Increase X by 1" 分支只写 Counter/Op/Delta，未赋 Target；EnglishCompiler.cs:2455-2470 "X starts at N" 只登记 _initialCounters[vn]，:272-286 生成 PassiveAura + ModifyCounterNode(Counter,Op,Delta) 亦未赋 Target；EffectNode.cs:294-299 定义 null=玩家级、非 null=实体级。运行时 CardVm.Nodes.cs:616-671 DoModifyCounter 双路径，协调员实测 aura 初始化写实体（BattleState.cs:1248-1251 MountInnateProperties），增量写玩家 → 作用域撕裂。
分级：确认失效（编译链缺陷；运行时表现由域A负责）。建议：这两处产出 TargetSpec.Self 并用对应的实体计数器读写。

B2【确认缺陷 · 编译器语义缺口】OnSpellboost 头默认 ActiveZone=Board，而「这张卡」在手牌。
卡面/卡号：10131320 "On Spellboost: Increase X by 1."、10132130/10133310/10232310/10831310/10833310/10834110 同句式；pack 18 条 on_spellboost: 全部未写 zone=Hand（grep 结果 18 个裸 on_spellboost:，zone=Hand 为 0）。
复现：AstCanonicalParser.cs:590-612 缺省 ActiveZone=Board；AstCanonicalPrinter.cs:291 只有非 Board 才打印 zone=，故 pack 里无 zone 标记即 Board。PrefixParser.cs:360 ("on spellboost", CoreEvent.OnSpellboost, null) condFlag=null，未像 :585/:595 的 fuse-to-this-card 那样设 ActiveZone=Hand；PrefixParser.cs:43 默认 Board。EnglishCompiler.cs:1094-1095 只有段级 zone 传播。
分级：确认失效（手牌中收不到事件）。建议：HeaderTable 给 on spellboost 加 zone_hand 或在 TryParseHeader 后强制 ActiveZoneScope.Hand。

B3【确认缺陷 · 重建管线缺口，非运行时失效】用当前 EnglishCompiler 重新编译 983 张 sva 英文：no_errors=857、with_errors=126、zero_abilities=73、静默丢弃标记 43（其中 43 张是纯裸关键词卡，InnateKeywords 生效但 Abilities=0，属误报式标记）；按报错类 76。带 X 句式的 5 张报错：10324110 (Fuse: Loot cards 过滤词未注册)、10473110 (Select an Artifact follower)、11022110/11044110/11053310 (Clue amulet 词表缺失)。
证据：.auditB/recompile.json 中这 5 张 errors；但它们的 pack.json PyAST 完整且 roundtrip 983/983 相等（例 10324110 deal ctr:fuse_kinds、11053310 deal ctr:clues）。游戏运行读 pack.json PyAST（GlobalCatalog.cs:220-258），所以这 5 张不是玩家实际失效，属于重建/合并管线的 Lexicon 词表缺口。前提不成立：不得列入运行时失效清单。建议：向 Lexicon 注册 Loot/Artifact/Clue 等英文名后再合并。

B4【语义漂移 · 仅供参考，不作失效判定】pack PyAST 与 fresh 编译的能力 AST 逐字节比较：compared=857、identical=841、drift=16（.auditB/semantic_diffs.json）。模式：① 目标过滤丢失 10114120 pack transform self:hand ... where (has clan 精灵 and prop:Cost<=2) vs fresh transform any:board All；10132120 pack count(...where has trait 巨像) vs fresh count(self:board All)；10771120/10771310/10772120/10773310/10774110/10873310/90034120 的 Artifact/Shikigami 过滤丢失。② 变量实现不同 10114130 pack count(妖精) vs fresh ctr:pixie_followers_hand；10903110 pack count(self:hand All where has clan 中立) vs fresh ctr:neutral_cards_hand。③ 布尔写法 10372110/10372210/10653110 pack if exists($selected) vs fresh if is selected。④ 10603210 中文兵种名 vs 英文 Encroacher。⑤ 90005150 英文原文缺失但 pack 有 on_play: deal 2 damage to opponent:leader。
口径：pack.json 是权威、游戏只读它，fresh 仅作编译器参考；不能据此判 pack 失效。

B5【前提不成立】纯裸关键词文本（Ward/Storm/Rush 等）在 fresh 编译中产出 0 abilities 且报「[静默丢弃]」：那是 EnglishCompiler.cs:156-161 的卡级黑洞检查只看 Abilities 不看 InnateKeywords；pack 里这些卡用 keywords: [ward] 属性落盘，能力数为 0 是设计。另 4 张完全无效果的 vanilla（10002120 商队猛犸象、10422120 如火斗志·菲泽、90001110 哥布林、90004120 深渊之主的仆从）英文原文为空、PyAST 只有 card 头，前提不成立。

## 必查项4 AstCanonicalPrinter→AstCanonicalParser 往返
全卡包 roundtrip：PrintCard(ParseCard(pack.text)) == pack.text 983/983；PrintCard -> ParseCard -> PrintCard 逐字节相等 983/983；不一致数量 0；parse_errors=0、reparse_errors=0、exception=0。典型例子：10131320、10114130、10423310 全部 0 差异。结论：在 983 张现有卡上零信息损失；不代表「任意未来 AST」零损失（Regress.PyAst.cs 的门禁只覆盖当前卡包）。

## 必查项5 带参关键词
pack 中 max_attacks_per_turn(2/3) 15 卡（10114120、10123110、10124110、10154120、10162120、10274120、10411110、10423310、10614110、10654110、10843110、10864110、10924120、11061120、90034350），damage_cap 4 卡（10401110、10444120、10464120、10711110），damage_taken_bonus 1 卡（10474120）。全部落在 ability body 的 grant_keyword 语句，innate=0；其中 aura 头 7+3 张，其余 on_play/fanfare/super_evolve/strike/crest 等。例 10114120 aura: grant_keyword max_attacks_per_turn(2) to $self；10444120 grant_keyword damage_cap(0) to self:leader [dur=UntilEndOfOpponentTurn]；10474120 grant_keyword damage_taken_bonus(1) to opponent:leader。
VM 读取点：CardVm.Nodes.cs:555-578 DoModifyKeyword 写 KeywordValues[n.Keyword]=EvalNum(n.Value)；CombatResolver.cs:73-75 max_attacks_per_turn；CombatResolver.cs:387-391 damage_taken_bonus / damage_cap；BattleState.cs:2214-2222 exact copy 复制 KeywordValues。Null ModifyKeywordNode.Target=0。结论：落位正确、VM 有读取，未发现该类别编译器缺陷。

## 协调员补充：X/Y/Z 动态数值专门复核（以 pack.json 为准）
按 sva_src.json 真实英文句式并集 56 张（starts at 7 / Increase X by 7 / X is the number of 30 / 其它 X is... 23 / Do this X 3 等）。按 pack PyAST 实际取值来源分四类：
· 内联 NumExpr（count(...) / prop:... / sum:... / ctr:<命名计数器> / repeat ctr:...）43 张；兜底常量 0 张；完全丢失 0 张；纯 ctr:X/Y/Z 7 张。
· 内联求值实测（.auditB/harness_evalx.stdout，构造 handCards=4、playerCounterX=5）：10111130 count(self:hand All) => 4（应有 4）；10163110 ctr:cards_destroyed_by_ability => 0（未破坏任何护符时正确）；10114130 count(self:hand<Follower> All where has trait 妖精) => 0（手牌 4 张 probe 无妖精兵种，符合场景）；10131320 ctr:X => 5（玩家级计数器被写入 5 时）。
· ctr:X 7 张全列：10131320 暴风破、10132130 玛纳利亚的学生·威廉、10133310 做作业啦！、10232310 混沌赤焰、10831310 暴风破、10833310 钢铁的小憩、10834110 恩爱的大地·坦忒拉&拉缇卡。每条的 2 个 ModifyCounterNode Target 均为 null → 玩家级（CardVm.Nodes.cs:662-671 WriteCounter）；协调员域A 实测 VmEval 读侧优先 Source 实体计数器（VmEval.cs:155-160），初始化由 MountInnateProperties 写实体（BattleState.cs:1248-1251），故是写/读作用域撕裂，不是「37 张整体失效」。

## 分级统计
确认失效(编译链缺陷) 2 类：B1 7 卡、B2 18 条 on_spellboost 能力（7 张 X 卡 + 其余 spellboost 卡）。
重建管线缺口 126 张 fresh 编译报错（其中 5 张带 X 句式），全部 pack PyAST 完好，非运行时失效。
语义漂移 16 张（参考，不作失效）。
前提不成立 43 张纯关键词静默标记 + 4 张 vanilla + 25 条 countdown 空 body。
未落地英文句式：0（重点 11 类均在 pack PyAST 找到对应结构）；HasStateExpr.None 兜底实例 0；VM 未处理节点实例 0（22 种节点全部覆盖 CardVm.Nodes.cs:57-80）。

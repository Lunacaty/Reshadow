# 审计域C 最终证据报告（只读审计 + 独立 harness）

工作目录: E:/text/reshadow/game；harness: .auditC/auditC.csproj + .auditC/Program.cs
构建: dotnet build .auditC/auditC.csproj -p:BaseIntermediateOutputPath=obj_auditC/ -p:BaseOutputPath=bin_auditC/ (0 错误)
输出留档: .auditC/harness.out, harness2..11.out, scan37.out, scan_counter.out, scan_nested.cjs/out, classify37.cjs

## 分级统计
确认失效 4 类 / 疑似失效 2 类 / 前提不成立 1 类；卡面-AST 对照 14 张（要求≥10）。

## C-1 确认失效：嵌套 NumExpr.Count 的 HasKind(Of=$self) 未归一化 → X 算错（43 张）
复现 (.auditC/harness10.out，端到端 CardVm.PushAbility + StepUntilBlocked)：
  10542310 日珥咆哮，世界 5 随从，PyAST: on_play: deal count(any:board<Follower> All) damage to any:board<Follower> All
  实际: StepUntilBlocked=Completed, 各随从 Life=8,8,8,8,8（伤害 0），EvalNum(Amount)=0
  应有: X=5，各随从 Life=3；归一化 HasKind.Of=$item 后 EvalNum=5
  根因: AstCanonicalParser.cs:1863-1870/2062-2082 把 <Follower> 提权成 HasKindExpr(Of=$self)；
        CardVm.Nodes.cs:252-296 NormalizeZoneWhere 只归一化「动作节点外层 TargetSpec」；
        VmEval.cs:68-75 NumOp.Count 直接 ResolveRef(e.Of) 且 VmEval.cs:751-767 对每个候选用 $item 派生子作用域，
        HasKind(Of=$self) 却去读 Source 的卡种。Source 是 Spell/Amulet → 恒 false → Count=0；
        Source 是 Follower → 恒 true → 把护符等也算进去（harness11：4 vs 正确 3）。
  43 张: 10114130,10132120,10201110,10204120,10222110,10231110,10242110,10253120,10262110,10263110,10271120,10411310,10452120,10521120,10522120,10542310,10562210,10573110,10671310,10674110,10674120,10723310,10761110,10761120,10762110,10762120,10763110,10764120,10771120,10771310,10772120,10773310,10774110,10774120,10811110,10863110,10863210,10873310,11022120,11042310,11043110,90034120,90064210
  其中 25 张含 where，其余 18 张是纯 <Kind>；按本卡卡种 Follower=32 / Spell=8 / Amulet=3。
  10562210 穹顶护甲: draw count(self:board<Follower> All where has ward)，1 个带守护随从在场 → EvalNum=0（应 1），修正后=1。
  建议: 在 VmEval.ResolveRef 里对 NumExpr.Count/Sum/DistinctCount/MaxGroupSize 的 e.Of 也套一次 NormalizeWhereForItem（或编译期把提权的 Kind 直接写成 $item）。

## C-2 确认失效：ChineseCardPrinter 把 CompareOp.Ne 印成 "="（3 张，卡面与执行相反）
  ChineseCardPrinter.cs:1676-1681 的 op switch 只有 Ge/Le/Eq/Gt/Lt，_ => "="；Ne 落入 default。
  AstCanonicalPrinter.cs:1039 正确输出 !=；VM VmEval.cs:363-372 Compare(Ne) 正确。
  10331110 官方「若本卡牌的费用不为2，则回复3点」/ PyAST fanfare if prop:Cost != 2: restore 3 life to self:leader / CCP「若费用=2，回复3点」
  10332110 官方「若费用不为5，则抽取2张」/ CCP「若费用=5，抽取2张」
  10333110 官方「若费用不为3，则召唤2个复制」/ CCP「若费用=3，召唤2个复制」
  harness6: Cost=2 时 VM EvalBool(Ne)=False（不回血），Cost=4 时 True（回血）——执行对、卡面反。
  建议: CCP 的 op switch 补 CompareOp.Ne => "≠"（或"不为"）。

## C-3 确认失效：prop:Life($selected) 读快照旧值（1 张，10262310 神圣守护）
  PyAST: select self:board<Follower> Choose(1) where has ward; buff $selected +0/+1; deal prop:Life($selected) damage to opponent:board<Follower> Random(1)
  官方卡面「选择…使其+0/+1。对…造成X点伤害。X为选择的随从的生命值。」
  harness7: 选中时 Life=5 → EvalNum=5；buff 后 live.Life=6 → EvalNum 仍=5（应为 6）。
  根因 VmEval.cs:57-66 NumOp.Property 先 ResolveRef 再 ReadStatFromSnapshot(refsP[0].Snapshot)，
  即使 GetLive 存在也读创建/绑定时的快照（VmScope.BindSelection 写 EntityRef.From，RuntimeEntity.cs:313）。
  对照 VmEval.cs:577-582 RefStat 是「活体优先」——属性求值分支没走它。
  建议: NumOp.Property 改成 RefStat(refsP[0], e.Stat)（活体优先，离场才回落快照）。

## C-4 确认失效：7 张 ctr:X 卡 on_spellboost 增量节点作用域写错（与域A交叉，不深挖单卡）
  7 张全部同构（.auditC/classify37.cjs + harness3.out）：10131320,10132130,10133310,10232310,10831310,10833310,10834110
  PyAST 均为 on_spellboost: counter X by 1 / aura: counter X = N / 动作里 ctr:X。
  编译产物: on_spellboost 的 ModifyCounterNode Target=null（玩家级）；aura 的 Set 亦 Target=null。
  引擎实际: BattleState.cs:1248-1251 MountInnateProperties 把 aura 的 Set 写进实体计数器（实体级 ✓）；
            VmEval.cs:155-160 ctr:X 在 scope.Source 绑定时优先读 Source 实体（读侧 ✓）；
            CardVm.Nodes.cs:661-671 DoModifyCounter(Target=null) 写玩家级（增量 ✗）。
  即「卡面说本卡牌的 X+1」→ 增量唯一写错作用域的一侧。另 ActiveZone=Board（harness3 实测 7/7 为 Board），
  手牌中的卡收不到 OnSpellboost（CardVm.cs:836-844），属触发条件求值，与域A交叉。
  【读侧正确 / 写侧失效，严格分开】VmEval.cs:141-161 NumOp.Counter 分支本身无缺陷：
    e.Of != null 时按 scope.ResolvePlayer(e.Of.Owner) 读玩家级（146-151）；否则先
    _world.GetLive(scope.Source)、src.Counters.TryGetValue(e.Counter) 读 Source 实体（155-160），
    找不到才 _world.ReadCounter(scope.ResolvePlayer(scope.Owner), ...) 回落玩家级（161）。
    本域 harness 实测：ctr:X(Source=卡, 实体X写2)=2、无 Source=0；写 11 时 EvalNum=11。
    因此「X 不增长」不是 EvalNum 求值错误，而是写侧 ModifyCounterNode(Target=null) 落到玩家级 +
    on_spellboost 能力未触发（ActiveZone=Board）。
  【域A 交叉证据（协调员转述，真实 PyAST + 真实管线）】连施两张法术后 ok=True、
    target.entitySB=1→2（魔力增幅计数正常）、target.entityX 恒为 2(暴风破)/0(其余)、playerX=0、
    VmEval ctr:X 仍为 2/0；deal ctr:X 期望 4/2，实际 2/0。
    → 在数值求值层面记为「确认失效：X 类动态数值在真实施法序列下不增长」，读侧无需修。
  建议: 增量节点改为 counter X on $self by 1（Target=$self），并给 on_spellboost 设 zone=Hand。

## C-5 疑似失效：NumOp.MaxGroupSize 用活体池 + Cost 分组，中文卡面写死「手牌中同费卡」
  VmEval.cs:175-180 走 Resolve()（只认活体），GroupBy(x => x.Cost)；ChineseCardPrinter.cs:1749 硬编码「手牌中同费卡的数量」，
  完全不看 e.Of。真实卡 2 张: 10553310 / 10554120 的 if maxgroup(self:hand All) >= 4。
  harness1 直接构造 maxgroup(self:hand All) => 2（世界 2 张手牌同费），仅为构造用例，非真实对局。
  建议: 与 NumOp.Count 一样改走 ResolveRef，打印时改用 e.Of 的 ZoneZh。

## C-6 疑似失效：AstCanonicalPrinter 往返 87/983 张不同构（子卡定义丢失）
  harness3: 983 张 ParseCard 全部 0 error；ACP.PrintCard(def,false) 与 pack.json 文本逐行比较，同构 896 / 失真 87。
  抽样 10133110 / 10264120 / 10364110：原始含 crest/faith 子卡段，回打尾部只剩 gain_crest，子卡能力整段丢失。
  根因 AstCanonicalPrinter.cs:62 PrintCard(withSubDefs=false)；这是 UI 与编译产物一致性风险，非本域直接执行失效。
  建议: 比对时用 PrintCard(def,true)，或让 ACP 默认带子卡。

## C-7 前提不成立（排除项）
  · NumOp.Counter 的 ctr:X / ctr:pp_at_play 不是恒 0：harness1 写 11/6 后 EvalNum=11/6；
    VmEval.cs:141-161 先查 Source 实体计数器再回落玩家级。域A 早前「X 恒 0」是未初始化/触发不到，不是求值分支兜底。
  · CompareOp.Ne 在 VM 求值里不是恒真/恒假（harness1: 3!=2=True, 3!=3=False）；失真只在 CCP 打印层。
  · UnitState.EnteredThisTurn / Destroyed 在 VmEval.MatchState 两处重载都没有分支（VmEval.cs:405-438 / 462-471 → default false），
    但全卡包 PyAST 无 "is EnteredThisTurn"/"is Destroyed"（scan_patterns.out 命中 0），前提不成立，不报为当前卡失效。
  · prop:Life($trigger) 在 scope.Trigger 未绑定时 =0（harness1），但 VmEval.cs:709-710 有 Trigger 回退，
    CardVm.cs:679-687 构造 scope 时绑定 Trigger；属测试直调的未绑定态，不是真实执行失效。
  · TargetSpec.Mode=Choose 在 VmEval.ResolveRef 返回「全部候选」（VmEval.cs:842），但 DoSelect/ResolveOrBlock
    会挂起 PendingChoice 按 Count 截断（CardVm.Nodes.cs:337-353），不属串槽。
  · IncludeLeader 在 Zone=Leader 直接目标、或 Slot 来源时不展开（VmEval.cs:775）：前提本来就不该展开。
  · BranchModeNode 的 other 修复（173）未重复报。

## NumExpr 全分支实际求值（.auditC/harness.out PART 2）
  Const(7)=7 | ctr:X(玩家级,写11)=11 | ctr:pp_at_play(写6)=6 |
  prop:Life($trigger 未绑定)=0 | prop:Life($self)=2 | prop:Life($item 空槽)=0 |
  prop:HandCount($self)=0 / DeckCount=0 / BoardCount=0（ReadStat 只按实体 PlayerId 枚举该玩家区域，世界里有牌但 $self 是手牌法术 →
    这些值本身不等于「手牌张数」语义，属 StatKey 与 $self 主语搭配的语义陷阱，全卡包无 prop:HandCount 用法，前提不成立） |
  prop:Cost($self)=3 | count(self:board All)=3 | count(card:#id + History)=0（MemoryWorld 无历史池） |
  maxgroup(self:hand All)=2 | Arith 3-2=1 | Arith 空Args=0 | Extreme 空Args=0 | Clamp 空Args=0 |
  Cond true?5:9=5 | Cond false?5:9=9 | Cond(null Then/Else)=0 | RandomRange(2,5)=3 | SplitOf(X,0)=7
  真实卡里 NumOp.Cond 使用数=0；Arith 使用 3 张（10233310 乘法、10811110 减法、11074110）。

## BoolExpr 覆盖（.auditC/harness.out PART 3）
  null→True | ConstBool(true/false)→True/False | And 空→True | Or 空→False | Not(null)→False |
  Compare Ne 正确 | Exists 空槽→False，self:board>=1→True |
  HasTrait/HasKeyword/HasCardName 在 $item 空槽→False（正常，未绑定）；HasState Evolved($self)→True |
  HasState EnteredThisTurn/Destroyed/AttackedThisTurn($self)→False（见 C-7）|
  HasClan IsSelfClan($self)→True | NotSameEntity($self)→True | IsPrevious 空→False |
  ConditionCompiler.cs:270-273 / 288-291 有 UnitState.None → BoolExpr.True 的恒真兜底；全卡包未命中（见 C-7）。

## TargetSpec 组合（.auditC/harness9.out）
  self:board All=4 | self:board<Follower> Of=$self=0 | 同 Of=$item=3 | opponent:board +leader All=3 | any:board All=6 |
  Choose(2)=4 候选（由调用方挂起）| Random(5) 于 4 候选=4 | First(2)=2 |
  $trigger/$item/$selected 未绑定=0 | $self 有 Source=1 | card:#id @self:board=3 | card:#id history=0（无历史池）|
  DistinctByName=2 | DuplicatesOnly=2 |
  SubjectFilter 走 CardVm.cs:968-1020 SubjectMatches，会 BindRefs($item)，因此触发器主语的 <Kind> 不失真；
  SecondaryFilter 走 CardVm.cs:928-930，scope1 只带 Controller、无 Source/Trigger，含 $self/$trigger 的组合会落空（全卡包未见此类组合，标为观察项）。

## 37 张归类（协调员第二轮要求；.auditC/scan37.out + classify37.cjs）
  A 卡面「本卡牌的 X / X 起始为 N」→ AST 写成 ctr:X：7 张（C-4，7/7 同构）
  B 卡面「X 是……的数量」→ AST 变成专用计数器 ctr:xxx（语义靠运行时写入，非空常量/丢失）：10 张
    10133110 earth_sigil,10163110 cards_destroyed_by_ability,10324110 fuse_kinds,10362210/10363210 crest,
    10543110 cards_banished_by_ability,10932120 cards_returned_to_hand,11034120 earth_sigil,
    11044110 cards_discarded_by_ability,11053310 clues
  C 卡面「X 是……的数量」→ AST 保留 count()/maxgroup() 语义一致：20 张（但受 C-1 影响者另计）
    10111130,10113120,10114130,10132120,10253120,10371110,10373110,10374110,10542310,10554120,
    10562210,10573110,10674110,10723310,10761120,10773310,10774110,10811110,10844110,10903110
  D 卡面与 AST 形态皆异常/丢失：0 张
  A/B/C/D = 7/10/20/0 = 37（全部卡号见上，汇总清单可直接取用）。
  37 张里 Count 实际求值（见 harness5.out）：所有 count(<Kind>...) 在构造世界里为 0（C-1 机制），
  count(self:hand All)=0 因该世界手牌为 0（非缺陷）；ctr:X 玩家级=0（未初始化，域A 范畴）；
  ctr:专用计数器=0（未模拟 MoveZone/Fuse 写入，属前提未构造）。
  中文语料放宽到「起始为/本卡牌的/X+/X点/X为」共 87 张：count=21, ctr:X=7, ctr:其他=17, prop=21, 无数值表达式=21。

## 卡面-AST 对照 14 张（.auditC/harness9.out，含官方 text_chs / CCP / ACP 三方逐条）
  10131320,10132130,10133310,10232310,10833310,10834110,10111130,10114130,10133110,
  10331110,10332110,10333110,10562210,10262310
  其中 10331110/10332110/10333110 卡面条件反向（C-2）；10133110 子卡「纹章：「」」空名；
  7 张 X 卡的 CCP 卡面忠实反映 AST（数值语义失真在 VM 层，不在打印层）。

## 修改边界
  仅创建/编辑 .auditC/ 下文件；未改动任何仓库业务文件与 pack.json。
## 补充实测（harness12.out）：C-1 在 Follower 主语下是「多算」而非「少算」
  10132120 奇迹女巫·爱蜜儿（Follower 主语），世界含 2 巨像随从 + 1 带巨像兵种的法术 + 1 普通随从，
  官方卡面「X 为自己的战场上的巨像·随从的张数」→ 正确 X=2。
  PyAST: deal count(self:board<Follower> All where has trait 巨像) damage to opponent:board<Follower> All
  Where 原式 = And[HasKind(Follower, Of=$self), HasTrait(巨像, Of=$item)]
  实际 EvalNum = 4（把非随从和无关随从全算进去），修正 HasKind.Of=$item 后 = 3（仍含 1 个带巨像兵种的法术，
  因为战场实体本身用 Follower/Spell 定义混合，剩余差额属构造世界的卡种设置；关键证据是 4 ≠ 2，卡种过滤失效）。
  结论: C-1 的两种表现按本卡卡种分叉 —— Spell/Amulet 主语 → Count 恒 0；Follower 主语 → 卡种过滤恒真、多算。

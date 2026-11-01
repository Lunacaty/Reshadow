# Reshadow

Shadowverse: Worlds Beyond 的粉丝引擎核心，用 Godot 4.7.2 + C# 实现。

本仓库只包含**内核代码**：符号系统、正交 AST、帧栈虚拟机、英文编译器、
DIY 四端、卡包/卡池/构筑系统。卡池数据、立绘、扩展包等运行时素材不在库内。

---

## 设计目标

三件事是这套内核的全部目的：

1. **引擎不硬编码任何一张卡** —— 所有卡牌都是运行时从卡面文本或 DIY 表达编译出来的。
2. **玩家用四种方式造卡，四者产出同一份 AST** —— 脚本 / 积木 / 表单 / 中文自然语言。
3. **同一份 AST 在两端都能渲染** —— 英文卡面与简中卡面互为投影，不靠手写模板。

因此「一张卡」在本仓库里的唯一真相是 `CardDef`（纯数据），
其余一切（编译器、反编译器、渲染器、校验器）都是它的投影。

---

## 模块地图

```
Core/     物理法则与符号系统       8 文件    878 行
Ast/      纯数据树                6 文件   1124 行
Vm/       帧栈式卡牌虚拟机        39 文件  16908 行
Lang/     英/中双向编译器与渲染   18 文件  13454 行
Battle/   对局状态机与战斗结算     7 文件   4162 行
Diy/      DIY 四端                5 文件   6913 行
Packs/    卡包 / 卡池 / 构筑       8 文件   1772 行
```

依赖方向是单向的：`Core ← Ast ← Vm ← Battle ← Packs`，
`Lang` 与 `Diy` 依赖 `Ast`，`Diy` 另需 `Lang` 的词法表。
没有反向依赖，也没有循环。

---

## Core —— 物理法则与内容分离

`Core` 只回答两个问题：**什么东西是不可变的物理法则**，**什么东西是玩家可以定义的内容**。

| 类别 | 可否 DIY | 代表类型 |
|---|---|---|
| 物理法则（enum） | 否 | `ZoneRef` / `CompareOp` / `CoreEvent` / `StatKey` / `SelectMode` |
| 内容词条（`SymbolId<T>`） | 是 | 兵种 `TraitTag` / 关键词 `KeywordTag` / 职业 `ClanTag` / 计数器 `CounterTag` |

内容词条由 `GameSchema` 统一发号。玩家注册「高达」兵种只需一行：

```csharp
schema.Trait("Mecha", "my-mod");
```

**`SymbolId<T>` 的相等性是 Id-only**，不含类型名与来源。
同名符号来自不同来源时抛 `SymbolConflictException` —— 不静默合并，
因为「两个作者各定义了一个叫『守护』的关键词」在语义上必然冲突。

### MatchRules —— 36 项可配置规则

主战者血量、每回合涨费、PP 上限、起手抽牌、进化/超进化解锁回合与加成、
觉醒阈值、奥义阈值、局外构筑张数区间与同名上限……
全部收口到 `MatchRules`，默认值与官方标准一致。

DIY 卡池可以任意覆写，而**不需要改引擎源码** —— 这是 DIY 的前提。

### StackableAmuletSpec —— 可堆叠资源

土之印、线索这类「计数器寄宿在场的衍生护符」的行为被抽象成数据：

```csharp
public sealed record StackableAmuletSpec(
    string CounterName,
    int DefaultTokenCardId,
    string MatchTraitName,        // 按兵种认同类（空 = 只认卡号）
    StackMergePolicy MergePolicy, // AbsorbQuietly / BanishOld
    bool ProtectedFromAbilities);
```

引擎逻辑只查这张表，不再认识任何具体卡号。
DIY 作者也能定义自己的可堆叠资源。

---

## Ast —— 19 个正交节点

原本 93 个 Verb 收敛成 19 个 `EffectNode` 子类，靠字段组合表达差异：

| 原 Verb | 收敛后 |
|---|---|
| `Damage` / `DamageAll` / `DamageRandom` | `DealDamageNode` + `TargetSpec.Mode` |
| `Heal` / `HealFull` / `HealLeader` | `RestoreLifeNode` + `FullRestore` |
| `Draw` / `DrawByName` / `DrawByCost` | `MoveZoneNode` + `TargetSpec.Where` |

```
SelectTargetNode   DealDamageNode      RestoreLifeNode    ModifyStatsNode
ModifyCostNode     ModifyKeywordNode   GrantAbilityNode   GrantCounterAbilityNode
SpawnCardNode      MoveZoneNode        TransformNode      EvolveNode
ModifyCounterNode  InvokeAbilityNode   ConditionalNode    LoopNode
BranchModeNode     ReplaceDeckNode     MacroNode
```

**85 个 Timing 收敛为 29 个 `CoreEvent` + `TriggerSpec`。**

`CardDef` 是一张卡的全部事实：卡号、卡名、卡种、身材、职业、兵种、
能力列表、融合声明、激奏/结晶分栏、纹章/信仰子卡、构筑期修正、卡包归属。

### 目标模型 `TargetSpec`

所有「作用于谁」的问题都由同一个结构表达：

```csharp
public sealed class TargetSpec
{
    public TargetSourceKind SourceKind { get; init; }  // Zone / Slot / CardId
    public ZoneScope Owner { get; init; }             // Self / Opponent / Any
    public ZoneRef Zone { get; init; }
    public string Slot { get; init; }                 // $self / $selected / $item ...
    public BoolExpr Where { get; init; }              // 任意布尔谓词
    public SelectMode Mode { get; init; }             // All / Choose / Random
    public NumExpr Count { get; init; }
    public SortKey Sort { get; init; }
    public int TopN { get; init; }
    public bool DistinctByName { get; init; }
    public HistoryScope History { get; init; }         // 本局/本回合历史池
    public bool IncludeLeader { get; init; }
}
```

条件用 `BoolExpr` 真值树表达，不是魔法字符串：

```
HasKindExpr    HasTraitExpr    HasKeywordExpr   HasClanExpr    HasPackExpr
HasStateExpr   CompareExpr     ExistsExpr       NotSameEntityExpr
IsPreviousExpr DeckRuleExpr    AndExpr          OrExpr         NotExpr
```

### DeckBuildModifier —— 构筑期修正

「这张卡在构筑时让牌组容量 +20」「允许混入指定职业」这类能力**在对局中不生效**，
只在组牌与开局那一刻参与计算：

```csharp
public sealed class DeckBuildModifier
{
    public DeckModKind Kind { get; init; }   // 7 种
    public int Value { get; init; }
    public bool StackPerCopy { get; init; }  // 按投入张数叠加
    public string ExclusiveGroup { get; init; }  // 互斥组，同组只生效一次
    ...
}
```

---

## Vm —— 帧栈 + 可挂起

```csharp
public VmStatus StepUntilBlocked()
{
    while (true)
    {
        if (Pending != null) return VmStatus.WaitingForInput;   // 不变量
        bool blocked = ExecNode(node, frame);
        if (blocked) return VmStatus.WaitingForInput;
        frame.Ip++;
    }
}
```

三个关键设计：

**① 显式帧栈** `Stack<VmFrame>` 取代同步递归。
效果结算到一半需要玩家选择时，整个中间状态可以挂起，恢复后原地继续。

**② `EntityRef(EntityId, EntrySerial, Snapshot)`**
流水号防止「回手再打出」误触发旧能力；快照允许读取已死亡卡牌的完整数据
（亡者召还、复制已死随从都依赖它）。

**③ 具名寄存器**
`$self` / `$trigger` / `$opposing` / `$selected` / `$created` / `$item` 取代单一槽位。
`$item` 专用于筛选上下文的「当前候选」，`$self` 是效果来源。

### 挂起与恢复的契约

```csharp
// 挂起：返回 true，且绝不能覆盖 OutSlot
if (Pending != null) return true;

// 恢复：卡已经造好了，直接前进 IP（不能重跑指令）
frame.Ip++;
Pending = null;
```

这个契约是硬性的。写成 `return false` 会让 `Ip` 提前自增；
写成「重跑指令」会在恢复后又弹一次同样的选择。

### 发现（Discover）—— 唯一的卡池取卡机制

「标准发现 / 混沌发现 / 指定卡包 / X 选 Y」不是几个动词，是同一个模型：

```
Discover(范围, 筛选, 展示X, 选取Y, 去向)
```

| 维度 | 表达方式 |
|---|---|
| 范围 | `TargetSpec.Zone` = `CardPool` / `GlobalPool`，或 `HasPackExpr` 指定卡包 |
| 筛选 | `TargetSpec.Where`（卡种 / 兵种 / 职业 / 费用区间 / 关键词 / 卡包） |
| 本职业 | `HasClanExpr.IsSelfClan`（读我方主战者职业） |
| 多职业区间 | `HasClanExpr.Clans`（`Swordcraft|Runecraft`） |
| 多卡包 | `HasPackExpr.PackId`（`core-wb|testset-v2`） |
| 展示 X 张 | `SpawnCardNode.OfferCount`（1–5，默认 3） |
| 选取 Y 张 | `SpawnCardNode.Count`（1–X，默认 1） |

条件筛选复用同一个 `VmEval.EvalBool`：
把候选卡造成**纯内存探针**（`EntityId = -1`，不进世界实体表），
绑到 `$item` 与 `$self` 上求值。于是「发现一张费用 ≤3 的机械随从」
不需要任何新语法、任何新求值逻辑。

**跨池发现**：`Where` 里一旦出现 `HasPackExpr`，底座池自动切到 `GlobalCatalog` ——
哪怕那个卡包没勾进当前卡池，只要装在硬盘上就能发现出来。
---

## Lang —— 英/中双向

### 英文编译器：三条铁律

1. **词法层之后只碰 `TokenCursor`** —— 禁止 `Substring` / `Regex`
2. **双引号在词法期递归产出 `Token.Quoted(Inner[])`**
3. **词典与 Schema 实时联动** —— 最长前缀匹配，`Rusty, Luxcard Trickster` 不被逗号切断

零兜底：任何无法解析的内容都抛 `LangException`，不静默跳过。

```csharp
// 守卫 1：每句结束必须消费完
public void AssertEnd(string context)
{
    while (Current.Kind == TokKind.Period || Current.Kind == TokKind.Semicolon) Advance();
    if (!IsAtEnd)
        throw new LangException($"{context}：句尾还有未消费的词 \"{Current}\"");
}

// 守卫 2：解析必须推进
int before = cur.Position;
var node = ParseOne(cur);
if (cur.Position == before)
    throw new LangException($"无法解析的动作：\"{cur.Current}\"");
```

句首前缀设计成 Kleene 星号，可任意叠加：

```
[TurnScope] [Frequency] [TurnBoundary] [Header:] [WhenClause] [IfClause] Body
```

例：`During your turn, once on each of your turns, whenever X, if Y, do Z.`

### 双向渲染

| 组件 | 方向 | 用途 |
|---|---|---|
| `EnglishCompiler` | 英文 → `CardDef` | 官方卡面编译 |
| `AstEnglishPrinter` | `CardDef` → 英文 | **往返判等的标尺** |
| `ChineseCardPrinter` | `CardDef` → 简中 | 卡面渲染 |
| `NaturalPrinter` | `CardDef` → 自然语言 | 调试与展示 |

`AstEnglishPrinter` 是四端同构的裁决者：它的输出就是「结构等价」的定义。
**任何它不打印的字段，对所有门禁都是不可见的** —— 因此它必须覆盖 `CardDef` 的每个字段。

`ChineseCardPrinter` 的硬约束是**零洋泾浜**：
卡面上不允许出现任何英文残留（含内部枚举名、裸 `N` 这类占位符）。
`TraitZh` / `ClanZh` / `CounterZh` / `KeywordZh` 都回落到 `Glossary`，
DIY 作者的译名只需注册一次。

---

## Battle —— 对局状态机

```
MatchRunner   开局 / 换牌 / 回合循环 / 合法动作枚举 / 胜负判定
  ├─ TurnRunner     12 步回合时序、涨费、进化、出牌
  ├─ CombatResolver 攻击、交战、伤害、破坏
  └─ BattleState    世界状态：区域、计数器、事件总线
```

### 状态机

```
NotStarted → Mulligan → Playing → Finished
```

`GetLegalActions()` / `Execute(action)` 是给 UI 的唯一入口。
`IChoicePolicy` 抽出「玩家如何做选择」：`FirstLegalChoicePolicy` 用于无头测试，
`ScriptedChoicePolicy` 用于可复现的定点测试。

### 阶段的硬守卫

所有操作入口（`EndTurn` / `PlayCard` / `Evolve` / 两个 `EngageAmulet` 重载 /
`FuseCards` / `UseExtraPp` / `Attack` / `GetLegalActions`）统一以这段开头：

```csharp
if (Phase != MatchPhase.Playing || IsWaitingForInput) return false;
```

少了它，换牌阶段能强行出牌、挂起时能重复结算。

### 非对称开局

`StartWithProfiles` 让双方各按自己的 `EffectiveDeckProfile` 建主战者、抽牌、给进化点。
「带 +10 血构筑卡的一方 30 血 vs 另一方 20 血」靠它实现。

---

## Diy —— 四端

同一张卡有四种表达方式，四者必须产出**结构等价**的 `CardDef`：

| 端 | 输入 | 编译器 |
|---|---|---|
| 脚本 | `card 900001 "名" (Follower, cost: 2) { on play { draw(1) } }` | `ReshadowScriptCompiler` |
| 积木 | `CardBlockWorkspace`（帽子/胶囊/堆叠/方块/六角） | `DiyBlockCompiler` |
| 表单 | `CardFormModel`（下拉 + 数值 + 目标筛选） | `DiyFormCompiler` |
| 中文 | 自然语言卡面 | `DiyWorkspace.CompileFromEnglishCnl` |

每一端都有反向：`DiyBlockDecompiler` / `TryCardDefToForm` / `Print`。
**双向无损是硬要求**：`Print(Parse(s))` 幂等，且 `Parse(Print(d))` 结构相等。

### DiyWorkspace —— 四端的公共宿主

```csharp
var ws = DiyWorkspace.Create(seed);
var def = ws.CompileFromEnglishCnl("【入场曲】抽1张牌。");
```

它持有 `CompileContext`（一份 `GameSchema` + `Lexicon`）与 `Glossary`，
并提供 `DefineTrait` / `DefineCounter` / `DefineClan` / `DefineKeyword` / `DefineCardName`。
**四端必须共用同一个 workspace** —— 各造一套 `GameSchema` 会让 `SymbolId.Id` 串号。

### 脚本语言的形态

```rust
card 940001 "星界旅者" (Follower, cost: 3, atk: 3, life: 4) {
    deck_mod size(+20)
    deck_mod leader_life(+10)
    deck_mod allow_clan(choose: 1)
    deck_mod self_copy(3)

    fuse: target(ally, hand, count: 1, trait: Artifact)

    on play {
        discover(target(ally, global_pool, follower,
                        clan: self, min_cost: 2, max_cost: 4,
                        pack: core-wb|testset-v2),
                 offer: 4, pick: 2, to: hand)
    }
}
```

`deck_mod` 是构筑期修正，`discover` 是卡池检索，两者都在四端有对应形状。

### 结构判等

`DiyWorkspace.StructurallyEqual(a, b, out diff)` 是四端同构的唯一判据。
它在比较前做归一化：`All` 模式下的 `Count` 视为等价（`null` ≡ `Const(1)`）、
`AndExpr` 展平并按内容排序、目标规格递归归一。

这几条归一化不是可选项：少了任何一条，
同义的两份 AST 会因为「项的顺序不同」「`null` 与 `Const(1)`」被判成两张不同的卡。
---

## Packs —— 内容包与赛制环境

三层结构，边界划得很死：

```
物理层   CardPack       packs/<pack_id>/pack.json    作者发布的最小单元
环境层   CardPool       pools/<name>.pool.json       启用哪些包 + 自定义规则
局外层   ConstructedDeck decks/<name>.deck.json      玩家的组牌配方
局内层   PlayerState.Deck                            对局中的活体实体列表
```

**卡包只管「我有什么」，卡池负责「把多个包合流成一个自洽宇宙并拦截冲突」。**

### 卡包文件

```jsonc
{
  "pack_id": "core-wb",
  "native_pool_id": "standard",     // 原生卡池：在这个池里不可被踢出
  "dependencies": [],               // 显式依赖
  "traits":   [{ "name": "Mecha", "name_chs": "机甲" }],
  "counters": [{ "name": "rage", "min": 0, "max": 10 }],
  "stackable_amulets": [ ... ],
  "named_decks": [ { "deck_name": "Apocalypse Deck", "entries": [...] } ],
  "cards": [
    {
      "local_id": "gundam",          // 只写局部号，全局卡号由卡池分配
      "name_eng": "Gundam",
      "keywords": ["ward"],
      "source_format": "reshadow_script",   // 或 "eng_cnl"
      "text": "card 900001 ...",
      "deck_modifiers": [ { "kind": "size", "value": 20 } ]
    }
  ]
}
```

`local_id` 让不同作者的卡号不撞车：`PoolCardIndex` 按 `PackId:LocalId`
分配确定性递增的全局卡号。

### 五步构建流水线

`CardPoolBuilder.Build(config, packs)` 按严格顺序执行：

```
1.    原生卡池防脱离 + 显式依赖检查
2.    Pass 1：词法表 / Schema / 卡号 / 卡名 预注册 + 冲突拦截
3.    具名牌组跨包解析
4.    Pass 2：编译全部卡片 AST
4.5   AST 静态引用完整性核验
5.    组装运行时环境
```

**两遍扫描是硬要求。** 卡包 A 的卡面写着 `Summon a Fairy`，
而 `Fairy` 定义在卡包 B —— 边扫边编译时 B 的名字还没进词法表，认不出、静默失败。

**第 4.5 步是第二重保险。** 两遍编译只保证「认得出名字」，
认得出不代表那张卡**在卡池里**。`PoolDependencyVerifier` 逐个检查
`SpawnCardNode` / `TransformNode` / `ReplaceDeckNode` / 具名牌组成员，
缺一张就精确报出「在哪个包」。

### 同名消歧三级优先

```
本包优先 → 显式依赖包优先 → 卡池全局唯一匹配
```

两个互不相关的包定义了同名卡且被模糊引用时，**拒绝启动该卡池** ——
谁赢都是错的，因为引用方没有表达偏好。

### GlobalCatalog —— 全量宇宙总表

启动时加载 `packs/` 下的全部卡包。

**卡池是「这个赛制允许用什么」，总表是「玩家电脑上装了什么」。**

分开的理由：卡池 A 里的卡若点名召唤卡包 B 的 Token，
B 已安装但没勾进 A 时照样能召出来（其余 B 的卡不污染 A 的构筑表）；
B 根本没装时才是错误。

### 局外构筑 vs 局内牌库

| | `ConstructedDeck` | `PlayerState.Deck` |
|---|---|---|
| 内容 | 卡号 + 张数 | `RuntimeEntity` 活体 |
| 生命周期 | 永久存档 | 对局结束即销毁 |
| 可被对局改变 | **否** | 是（洗牌 / 抽走 / 减费 / 整副替换） |

混在一起的后果不是崩溃，是「过两天玩家发现自己的牌组变了」。

### 两阶段构筑计算

```csharp
var profile = DeckRuleResolver.ResolveAndValidate(deck, pool);
```

**阶段 1** 扫牌组里每张卡的 `DeckBuildModifier`，算出「这套牌组的有效规则」；
**阶段 2** 用有效规则校验整副牌组。

必须分两步：边扫边校验会让「扩容 +20」这张卡自己
在扫描到它之前就被旧的「40 张」规则判为超限。

---

## 静态校验

`python tools/check_engine.py` 是提交前的静态门禁，四项全绿才算通过：

| 脚本 | 检查什么 |
|---|---|
| `check_keywords.py` | 关键词的写入端与读取端是否成对 |
| `check_zones.py` | 区域容量上限是否有对应的溢出处理；新枚举值是否漏映射 |
| `check_ast_traps.py` | 写法层面的防呆陷阱（引用判等、无边界吞词等） |
| `check_matrix.py` | AST 支持矩阵：节点在四端两向的覆盖、字段往返、分支顺序、重复派发 |

`check_matrix.py` 针对的是「加了 A 端忘了 B 端」这类结构性遗漏 ——
四端 × 三向 = 12 个落点，靠人脑记必然漏，所以写成表用脚本校验。

---

## 构建

需要 Godot 4.7.2 与 .NET 9 SDK：

```bash
dotnet build ShadowverseFan.csproj -v q --nologo
```

本仓库不含 `assets/`，直接运行会缺少立绘与音频。
内核本身不依赖它们，可单独喂文本测试。

---

## 目录速查

```
src/KernelV3/
├── Core/     ZoneRef / CoreEvent / SymbolId / GameSchema / MatchRules / PoolCardIndex
├── Ast/      EffectNode 19 子类 / TargetSpec / BoolExpr / CardDef / DeckBuildModifier
├── Vm/       CardVm / VmFrame / VmScope / VmEval / RuntimeEntity / IVmWorld
├── Lang/     Tokenizer / TokenCursor / EnglishCompiler / 三个 Printer / Glossary
├── Battle/   MatchRunner / TurnRunner / CombatResolver / BattleState / PlayerState
├── Diy/      ReshadowScriptCompiler / DiyBlockModel / DiyFormModel / DiyWorkspace
└── Packs/    CardPackLoader / CardPoolBuilder / GlobalCatalog / DeckRuleResolver

data/
├── packs/    core-wb（17 张衍生卡 + 2 份具名牌组）/ testset-v2（30 张可收藏卡）
├── pools/    official-standard / custom-high-mana
└── decks/    示例构筑
```

---

## 未完成

- Godot UI 层
- 局域网联机
- 英文编译器剩余个例化句子（每类 1–2 句）
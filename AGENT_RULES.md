# Reshadow KernelV3 · 编码规范与 AI 防呆铁律

> 本文件的每一条规则，都对应本仓库历史上真实发生过的 Bug。
> 改代码前逐条对照；`python tools/check_engine.py` 会机械卡死其中可自动检测的部分。

---

## 一、C# 类型、相等性与默认值

### 1.1 严禁对 AST 节点用 `==` / `!=` 做语义判等

`TargetSpec`、`EffectNode`、`NumExpr`、`BoolExpr` 都是 `class`，未重载 `operator ==`；
而 `TargetSpec.Self` / `OpponentLeader` / `Previous` 等静态属性是 `=> new()`，
**每次访问都是一个全新实例**。于是 `ms.Target == TargetSpec.Self` 是引用比较，恒为 `false`。

```csharp
// ❌ 100% 死代码：恒 false，分支永远不执行
case ModifyStatsNode ms when ms.Target == TargetSpec.Self:

// ✅ 按字段判别语义
private static bool IsSelfTarget(TargetSpec t)
    => t != null && t.SourceKind == TargetSourceKind.Slot && t.Slot == TargetSpec.SlotSelf;
```

自动检测：`check_ast_traps.py` 陷阱 1。

### 1.2 可选属性用 `null` 表示「不修改」，不用 `Const(0)`

「不修改攻击力」与「把攻击力设为 0」是两件完全不同的事。
对 `ModifyStatsNode` 的 `Attack` / `Life` 用 `Const(0)` 当默认值，
会让 `Set the attack ... to 4` 把生命也赋成 0，敌方全场随从当场被 SBA 判死。

执行端必须先判 `null` 再求值，绝不能对 `null` 调 `EvalNum` 后直接赋值。

### 1.3 `GameSchema` 单一真相源

`SymbolId<T>` 的整数 `Id` 由 `GameSchema` 实例在注册时分配，`BattleState` 按 `Id` 索引计数器。

- ❌ 生产代码里调 `SymbolId<T>.FromName(...)`（`Id = -1`，所有计数器串号）
- ❌ 把动态符号缓存成 `static readonly SymbolId<T>`（不同 Schema 实例分配的 `Id` 不同）
- ✅ 始终通过当前对局的 `Schema.Counter(name)` / `Keyword(name)` / `Trait(name)` 取

补充：`SymbolId<T>` 的相等性**只按 `Id`**，不按 `Name`。
同名符号在不同 Schema 实例里可能以不同大小写首次注册（`loot` vs `Loot`），
若按 `(Id, Name)` 全等比较，卡身上的兵种与条件里的兵种会被判成两个不同的东西。

---

## 二、AST 与枚举变更「五点同步」

新增一个枚举值（`UnitState` / `StatKey` / `SortKey` / `CoreEvent`）或一个 `TargetSpec` 字段时，
**必须一次改齐以下 5 处**：

| # | 位置 | 文件 | 漏改后果 |
|---|---|---|---|
| 1 | 解析构造点 | `TargetParser` / `ActionParser` / `ConditionCompiler` / `PrefixParser` | 语法认识，但没写进 AST |
| 2 | 克隆与透镜点 | `TargetSpec.WithWhere`、`ActionParser.OmittedTargetSpec`、`ActionParser.NormalizeAllCount`、`InsteadWeaver.InheritTarget` | 经过一次条件追加或 instead 透镜后字段静默丢失 |
| 3 | VM 求值点（活体 + 快照双路径） | `VmEval.ReadStat(RuntimeEntity)` **和** `ReadStatFromSnapshot`；`MatchState(RuntimeEntity)` **和** `MatchState(EntitySnapshot)` | 编译通过、AST 正常，运行时落进 `_ => 0` / `_ => false` |
| 4 | 打印与双向校验点 | `AstEnglishPrinter`、`NaturalPrinter`、`RoundTripVerifier` | 审计与还原校验失明 |
| 5 | 静态检查表 | `GameSchema.BuiltinKeywords` / `BuiltinCounterDefs`、`tools/check_*.py` | 静态门禁报错 |

自动检测：`check_ast_traps.py` 陷阱 2（枚举求值）与陷阱 3（克隆点漏拷）。

---

## 三、英文编译器（`Lang/`）游标防吞词

### 3.1 严禁无边界吞词

`while (!c.IsAtEnd) c.Advance();` 会把句尾的 `or less` / `or more` 或
复合条件的后半截 `and you have 3 play points` 一起吃掉，而且不报错。

- 任何词消费必须是精确的 `MatchWord` / `MatchPhrase`
- 跳过已知冗余尾缀时用 `c.AdvanceToClauseBoundary()`，它在句读与连词前停下

自动检测：`check_ast_traps.py` 陷阱 5。

### 3.2 严禁在词法期之后把 Token 拼回字符串做子串匹配

`Tokenizer` 之后只允许操作 `TokenCursor`。
拼成字符串再 `.Contains()` / `.Substring()` / Regex，会连兵种名、卡种名、归属限定词一起吞掉。

收集名字（卡名 / 纹章名 / 牌组名）后整体交给词法层查表是允许的 —— 那不是匹配，是查表。

自动检测：`check_ast_traps.py` 陷阱 4。

### 3.3 消费通用状语前必须校验前置语义状态

`this turn` / `this match` 既可能是历史池后置定语（`followers destroyed this turn`），
也可能是动作持续时间（`give it Rush this turn`）。
`TargetParser` 中**只有 `history != HistoryScope.None` 时**才允许把裸 `this turn` 当历史限定语消费。

### 3.4 「支付资源以执行效果」必须同生共死

`Spend N play points to ...`、`Reduce your faith's value by N to ...`、`Consume N clues to ...`
这类带 `to <后续动作>` 的句式，必须用 `PayThenDo`（`ConditionalNode(ctr >= N)`）把扣费与后续动作包在同一分支。
裸写顺序节点会让 0 资源的玩家靠 `ClampCounter` 钳到 0 白嫖效果。

---

## 四、实体状态与生命周期「对称守恒」

### 4.1 装配与洗白的唯一通道

实体的 4 个重置/出生点必须严格对称：
**① `CreateEntity`（出生）、② `TransformEntity`（变身）、③ `ResetToPrintedValues`（回手洗白）、④ `PlayAsAltMode`（结晶进场）**。

1. 洗白旧状态时必须同时清空 `Keywords`、`KeywordValues`、`Counters`、`TempModifiers`、
   `ClearGrantedAbilities(e)`、`e.LastWordsRemoved = false`
2. 回手时若 `OriginalDef != null`（激奏/结晶卡），先还原 `t.Def = t.OriginalDef; t.OriginalDef = null;`
3. 装配新状态时统一调 `MountInnateProperties(e)`，严禁各处手写残缺副本

### 4.2 属性增减与限时到期对称

- **生命上限同步**：`DoModifyStats` 在 `Add`（life > 0）与 `Set` / `Swap` 时同步 `MaxLife`；
  `BattleState.ExpireOn` 与 `MemoryWorld.ExpireOn` 撤销限时加成时对称扣回
  `if (m.Life > 0) e.MaxLife = Math.Max(e.Life, e.MaxLife - m.Life);`
- **印制能力与授予能力对称**：任何拦截实体能力的机制（`IsSilenced`、`LastWordsRemoved`），
  在 `FlushNextTrigger` 中必须同时拦 `e.Def.Abilities` **和** `_world.GrantedAbilities`

### 4.3 战斗攻防双方对称

- `OnAttack` 与 `OnClash` 都必须传 `Object = EntityRef.From(defender)`
- 必杀被屏障抵消的判定必须双向：防守方屏障抵消攻击方 Bane，**攻击方屏障同样抵消防守方 Bane**

---

## 五、单一权威管线与帧栈挂起

### 5.1 严禁绕过 `BattleState` 的权威管线

| 物理操作 | 唯一入口 | 错误写法与后果 |
|---|---|---|
| 造成伤害 | `CombatResolver.ApplyDamage` | `t.Life -= dmg` 绕过超进化免伤、增伤、上限、屏障、血怒计数与 `OnDamaged` |
| 能力破坏 | `DestroyEntity(t)` | `MoveEntity(t, Cemetery)` 漏掉免破坏检查、`shadow+1`、`DestroyedThisMatch`、`OnDestroy` |
| 能力放逐 | `BanishEntity(t)` | 只 `MoveEntity(t, Banished)` 会漏 `OnBanish` |
| 离场事件 | 由 `MoveEntity` 在 `leftBoard` 时统一发 | 调用前再手发一次会触发两遍 `OnLeaveField` |
| 普通抽牌 | `DrawCards(playerId, count)` | `MoveEntity` 绕过爆牌、`OnDraw`、抽空判负 |

### 5.2 离场自身触发器（`graveSelf`）的四事件白名单

事件到达 `MatchesTrigger` 时，实体已经位于 `Cemetery` / `Banished`（到期纹章位于 `None`）。
`graveSelf` 必须完整包含：`OnDestroy`、`OnLeaveField`、**`OnBanish`**、**`OnDiscard`**。

### 5.3 手选目标的三处过滤一致 + 帧栈重入标识

1. `ResolveOrBlock`、`DoSelect`、`CountLegalTargets` 三处计算 `Choose` 候选池时必须同时：
   经 `VmEval.ResolveRef` 应用 `spec.Where`、过滤 `IsProtectedAmulet`、过滤对手的 `ambush` / `aura`
2. ❌ 严禁用 `if (scope.Read(slot).Count > 0)` 判断玩家是否已为当前节点选过 ——
   同一能力内有两句 `Select` 时，第二句会误读第一句的结果而跳过弹窗
   ✅ 用 `frame.ResolvedChoiceIp == frame.Ip` 判定，消费后复位为 `-1`，否则先 `scope.Clear(slot)`

---

## 六、测试门禁（消灭假绿测试）

1. **禁止只测 AST 打印串**：修复或新功能必须在 `Regress.*.cs` 里构造真实
   `BattleState + TurnRunner + CardVm` 跑完触发链，断言最终的攻防 / 区域 / 计数器数值。
2. **正反双向断言**：
   - 测条件与代价时，必须同时测「不满足时不触发」与「满足时触发」
   - 测过滤与排除（`another` / `with Fuse` / `duplicates`）时，必须同时在池子里放
     **符合条件的目标**与**不符合条件的干扰项**，断言干扰项不受影响

---

## 七、提交前自检清单

```
1. dotnet build ShadowverseFan.csproj -v q --nologo     → 0 个错误
2. cd .et && dotnet run -c Debug                        → VM 自测 / 文档验收 / 全部 Regress 全绿
3. python tools/check_engine.py                         → ALL ENGINE CHECKS PASSED
```

三项全过才算完成。任何一项红着都不能提交，也不能说「已修复」。

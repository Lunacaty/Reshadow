# Reshadow

Shadowverse: Worlds Beyond 的粉丝引擎核心，用 Godot 4.7.2 + C# 实现。

本仓库只包含**内核代码**：符号系统、AST、帧栈虚拟机、英文编译器。
卡池数据、立绘、扩展包等运行时素材不在库内。

---

## 设计目标

让玩家用三种方式造卡，三者产出同一份 AST：

1. **脚本语言**（ReshadowScript）
2. **Scratch 积木**
3. **表单 UI**

因此引擎不硬编码任何一张卡：所有卡牌都是运行时从英文卡面文本编译出来的。

---

## 架构

```
Core/   物理法则与符号系统
Ast/    纯数据树（17 个效果节点 / 24 个事件 / 表达式）
Vm/     帧栈式卡牌虚拟机
Lang/   英文编译器（词法 → 语法 → AST）
```

### Core —— 物理与内容分离

| 类型 | 可否由玩家 DIY | 例子 |
|---|---|---|
| 物理法则（enum） | 否 | `ZoneRef` / `CompareOp` / `CoreEvent` / `StatKey` |
| 内容词条（`SymbolId<T>`） | 是 | 兵种 / 关键词 / 职业 / 计数器 |

玩家注册"高达"兵种只需 `schema.Traits.Register("高达", "my-mod")`。
同名符号来自不同来源时会抛 `SymbolConflictException`，不静默合并。

### Ast —— 17 个正交节点

原本 93 个 Verb 收敛为 17 个节点，靠字段组合表达差异：

| 原 Verb | 收敛后 |
|---|---|
| `Damage` / `DamageAll` / `DamageRandom` | `DealDamageNode` + `TargetSpec.Mode` |
| `Heal` / `HealFull` / `HealLeader` | `RestoreLifeNode` + `FullRestore` |
| `Draw` / `DrawByName` / `DrawByCost` | `MoveZoneNode` + `TargetSpec.Where` |

```
SelectTargetNode  DealDamageNode   RestoreLifeNode  ModifyStatsNode
ModifyCostNode    ModifyKeywordNode GrantAbilityNode SpawnCardNode
MoveZoneNode      TransformNode   EvolveNode       ModifyCounterNode
InvokeAbilityNode ConditionalNode LoopNode         BranchModeNode
MacroNode
```

85 个 Timing 收敛为 **24 个 `CoreEvent` + `TriggerSpec`**：

```csharp
public sealed class TriggerSpec
{
    public CoreEvent Event { get; init; }
    public TargetSpec SubjectFilter { get; init; }   // 谁触发才算数
    public ActiveZoneScope ActiveZone { get; init; }
    public TurnOwnerScope DuringTurn { get; init; }
    public int MaxTriggersPerTurn { get; init; }
}
```

### Vm —— 帧栈 + 可挂起

```csharp
public VmStatus StepUntilBlocked()
{
    while (true)
    {
        if (Pending != null) return VmStatus.WaitingForInput;  // 不变量
        bool blocked = ExecNode(node, frame);
        if (blocked) return VmStatus.WaitingForInput;
        frame.Ip++;
    }
}
```

关键设计：

- **显式帧栈** `Stack<VmFrame>` 取代同步递归，支持结算中途等待玩家选择
- **`EntityRef(EntityId, EntrySerial, Snapshot)`**：流水号防止回手再打出误触发；快照允许读取已死亡卡牌的数据
- **具名寄存器** `$self` / `$trigger` / `$opposing` / `$selected` / `$created` / `$item` 取代单一槽位

### Lang —— 三条铁律

1. **词法层之后只碰 `TokenCursor`**，禁止 `Substring` / `Regex`
2. **双引号在词法期递归产出 `Token.Quoted(Inner[])`**
3. **词典与 Schema 实时联动**（最长前缀匹配，`Rusty, Luxcard Trickster` 不被逗号切断）

零兜底：任何无法解析的内容都抛异常。

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

---

## 规模

| 指标 | 数值 |
|---|---:|
| 文件 | 25 |
| 行数 | 6450 |
| 兜底代码 | 0 |
| 编译错误 | 0 |

---

## 验证状态

内置 **11 项 VM 自测**全部通过（`VmSelfTest.Run()`）：

| 用例 | 验证的能力 |
|---|---|
| 挂起等玩家选择 | `PendingChoice` |
| 选择后正确执行 | `SubmitChoice` 恢复 |
| 已死亡不重复受伤 | `EntityRef` 判活 |
| 死后仍能复制 | `EntitySnapshot` |
| 回手再打出的旧引用失效 | `EntrySerial` |
| 槽位不被中间动作覆盖 | 具名寄存器 |
| 循环被硬上限截断 | `MaxIterations` |

英文编译器对 36 号文档的 **1450 个去重句子**做验收，当前通过 **1347（92.9%）**。

---

## 构建

需要 Godot 4.7.2 与 .NET 9 SDK：

```bash
dotnet build
```

本仓库不含 `assets/`，直接运行会缺少卡池数据。
编译器本身不依赖卡池，可单独喂文本测试。

---

## 未完成

- 英文编译器剩余 103 句（个例化，每类 1-2 句）
- `IVmWorld` 的真实战斗层实现
- Godot UI
- 局域网联机

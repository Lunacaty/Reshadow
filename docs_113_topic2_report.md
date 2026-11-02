# 专题二（预选事务）失败报告

> 提交基线：`4249a8d`（战场布局 + 专题一词法表同步）
> 状态：**已整体回退，基线全绿**。下面是实现过程中暴露的问题。
> 用途：请外援判断正确的实现路径。

## 一、文档要做什么

113 号文档专题二要求把出牌顺序改成「先选目标 → 再扣费上场」：

```
原实现：出牌 → 扣 PP → 移入战场 → 播入场动画 → VM 跑到 Select 才挂起
要求：  出牌 → 预扫描 → 挂起问目标（可取消）→ 选定 → 扣 PP → 移入战场 → 用预选答案结算
```

文档给三件事：加 `PendingChoice.CanCancel`；建 `PreActionSession`；提交时把答案塞进 `VmFrame.PreCommittedChoices`，VM 执行到对应节点时直接出队。

## 二、已验证可用的部分

| 组件 | 结果 |
|---|---|
| `PendingChoice.CanCancel` | 已加；`Block()` 重建时会丢，已补保留 |
| `PreChoiceInspector` 预扫描器 | 能沿 AST 走条件/模式分支，遇抽牌/随机即停 |
| `PreActionSession` | 存答案、当前 prompt、操作种类 |
| 预选挂起 | **实测通过**：PP/手牌/墓地全部未变 |
| 取消 | **实测通过**：零消耗退回 |

实测输出：

```
打出前: PP=10 手牌=6
TryPlayCard=True
挂起=True  可取消=True  HasPreAction=True
  kind=SelectTarget  候选=6  CanCancel=True
PP=10  手牌=6  墓地=0            ← 还没扣费、还没进墓地
取消后: PP=10  手牌=6  挂起=False  墓地=0    ← 零消耗退回
```

## 三、卡在哪里

**提交答案后 VM 没有执行能力，仍然二次弹窗。**

```
1) 挂起=True 敌血=2(应2) 墓地=0 手牌=6        ← 预选正确
2) 提交后 敌血=2(期望0) 墓地=0 还有挂起=True    ← 失败
```

## 四、根因（已用日志逐段验证）

### 4.1 链路本身是通的

```
[submit] vmPending=False committing=True     ← 提交前挂起已清空
[play] reached cost, committing=True         ← 进入扣费
[play] spell branch, vm=True                 ← 走法术分支，VM 存在
[drain] pending=1                            ← 答案确实注入到了 VmFrame
```

但 `[doselect]` 从不打印 —— `DoSelect` 根本没被调用。

### 4.2 根因：答案注入到了错误的帧

```
PlayCard → EmitOrEnqueue(vm, OnPlay)
  → BattleState.EmitEvent
      → TriggerSink（CardVm 构造时注册为 OnWorldEvent）
      → CardVm.EnqueueEvent → vm._triggerQueue

StepUntilBlocked
  → 帧栈空 → FlushNextTrigger
      → 遍历所有候选能力（可能多个）
      → PushBody(ability.Body, scope)   ← 每个能力建一帧
          → DrainPreCommitInto(frame)
```

`FlushNextTrigger` 一次会压入**多个**能力帧（同一事件诱发多张卡）。

我的 `PendingPreCommit` 是单条全局队列，`DrainPreCommitInto` 只能把答案给「第一个满足条件的帧」。答案给了错误的能力帧，真正要 `SelectTarget` 的那帧照常弹窗。

### 4.3 不加过滤更糟

试过「见帧就 drain」：`[flush] queue=17` 那批初始化事件里就把答案消耗光了。

## 五、试过但失败的两种改法

| 改法 | 结果 |
|---|---|
| 无过滤 drain | 答案被初始化事件消耗，比原来更糟 |
| 按「帧内是否含 Choose 目标」过滤 | 过滤过严，`[drain]` 不再打印，能力完全不执行 |

第二种失败说明我对「哪一帧才是本次出牌触发的」判断不准 —— 只看 Body 内容不够。

## 六、初步方向（需外援确认）

答案应绑定到**发起预选那张卡**，而非全局队列：

```csharp
Dictionary<long, Queue<IReadOnlyList<int>>> PreCommitByCard
```

`DrainPreCommitInto` 判断「这一帧是否属于那张卡」才给。

**但 `VmFrame` 当前只有 `Ability`，没有记录能力归属的实体**，需要新增字段。

## 七、需要外援回答

1. `VmFrame` 增加 owner（实体 id）是否安全？它被 `DeriveForBranch`、`PushBody` 等多处使用。
2. `FlushNextTrigger` 里一个事件诱发多个能力帧时，有没有现成办法判断「哪一帧属于触发源」？
3. 文档说「挂进 `VmFrame.PreCommittedChoices`」，是否意味着只应给 `OnPlay` 触发的那一帧、且 `FlushNextTrigger` 需要按 `ev.Subject` 匹配？
4. 现有 `Block()` 会重建 `PendingChoice`，外部 `SetExternalPending` 设的值会被覆盖 —— 这条路径是否该改？

## 八、回退说明

已 `git checkout` 还原到 `4249a8d`：

```
src/KernelV3/Battle/TurnRunner.cs
src/KernelV3/Battle/MatchRunner.cs
src/KernelV3/Vm/CardVm.cs
src/KernelV3/Vm/CardVm.Nodes.cs
src/KernelV3/Vm/VmFrame.cs
src/Ui/Bridge/BattleSessionController.cs
```

删除：`PreActionSession.cs`、`PreChoiceInspector.cs`

基线回归：

```
ALL ENGINE CHECKS PASSED
VM 50/50    测试集效用 12/12    逐卡矩阵 31/32（既有 Mjerrabaine）
完整对局 7/7    DIY 8/8    卡池 22/22    战斗 UI 31/31
```

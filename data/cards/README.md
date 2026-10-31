# 测试卡集（手工设计）

本目录**不再存放官方卡池**。原来的 `cards_eng_flat.json`（1.1 MB）和
`sva_cards_text.json`（1.0 MB）已删除，改用一份手工拼装的测试卡集。

## 为什么换

官方卡池占 2 MB，但它是**参考数据**，不是本项目要交付的东西。
测试真正需要的是「每个要素至少被覆盖一次」，而不是「982 张真卡都跑一遍」。
用真卡池还有个副作用：失败时很难判断是引擎错了还是这张卡的写法太特殊。

## 设计原则

1. **不引用任何原生卡面**，全部按官方语法自行拼装。
2. 每张卡聚焦 1~3 个要素，失败时能直接定位。
3. 每个维度（触发器 / 动词 / 条件 / 子形态 / 战斗关键词）至少覆盖一次。

## 文件

| 文件 | 条目 | 体积 | 内容 |
|---|---:|---:|---|
| `testset_cards.json` | 83 张 | 32 KB | 测试卡集本体（含卡名 / 卡种 / 数值 / 效果文本） |
| `testset_lexicon.json` | 83 条 | 12 KB | 卡名词表（编译器识别卡名用） |
| `testset_sentences.json` | 94 句 | 8.7 KB | 句级语料（从卡面切出的独立句子） |

合计约 53 KB，是原来 2.1 MB 的 **2.5%**。

## 覆盖的维度

### 触发器（16 种）
`Fanfare` `Evolve` `Super-Evolve` `Last Words` `On Spellboost`
`Clash` `Strike` `Engage` `Fuse`
`At the start of your turn` `At the end of your turn`
`Whenever <主语> enters the field` `When this follower takes damage`
`Activates in hand` `Activates in deck`

### 动词（32 种）
`Select` `Deal` `Restore` `Draw` `Summon` `Add` `Give` `Remove`
`Set` `Halve` `Swap` `Reduce` `Destroy` `Banish` `Discard` `Return`
`Transform` `Evolve` `Gain` `Consume` `Recover` `Spellboost` `Reanimate`
`Advance` `Delay` `Invoke` `Replace` `Replicate` `Do this N times`
`Activate N random abilities` `Gain Crest` `exact copy`

### 条件与机制（16 种）
`Combo` `Enhance` `Necromancy` `Earth Rite` `Skybound Art` `Super Skybound Art`
`Overflow` `Mode` `activate all of them instead` `X is the number of...`
`X starts at N` `instead` `if its cost is N` `with a base cost of N or more`
`until the end of your opponent's turn`

### 分段与格式（6 种）
`Countdown` `同卡多能力` `<ev>/<sev> 三域` `共享能力头的多动作`
`引号内嵌能力` `缩进嵌套模式`

### 战斗关键词（10 种）
`Ward` `Storm` `Rush` `Bane` `Drain` `Ambush` `Aura` `Intimidate` `Barrier` `多关键词叠加`

## 重新生成

```bash
python tools/gen_testset.py
```

脚本在 `tools/gen_testset.py`，改完卡面后重跑即可重新产出上面三个文件。

## 验证

```
CARD 83 pass=83 err=0      # 整卡级编译
SENT 94 ok=84 zero=10 err=0 # 句级编译（10 句是纯声明，本就不产生动作）
```

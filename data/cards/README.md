# 卡牌数据

本目录分两部分：

| 类别 | 文件 | 用途 | 进 64 号文档 |
|---|---|---|---|
| **官方卡池** | `cards_eng_flat.json`（1.1 MB）<br>`sva_cards_text.json`（1.0 MB） | 参考数据，编译器的真实语料来源 | ❌ 排除 |
| **测试卡池** | `testset_cards.json`（39 KB）<br>`testset_lexicon.json`（15 KB）<br>`testset_sentences.json`（11 KB） | 回归测试专用，覆盖全部要素 | ✅ 追踪 |

官方卡池体积大，只作参考，不进文档。测试卡池是手工设计的，进文档。

---

## 官方卡池

`cards_eng_flat.json` 是把 `sva_cards_text.json` 的 `skill_texts` 按卡合并后的扁平结构，
顶层为 `{ "_schema", "cards" }`。**编译器只读这个文件。**

### HTML 标记处理

| 类别 | 标记 | 处理 |
|---|---|---|
| 结构标记 | `<hr>` `<ev>` `<sev>` `<ridx=N>` `<color=Keyword>` | **保留** |
| 排版标记 | `<b>` `<i>` `<u>` | 剥离，内部文字保留 |

结构标记承载语义，不能剥：

| 标记 | 含义 |
|---|---|
| `<hr>` | 能力分隔符 |
| `<ev>…</ev>` | 进化后才生效 |
| `<sev>…</sev>` | 超进化后才生效 |
| `<ridx=N>…</ridx>` | 抉择卡第 N 个选项（从 0 起） |
| `<color=Keyword>X</color>` | 机制关键词（Fanfare / Last Words / Ward …） |

---

## 测试卡池

**设计约束：用语必须与官方一致，不自造写法。**

所有句式都是从官方卡池 `cards_eng_flat.json` 里抽取原句后仿写的。
例：官方只有 `to your hand` / `to hand`，**没有** `to its hand`。

### 格式约定

| 类别 | 写法 |
|---|---|
| 能力头 / 关键词 | `<b><color=Keyword>Fanfare</color></b>:` |
| 卡名引用 | `<b><i><color=Keyword>Knight</color></i></b>` |
| 能力头分隔 | 冒号 `:`（Fanfare / Evolve / Enhance / Engage / Last Words） |
| 条件前缀 | 短横 ` - `（Combo / Necromancy / Earth Rite / Skybound Art） |

### 覆盖的维度

**触发器 20 种**
`Fanfare` `Evolve` `Super-Evolve` `Last Words` `On Spellboost` `Clash` `Strike`
`Engage` `Enhance` `Combo` `Necromancy` `Earth Rite` `Skybound Art` `Super Skybound Art`
`At the start of your turn` `At the end of your turn`
`Whenever <主语> enters the field` `Activates in hand` `Activates in deck`

**动词 44 种**
`Select` `Deal`（单体/随机/分裂）`Restore` `Draw` `Summon` `Add` `Give`
（属性/关键词/多授予）`Remove`（单词/全能力）`Set`（攻击/生命上限）`Halve`（防御/费用）
`Swap` `Reduce` `Destroy` `Banish` `Discard` `Return` `Transform` `Evolve`
`Gain` `Consume` `Recover` `Spellboost` `Reanimate` `Advance` `Delay`
`Invoke` `Replace` `Replicate` `Do this N times` `Activate N random` `Gain Crest` `exact copy`

**条件与模式 11 种**
`Mode` `activate all of them instead` `activate in sequence`
`X is the number of...` `X starts at N` `instead` `Overflow`
`if its cost is N` `with a base cost of N or more` `with N defense or less`
`until the end of your opponent's turn`

**分段与格式 7 种**
`Countdown` `同卡多能力` `<ev>/<sev> 三域` `共享能力头的多动作`
`引号内嵌能力` `Fuse 行` `缩进嵌套模式`

**战斗关键词 10 种**
`Ward` `Storm` `Rush` `Bane` `Drain` `Ambush` `Aura` `Intimidate` `Barrier` `多关键词叠加`

### 重新生成

```bash
python tools/gen_testset.py
```

### 验证

```
TESTSET 97 pass=97 err=0
```

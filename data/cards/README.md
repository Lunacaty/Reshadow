# 卡牌文本数据

数据来源：**sva.hypd.asia/en**

本目录**只包含英文与中文两种语言的文本**，不含任何卡面立绘、图片路径或图片哈希。

---

## 文件

| 文件 | 卡片数 | 体积 | 内容 |
|---|---:|---:|---|
| `sva_cards_text.json` | 904 | 1.0 MB | 原始记录（英/中双语） |
| `cards_eng_flat.json` | 982 | 1.0 MB | 扁平化英文，编译器直接消费 |

---

## 剥离的字段

导出时丢弃以下字段，可用 `grep -c "textures/" data/cards/*.json` 验证为 0：

| 类别 | 字段 |
|---|---|
| 图片 / 纹理 | `textures` `skin_names` `card_image_hash` `card_banner_image_hash` |
| 日文 | `name_jpn` `tribe_name_jpn` `text_jpn` `type_jpn` `name_romaji` `text_romaji` |
| 韩文 | `name_kor` `tribe_name_kor` `text_kor` `type_kor` |
| 繁体中文 | `name_cht` `tribe_name_cht` `text_cht` `type_cht` |
| 拼音 | `name_pinyin` `text_pinyin` |

校验：文件内假名（平假名 / 片假名）字符数为 **0**，`.png` 出现次数为 **0**。

---

## sva_cards_text.json

原始 API 记录，逐条保留英/中双语文本。

### 字段

| 字段 | 说明 |
|---|---|
| `card_id` | 卡片 ID |
| `base_card_id` | 基础卡 ID（进化前后同源） |
| `card_set_id` | 卡包 ID |
| `name_eng` `name_chs` | 英文 / 中文卡名 |
| `type` | 卡种（随从 / 法术 / 护符） |
| `class` | 职业 |
| `cost` `atk` `life` `rarity` | 数值 |
| `tribe` `tribe_name_eng` | 兵种 |
| `is_evolution` | 是否进化形态 |
| `evolves_to` | 进化后的 card_id |
| `skills` | 技能 ID 列表 |
| `skill_texts` | **技能文本**，`key` + `text_eng` + `text_chs` |
| `alt_modes` | 抉择分支 |
| `voice_variants` | 语音变体 ID（无音频） |

### skill_texts 示例

```json
{
  "key": "SD_10001110_01",
  "text_eng": "<b><color=Keyword>Enhance</color> (4)</b>: Give this follower +3/+3.",
  "text_chs": "【<color=Keyword>爆能强化</color>_4】本随从+3/+3。"
}
```

---

## cards_eng_flat.json

把 `skill_texts` 按卡合并后的扁平结构，顶层为 `{ "_schema", "cards" }`。

**编译器只读这个文件。**

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

## 重新导出

```python
import json, io

DROP = {
    'textures','skin_names','card_image_hash','card_banner_image_hash',
    'name_jpn','tribe_name_jpn','text_jpn','type_jpn','name_romaji','text_romaji',
    'name_kor','tribe_name_kor','text_kor','type_kor',
    'name_cht','tribe_name_cht','text_cht','type_cht',
    'name_pinyin','text_pinyin',
}

def strip(o):
    if isinstance(o, dict):
        return {k: strip(v) for k, v in o.items() if k not in DROP}
    if isinstance(o, list):
        return [strip(x) for x in o]
    return o

d = json.load(io.open('assets/api/sva_cards_raw.json', encoding='utf-8'))
io.open('data/cards/sva_cards_text.json','w',encoding='utf-8').write(
    json.dumps(strip(d), ensure_ascii=False, indent=1))
```

using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Lang;

// ============================================================================
// 术语表（Glossary）
//
// 数据源：assets/kernel/glossary.json（官方术语表的英文 / 简体中文对照）。
// 渲染器用它把 AST 里的英文关键词翻成中文，保证：
//   · 同一个英文术语在任何地方都渲染成同一个中文词
//   · 术语不是渲染器作者临时编的，而是官方对照表里的
//
// 词表本身允许玩家扩展（DIY 关键词），所以查不到时回落到原文，
// 而不是抛错 —— 渲染是给人看的，不是校验。
// ============================================================================

public sealed class Glossary
{
    private readonly Dictionary<string, string> _enToZh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _enToEn = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _enToZh.Count;

    /// <summary>登记一条术语（英文 -> 中文）。</summary>
    public void Add(string en, string zh)
    {
        if (string.IsNullOrWhiteSpace(en)) return;
        _enToZh[en.Trim()] = (zh ?? "").Trim();
        _enToEn[en.Trim()] = en.Trim();
    }

    /// <summary>英文术语 -> 中文；查不到时原样返回。</summary>
    public string ToChinese(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";
        return _enToZh.TryGetValue(en.Trim(), out var zh) && zh.Length > 0 ? zh : en.Trim();
    }

    /// <summary>规范英文写法（大小写归一）。</summary>
    public string ToEnglish(string en)
    {
        if (string.IsNullOrWhiteSpace(en)) return "";
        return _enToEn.TryGetValue(en.Trim(), out var v) ? v : en.Trim();
    }

    public bool Has(string en) => !string.IsNullOrWhiteSpace(en) && _enToZh.ContainsKey(en.Trim());

    /// <summary>
    /// 官方内置术语。这里只登记渲染器真正会用到的部分：
    /// 关键词、时机头、动作动词、区域名、卡种。
    /// 完整 115 条在 glossary.json 里，玩家可自行扩充。
    /// </summary>
    public static Glossary CreateDefault()
    {
        var g = new Glossary();

        // ---------- 能力关键词（时机头） ----------
        g.Add("Fanfare", "入场曲");
        g.Add("Last Words", "谢幕曲");
        g.Add("Evolve", "进化时");
        g.Add("Super-Evolve", "超进化时");
        g.Add("Strike", "攻击时");
        g.Add("Clash", "交战时");
        g.Add("On Spellboost", "魔力增幅时");
        g.Add("Countdown", "吟唱");
        g.Add("Enhance", "爆能强化");
        g.Add("Accelerate", "激奏");
        g.Add("Crystallize", "结晶");
        g.Add("Fuse", "融合");
        g.Add("Engage", "启动");
        g.Add("Invoke", "瞬念召唤");
        g.Add("Skybound Art", "奥义");
        g.Add("Super Skybound Art", "解放奥义");

        // ---------- 关键词能力 ----------
        g.Add("Ward", "守护");
        g.Add("Storm", "疾驰");
        g.Add("Rush", "突进");
        g.Add("Ambush", "潜行");
        g.Add("Bane", "毁灭");
        g.Add("Drain", "虹吸");
        g.Add("Intimidate", "威慑");
        g.Add("Aura", "灵气");
        g.Add("Barrier", "屏障");

        // ---------- 机制 ----------
        g.Add("Combo", "连击");
        g.Add("Rally", "协作");
        g.Add("Necromancy", "唤灵");
        g.Add("Earth Rite", "土之秘术");
        g.Add("Earth Sigil", "土之印");
        g.Add("Spellboost", "魔力增幅");
        g.Add("Overflow", "觉醒");
        g.Add("Reanimate", "亡者召还");
        g.Add("Mode", "模式");
        g.Add("Crest", "纹章");
        g.Add("Faith", "信仰");
        g.Add("Apocalypse Deck", "启示录牌组");

        // ---------- 动作 ----------
        g.Add("Damage", "伤害");
        g.Add("Restore", "回复");
        g.Add("Draw", "抽取");
        g.Add("Add to Hand", "加入手牌");
        g.Add("Summon", "召唤");
        g.Add("Destroy", "破坏");
        g.Add("Banish", "消失");
        g.Add("Leave the Field", "离场");
        g.Add("Split Damage", "分配伤害");
        g.Add("Select", "选择");
        g.Add("Exact Copy", "复制");
        g.Add("Return to Deck", "返回牌组");
        g.Add("Return to Hand", "返回手牌");
        g.Add("Discard", "舍弃");
        g.Add("Transform", "变身");
        g.Add("Give +X/+Y", "+1/+2");

        // ---------- 卡种与区域 ----------
        g.Add("Follower", "随从");
        g.Add("Spell", "法术");
        g.Add("Amulet", "护符");
        g.Add("Token", "附属卡牌");
        g.Add("Leader", "主战者");
        g.Add("Deck", "牌组");
        g.Add("Field", "战场");
        g.Add("Hand", "手牌");
        g.Add("Cemetery", "墓场");
        g.Add("Banished", "消失区");
        g.Add("Ex Area", "额外区");
        g.Add("Crest Area", "纹章区");
        g.Add("Play Point", "能量点");
        g.Add("EP (Evolution Point)", "进化点");
        g.Add("SEP (Super-Evolution Point)", "超进化点");
        g.Add("Cost", "费用");
        g.Add("Attack (Stat)", "攻击力");
        g.Add("Defense", "生命值");

        // ---------- 渲染器用到的句式词 ----------
        g.Add("Deal", "造成");
        g.Add("Give", "给予");
        g.Add("Change", "变更");
        g.Add("Replace", "替换");
        g.Add("Invoke", "瞬念召唤");
        g.Add("Fully restore", "完全回复");
        g.Add("Remove", "移除");
        g.Add("Move", "移动");
        g.Add("Repeat", "重复");
        g.Add("For each", "对每个");
        g.Add("Do this", "执行");
        g.Add("times", "次");
        g.Add("time", "次");
        g.Add("otherwise", "否则");
        g.Add("copies of", "份");
        g.Add("the previous card", "上一张卡");
        g.Add("the created card", "生成的卡");
        g.Add("the triggering card", "触发本次能力的卡");
        g.Add("the opposing follower", "对方的随从");
        g.Add("this follower", "此随从");
        g.Add("it", "它");
        g.Add("a", "一个");
        g.Add("allied", "己方");
        g.Add("enemy", "敌方");
        g.Add("random", "随机");
        g.Add("differently named", "不同名");
        g.Add("on the field", "战场上的");
        g.Add("in your hand", "手牌中的");
        g.Add("from your deck", "牌组中的");
        g.Add("with the highest", "最高");
        g.Add("all", "全部");
        g.Add("card", "卡牌");
        g.Add("cards", "卡牌");
        g.Add("into", "变为");
        g.Add("Select a Mode to activate", "选择1个模式发动");
        g.Add("Activate", "发动");
        g.Add("random abilities from the following", "从以下随机能力");
        g.Add("Activate an ability in sequence from the following", "从以下按顺序发动能力");
        g.Add("Fully restore", "完全回复");
        g.Add("Super-evolve", "超进化");
        g.Add("the number of", "数量");
        g.Add("the total", "总计");
        g.Add("true", "成立");
        g.Add("pp", "能量点");
        g.Add("ep", "进化点");
        g.Add("countdown", "吟唱");
        g.Add("Attack", "攻击力");
        g.Add("Defense", "生命值");
        g.Add("Cost", "费用");
        g.Add("Life", "生命值");
        g.Add("BaseCost", "基础费用");
        g.Add("max_pp", "最大能量点");
        g.Add("shadow", "墓场");
        g.Add("combo", "连击");
        g.Add("earth_sigil", "土之印");
        g.Add("Enter the Field", "进入战场时");
        g.Add("Leave the Field", "离场时");
        g.Add("Damaged", "受到伤害时");
        g.Add("Healed", "回复时");
        g.Add("Summon", "召唤");
        g.Add("Battle Start", "对战开始时");
        g.Add("Counter Change", "计数器变化时");

        // ---------- 状态词 ----------
        g.Add("Evolved", "已进化");
        g.Add("SuperEvolved", "已超进化");
        g.Add("Damaged", "已受伤");
        g.Add("Silenced", "已沉默");
        g.Add("AttackedThisTurn", "本回合已攻击");
        g.Add("EnteredThisTurn", "本回合入场");
        g.Add("CostModified", "费用已变更");
        g.Add("HasLastWords", "拥有谢幕曲");
        g.Add("HasOnSpellboost", "拥有魔力增幅");
        g.Add("Destroyed", "已被破坏");
        g.Add("AttackedLeaderLastTurn", "上回合攻击过主战者");

        return g;
    }
}

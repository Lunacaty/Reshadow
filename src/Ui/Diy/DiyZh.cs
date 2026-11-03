using System;
using System.Collections.Generic;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// 积木编辑器用的中文词汇表。
///
/// 存在的理由：玩家不该对着 `OnPlay`、`Ge`、`ZoneScope.Opponent` 手敲英文。
/// 这些枚举原本直接裸露在检视器的文本/数字框里，等于把内存结构体当表单。
/// 这里只做「枚举值 ⇄ 中文标签」的映射，不碰业务逻辑。
/// </summary>
public static class DiyZh
{
    /// <summary>下拉项：(存进 Block 的值, 显示给玩家的中文)。</summary>
    public static readonly (string Key, string Zh)[] Events =
    {
        ("OnPlay", "【入场曲】打出时"),
        ("OnEnterField", "【进入战场时】"),
        ("OnDestroy", "【谢幕曲】被破坏时"),
        ("OnLeaveField", "【离场时】"),
        ("OnBanish", "【被消失时】"),
        ("OnSummon", "【被召唤时】"),
        ("OnDraw", "【抽牌时】"),
        ("OnDiscard", "【被舍弃时】"),
        ("OnReturnToHand", "【返回手牌时】"),
        ("OnAddToHand", "【加入手牌时】"),
        ("OnAttack", "【攻击时】"),
        ("OnClash", "【交战时】"),
        ("OnDamaged", "【受到伤害时】"),
        ("OnHealed", "【回复生命时】"),
        ("OnEvolve", "【进化时】"),
        ("OnSuperEvolve", "【超进化时】"),
        ("OnTransform", "【变身时】"),
        ("OnEngage", "【启动能力时】"),
        ("OnFuse", "【融合时】"),
        ("OnSpellboost", "【魔力增幅时】"),
        ("OnCountdownExpire", "【吟唱结束时】"),
        ("OnSelectMode", "【选择模式时】"),
        ("OnEarthRite", "【土之秘术时】"),
        ("OnDeckOut", "【牌组耗尽时】"),
        ("TurnStart", "【回合开始时】"),
        ("TurnEnd", "【回合结束时】"),
        ("BattleStart", "【战斗开始时】"),
        ("PassiveAura", "【常驻光环】"),
    };

    public static readonly (string Key, string Zh)[] Zones =
    {
        ("Board", "战场"),
        ("Hand", "手牌"),
        ("Deck", "牌组"),
        ("Cemetery", "墓场"),
        ("Banished", "放逐区"),
        ("ExArea", "额外区"),
        ("CrestArea", "纹章区"),
        ("Leader", "主战者"),
    };

    public static readonly (string Key, string Zh)[] Owners =
    {
        ("Self", "我方"),
        ("Opponent", "敌方"),
        ("Any", "双方"),
    };

    public static readonly (string Key, string Zh)[] SelectModes =
    {
        ("All", "全部"),
        ("Choose", "手动选择"),
        ("Random", "随机"),
        ("First", "第一个"),
    };

    public static readonly (string Key, string Zh)[] Kinds =
    {
        ("Follower", "随从"),
        ("Spell", "法术"),
        ("Amulet", "护符"),
        ("Crest", "纹章"),
        ("Faith", "信仰"),
    };

    public static readonly (string Key, string Zh)[] Keywords =
    {
        ("ward", "守护"),
        ("bane", "必杀"),
        ("drain", "吸血"),
        ("rush", "突进"),
        ("storm", "疾驰"),
        ("ambush", "潜伏"),
        ("barrier", "护盾"),
        ("immune_effects", "不受能力影响"),
        ("immune_effects_except_stats", "不受能力影响（身材除外）"),
        ("free_evolve", "免费进化"),
        ("free_super_evolve", "免费超进化"),
        ("multi_attack", "多次攻击"),
        ("intimidate", "威吓"),
    };

    public static readonly (string Key, string Zh)[] Counters =
    {
        ("pp", "能量点"),
        ("pp_at_play", "打出时能量点"),
        ("combo", "连击数"),
        ("rally", "协作数"),
        ("shadow", "墓场数"),
        ("evolve_count", "进化次数"),
        ("faith", "信仰值"),
        ("earth_sigil", "土之印"),
        ("clues", "线索"),
        ("wrath", "血怒"),
    };

    public static readonly (string Key, string Zh)[] Stats =
    {
        ("Attack", "攻击力"),
        ("Life", "生命值"),
        ("Cost", "费用"),
        ("BaseAttack", "基础攻击力"),
        ("BaseLife", "基础生命值"),
    };

    public static readonly (string Key, string Zh)[] Compares =
    {
        ("Ge", "≥ 大于等于"),
        ("Gt", "＞ 大于"),
        ("Le", "≤ 小于等于"),
        ("Lt", "＜ 小于"),
        ("Eq", "= 等于"),
        ("Ne", "≠ 不等于"),
    };

    public static readonly (string Key, string Zh)[] StatOps =
    {
        ("Add", "增减"),
        ("Set", "设为"),
    };

    public static readonly (string Key, string Zh)[] CostOps =
    {
        ("Add", "增减"),
        ("Set", "设为"),
        ("Half", "减半"),
    };

    public static readonly (string Key, string Zh)[] KeywordOps =
    {
        ("Grant", "给予"),
        ("Remove", "移除"),
    };

    public static readonly (string Key, string Zh)[] Durations =
    {
        ("Permanent", "永久"),
        ("ThisTurn", "本回合"),
        ("NextTurn", "下回合"),
    };

    public static readonly (string Key, string Zh)[] DamageAuraKinds =
    {
        ("TakenDelta", "受到的伤害增减"),
        ("TakenCap", "受到的伤害不能超过"),
        ("DealtDelta", "造成的伤害增减"),
    };

    public static readonly (string Key, string Zh)[] SourceMasks =
    {
        ("All", "全部伤害"),
        ("Combat", "仅战斗伤害"),
        ("Ability", "仅能力伤害"),
    };

    public static readonly (string Key, string Zh)[] ModeKinds =
    {
        ("Choose", "玩家抉择"),
        ("Random", "随机选"),
        ("Sequence", "依次执行"),
        ("RandomUnused", "随机未使用"),
        ("SequenceUnused", "依次未使用"),
    };

    public static readonly (string Key, string Zh)[] ArithOps =
    {
        ("Add", "＋ 加"),
        ("Sub", "－ 减"),
        ("Mul", "× 乘"),
        ("Div", "÷ 除"),
    };

    public static readonly (string Key, string Zh)[] LoopKinds =
    {
        ("Times", "重复 N 次"),
        ("ForEach", "对每个目标"),
        ("RepeatUntil", "重复直到"),
    };

    public static readonly (string Key, string Zh)[] States =
    {
        ("None", "（无）"),
        ("Evolved", "已进化"),
        ("SuperEvolved", "已超进化"),
        ("Damaged", "已受伤"),
    };

    /// <summary>把内部值翻成中文标签；没登记的返回原值，不印空白。</summary>
    public static string Zh((string Key, string Zh)[] table, string key)
    {
        if (table == null || key == null) return key ?? "";
        foreach (var (k, z) in table)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return z;
        return key;
    }

    /// <summary>反查：中文标签 → 内部值（下拉框回写用）。</summary>
    public static int IndexOf((string Key, string Zh)[] table, string key)
    {
        if (table == null || key == null) return -1;
        for (int i = 0; i < table.Length; i++)
            if (string.Equals(table[i].Key, key, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}

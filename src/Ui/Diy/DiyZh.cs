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
    /// <summary>
    /// 事件（触发时机）词表。
    ///
    /// **以卡面为准**（ChineseCardPrinter 的触发头写法）。积木端不另立一套叫法 ——
    /// 同一个人在积木里选的词和最后印到卡面上的词必须是同一个，
    /// 否则玩家选「【入场曲】打出时」而卡面印「【入场曲】」，纯属冗余。
    /// </summary>
    public static readonly (string Key, string Zh)[] Events =
    {
        ("OnPlay", "【入场曲】"),
        ("OnEnterField", "【进入战场时】"),
        ("OnDestroy", "【谢幕曲】"),
        ("OnLeaveField", "【离场时】"),
        ("OnBanish", "【消失时】"),
        ("OnSummon", "【召唤时】"),
        ("OnDraw", "【抽牌时】"),
        ("OnDiscard", "【被舍弃时】"),
        ("OnReturnToHand", "【返回手牌时】"),
        ("OnAddToHand", "【加入手牌时】"),
        ("OnAttack", "【攻击时】"),
        ("OnClash", "【交战时】"),
        ("OnDamaged", "【受到伤害时】"),
        ("OnHealed", "【回复时】"),
        ("OnEvolve", "【进化时】"),
        ("OnSuperEvolve", "【超进化时】"),
        ("OnTransform", "【变身时】"),
        ("OnEngage", "【启动】"),
        ("OnFuse", "【融合时】"),
        ("OnSpellboost", "【魔力增幅时】"),
        ("OnCountdownExpire", "【吟唱归零时】"),
        ("OnSelectMode", "【选择模式时】"),
        ("OnEarthRite", "【土之秘术时】"),
        ("OnCounterChange", "【计数器变化时】"),
        ("OnDeckOut", "【牌组抽空时】"),
        ("TurnStart", "【回合开始时】"),
        ("TurnEnd", "【回合结束时】"),
        ("BattleStart", "【对战开始时】"),
        ("PassiveAura", "【常驻光环】"),
        ("OnWatchEvent", "【旁观监听】"),
    };

    /// <summary>目标槽位（tgt_slot 的 Text 字段）。</summary>
    public static readonly (string Key, string Zh)[] Slots =
    {
        ("$self", "本卡牌"),
        ("$selected", "刚选中的目标"),
        ("$created", "刚生成的卡牌"),
        ("$trigger", "触发源卡牌"),
        ("$opposing", "交战敌方随从"),
    };

    /// <summary>
    /// 区域词表。**顺序必须与 ZoneCode 完全一致**。
    ///
    /// 早先这里按「战场/手牌/牌组/墓场/放逐/额外区/纹章区/主战者」排，
    /// 而 ZoneCode 是 Leader=5 / CrestArea=6 / ExArea=7 ——
    /// 玩家选「额外区」实际编译成「主战者」，选「主战者」编成「额外区」，
    /// 而且界面上看不出任何异常。改这里之前先看 ZoneCode。
    /// </summary>
    public static readonly (string Key, string Zh)[] Zones =
    {
        ("Board", "战场"),
        ("Hand", "手牌"),
        ("Deck", "牌组"),
        ("Cemetery", "墓场"),
        ("Banished", "消失区"),
        ("Leader", "主战者"),
        ("CrestArea", "纹章区"),
        ("ExArea", "额外区"),
        ("CardPool", "当前卡池"),
        ("GlobalPool", "全部卡池"),
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
        ("bane", "毁灭"),
        ("drain", "虹吸"),
        ("rush", "突进"),
        ("storm", "疾驰"),
        ("ambush", "潜行"),
        ("barrier", "屏障"),
        ("immune_effects", "不受能力影响"),
        ("immune_effects_except_stats", "不受能力影响（攻防变化除外）"),
        ("free_evolve", "无需进化点即可进化"),
        ("free_super_evolve", "无需超进化点即可超进化"),
        // 引擎侧的规范名是 max_attacks_per_turn（见 GameSchema.BuiltinKeywords
        // 与 CombatResolver.CanAttack）。写 multi_attack 会注册一个引擎不认识的关键词，
        // 卡面显示「多次攻击」但实战仍然只能攻击 1 次。
        ("max_attacks_per_turn", "1回合可攻击"),
        ("damage_cap", "受到伤害上限"),
        ("damage_taken_bonus", "受到伤害增加"),
        ("intimidate", "威慑"),
    };

    /// <summary>
    /// 内置计数器词表。
    ///
    /// 这份表只是「常用项」，真正的全集在 GameSchema.Counters.All ——
    /// 检视器会把两者合并，所以玩家在卡池里自定义的计数器也能选到。
    /// 漏项的表现是「想选某个计数器，下拉框里根本没有」。
    /// </summary>
    public static readonly (string Key, string Zh)[] Counters =
    {
        ("pp", "能量点"),
        ("pp_at_play", "打出时能量点"),
        ("max_pp", "最大能量点"),
        ("ep", "进化点"),
        ("sep", "超进化点"),
        ("combo", "连击"),
        ("rally", "协作"),
        ("shadow", "墓场"),
        ("evolve_count", "进化次数"),
        ("faith", "信仰"),
        ("earth_sigil", "土之印"),
        ("clues", "证据"),
        ("wrath", "血怒"),
        ("crest", "纹章"),
        ("countdown", "吟唱"),
        ("spellboost", "魔力增幅"),
        ("skybound_gauge", "奥义值"),
        ("mode_count", "可选模式数"),
        ("cards_returned_to_hand", "本回合返回手牌数"),
        ("fuse_kinds", "融合材料种类数"),
    };

    /// <summary>可读属性。顺序与 StatKey 的重要程度对齐；
    /// 编译器用 Enum.TryParse 覆盖全部 StatKey，这里缺项就等于玩家选不到。</summary>
    public static readonly (string Key, string Zh)[] Stats =
    {
        ("Attack", "攻击力"),
        ("Life", "生命值"),
        ("Cost", "费用"),
        ("BaseAttack", "基础攻击力"),
        ("BaseLife", "基础生命值"),
        ("BaseCost", "基础费用"),
        ("MaxLife", "生命值上限"),
        ("Countdown", "吟唱计数"),
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
        ("Halve", "减半"),
    };

    public static readonly (string Key, string Zh)[] KeywordOps =
    {
        ("Grant", "给予"),
        ("Remove", "移除"),
    };

    /// <summary>
    /// 持续时间。键必须与 DurationKind 枚举逐字一致 ——
    /// 早先写的是 NextTurn，而枚举里叫 UntilEndOfOpponentTurn，
    /// Enum.TryParse 失败后静默回落成 Permanent，
    /// 于是「本回合 +2/+2」变成「永久 +2/+2」。
    /// </summary>
    public static readonly (string Key, string Zh)[] Durations =
    {
        ("Permanent", "永久"),
        ("ThisTurn", "本回合"),
        ("UntilEndOfOpponentTurn", "直到敌方回合结束"),
        ("ThisMatch", "本局对战"),
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
        ("Mod", "％ 取余"),
    };

    public static readonly (string Key, string Zh)[] LoopKinds =
    {
        ("Times", "重复 N 次"),
        ("ForEach", "对每个目标"),
        ("RepeatUntil", "重复直到"),
    };

    /// <summary>可选状态。必须与 DiyBlockCompiler 的 bool_has_state 分支一一对应，
    /// 漏一项就意味着那个状态在积木端永远选不到（编译器支持 6 种，这里也必须是 6 种）。</summary>
    public static readonly (string Key, string Zh)[] States =
    {
        ("Evolved", "已进化"),
        ("SuperEvolved", "已超进化"),
        ("Damaged", "已受伤"),
        ("HasFuse", "带有融合"),
        ("HasLastWords", "带有谢幕曲"),
        ("HasOnSpellboost", "带有魔力增幅"),
    };


    /// <summary>目标孔位的三种来源。</summary>
    public static readonly (string Key, string Zh)[] TargetOps =
    {
        ("tgt_slot", "从槽位取（本卡 / 已选 / 生成物）"),
        ("tgt_leader", "主战者"),
        ("tgt_query", "从区域筛选（战场 / 手牌 / 牌组…）"),
    };

    /// <summary>数值孔位的六种形态。</summary>
    public static readonly (string Key, string Zh)[] NumOps =
    {
        ("num_const", "固定数值"),
        ("num_rand", "锁定随机数"),
        ("num_counter", "计数器 / 能量点"),
        ("num_prop", "属性读取（攻击力 / 生命值 / 费用）"),
        ("num_count", "统计目标数量"),
        ("num_arith", "四则运算"),
    };

    /// <summary>条件孔位的九种形态。</summary>
    public static readonly (string Key, string Zh)[] CondOps =
    {
        ("bool_compare", "数值比较（≥ ≤ = …）"),
        ("bool_exists", "存在满足条件的单位"),
        ("bool_has_kw", "拥有某关键词"),
        ("bool_has_trait", "拥有某兵种"),
        ("bool_has_clan", "属于某职业"),
        ("bool_has_state", "处于某状态（已进化…）"),
        ("bool_has_card_name", "卡名为指定名称/同名"),
        ("bool_has_pack", "属于指定卡包"),
        ("bool_and", "同时满足全部"),
        ("bool_or", "满足任意一个"),
        ("bool_not", "不满足"),
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

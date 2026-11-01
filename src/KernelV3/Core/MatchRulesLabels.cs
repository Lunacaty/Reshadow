using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

/// <summary>
/// MatchRules 字段名 → 简中显示名。
///
/// 放在 Core 而不是 UI：这是规则的“术语”，不是界面文案。
/// 与 MatchRules 本身同目录，加字段时一眼就能看到需要同步登记，
/// 而且卡池配置器、调试面板、日志导出都能共用同一份。
///</summary>
public static class MatchRulesLabels
{
    private static readonly Dictionary<string, string> Zh = new()
    {
        ["LeaderInitialLife"] = "主战者初始生命",
        ["LeaderMaxLife"] = "主战者生命上限",
        ["MaxBoardSize"] = "场上随从上限",
        ["MaxHandSize"] = "手牌上限",
        ["MaxCrestSize"] = "纹章槽数",
        ["InitialMaxPp"] = "初始能量点上限",
        ["PpGainPerTurn"] = "每回合涨费",
        ["MaxPpCap"] = "能量点硬上限",
        ["OverflowThreshold"] = "觉醒阈值",
        ["EnableExtraPpForSecondPlayer"] = "后手额外能量点",
        ["ExtraPpEarlyPhaseLastTurn"] = "额外能量点前半段截止回合",
        ["StartingHandSize"] = "起手抽牌数",
        ["CardsDrawnPerTurn"] = "每回合抽牌数",
        ["FirstPlayerDrawsOnTurn1"] = "先手第1回合抽牌",
        ["EvolveUnlockTurnFirstPlayer"] = "先手进化解锁回合",
        ["EvolveUnlockTurnSecondPlayer"] = "后手进化解锁回合",
        ["EvolveBonusAttack"] = "进化攻击加成",
        ["EvolveBonusLife"] = "进化生命加成",
        ["SuperEvolveUnlockTurnFirstPlayer"] = "先手超进化解锁回合",
        ["SuperEvolveUnlockTurnSecondPlayer"] = "后手超进化解锁回合",
        ["SuperEvolveBonusAttack"] = "超进化攻击加成",
        ["SuperEvolveBonusLife"] = "超进化生命加成",
        ["SuperEvolveOverflowDamage"] = "超进化溢出伤害",
        ["SkyboundArtThreshold"] = "奥义阈值",
        ["SuperSkyboundArtThreshold"] = "真奥义阈值",
        ["InitialEpFirstPlayer"] = "先手初始进化点",
        ["InitialEpSecondPlayer"] = "后手初始进化点",
        ["MaxEpCap"] = "进化点上限",
        ["InitialSepFirstPlayer"] = "先手初始超进化点",
        ["InitialSepSecondPlayer"] = "后手初始超进化点",
        ["MaxSepCap"] = "超进化点上限",
        ["MinDeckSize"] = "构筑张数下限",
        ["MaxDeckSize"] = "构筑张数上限",
        ["MaxCopiesPerCard"] = "同名卡张数上限",
        ["AllowCrossCraft"] = "允许跨职业",
        ["AllowTokensInConstructedDeck"] = "允许衍生卡进构筑",
    };

    /// <summary>英文字段名 → 简中；未登记的返回原名（不静默丢信息）。</summary>
    public static string Of(string propName)
        => string.IsNullOrEmpty(propName) ? ""
         : Zh.TryGetValue(propName, out var zh) ? zh : propName;

    /// <summary>已登记的中文名条数（供门禁核对覆盖度）。</summary>
    public static int Count => Zh.Count;

    public static bool Has(string propName) => !string.IsNullOrEmpty(propName) && Zh.ContainsKey(propName);
}
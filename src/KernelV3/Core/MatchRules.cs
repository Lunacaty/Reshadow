using System;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// 卡池级自定义对局与构筑规则（Match & Deck Construction Rules）
//
// 为什么必须把规则抽成数据：
//   之前 20 血、5 格、7 觉醒、10 费上限、第 5 回合解锁进化……
//   全部散落在 TurnRunner / MatchRunner / CombatResolver / 编译器里写死。
//   一旦某个 DIY 卡池想改其中任何一条，就要去改引擎源码 ——
//   而 DIY 的前提恰恰是「不改引擎」。
//
// 默认值严格对齐《影之诗：超凡世界》官方标准规则：
//   所有现有测试用 MatchRules.CreateStandard() 跑，行为完全不变。
// ============================================================================
public sealed class MatchRules
{
    // ---- 1. 主战者与区域容量 ----
    public int LeaderInitialLife { get; init; } = 20;
    public int LeaderMaxLife { get; init; } = 20;
    public int MaxBoardSize { get; init; } = 5;
    public int MaxHandSize { get; init; } = 9;
    public int MaxCrestSize { get; init; } = 5;

    // ---- 2. 能量点 (PP) 与觉醒规则 ----
    public int InitialMaxPp { get; init; } = 0;
    public int PpGainPerTurn { get; init; } = 1;
    public int MaxPpCap { get; init; } = 10;

    /// <summary>觉醒（Overflow）阈值：最大能量点达到该值即视为觉醒。</summary>
    public int OverflowThreshold { get; init; } = 7;

    // ---- 3. 后手额外能量点 ----
    public bool EnableExtraPpForSecondPlayer { get; init; } = true;

    /// <summary>后手多得 1 点能量的分界回合：不超过该回合时给 1 次，之后给 1 次。</summary>
    public int ExtraPpEarlyPhaseLastTurn { get; init; } = 5;

    // ---- 4. 抽牌规则 ----
    public int StartingHandSize { get; init; } = 4;
    public int CardsDrawnPerTurn { get; init; } = 1;

    /// <summary>
    /// 先手第 1 回合是否抽牌。
    ///
    /// 默认 true —— 与改造前引擎的既有行为**逐字一致**。
    /// 这不是「官方是不是这样」的问题：Step 1 的硬要求是
    /// 「默认 MatchRules.CreateStandard() 保证现有所有测试 100% 零修改全绿」。
    /// 改成 false 会让 4 个既有文档验收当场失败。
    /// </summary>
    public bool FirstPlayerDrawsOnTurn1 { get; init; } = true;

    // ---- 5. 普通进化 (Evolve) ----
    public int InitialEpFirstPlayer { get; init; } = 2;
    public int InitialEpSecondPlayer { get; init; } = 2;
    public int MaxEpCap { get; init; } = 2;
    public int EvolveUnlockTurnFirstPlayer { get; init; } = 5;
    public int EvolveUnlockTurnSecondPlayer { get; init; } = 4;
    public int EvolveBonusAttack { get; init; } = 2;
    public int EvolveBonusLife { get; init; } = 2;

    // ---- 6. 超进化 (Super-Evolve) ----
    public int InitialSepFirstPlayer { get; init; } = 2;
    public int InitialSepSecondPlayer { get; init; } = 2;
    public int MaxSepCap { get; init; } = 2;
    public int SuperEvolveUnlockTurnFirstPlayer { get; init; } = 7;
    public int SuperEvolveUnlockTurnSecondPlayer { get; init; } = 6;
    public int SuperEvolveBonusAttack { get; init; } = 3;
    public int SuperEvolveBonusLife { get; init; } = 3;

    /// <summary>超进化随从击杀随从时，对敌方主战者造成的溢出伤害。</summary>
    public int SuperEvolveOverflowDamage { get; init; } = 1;

    // ---- 7. 奥义阈值 ----
    public int SkyboundArtThreshold { get; init; } = 10;
    public int SuperSkyboundArtThreshold { get; init; } = 15;

    // ---- 8. 局外构筑 ----
    public int MinDeckSize { get; init; } = 40;
    public int MaxDeckSize { get; init; } = 40;
    public int MaxCopiesPerCard { get; init; } = 3;

    /// <summary>是否允许跨职业混组（false = 只能本职业 + 中立）。</summary>
    public bool AllowCrossCraft { get; init; } = false;

    /// <summary>是否允许把衍生 Token 放进构筑牌组。</summary>
    public bool AllowTokensInConstructedDeck { get; init; } = false;

    public static MatchRules CreateStandard() => new();

    /// <summary>某玩家第 N 回合能否手动进化。</summary>
    public int EvolveUnlockTurn(int playerId)
        => playerId == 0 ? EvolveUnlockTurnFirstPlayer : EvolveUnlockTurnSecondPlayer;

    public int SuperEvolveUnlockTurn(int playerId)
        => playerId == 0 ? SuperEvolveUnlockTurnFirstPlayer : SuperEvolveUnlockTurnSecondPlayer;

    public int InitialEp(int playerId)
        => playerId == 0 ? InitialEpFirstPlayer : InitialEpSecondPlayer;

    public int InitialSep(int playerId)
        => playerId == 0 ? InitialSepFirstPlayer : InitialSepSecondPlayer;

    /// <summary>基础校验：数值必须自洽，否则卡池配置一加载就该报错。</summary>
    public void Validate(System.Collections.Generic.List<string> errors)
    {
        if (errors == null) return;
        if (LeaderInitialLife <= 0) errors.Add("LeaderInitialLife 必须为正");
        if (LeaderMaxLife < LeaderInitialLife) errors.Add("LeaderMaxLife 不能小于 LeaderInitialLife");
        if (MaxBoardSize < 1) errors.Add("MaxBoardSize 至少为 1");
        if (MaxHandSize < 1) errors.Add("MaxHandSize 至少为 1");
        if (MaxPpCap < InitialMaxPp) errors.Add("MaxPpCap 不能小于 InitialMaxPp");
        if (PpGainPerTurn < 0) errors.Add("PpGainPerTurn 不能为负");
        if (MinDeckSize < 1) errors.Add("MinDeckSize 至少为 1");
        if (MaxDeckSize < MinDeckSize) errors.Add("MaxDeckSize 不能小于 MinDeckSize");
        if (MaxCopiesPerCard < 1) errors.Add("MaxCopiesPerCard 至少为 1");
    }
}

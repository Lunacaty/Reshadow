using System.Collections.Generic;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;

namespace ShadowverseFan.KernelV3.Packs;

// ============================================================================
// 物理卡包（CardPack）—— 创作者发布的最小物理单元
//
// 一个 pack.json 里装的是该作者创造的**一切**：
// 词条符号（兵种/职业/关键词/计数器）、可堆叠资源规格、具名牌组、卡片。
//
// 「卡包（内容载体）」与「卡池（赛制环境）」是两层：
//   卡包只管「我有什么」，卡池负责「把多个包合流成一个自洽宇宙并拦截冲突」。
// 混在一起的话，一个只想复用别人 Token 的作者就得连别人的整副卡组一起吞进来。
// ============================================================================

/// <summary>卡包里的卡片原始条目（未编译）。</summary>
public sealed class CardRawEntry
{
    /// <summary>
    /// 包内局部编号（字符串）。作者只写这个，由卡池分配全局 int 卡号。
    /// 与 CardId 二选一：给了 CardId 就直接用（作者自留号），否则走分配。
    /// </summary>
    public string LocalId { get; init; } = "";

    public int CardId { get; init; }
    public string NameEng { get; init; } = "";
    public string NameChs { get; init; } = "";
    public CardKind Kind { get; init; } = CardKind.Follower;

    /// <summary>true = 衍生卡（Token），不可直接放进局外构筑。</summary>
    public bool IsToken { get; init; }

    /// <summary>
    /// 立绘来源卡号。0 表示用 CardId 本身。
    ///
    /// 另存新卡 / 卡号冲突改号时，立绘文件仍属于原卡，
    /// 这个字段必须一路落盘，否则重载后新卡去找不存在的图片变黑底。
    /// </summary>
    public int ArtCardId { get; init; }

    public string Clan { get; init; } = "中立";
    public List<string> Traits { get; init; } = new();
    public int Cost { get; init; }
    public int Attack { get; init; }
    public int Life { get; init; }

    /// <summary>效果来源："eng_cnl"（官方英文）或 "reshadow_script"（DIY 脚本）。</summary>
    public string SourceFormat { get; init; } = "eng_cnl";

    public string Text { get; init; } = "";

    /// <summary>
    /// 卡面自带的关键词（ward / storm / rush …）。
    ///
    /// 之前这里没有这个字段，loader 也不读 JSON 的 "keywords"，
    /// 结果从 pack.json 加载的《守护魔像》没了【守护】、《寂静骑士》没了【疾驰】 ——
    /// 而卡面中文完全正常，肉眼查不出来。
    /// </summary>
    public List<string> Keywords { get; init; } = new();

    /// <summary>构筑期修正词条（对局中不生效）。</summary>
    public List<DeckBuildModifier> DeckModifiers { get; init; } = new();

    public List<AltModeRawEntry> AltModes { get; init; } = new();

    /// <summary>稀有度：1=青铜 2=白银 3=黄金 4=虹彩 0=未标注（只影响卡框配色）。</summary>
    public int Rarity { get; init; }
}

public sealed class AltModeRawEntry
{
    public string TypeEng { get; init; } = "";
    public int Cost { get; init; }
    public string TextEng { get; init; } = "";
}

/// <summary>具名牌组的原始条目。成员可跨卡包引用（按卡名或卡号）。</summary>
public sealed class NamedDeckRawDef
{
    public string DeckName { get; init; } = "";
    public string DeckNameChs { get; init; } = "";
    public List<NamedDeckEntryRaw> Entries { get; init; } = new();
}

public sealed class NamedDeckEntryRaw
{
    public int CardId { get; init; }
    public string CardName { get; init; } = "";
    public int Copies { get; init; } = 1;
}

/// <summary>物理卡包定义（对应 packs/&lt;pack_id&gt;/pack.json）。</summary>
public sealed class CardPackDef
{
    public string PackId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Author { get; init; } = "";
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// 原生卡池 ID：本卡包在它的原生卡池里处于锁定（不可踢出）状态。
    /// 这是「卡包不能脱离原生卡池」那条铁律的落点。
    /// </summary>
    public string NativePoolId { get; init; } = "standard";

    public List<string> Dependencies { get; init; } = new();

    // ---- 本包扩展的词条符号 ----
    public List<SymbolRawEntry> Traits { get; init; } = new();
    public List<SymbolRawEntry> Clans { get; init; } = new();
    public List<SymbolRawEntry> Keywords { get; init; } = new();
    public List<CounterRawEntry> Counters { get; init; } = new();

    /// <summary>本包定义的可堆叠资源规格（如土之印、线索）。</summary>
    public List<StackableAmuletSpec> StackableAmulets { get; init; } = new();

    // ---- 本包定义的具名牌组与卡片 ----
    public List<NamedDeckRawDef> NamedDecks { get; init; } = new();
    public List<CardRawEntry> Cards { get; init; } = new();
}

public sealed class SymbolRawEntry
{
    public string Name { get; init; } = "";
    public string NameChs { get; init; } = "";
}

public sealed class CounterRawEntry
{
    public string Name { get; init; } = "";
    public string NameChs { get; init; } = "";
    public int Min { get; init; }
    public int Max { get; init; } = 99;
}

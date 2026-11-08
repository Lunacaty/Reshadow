using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Core;

// ============================================================================
// GameSchema —— 五张符号注册表的容器
//
// 这是「玩家可 DIY 兵种 / 资源 / 关键词」的落点：
//   · 内置词条由引擎在启动时注册（source = "builtin"）
//   · 玩家自造词条由 Mod 注册（source = "mod:<名字>"）
//   · 同名但来源不同 → 抛 SymbolConflictException，绝不静默合并
//
// ★ 关键约定：Lang 层**禁止**裸调 SymbolId<T>.FromName()。
//   FromName 产出的 Id 恒为 -1，会让所有计数器在运行时读写同一个槽位。
//   必须走 GameSchema 的 Register / Require 拿到真实正整数 Id。
// ============================================================================

public sealed class GameSchema
{
    /// <summary>计数器名（不区分大小写） -> (下限, 上限)。上限 -1 表示不限。</summary>
    public Dictionary<string, (int min, int max)> CounterLimits { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 可堆叠护符绑定表：计数器名 → 规格。
    ///
    /// 由卡包在加载时注册（核心包注册 earth_sigil 与 clues）。
    /// BattleState 读写计数器、合并护符、判断保护时都查这张表，
    /// 于是引擎本体不再出现任何具体卡号。
    /// </summary>
    /// <summary>
    /// 本 Schema 所属对局的规则。
    ///
    /// 放在 Schema 上是因为**编译器**也要读阈值（觉醒 7 / 奥义 10/15），
    /// 而编译器只拿得到 Lexicon → Schema，拿不到 BattleState。
    /// 不这样做的话，这些阈值只能写死在编译器里。
    /// </summary>
    public MatchRules Rules { get; set; } = MatchRules.CreateStandard();

    public Dictionary<string, StackableAmuletSpec> StackableAmulets { get; }
        = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>把计数器值钳制到它自己的范围内。</summary>
    public int ClampCounter(SymbolId<CounterTag> ctr, int value)
    {
        if (ctr == null || !CounterLimits.TryGetValue(ctr.Name ?? "", out var lim)) return value;
        if (lim.max >= 0 && value > lim.max) value = lim.max;
        if (value < lim.min) value = lim.min;
        return value;
    }

    public SymbolRegistry<TraitTag> Traits { get; } = new();
    public SymbolRegistry<CounterTag> Counters { get; } = new();
    public SymbolRegistry<KeywordTag> Keywords { get; } = new();
    public SymbolRegistry<ClanTag> Clans { get; } = new();
    public SymbolRegistry<MacroTag> Macros { get; } = new();

    /// <summary>注册一个计数器并返回带真实 Id 的句柄（已在则复用）。</summary>
    public SymbolId<CounterTag> Counter(string name, string source = "builtin")
        => GetOrRegister(Counters, name, source);

    public SymbolId<KeywordTag> Keyword(string name, string source = "builtin")
        => GetOrRegister(Keywords, name, source);

    public SymbolId<TraitTag> Trait(string name, string source = "builtin")
        => GetOrRegister(Traits, name, source);

    public SymbolId<ClanTag> Clan(string name, string source = "builtin")
        => GetOrRegister(Clans, name, source);

    public SymbolId<MacroTag> Macro(string name, string source = "builtin")
        => GetOrRegister(Macros, name, source);

    private static SymbolId<T> GetOrRegister<T>(SymbolRegistry<T> reg, string name, string source)
    {
        if (string.IsNullOrWhiteSpace(name)) return SymbolId<T>.Invalid;
        var existing = reg.TryGet(name);
        return existing.IsValid ? existing : reg.Register(name, source);
    }

    /// <summary>引擎内置的全部资源计数器。</summary>
    /// <summary>
    /// 内置资源计数器的定义：名字 + 下限 + 上限（-1 表示不限）。
    /// 官方规则：能量点上限 10、进化点 2、超进化点 2。
    /// 没有上限约束时，效果会把 pp 写成负数或超量而不被发现。
    /// </summary>
    public static readonly (string name, int min, int max)[] BuiltinCounterDefs =
    {
        ("pp", 0, 10),
        // 打出这张卡之前的 PP。Enhance 分支靠它判定能走哪一档，
        // 因为基础费已经扣掉了。
        ("pp_at_play", 0, 10),
        ("max_pp", 0, 10),
        ("ep", 0, 2),
        ("sep", 0, 2),
        ("countdown", 0, 99),
        ("spellboost", 0, 99),
        ("earth_sigil", 0, 99),
        ("clues", 0, 99),
        ("crest", 0, 99),
        ("faith", 0, 99),
        ("shadow", 0, 999),
        // 狂乱（Wrath）：本局**自己回合内**我方主战者累计受伤次数。
        // 「If you're in Wrath, ...」类条件靠它判定，此前全仓没有任何计数点。
        ("wrath", 0, 999),
        ("X", 0, 999), ("Y", 0, 999), ("Z", 0, 999),
        ("turn_play", 0, 99),
        ("match_play", 0, 999),
        // 连击（Combo）：本回合已打出的卡数，回合开始时清零。
        ("combo", 0, 999),
        // 「我方随从本回合攻击过几次」——`if allied followers didn't attack this turn` 的判据。
        // 用计数器而不是遍历战场：随从攻击后可能马上被消灭，遍历会漏掉。
        ("turn_ally_attacks", 0, 999),
        // 「我方上回合是否有随从攻击过主战者」（《利牙》的替代条件）。
        // 0/1 二值，回合开始时重置并把上一回合的值转存到 last 槽。
        ("ally_attacked_leader_last_turn", 0, 1),
        ("ally_attacked_leader_this_turn", 0, 1),
        // 协作（Rally）：本局对战中已进入过战场的己方随从总数。
        // 不含当前正在上场的这张（入场曲结算完才 +1），护符不算。
        ("rally", 0, 999),
        ("evolve_count", 0, 99),
        // 融合有三个语义完全不同的计数器，卡面用哪个必须逐个对：
        //   fuse_count —— 融合**次数**        「If you've Fused to this card」
        //   fuse_cost  —— 材料**总费用**      「based on the total cost of the cards fused」
        //   fuse_kinds —— **不同卡号的数量**  「Fused both」/「differently named cards Fused」
        // 三者全部挂在**实体上**（baseCard.Counters），不是玩家级：
        // 卡面写的是「Fused to **this card**」，是这张卡各自的累计。
        ("fuse_count", 0, 99),
        ("fuse_cost", 0, 99),
        ("fuse_kinds", 0, 99),
        // 奥义槽 = 当前回合数 + 本卡在手时自己随从进化/超进化的次数。
        // 奥义 >= 10 可发动，解放奥义 >= 15。
        ("skybound_gauge", 0, 99),
        // 以下三个是 ConditionCompiler 会生成的条件计数器。
        // 必须与编译期用同一份内置表：SymbolId 的 Id 由注册顺序分配，
        // 运行期单独注册会整体错位，让卡面 AST 里的 ctr: 读到别的计数器
        // （实测 cards_returned_to_hand 被读成 distinct_base_costs_played）。
        ("distinct_base_costs_played", 0, 99),
        ("ced_engage", 0, 99),
        ("damage_dealt_this_turn", 0, 999),
    };

    public static readonly string[] BuiltinKeywords =
    {
        "ward", "rush", "storm", "bane", "drain", "ambush", "aura",

        // 封印光环：入场曲 / 爆能强化无法发动。
        // 由《米路缇欧&卢泽》纹章授予，运行期在触发与爆能档位两处拦截。
        "suppress_fanfare", "suppress_enhance",
        "intimidate", "barrier", "cannot_attack", "cannot_destroy", "cannot_be_played",
        "attack_limit", "damage_cap", "ignores ward",

        // 带参关键词与派生限制。不注册的话 check_keywords.py 扫不到，
        // 写入端与读取端名字写岔了也没人发现（damage_taken_bonus 就出过这种事）。
        "damage_taken_bonus", "max_attacks_per_turn", "cannot_attack_leaders",
        "cannot_attack_followers", "targeting_restriction",

        // 免点数进化。持有这些关键词的随从，即使 EP / SEP 为 0 也能手动进化，
        // 且进化时不扣点。
        "free_evolve", "free_super_evolve",

        // 绝对免疫家族（一代经典）。五个正交词条：
        //   immune_damage              不受任何伤害
        //   immune_combat_damage       不受战斗伤害
        //   immune_ability_damage      不受能力伤害
        //   immune_effects             不受能力影响（含破坏/消失/变身/沉默/剥夺关键词）
        //   immune_effects_except_stats 同上，但放行攻防加减
        "immune_damage", "immune_combat_damage", "immune_ability_damage",
        "immune_effects", "immune_effects_except_stats",
    };

    /// <summary>构造一份装好内置词条的 schema。</summary>
    public static GameSchema CreateDefault()
    {
        var s = new GameSchema();
        foreach (var (name, min, max) in BuiltinCounterDefs)
        {
            s.Counters.Register(name, "builtin");
            s.CounterLimits[name] = (min, max);
        }
        foreach (var k in BuiltinKeywords) s.Keywords.Register(k, "builtin");
        return s;
    }
}

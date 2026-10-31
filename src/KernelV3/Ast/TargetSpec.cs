using System;
using System.Collections.Generic;

namespace ShadowverseFan.KernelV3.Ast;

// ============================================================================
// 目标选择器 (TargetSpec)
//
// 这是对 Bloodymoon「解析期无法确定目标」那堵墙的正面解法。
//
// V2 的做法是：TargetSpec + static Previous —— 但 Previous 只有单槽位，
// 且存的是活体 Unit 引用，卡一离场就读不到。
//
// V3 的做法（Gemini 第三刀）：
//   · SourceKind 可以是 Zone（从区域捞）或 Slot（从寄存器槽位捞）
//   · 从槽位捞也照样能 Where 二次过滤，这是「对刚抽到的牌再筛」的关键
//   · 槽位里存的是 EntityRef（含 EntrySerial + 快照），不是活体引用
// ============================================================================

/// <summary>
/// TargetSpec 的字段位集，用于记录「哪些字段是解析器兜底填的」。
///
/// 替代句透镜（InsteadWeaver.ApplyLens）只继承置位的字段，
/// 未置位的字段一律保留替代句自己的值 —— 显式写出的目标因此不会被前句污染。
/// </summary>
[Flags]
public enum TargetField
{
    None = 0,
    SourceKind = 1 << 0,
    Owner = 1 << 1,
    Zone = 1 << 2,
    Slot = 1 << 3,
    /// <summary>CardId + CardName。</summary>
    Card = 1 << 4,
    Where = 1 << 5,
    /// <summary>Sort + SortDescending。</summary>
    Sort = 1 << 6,
    TopN = 1 << 7,
    DistinctByName = 1 << 8,
    Mode = 1 << 9,
    Count = 1 << 10,
    History = 1 << 11,
    IncludeLeader = 1 << 12,

    /// <summary>整段目标都是兜底出来的（原文没写目标）。</summary>
    All = SourceKind | Owner | Zone | Slot | Card | Where | Sort | TopN
        | DistinctByName | Mode | Count | History | IncludeLeader,
}

/// <summary>目标候选池从哪来。</summary>
public enum TargetSourceKind
{
    /// <summary>从游戏区域捞（默认）。</summary>
    Zone = 0,
    /// <summary>从寄存器槽位捞（"it" / "them" / "the exact copy"）。</summary>
    Slot,
    /// <summary>指向一个具名卡（用于「召唤某张具体卡」）。</summary>
    CardId,
}

public sealed class TargetSpec
{
    public TargetSourceKind SourceKind { get; init; } = TargetSourceKind.Zone;

    // ---- SourceKind.Zone 时有效 ----
    public Core.ZoneScope Owner { get; init; } = Core.ZoneScope.Any;
    public Core.ZoneRef Zone { get; init; } = Core.ZoneRef.Board;

    // ---- SourceKind.Slot 时有效 ----
    public string Slot { get; init; } = "";

    // ---- SourceKind.CardId 时有效 ----
    public int CardId { get; init; }
    public string CardName { get; init; } = "";

    // ---- 过滤 / 排序 / 选取（Slot 来源同样适用）----
    /// <summary>过滤条件。真值走 BoolExpr，不再是魔法字符串。</summary>
    public BoolExpr Where { get; init; } = BoolExpr.True;
    public Core.SortKey Sort { get; init; } = Core.SortKey.None;
    public bool SortDescending { get; init; }

    /// <summary>取排序后的前 N 项（如「the 3 highest base costs」里的 3）。0 表示不限。</summary>
    public int TopN { get; init; }

    public bool DistinctByName { get; init; }

    /// <summary>
    /// 只保留**重复**的那些（每张卡的第一张留下，多余的取出）。
    ///
    /// 「Banish all duplicates from your deck」用它。
    /// 没有这个标记时，TargetParser 会吃掉 duplicates 这个词却不留痕迹，
    /// 候选池退化成「牌库里的全部卡」—— 一句就把整副牌库放逐光。
    /// </summary>
    public bool DuplicatesOnly { get; init; }
    public Core.SelectMode Mode { get; init; } = Core.SelectMode.All;
    public NumExpr Count { get; init; } = NumExpr.Const(1);
    /// <summary>
    /// 历史池作用域：把候选池从"场上/手牌"切换到"本局/本回合的历史记录"。
    /// 例："Summon a copy of a random allied amulet destroyed this match"。
    /// </summary>
    public Core.HistoryScope History { get; init; } = Core.HistoryScope.None;

    /// <summary>
    /// 是否把主战者纳入候选池。
    /// 影之诗规则：「all enemies」= 敌方随从 + 敌方主战者；
    /// 「all enemy followers」= 仅随从。
    /// </summary>
    public bool IncludeLeader { get; init; }

    /// <summary>
    /// 这个 TargetSpec 里哪些字段是「解析器兜底填的、原文没写」。
    ///
    /// 只有 ParseDeal / ParseRestore 的兜底分支会置位；其余所有构造点
    /// （TargetParser 解析出的、手写的、本类的静态构造）一律保持 None ——
    /// None 的意思是「这些字段由这句话或调用方给出，替代句透镜不得覆写」。
    ///
    /// 为什么不判值：兜底出来的 "deal 4 damage" 与显式写出的
    /// "deal 4 damage to all enemy followers" 在 Mode / Owner / Zone / Count 上
    /// 完全一样，值判等一定会把显式目标改坏。所以判据只能是这个显式标记。
    /// </summary>
    public TargetField OmittedFields { get; init; } = TargetField.None;

    /// <summary>这个目标是不是整段都由解析器兜底出来的（原文压根没写目标）。</summary>
    public bool IsOmitted => OmittedFields != TargetField.None;

    /// <summary>我方主战者。</summary>
    public TargetSpec WithWhere(BoolExpr extra) => new()
    {
        SourceKind = SourceKind, Owner = Owner, Zone = Zone, Slot = Slot,
        CardId = CardId, CardName = CardName, Sort = Sort,
        SortDescending = SortDescending, DistinctByName = DistinctByName, DuplicatesOnly = DuplicatesOnly,
        Mode = Mode, Count = Count,
        IncludeLeader = IncludeLeader,
        OmittedFields = OmittedFields & ~TargetField.Where,
        Where = BoolExpr.And(Where, extra),
    };

    public static TargetSpec SelfLeader => new()
    {
        SourceKind = TargetSourceKind.Zone, Owner = Core.ZoneScope.Self,
        Zone = Core.ZoneRef.Leader, IncludeLeader = false, Mode = Core.SelectMode.All,
    };

    /// <summary>敌方主战者。</summary>
    /// <summary>对方主战者本体。</summary>
    /// <remarks>
    /// IncludeLeader 必须为 false：它的语义是「随从 + 额外包含主战者」。
    /// 目标本就是主战者时再置 true，ParseDeal 会额外追加一条打脸，
    /// 导致同一能力里出现两次完全相同的伤害。
    /// </remarks>
    public static TargetSpec OpponentLeader => new()
    {
        SourceKind = TargetSourceKind.Zone, Owner = Core.ZoneScope.Opponent,
        Zone = Core.ZoneRef.Leader, IncludeLeader = false, Mode = Core.SelectMode.All,
    };

    // ---------- 内置槽位名 ----------
    public const string SlotSelf = "$self";
    public const string SlotTrigger = "$trigger";
    public const string SlotOpposing = "$opposing";
    public const string SlotSelected = "$selected";
    public const string SlotCreated = "$created";

    /// <summary>\u88AB\u64cd\u4F5C\u7684\u8D44\u6E90\u8BA1\u6570\u5668\uFF08\u5982\u300CReduce your faith's value ... to give it\u300D\u91CC\u7684 it\uFF09\u3002</summary>
    public const string SlotCounter = "$counter";
    public const string SlotItem = "$item";

    /// <summary>
    /// "other / another" 的排除槽位**待定**：编译期还不知道这句话落在哪种上下文里。
    ///
    /// 语义事实：other 要排除的是「这句效果的主体视角」，而视角随上下文切换：
    ///   · "Whenever an allied follower enters the field, deal 1 damage to all other followers."
    ///     —— 触发上下文，other 排除**事件主体**（刚进场的那个），不是监听者自己；
    ///   · "Fanfare: Deal 2 damage to another enemy follower." / "Select another follower"
    ///     —— 普通动作上下文，other 排除**本卡自身**。
    ///
    /// 编译期不猜：TargetParser 只在「本句处于触发器从句的效果体里」（DefaultPronounSlot
    /// 已被 EnglishCompiler 置成 $trigger，且当前不是在解析从句主语本身）时写这个待定值，
    /// 其余一律直接写 $self —— 包括触发器从句的**主语**，
    /// "Whenever another allied follower enters the field" 里的 another 指本卡以外，
    /// 排除事件主体会让过滤条件恒假、能力永不触发。
    /// 求值时由 VmEval 按 scope.Trigger 有无所指落定，见 VmEval.NotSameEntityExpr。
    /// </summary>
    public const string SlotExcludeTriggerOrSelf = "$excludeTriggerOrSelf";

    // ---------- 常用构造 ----------

    /// <summary>本卡自身。</summary>
    public static TargetSpec Self => new()
    {
        SourceKind = TargetSourceKind.Slot, Slot = SlotSelf,
        Mode = Core.SelectMode.All,
    };

    /// <summary>上一条动作选中的目标（"it" / "them"）。</summary>
    public static TargetSpec Previous => new()
    {
        SourceKind = TargetSourceKind.Slot, Slot = SlotSelected,
        Mode = Core.SelectMode.All,
    };

    /// <summary>刚生成 / 召唤 / 抽到的卡（"the exact copy" / "followers drawn"）。</summary>
    public static TargetSpec Created => new()
    {
        SourceKind = TargetSourceKind.Slot, Slot = SlotCreated,
        Mode = Core.SelectMode.All,
    };

    /// <summary>循环当前项 / 过滤器里的「当前实体」。</summary>
    public static TargetSpec Item => new()
    {
        SourceKind = TargetSourceKind.Slot, Slot = SlotItem,
        Mode = Core.SelectMode.All,
    };

    /// <summary>触发本次能力的实体。</summary>
    public static TargetSpec Trigger => new()
    {
        SourceKind = TargetSourceKind.Slot, Slot = SlotTrigger,
        Mode = Core.SelectMode.All,
    };

    /// <summary>从某区域取。</summary>
    public static TargetSpec InZone(Core.ZoneRef zone, Core.ZoneScope owner = Core.ZoneScope.Self)
        => new() { Zone = zone, Owner = owner, Mode = Core.SelectMode.All };

    /// <summary>从某区域随机取 N 个。</summary>
    public static TargetSpec RandomInZone(Core.ZoneRef zone, Core.ZoneScope owner, int n)
        => new() { Zone = zone, Owner = owner, Mode = Core.SelectMode.Random, Count = NumExpr.Const(n) };

    /// <summary>从某区域由玩家选 N 个。</summary>
    public static TargetSpec ChooseInZone(Core.ZoneRef zone, Core.ZoneScope owner, int n)
        => new() { Zone = zone, Owner = owner, Mode = Core.SelectMode.Choose, Count = NumExpr.Const(n) };
}

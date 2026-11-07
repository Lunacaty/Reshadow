using System;
using System.Collections.Generic;
using ShadowverseFan.KernelV3.Ast;

namespace ShadowverseFan.KernelV3.Diy;

/// <summary>
/// 积木树的数据层操纵器：建默认积木、在任意嵌套层级里定位与增删移动。
///
/// 与 UI 完全解耦 —— 这里只碰 Block 纯数据，可以在无头环境里直接单测。
///
/// 为什么需要它：原来的 AddActionBlock / MoveActionBlock / RemoveActionBlock
/// 只在 hat.Stacks 顶层做 IndexOf，动作块一旦嵌进 if-else、repeat 或 mode 分支，
/// 上下移、删除、复制全部失效。
/// </summary>
public static class DiyBlockTree
{
    private static Block ConstNum(int v) => new() { Opcode = "num_const", IntA = v };

    private static Block SelfSlot() => new() { Opcode = "tgt_slot", Text = TargetSpec.SlotSelf };

    private static Block EnemyFollower() => new()
    {
        Opcode = "tgt_query", IntA = 0, IntB = 1, Text = "Follower",
        EnumA = "Choose", Number = ConstNum(1),
    };

    /// <summary>按 opcode 造一颗带合理默认孔位的新积木（调色盘点击时用）。</summary>
    public static Block CreateDefaultBlock(string opcode)
    {
        switch (opcode)
        {
            // ---- 事件帽 ----
            case "hat_self_event":
                return new Block { Opcode = opcode, Text = "OnPlay" };
            case "hat_watch_event":
                return new Block
                {
                    Opcode = opcode, Text = "OnEnterField",
                    Target = new Block { Opcode = "tgt_query", IntA = 0, IntB = 0, Text = "Follower", EnumA = "All" },
                };
            case "hat_passive":
                return new Block { Opcode = opcode, Text = "PassiveAura" };

            // ---- 控制流 ----
            case "c_if_else":
                return new Block
                {
                    Opcode = opcode,
                    Condition = new Block
                    {
                        Opcode = "bool_compare", EnumA = "Ge",
                        Number = new Block { Opcode = "num_counter", Text = "combo" },
                        Number2 = ConstNum(3),
                    },
                };
            case "c_repeat":
                return new Block { Opcode = opcode, Number = ConstNum(2) };
            // 180 号文档 D7：从调色盘点「+ 重复直到…」时必须带一个可用的
            // 默认停止条件，否则新积木的 Condition 为空，编译直接报「缺少条件」。
            case "c_repeat_until":
                return new Block
                {
                    Opcode = opcode,
                    Condition = new Block
                    {
                        Opcode = "bool_compare", EnumA = "Ge",
                        Number = new Block { Opcode = "num_counter", Text = "combo" },
                        Number2 = ConstNum(3),
                    },
                };
            case "c_for_each":
                return new Block { Opcode = opcode, Target = EnemyFollower() };
            case "c_mode":
                return new Block
                {
                    Opcode = opcode, EnumA = "Choose", IntA = 1, Number = ConstNum(1),
                    Branches = new List<List<Block>> { new(), new() },
                };

            // ---- 伤害 / 回复 ----
            case "act_damage":
            case "act_split_damage":
                return new Block { Opcode = opcode, Target = EnemyFollower(), Number = ConstNum(2) };
            case "act_restore":
                return new Block
                {
                    Opcode = opcode,
                    Target = new Block { Opcode = "tgt_leader", EnumA = "Self" },
                    Number = ConstNum(2),
                };
            case "act_full_restore":
            case "act_evolve":
            case "act_super_evolve":
            case "act_reveal":
            case "act_swap_stats":
                return new Block { Opcode = opcode, Target = SelfSlot() };

            // ---- 抽牌 / 召唤 / 发现 ----
            case "act_draw":
                return new Block { Opcode = opcode, Number = ConstNum(1) };
            case "act_summon":
            case "act_add_hand":
                // 默认给一个真实卡号（妖精 90011110）：只填卡名的话编译出的 CardId=0，
                // 打出去召唤的是个白板空壳，而卡面看起来完全正常。
                return new Block { Opcode = opcode, IntB = 90011110, Number = ConstNum(1) };
            case "act_gain_crest":
                return new Block { Opcode = opcode, IntB = 90011110, Text = "Aria, Lady of the Woods", EnumA = "Self" };
            case "act_copy":
                return new Block
                {
                    Opcode = opcode, Target = SelfSlot(), FlagA = true,
                    Number = ConstNum(1), EnumA = "Board",
                };
            case "act_discover":
                return new Block
                {
                    Opcode = opcode, IntA = 1, IntB = 3, EnumA = "hand",
                    Target = new Block { Opcode = "tgt_query", IntA = 8, IntB = 0, Text = "Follower", EnumA = "Choose" },
                };
            case "act_reanimate":
                return new Block { Opcode = opcode, Number = ConstNum(3) };
            case "act_replace_deck":
                return new Block { Opcode = opcode, Text = "Apocalypse Deck" };

            // ---- 解场 / 移区 ----
            case "act_select":
                return new Block { Opcode = opcode, Target = EnemyFollower() };
            case "act_destroy":
            case "act_banish":
            case "act_bounce":
            case "act_return_deck":
            case "act_silence":
                return new Block { Opcode = opcode, Target = EnemyFollower() };
            case "act_discard":
                return new Block
                {
                    Opcode = opcode,
                    Target = new Block { Opcode = "tgt_query", IntA = 1, IntB = 0, EnumA = "Choose", Number = ConstNum(1) },
                };
            case "act_transform":
                return new Block { Opcode = opcode, Target = EnemyFollower(), IntB = 90011110, Text = "Fairy" };
            case "act_move_to_board":
                return new Block { Opcode = opcode, Target = SelfSlot() };

            // ---- 身材 / 费用 / 关键词 ----
            case "act_buff":
            case "act_set_stats":
                return new Block { Opcode = opcode, Target = SelfSlot(), Number = ConstNum(1), Number2 = ConstNum(1) };
            case "act_mod_cost":
                return new Block { Opcode = opcode, Target = SelfSlot(), EnumA = "Add", Number = ConstNum(-1) };
            case "act_grant_kw":
            case "act_remove_kw":
                return new Block { Opcode = opcode, Target = SelfSlot(), Text = "ward" };
            case "act_damage_aura":
                return new Block
                {
                    Opcode = opcode,
                    Target = new Block { Opcode = "tgt_leader", EnumA = "Self" },
                    EnumA = "TakenCap", EnumB = "All", EnumC = "1", Number = ConstNum(3),
                };

            // ---- 计数 / 赋能力 ----
            case "act_mod_counter":
                return new Block { Opcode = opcode, Text = "pp", EnumA = "Add", Number = ConstNum(1) };
            case "act_grant_entity_ability":
                return new Block
                {
                    Opcode = opcode, Target = SelfSlot(),
                    Stacks = new List<Block> { new() { Opcode = "hat_self_event", Text = "OnDestroy" } },
                };
            case "act_grant_ability":
                return new Block
                {
                    Opcode = opcode, Text = "faith",
                    Stacks = new List<Block> { new() { Opcode = "hat_self_event", Text = "OnEvolve" } },
                };
            case "act_invoke":
                return new Block { Opcode = opcode, Text = "OnPlay", Target = SelfSlot() };
            case "act_deck_mod":
                return new Block { Opcode = opcode, Text = "size", IntA = 10 };

            default:
                return new Block { Opcode = opcode, Number = ConstNum(1) };
        }
    }

    /// <summary>工作区的六个根列表（能力域入口）。</summary>
    public static List<List<Block>> RootLists(CardBlockWorkspace ws)
    {
        var roots = new List<List<Block>>();
        if (ws == null) return roots;
        void Add(List<Block> l) { if (l != null) roots.Add(l); }
        Add(ws.Hats);
        Add(ws.CrestHats);
        Add(ws.FaithHats);
        Add(ws.AccelerateHats);
        Add(ws.CrystallizeHats);
        Add(ws.DeckModBlocks);
        return roots;
    }

    /// <summary>
    /// 在整棵积木树里递归找到「直接包含 target 的那个列表」。
    ///
    /// 递归必须覆盖三条子路径：Stacks（动作序列 / Then）、ElseStacks（Else）、
    /// Branches（mode 的多分支）。漏掉任何一条，该分支里的积木就完全不可操作。
    /// </summary>
    public static List<Block> FindParentContainer(CardBlockWorkspace ws, Block target)
    {
        if (ws == null || target == null) return null;
        foreach (var root in RootLists(ws))
        {
            var hit = SearchInList(root, target);
            if (hit != null) return hit;
        }
        return null;
    }

    private static List<Block> SearchInList(List<Block> list, Block target)
    {
        if (list == null) return null;
        if (list.Contains(target)) return list;
        foreach (var b in list)
        {
            if (b == null) continue;
            var r = SearchInList(b.Stacks, target) ?? SearchInList(b.ElseStacks, target);
            if (r != null) return r;
            if (b.Branches != null)
            {
                foreach (var br in b.Branches)
                {
                    var rb = SearchInList(br, target);
                    if (rb != null) return rb;
                }
            }
        }
        return null;
    }

    /// <summary>在兄弟列表里上下移动一格。返回是否真的动了。</summary>
    public static bool Move(CardBlockWorkspace ws, Block blk, int dir)
    {
        var list = FindParentContainer(ws, blk);
        if (list == null) return false;
        int idx = list.IndexOf(blk);
        int ni = idx + dir;
        if (idx < 0 || ni < 0 || ni >= list.Count) return false;
        (list[idx], list[ni]) = (list[ni], list[idx]);
        return true;
    }

    /// <summary>从它所属的父列表里摘掉。</summary>
    public static bool Remove(CardBlockWorkspace ws, Block blk)
    {
        var list = FindParentContainer(ws, blk);
        return list != null && list.Remove(blk);
    }

    /// <summary>把 blk 插到它同级的下一个位置（复制用）。</summary>
    public static bool Duplicate(CardBlockWorkspace ws, Block blk)
    {
        var list = FindParentContainer(ws, blk);
        if (list == null) return false;
        int idx = list.IndexOf(blk);
        if (idx < 0) return false;
        list.Insert(idx + 1, Clone(blk));
        return true;
    }

    /// <summary>深拷贝一颗积木（含全部子容器与孔位）。</summary>
    public static Block Clone(Block b)
    {
        if (b == null) return null;
        var c = new Block
        {
            Opcode = b.Opcode, Text = b.Text, Text2 = b.Text2,
            IntA = b.IntA, IntB = b.IntB, IntC = b.IntC,
            FlagA = b.FlagA, FlagB = b.FlagB, FlagC = b.FlagC,
            FlagD = b.FlagD, FlagE = b.FlagE,
            EnumA = b.EnumA, EnumB = b.EnumB, EnumC = b.EnumC,
            CardNameA = b.CardNameA,
            Target = Clone(b.Target),
            Number = Clone(b.Number),
            Number2 = Clone(b.Number2),
            Condition = Clone(b.Condition),
            // 事件帽专属：漏了这几个，复制出来的帽子会丢掉
            // 「在哪个区域生效 / 哪个回合生效 / 第二个目标 / 发动代价」。
            Target2 = Clone(b.Target2),
            ActiveZone = b.ActiveZone,
            DuringTurn = b.DuringTurn,
            Costs = b.Costs != null ? new List<(string kind, int amount)>(b.Costs) : new(),
        };
        if (b.Stacks != null) foreach (var x in b.Stacks) c.Stacks.Add(Clone(x));
        if (b.ElseStacks != null) foreach (var x in b.ElseStacks) c.ElseStacks.Add(Clone(x));
        if (b.Branches != null)
            foreach (var br in b.Branches)
            {
                var nb = new List<Block>();
                if (br != null) foreach (var x in br) nb.Add(Clone(x));
                c.Branches.Add(nb);
            }
        return c;
    }

    /// <summary>把 blk 追加到指定列表末尾。空列表返回 false。</summary>
    public static bool AppendTo(List<Block> list, Block blk)
    {
        if (list == null || blk == null) return false;
        list.Add(blk);
        return true;
    }
}

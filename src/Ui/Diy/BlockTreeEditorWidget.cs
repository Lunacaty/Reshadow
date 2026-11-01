using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// Scratch 五彩几何积木树的可视化编辑器。
///
/// 形状与颜色都按 Block.Shape 走，不统一成一个灰框 ——
/// 「事件帽 / 控制流夹 / 动作块 / 目标胶囊 / 条件六角」这套形状语言
/// 正是积木端存在的理由；退化成文字列表就等于砍掉了这一端。
/// </summary>
public partial class BlockTreeEditorWidget : VBoxContainer
{
    private static Color ShapeColor(BlockShape s) => s switch
    {
        BlockShape.Hat   => new Color("D97706"),  // 琥珀金：事件帽
        BlockShape.C     => new Color("DB2777"),  // 玫红：控制流夹
        BlockShape.Stack => new Color("2563EB"),  // 宝石蓝：动作块
        BlockShape.Pill  => new Color("059669"),  // 翡翠绿：目标/数值胶囊
        BlockShape.Hex   => new Color("7C3AED"),  // 奥术紫：布尔条件
        _                => new Color("475569"),
    };

    private bool _editable;
    private Action<Block> _onAddAction;
    private Action<Block> _onRemoveAction;
    private Action<Block, int> _onMoveAction;

    /// <summary>
    /// 构建积木树。
    ///
    /// editable=true 时每颗动作块右侧出现 [▲][▼][🗑]，事件帽下出现 [＋添加动作] ——
    /// 积木端如果只能看不能改，那它就只是一张示意图，不是编辑器。
    /// </summary>
    public void Build(CardBlockWorkspace ws, ChineseCardPrinter printer,
                      bool editable = false,
                      Action<Block> onAddAction = null,
                      Action<Block> onRemoveAction = null,
                      Action<Block, int> onMoveAction = null)
    {
        _editable = editable;
        _onAddAction = onAddAction;
        _onRemoveAction = onRemoveAction;
        _onMoveAction = onMoveAction;
        foreach (var c in GetChildren()) c.QueueFree();
        AddThemeConstantOverride("separation", 6);
        if (ws == null) return;

        var head = new Label { Text = "卡牌积木树：" + (ws.Name ?? "") };
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AddThemeColorOverride("font_color", new Color("FEF08A"));
        AddChild(head);

        if (ws.DeckModBlocks != null && ws.DeckModBlocks.Count > 0)
        {
            var dmTitle = new Label { Text = "构筑期修正积木" };
            dmTitle.AddThemeFontSizeOverride("font_size", 12);
            dmTitle.AddThemeColorOverride("font_color", SvWbTheme.AccentPurple);
            AddChild(dmTitle);
            foreach (var b in ws.DeckModBlocks) AddChild(BlockLeaf(b, 0));
        }

        if (ws.Keywords != null && ws.Keywords.Count > 0)
        {
            AddChild(BlockBox(BlockShape.Pill,
                "关键词：" + string.Join("、", ws.Keywords), null));
        }

        if (ws.Hats != null && ws.Hats.Count > 0)
        {
            var hatTitle = new Label { Text = "能力积木（事件帽）" };
            hatTitle.AddThemeFontSizeOverride("font_size", 12);
            hatTitle.AddThemeColorOverride("font_color", new Color("FBBF24"));
            AddChild(hatTitle);
            foreach (var h in ws.Hats)
            {
                AddChild(BlockLeaf(h, 0));
                if (_editable && _onAddAction != null)
                {
                    var addBtn = new Button { Text = "＋ 添加动作块", CustomMinimumSize = new Vector2(130, 26) };
                    SvWbTheme.StyleButton(addBtn, customAccent: new Color("2563EB"));
                    var boundHat = h;
                    addBtn.Pressed += () => _onAddAction(boundHat);
                    AddChild(addBtn);
                }
            }
        }

        if (ws.AccelerateHats != null && ws.AccelerateHats.Count > 0)
        {
            AddChild(BlockBox(BlockShape.Hat, "⚡ 激奏 (" + ws.AccelerateCost + " 费)", null));
            foreach (var h in ws.AccelerateHats) AddChild(BlockLeaf(h, 1));
        }

        if (ws.CrystallizeHats != null && ws.CrystallizeHats.Count > 0)
        {
            AddChild(BlockBox(BlockShape.Hat, "❖ 结晶 (" + ws.CrystallizeCost + " 费)", null));
            foreach (var h in ws.CrystallizeHats) AddChild(BlockLeaf(h, 1));
        }
    }

    private Control BlockLeaf(Block blk, int depth)
    {
        if (blk == null) return new Control();

        var shape = blk.Shape;
        string label = BuildLabel(blk);

        var wrap = new VBoxContainer();
        wrap.AddThemeConstantOverride("separation", 4);

        // 动作块行：色块 + 可选的 [▲][▼][🗑]
        var lineRow = new HBoxContainer();
        lineRow.AddThemeConstantOverride("separation", 4);
        wrap.AddChild(lineRow);

        var box = BlockBox(shape, label, null, depth);
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        lineRow.AddChild(box);

        if (_editable && _onRemoveAction != null && shape == BlockShape.Stack)
        {
            var up = new Button { Text = "▲", CustomMinimumSize = new Vector2(28, 28) };
            SvWbTheme.StyleButton(up);
            var b1 = blk;
            up.Pressed += () => _onMoveAction?.Invoke(b1, -1);
            lineRow.AddChild(up);

            var down = new Button { Text = "▼", CustomMinimumSize = new Vector2(28, 28) };
            SvWbTheme.StyleButton(down);
            var b2 = blk;
            down.Pressed += () => _onMoveAction?.Invoke(b2, 1);
            lineRow.AddChild(down);

            var del = new Button { Text = "🗑", CustomMinimumSize = new Vector2(28, 28) };
            SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
            var b3 = blk;
            del.Pressed += () => _onRemoveAction(b3);
            lineRow.AddChild(del);
        }

        foreach (var inner in blk.Stacks ?? new List<Block>())
            wrap.AddChild(BlockLeaf(inner, depth + 1));
        foreach (var inner in blk.ElseStacks ?? new List<Block>())
            wrap.AddChild(BlockLeaf(inner, depth + 1));
        foreach (var inner in blk.Branches ?? new List<List<Block>>())
            foreach (var b in inner) wrap.AddChild(BlockLeaf(b, depth + 1));

        return wrap;
    }

    /// <summary>数值块的显示文本。数值挂在 Number 子积木上，不在块自身 IntA。</summary>
    private static string NumText(Block numBlk, int fallback = 1)
    {
        if (numBlk == null) return fallback.ToString();
        return numBlk.Opcode switch
        {
            "num_const" => numBlk.IntA.ToString(),
            "num_counter" => "【" + (numBlk.Text ?? "计数") + "】",
            "num_count" => "统计数量",
            "num_prop" => (numBlk.EnumA ?? "属性"),
            _ => string.IsNullOrEmpty(numBlk.Opcode) ? fallback.ToString() : numBlk.Opcode,
        };
    }

    /// <summary>
    /// 积木的卡面化描述。
    ///
    /// 两个坑都在这里踩过：
    ///   1) 数值要读 blk.Number（子积木），读 blk.IntA 恒为 0
    ///      —— 于是「打 3 点伤害」显示成「打 0 点伤害」；
    ///   2) opcode 的真实拼写是 act_restore / act_summon，
    ///      写成 act_heal / act_spawn 永远匹配不上，只会落到 default 分支。
    /// </summary>
    private static string BuildLabel(Block blk)
    {
        string op = blk.Opcode ?? "";
        string txt = blk.Text ?? "";

        return op switch
        {
            "hat_self_event" => "🎩 当自身【" + txt + "】时",
            "hat_watch_event" => "🎩 每当监听到【" + txt + "】时",
            "hat_passive" => "🎩 常驻光环 / 初始设置",
            "c_if_else" => "🔀 如果满足条件…否则…",
            "c_repeat" => "🔁 重复执行 " + NumText(blk.Number) + " 次",
            "c_mode" => "❖ 模式抉择 (" + (blk.EnumA ?? "") + ", 选 " + Math.Max(1, blk.IntA) + " 项)",
            "act_damage" => "⚔ 对目标造成 " + NumText(blk.Number, 0) + " 点伤害",
            "act_split_damage" => "⚔ 分配造成 " + NumText(blk.Number, 0) + " 点伤害",
            "act_restore" => "💚 回复 " + NumText(blk.Number, 0) + " 点生命",
            "act_full_restore" => "💚 完全回复生命",
            "act_draw" => "🃏 抽取 " + NumText(blk.Number, 1) + " 张牌",
            "act_summon" => "✨ 召唤 " + NumText(blk.Number, 1) + " 个《" + txt + "》",
            "act_add_hand" => "📥 将 " + NumText(blk.Number, 1) + " 张《" + txt + "》加入手牌",
            "act_gain_crest" => "❖ 获得纹章：《" + txt + "》",
            "act_destroy" => "💀 破坏目标",
            "act_banish" => "🌀 消失目标",
            "act_discard" => "🗑 舍弃目标",
            "act_bounce" => "↩ 将目标返回手牌",
            "act_buff" => "⬆ 给予 +" + NumText(blk.Number, 0) + "/+" + NumText(blk.Number2, 0),
            "act_grant_kw" => "🛡 给予关键词：【" + txt + "】",
            "act_evolve" => "✦ 使目标进化",
            "act_super_evolve" => "★ 使目标超进化",
            "act_transform" => "🦋 使目标变身为《" + txt + "》",
            "act_reanimate" => "⚰ 亡者召还 (" + NumText(blk.Number, 0) + ")",
            "act_discover" => "🔍 发现 (" + (blk.IntB > 0 ? blk.IntB : 3) + " 选 "
                                + (blk.IntA > 0 ? blk.IntA : 1) + ")",
            "act_deck_mod" => "📐 构筑修正：" + txt + "(" + blk.IntA + ")",
            "act_copy" => "📋 复制目标",
            "act_move_zone" => "📦 移动目标区域",
            "act_flip_damage" => "⚔ 翻转伤害",
            "" => string.IsNullOrEmpty(txt) ? "(未知积木)" : txt,
            _ => string.IsNullOrEmpty(txt) ? op : op + "  " + txt,
        };
    }

    /// <summary>按形状画一个带切角/圆角的色块。</summary>
    private Control BlockBox(BlockShape shape, string text, string pill, int depth = 0)
    {
        Color col = ShapeColor(shape);
        int radius = shape switch
        {
            BlockShape.Pill => 14,
            BlockShape.Hex  => 4,
            BlockShape.Hat  => 10,
            _ => 6,
        };

        var box = new PanelContainer { CustomMinimumSize = new Vector2(0, 34) };
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            col.Darkened(0.28f), col.Lightened(0.15f), radius, 1, 2));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);
        box.AddChild(row);

        if (depth > 0)
            row.AddChild(new Control { CustomMinimumSize = new Vector2(depth * 18f, 0) });

        var lbl = new Label { Text = text, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        lbl.AddThemeColorOverride("font_color", Colors.White);
        row.AddChild(lbl);

        if (!string.IsNullOrEmpty(pill))
        {
            var p = new PanelContainer { CustomMinimumSize = new Vector2(72, 22) };
            p.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                new Color("0891B2"), new Color("22D3EE"), 11, 1, 0));
            var pl = new Label { Text = pill, HorizontalAlignment = HorizontalAlignment.Center };
            pl.AddThemeFontSizeOverride("font_size", 11);
            p.AddChild(pl);
            row.AddChild(p);
        }

        return box;
    }
}
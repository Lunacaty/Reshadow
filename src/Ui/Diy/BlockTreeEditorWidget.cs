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

    public void Build(CardBlockWorkspace ws, ChineseCardPrinter printer)
    {
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
            foreach (var h in ws.Hats) AddChild(BlockLeaf(h, 0));
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
        wrap.AddChild(BlockBox(shape, label, null, depth));

        foreach (var inner in blk.Stacks ?? new List<Block>())
            wrap.AddChild(BlockLeaf(inner, depth + 1));
        foreach (var inner in blk.ElseStacks ?? new List<Block>())
            wrap.AddChild(BlockLeaf(inner, depth + 1));
        foreach (var inner in blk.Branches ?? new List<List<Block>>())
            foreach (var b in inner) wrap.AddChild(BlockLeaf(b, depth + 1));

        return wrap;
    }

    private static string BuildLabel(Block blk)
    {
        string op = blk.Opcode ?? "";
        string txt = blk.Text ?? "";
        string t2 = blk.Text2 ?? "";

        string core = op switch
        {
            "act_damage"   => "对目标造成 " + blk.IntA + " 点伤害",
            "act_draw"     => "抽 " + (blk.IntA > 0 ? blk.IntA : 1) + " 张牌",
            "act_heal"     => "回复 " + blk.IntA + " 点生命",
            "act_spawn"    => "召唤 " + (string.IsNullOrEmpty(t2) ? "随从" : t2),
            "act_discover" => "发现 " + (blk.IntA > 0 ? blk.IntA : 1) + " 张牌",
            "act_deck_mod" => "构筑修正：" + txt,
            "act_grant_kw" => "赋予关键词：" + txt,
            ""             => string.IsNullOrEmpty(txt) ? "(未知积木)" : txt,
            _              => string.IsNullOrEmpty(txt) ? op : op + "  " + txt,
        };

        return core;
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
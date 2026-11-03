using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Diy;
using ShadowverseFan.KernelV3.Lang;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Diy;

/// <summary>
/// Scratch 五彩几何积木树的可视化编辑器（三栏工作台）。
///
/// 形状与颜色都按 Block.Shape 走，不统一成一个灰框 ——
/// 「事件帽 / 控制流夹 / 动作块 / 目标胶囊 / 条件六角」这套形状语言
/// 正是积木端存在的理由；退化成文字列表就等于砍掉了这一端。
///
/// 三栏：左调色盘（按色分类点击插入）｜中画布（焦点容器 + 全树操作）
/// ｜右孔位镶嵌器（Target / Number / Condition 三套递归编辑器）。
/// </summary>
public partial class BlockTreeEditorWidget : VBoxContainer
{
    private static Color ShapeColor(BlockShape s) => s switch
    {
        BlockShape.Hat   => new Color("D97706"),
        BlockShape.C     => new Color("DB2777"),
        BlockShape.Stack => new Color("2563EB"),
        BlockShape.Pill  => new Color("059669"),
        BlockShape.Hex   => new Color("7C3AED"),
        _                => new Color("475569"),
    };

    private bool _editable;
    private DiyWorkshopController _ctrl;
    private ChineseCardPrinter _printer;
    private CardBlockWorkspace _ws;

    private int _scopeTab;
    private List<Block> _insertList;
    private Block _selected;

    private VBoxContainer _canvasCol;
    private VBoxContainer _inspectorCol;

    public void Build(CardBlockWorkspace ws, ChineseCardPrinter printer,
                      bool editable = false,
                      Action<Block> onAddAction = null,
                      Action<Block> onRemoveAction = null,
                      Action<Block, int> onMoveAction = null)
    {
        _ws = ws;
        _printer = printer;
        _editable = editable;

        ClearSelf();
        AddThemeConstantOverride("separation", 6);
        if (ws == null) return;

        if (!_editable)
        {
            AddTitle();
            BuildReadOnly(ws);
            return;
        }

        EnsureInsertList();
        Refresh();
    }

    public void BindController(DiyWorkshopController ctrl) => _ctrl = ctrl;

    private void ClearSelf()
    {
        foreach (var c in GetChildren())
        {
            RemoveChild(c);
            c.QueueFree();
        }
    }

    private void AddTitle()
    {
        var head = new Label { Text = "卡牌积木树：" + (_ws?.Name ?? "") };
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AddThemeColorOverride("font_color", new Color("FEF08A"));
        AddChild(head);
    }

    private void Refresh()
    {
        ClearSelf();
        AddThemeConstantOverride("separation", 6);
        AddTitle();
        AddChild(BuildScopeTabs());

        var wb = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        wb.AddThemeConstantOverride("separation", 8);
        AddChild(wb);

        wb.AddChild(BuildPaletteColumn());

        _canvasCol = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _canvasCol.AddThemeConstantOverride("separation", 4);
        wb.AddChild(_canvasCol);

        _inspectorCol = new VBoxContainer { CustomMinimumSize = new Vector2(300, 0) };
        _inspectorCol.AddThemeConstantOverride("separation", 4);
        wb.AddChild(_inspectorCol);

        RedrawCanvas();
        RedrawInspector();
    }

    private void EnsureInsertList()
    {
        if (_insertList != null) return;
        var root = _ctrl?.RootListOf(_scopeTab);
        if (root == null) root = ScopeRootFallback();
        _insertList = root;
        _ctrl?.SetActiveInsertList(root);
    }

    private List<Block> ScopeRootFallback() => _scopeTab switch
    {
        1 => _ws?.CrestHats,
        2 => _ws?.FaithHats,
        3 => _ws?.AccelerateHats,
        4 => _ws?.CrystallizeHats,
        5 => _ws?.DeckModBlocks,
        _ => _ws?.Hats,
    };

    private Control BuildScopeTabs()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var labels = new[]
        {
            "本体能力 (" + (_ws?.Hats?.Count ?? 0) + ")",
            "关联纹章 (" + (_ws?.CrestHats?.Count ?? 0) + ")",
            "关联信仰 (" + (_ws?.FaithHats?.Count ?? 0) + ")",
            "激奏" + ((_ws?.AccelerateCost ?? -1) >= 0 ? " (" + _ws.AccelerateCost + "费)" : ""),
            "结晶" + ((_ws?.CrystallizeCost ?? -1) >= 0 ? " (" + _ws.CrystallizeCost + "费)" : ""),
            "构筑与词条",
        };
        for (int i = 0; i < labels.Length; i++)
        {
            int tab = i;
            var b = new Button { Text = labels[i], CustomMinimumSize = new Vector2(0, 32) };
            SvWbTheme.StyleButton(b, isPrimary: _scopeTab == tab);
            b.Pressed += () =>
            {
                _scopeTab = tab;
                _insertList = null;
                _selected = null;
                EnsureInsertList();
                Refresh();
            };
            row.AddChild(b);
        }
        return row;
    }

    private static readonly (string Title, (string Op, string Label)[] Items)[] Palette =
    {
        ("事件帽", new[]
        {
            ("hat_self_event", "自身时机帽"),
            ("hat_watch_event", "旁观监听帽"),
            ("hat_passive", "常驻光环帽"),
        }),
        ("控制流", new[]
        {
            ("c_if_else", "如果…否则…"),
            ("c_repeat", "重复 N 次"),
            ("c_for_each", "遍历每个目标"),
            ("c_mode", "模式抉择 / 随机"),
        }),
        ("伤害 / 回复", new[]
        {
            ("act_damage", "造成伤害"),
            ("act_split_damage", "分配伤害"),
            ("act_restore", "回复生命"),
            ("act_full_restore", "完全回复"),
            ("act_damage_aura", "减伤 / 上限光环"),
        }),
        ("抽牌 / 召唤", new[]
        {
            ("act_draw", "抽取卡牌"),
            ("act_summon", "召唤到战场"),
            ("act_add_hand", "生成加入手牌"),
            ("act_copy", "复制目标卡牌"),
            ("act_discover", "从卡池发现"),
            ("act_reanimate", "亡者召还"),
            ("act_gain_crest", "获得纹章"),
            ("act_reveal", "公开手牌"),
            ("act_replace_deck", "替换整副牌组"),
        }),
        ("解场 / 移区", new[]
        {
            ("act_select", "显式选择目标"),
            ("act_destroy", "破坏目标"),
            ("act_banish", "消失目标"),
            ("act_discard", "舍弃手牌"),
            ("act_bounce", "返回手牌"),
            ("act_return_deck", "洗回牌组"),
            ("act_transform", "使目标变身"),
            ("act_move_to_board", "移动到战场"),
        }),
        ("身材 / 费用 / 词条", new[]
        {
            ("act_buff", "攻防增减"),
            ("act_set_stats", "设定攻防"),
            ("act_swap_stats", "交换攻防"),
            ("act_mod_cost", "修改费用"),
            ("act_grant_kw", "给予关键词"),
            ("act_remove_kw", "移除关键词"),
            ("act_silence", "沉默目标"),
        }),
        ("进化 / 计数 / 赋能力", new[]
        {
            ("act_evolve", "使目标进化"),
            ("act_super_evolve", "使目标超进化"),
            ("act_mod_counter", "修改计数器"),
            ("act_grant_entity_ability", "赋予子能力"),
            ("act_grant_ability", "赋予常驻能力"),
            ("act_invoke", "复刻时机能力"),
            ("act_deck_mod", "构筑规则修正"),
        }),
    };


    /// <summary>
    /// 激奏 / 结晶费用设置。原来页签只显示「激奏」两个字，
    /// 玩家在积木端根本没法开启或改价。
    /// </summary>
    private void BuildAcceleratePanel()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6);

        var title = new Label { Text = "激奏 / 结晶费用" };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", new Color("FDE68A"));
        col.AddChild(title);

        var hint = new Label
        {
            Text = "设为 -1 表示不启用。启用后可在页签里往对应能力域添加积木。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        col.AddChild(hint);

        col.AddChild(BuildCostSpin("激奏费用",
            _ws?.AccelerateCost ?? -1,
            v =>
            {
                if (_ws == null || _ctrl == null) return;
                _ws.AccelerateCost = v;
                _ctrl.CompileFromBlocks(_ws);
                _ws = _ctrl.BlockModel ?? _ws;
                Refresh();
            }));

        col.AddChild(BuildCostSpin("结晶费用",
            _ws?.CrystallizeCost ?? -1,
            v =>
            {
                if (_ws == null || _ctrl == null) return;
                _ws.CrystallizeCost = v;
                _ctrl.CompileFromBlocks(_ws);
                _ws = _ctrl.BlockModel ?? _ws;
                Refresh();
            }));

        _canvasCol.AddChild(col);
    }

    private static Control BuildCostSpin(string label, int cur, Action<int> setter)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(88, 0), VerticalAlignment = VerticalAlignment.Center };
        lbl.AddThemeFontSizeOverride("font_size", 12);
        row.AddChild(lbl);

        var spin = new SpinBox
        {
            MinValue = -1, MaxValue = 20, Step = 1, Value = cur,
            CustomMinimumSize = new Vector2(120, 0),
        };
        spin.ValueChanged += v => setter((int)v);
        row.AddChild(spin);
        return row;
    }

    /// <summary>
    /// 构筑与词条：关键词勾选 + 费用/攻防基础值。
    /// 原来这个页签只能看一串关键词文本，改不了。
    /// </summary>
    private void BuildKeywordsPanel()
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6);

        var title = new Label { Text = "常驻关键词（构筑期生效）" };
        title.AddThemeFontSizeOverride("font_size", 13);
        title.AddThemeColorOverride("font_color", new Color("FDE68A"));
        col.AddChild(title);

        foreach (var (key, zh) in DiyZh.Keywords)
        {
            var cb = new CheckBox { Text = zh, ButtonPressed = _ws?.Keywords?.Contains(key) == true };
            string captured = key;
            cb.Toggled += on =>
            {
                if (_ws == null || _ctrl == null) return;
                _ws.Keywords ??= new List<string>();
                if (on)
                {
                    if (!_ws.Keywords.Contains(captured)) _ws.Keywords.Add(captured);
                }
                else _ws.Keywords.Remove(captured);

                _ctrl.CompileFromBlocks(_ws);
                _ws = _ctrl.BlockModel ?? _ws;
            };
            col.AddChild(cb);
        }

        _canvasCol.AddChild(col);
    }
    private Control BuildPaletteColumn()
    {
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(215, 460) };
        SvWbTheme.StyleScroll(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 3);
        scroll.AddChild(col);

        // 构筑规则域只收 act_deck_mod：编译端对 DeckModBlocks 里其他 opcode
        // 直接 continue 静默忽略，摆一片能力积木进去等于白点。
        if (_scopeTab == 5)
        {
            var only = new Label { Text = "构筑规则（仅此类积木生效）" };
            only.AddThemeFontSizeOverride("font_size", 12);
            only.AddThemeColorOverride("font_color", new Color("FDE68A"));
            col.AddChild(only);

            var bd = new Button
            {
                Text = "+ 构筑规则修正",
                CustomMinimumSize = new Vector2(0, 26),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            SvWbTheme.StyleButton(bd, customAccent: ShapeColor(BlockShape.Pill));
            bd.Pressed += () => InsertBlock("act_deck_mod");
            col.AddChild(bd);
            return scroll;
        }

        foreach (var (title, items) in Palette)
        {
            var h = new Label { Text = title };
            h.AddThemeFontSizeOverride("font_size", 12);
            h.AddThemeColorOverride("font_color", new Color("FDE68A"));
            col.AddChild(h);
            foreach (var (op, label) in items)
            {
                var b = new Button
                {
                    Text = "+ " + label,
                    CustomMinimumSize = new Vector2(0, 26),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill,
                };
                string captured = op;
                SvWbTheme.StyleButton(b, customAccent: ShapeColor(DiyBlockModel.ShapeOf(captured)));
                b.Pressed += () => InsertBlock(captured);
                col.AddChild(b);
            }
        }
        return scroll;
    }

    private void InsertBlock(string opcode)
    {
        if (_ctrl == null) return;
        EnsureInsertList();
        if (!_ctrl.InsertBlock(opcode, _scopeTab)) return;
        _ws = _ctrl.BlockModel;
        Refresh();
    }

    private void RedrawCanvas()
    {
        if (_canvasCol == null) return;
        foreach (var c in _canvasCol.GetChildren())
        {
            _canvasCol.RemoveChild(c);
            c.QueueFree();
        }

        var root = _ctrl?.RootListOf(_scopeTab) ?? ScopeRootFallback() ?? new List<Block>();

        var bar = new HBoxContainer();
        var focusLbl = new Label
        {
            Text = _insertList == null ? "插入点：未指定" : "插入点：动作序列（" + _insertList.Count + " 项）",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        focusLbl.AddThemeFontSizeOverride("font_size", 11);
        focusLbl.AddThemeColorOverride("font_color", new Color("7DD3FC"));
        bar.AddChild(focusLbl);

        var toRoot = new Button { Text = "设为域根", CustomMinimumSize = new Vector2(0, 26) };
        SvWbTheme.StyleButton(toRoot);
        toRoot.Pressed += () =>
        {
            _insertList = root;
            _ctrl?.SetActiveInsertList(root);
            RedrawCanvas();
        };
        bar.AddChild(toRoot);
        _canvasCol.AddChild(bar);

        if (root.Count == 0)
        {
            var hint = new Label { Text = "（此能力域为空：从左侧调色盘点「事件帽」开始）", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _canvasCol.AddChild(hint);
            return;
        }

        if (_scopeTab == 5) BuildKeywordsPanel();
        if (_scopeTab == 3 || _scopeTab == 4) BuildAcceleratePanel();

        foreach (var b in root.ToList()) _canvasCol.AddChild(RenderBlock(b, 0));
    }

    private Control RenderBlock(Block blk, int depth)
    {
        if (blk == null) return new Control();

        var wrap = new VBoxContainer();
        wrap.AddThemeConstantOverride("separation", 3);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 4);
        wrap.AddChild(row);

        var box = BlockBox(blk.Shape, BuildLabel(blk), null, depth);
        box.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        box.MouseFilter = Control.MouseFilterEnum.Stop;
        box.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _selected = blk;
                RedrawInspector();
                box.AcceptEvent();
            }
        };
        row.AddChild(box);

        if (_editable && _ctrl != null)
        {
            var up = new Button { Text = "▲", CustomMinimumSize = new Vector2(26, 26) };
            SvWbTheme.StyleButton(up);
            var c1 = blk;
            up.Pressed += () => { _ctrl.MoveBlockInTree(c1, -1); _ws = _ctrl.BlockModel; Refresh(); };
            row.AddChild(up);

            var down = new Button { Text = "▼", CustomMinimumSize = new Vector2(26, 26) };
            SvWbTheme.StyleButton(down);
            var c2 = blk;
            down.Pressed += () => { _ctrl.MoveBlockInTree(c2, 1); _ws = _ctrl.BlockModel; Refresh(); };
            row.AddChild(down);

            var cp = new Button { Text = "+", CustomMinimumSize = new Vector2(26, 26) };
            SvWbTheme.StyleButton(cp);
            var c3 = blk;
            cp.Pressed += () => { _ctrl.DuplicateBlockInTree(c3); _ws = _ctrl.BlockModel; Refresh(); };
            row.AddChild(cp);

            var del = new Button { Text = "x", CustomMinimumSize = new Vector2(26, 26) };
            SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
            var c4 = blk;
            del.Pressed += () =>
            {
                _ctrl.RemoveBlockFromTree(c4);
                if (ReferenceEquals(_selected, c4)) _selected = null;
                _ws = _ctrl.BlockModel;
                Refresh();
            };
            row.AddChild(del);
        }

        AddSubList(wrap, blk.Stacks, "Then", depth);
        AddSubList(wrap, blk.ElseStacks, "Else", depth);

        if (blk.Branches != null)
        {
            for (int i = 0; i < blk.Branches.Count; i++)
            {
                var br = blk.Branches[i];
                var head = new HBoxContainer();
                var lbl = new Label
                {
                    Text = "分支 " + (i + 1) + "（" + (br?.Count ?? 0) + " 项）",
                    SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                };
                lbl.AddThemeFontSizeOverride("font_size", 10);
                lbl.AddThemeColorOverride("font_color", new Color("F0ABFC"));
                head.AddChild(lbl);

                var pick = new Button { Text = "设为插入点", CustomMinimumSize = new Vector2(0, 22) };
                SvWbTheme.StyleButton(pick);
                var cbr = br;
                pick.Pressed += () =>
                {
                    _insertList = cbr;
                    _ctrl?.SetActiveInsertList(cbr);
                    RedrawCanvas();
                };
                head.AddChild(pick);
                wrap.AddChild(head);

                if (br != null)
                    foreach (var b in br.ToList()) wrap.AddChild(RenderBlock(b, depth + 1));
            }
        }

        return wrap;
    }

    private void AddSubList(VBoxContainer wrap, List<Block> list, string name, int depth)
    {
        if (list == null) return;
        var head = new HBoxContainer();
        var lbl = new Label
        {
            Text = name + " 流（" + list.Count + " 项）",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        lbl.AddThemeFontSizeOverride("font_size", 10);
        lbl.AddThemeColorOverride("font_color", new Color("FBCFE8"));
        head.AddChild(lbl);

        var pick = new Button { Text = "设为插入点", CustomMinimumSize = new Vector2(0, 22) };
        SvWbTheme.StyleButton(pick);
        pick.Pressed += () =>
        {
            _insertList = list;
            _ctrl?.SetActiveInsertList(list);
            RedrawCanvas();
        };
        head.AddChild(pick);
        wrap.AddChild(head);

        foreach (var b in list.ToList()) wrap.AddChild(RenderBlock(b, depth + 1));
    }


    /// <summary>
    /// 事件帽的发动代价增删器（爆能强化 / 唤灵 / 连击 / 吟唱）。
    ///
    /// 原来积木端完全没法给帽子加代价 ——「爆能强化4」这种最常见的
    /// 影之诗机制在 Scratch 端根本表达不出来，只能回脚本端手写。
    /// </summary>
    private void BuildHatCostPanel(VBoxContainer col, Block b)
    {
        var head = new Label { Text = "发动代价" };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("FCA5A5"));
        col.AddChild(head);

        b.Costs ??= new List<(string kind, int amount)>();

        var zhMap = new (string Key, string Zh)[]
        {
            ("enhance", "爆能强化"),
            ("necromancy", "唤灵"),
            ("combo", "连击"),
            ("rally", "协作"),
            ("countdown", "吟唱"),
            ("earth_rite", "土之秘术"),
        };

        for (int i = 0; i < b.Costs.Count; i++)
        {
            int idx = i;
            var (kind, amount) = b.Costs[i];
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 4);

            var opt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            for (int k = 0; k < zhMap.Length; k++) opt.AddItem(zhMap[k].Zh, k);
            int sel = DiyZh.IndexOf(zhMap, kind);
            if (sel >= 0) opt.Selected = sel;
            opt.ItemSelected += s0 =>
            {
                int k = (int)s0;
                if (k < 0 || k >= zhMap.Length || idx >= b.Costs.Count) return;
                b.Costs[idx] = (zhMap[k].Key, b.Costs[idx].amount);
                Apply();
            };
            row.AddChild(opt);

            var spin = new SpinBox { MinValue = 0, MaxValue = 99, Step = 1, Value = amount, CustomMinimumSize = new Vector2(90, 0) };
            spin.ValueChanged += v =>
            {
                if (idx >= b.Costs.Count) return;
                b.Costs[idx] = (b.Costs[idx].kind, (int)v);
                Apply();
            };
            row.AddChild(spin);

            var del = new Button { Text = "x", CustomMinimumSize = new Vector2(26, 26) };
            SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
            del.Pressed += () =>
            {
                if (idx < b.Costs.Count) b.Costs.RemoveAt(idx);
                Apply();
                Refresh();
            };
            row.AddChild(del);

            col.AddChild(row);
        }

        var add = new Button { Text = "+ 添加发动代价", CustomMinimumSize = new Vector2(0, 26) };
        SvWbTheme.StyleButton(add);
        add.Pressed += () =>
        {
            b.Costs.Add(("enhance", 4));
            Apply();
            Refresh();
        };
        col.AddChild(add);
    }
    private void RedrawInspector()
    {
        if (_inspectorCol == null) return;
        foreach (var c in _inspectorCol.GetChildren())
        {
            _inspectorCol.RemoveChild(c);
            c.QueueFree();
        }

        var title = new Label
        {
            Text = _selected == null ? "选中积木 · 属性" : "当前选中：" + BuildLabel(_selected),
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        title.AddThemeFontSizeOverride("font_size", 12);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        _inspectorCol.AddChild(title);

        if (_selected == null)
        {
            var hint = new Label { Text = "（点击画布上任意一颗积木开始编辑）", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _inspectorCol.AddChild(hint);
            return;
        }

        var scroll = new ScrollContainer { SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        SvWbTheme.StyleScroll(scroll);
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 5);
        scroll.AddChild(col);
        _inspectorCol.AddChild(scroll);

        // 按 opcode 定制：每种积木只渲染它自己真正拥有的孔位。
        // 原来不管点中什么都列出「整数A / 文本 / 枚举A」七个裸字段，
        // 玩家想把入场曲改成谢幕曲得自己猜着敲 OnDestroy —— 那不是 Scratch。
        BuildScriptSpecificInspector(col, _selected);
    }

    /// <summary>按积木语义定制的中文表单。</summary>
    private void BuildScriptSpecificInspector(VBoxContainer col, Block b)
    {
        switch (b.Opcode)
        {
            case "hat_self_event":
            case "hat_watch_event":
            case "hat_passive":
                BuildEnumCombo(col, "触发时机", DiyZh.Events, b.Text,
                               v => { b.Text = v; Apply(); });
                BuildEnumCombo(col, "生效区域", DiyZh.Zones, BlockZone(b),
                               v => { SetBlockZone(b, v); Apply(); });
                if (b.Opcode == "hat_watch_event")
                    BuildTargetSlotEditor(col, "监听的随从", b.Target,
                                          v => { b.Target = v; Apply(); });
                BuildHatCostPanel(col, b);
                break;

            case "c_if_else":
                BuildConditionSlotEditor(col, "成立条件", b.Condition,
                                         v => { b.Condition = v; Apply(); });
                break;
            case "c_repeat":
                BuildNumberSlotEditor(col, "重复次数", b.Number, v => { b.Number = v; Apply(); });
                break;
            case "c_for_each":
                BuildTargetSlotEditor(col, "遍历集合", b.Target, v => { b.Target = v; Apply(); });
                break;
            case "c_mode":
                BuildEnumCombo(col, "抉择方式", DiyZh.ModeKinds, b.EnumA,
                               v => { b.EnumA = v; Apply(); });
                BuildNumberSlotEditor(col, "选取个数", b.Number, v => { b.Number = v; Apply(); });
                var addBranch = new Button { Text = "+ 新增模式分支", CustomMinimumSize = new Vector2(0, 26) };
                SvWbTheme.StyleButton(addBranch);
                addBranch.Pressed += () =>
                {
                    b.Branches ??= new List<List<Block>>();
                    b.Branches.Add(new List<Block>());
                    Apply();
                    Refresh();
                };
                col.AddChild(addBranch);
                break;

            case "act_damage":
            case "act_split_damage":
                BuildTargetSlotEditor(col, "对谁造成伤害", b.Target, v => { b.Target = v; Apply(); });
                BuildNumberSlotEditor(col, "伤害值", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_restore":
                BuildTargetSlotEditor(col, "回复谁", b.Target, v => { b.Target = v; Apply(); });
                BuildNumberSlotEditor(col, "回复量", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_draw":
                BuildNumberSlotEditor(col, "抽几张", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_buff":
            case "act_set_stats":
                BuildTargetSlotEditor(col, "对谁", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "方式", DiyZh.StatOps, b.EnumA, v => { b.EnumA = v; Apply(); });
                BuildNumberSlotEditor(col, "攻击力", b.Number, v => { b.Number = v; Apply(); });
                BuildNumberSlotEditor(col, "生命值", b.Number2, v => { b.Number2 = v; Apply(); });
                break;

            case "act_grant_kw":
            case "act_remove_kw":
                BuildEnumCombo(col, "关键词", DiyZh.Keywords, b.Text, v => { b.Text = v; Apply(); });
                BuildTargetSlotEditor(col, "给予谁", b.Target, v => { b.Target = v; Apply(); });
                break;

            case "act_mod_cost":
                BuildTargetSlotEditor(col, "对谁", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "方式", DiyZh.CostOps, b.EnumA, v => { b.EnumA = v; Apply(); });
                BuildNumberSlotEditor(col, "费用变化", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_mod_counter":
                BuildEnumCombo(col, "计数器", DiyZh.Counters, b.Text, v => { b.Text = v; Apply(); });
                BuildNumberSlotEditor(col, "变化量", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_summon":
            case "act_add_hand":
                BuildTextEditor(col, "卡名", b.Text, v => { b.Text = v; Apply(); });
                BuildNumberSlotEditor(col, "数量", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_destroy":
            case "act_banish":
            case "act_bounce":
            case "act_return_deck":
            case "act_silence":
            case "act_select":
            case "act_reveal":
            case "act_evolve":
            case "act_super_evolve":
            case "act_swap_stats":
            case "act_full_restore":
            case "act_move_to_board":
                BuildTargetSlotEditor(col, "目标", b.Target, v => { b.Target = v; Apply(); });
                break;

            case "act_damage_aura":
                BuildTargetSlotEditor(col, "作用对象", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "光环类型", DiyZh.DamageAuraKinds, b.EnumA,
                               v => { b.EnumA = v; Apply(); });
                BuildEnumCombo(col, "伤害来源", DiyZh.SourceMasks, b.EnumB,
                               v => { b.EnumB = v; Apply(); });
                BuildNumberSlotEditor(col, "数值", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_deck_mod":
                BuildTextEditor(col, "规则项", b.Text, v => { b.Text = v; Apply(); });
                BuildIntEditor(col, "数值", b.IntA, v => { b.IntA = v; Apply(); });
                break;

            default:
                // 兜底：确实需要手填的少数积木，但也要给出中文说明。
                BuildTextEditor(col, "卡名 / 名称", b.Text, v => { b.Text = v; Apply(); });
                BuildNumberSlotEditor(col, "数值", b.Number, v => { b.Number = v; Apply(); });
                break;
        }
    }

    private static string BlockZone(Block b) => string.IsNullOrEmpty(b.EnumA) ? "Board" : b.EnumA;

    private static void SetBlockZone(Block b, string zone) => b.EnumA = zone;

    /// <summary>中文下拉框：选项来自词表，回写内部值。</summary>
    private void BuildEnumCombo(VBoxContainer col, string label,
                                (string Key, string Zh)[] table, string cur, Action<string> setter)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(88, 0), VerticalAlignment = VerticalAlignment.Center };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);

        var opt = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        for (int i = 0; i < table.Length; i++) opt.AddItem(table[i].Zh, i);

        int idx = DiyZh.IndexOf(table, cur);
        if (idx >= 0) opt.Selected = idx;

        opt.ItemSelected += sel =>
        {
            int i = (int)sel;
            if (i < 0 || i >= table.Length) return;
            setter(table[i].Key);
        };
        row.AddChild(opt);
        col.AddChild(row);
    }
    private void Apply()
    {
        if (_ctrl == null || _ws == null) return;

        // CompileFromBlocks 会**整棵重建** BlockModel ——
        // _selected 立刻变成孤儿，后面再编辑改的是一棵已经不在树里的积木，
        // 界面上什么都不会发生。所以先记下路径，编译后按路径找回同一颗。
        var path = PathOf(_selected);

        _ctrl.CompileFromBlocks(_ws);
        var fresh = _ctrl.BlockModel;
        if (fresh == null) return;
        _ws = fresh;

        _selected = path == null ? null : ResolvePath(fresh, path);

        // 编译会重建整棵模型：画布与检视器必须跟着重画，
        // 否则界面还停在上一次的旧积木上，看着像「点了没反应」。
        EnsureInsertList();
        RedrawCanvas();
        RedrawInspector();
    }

    /// <summary>路径的一步：进入某个容器，再取该容器里的第几个。</summary>
    private readonly struct Step
    {
        public Step(int owner, int kind, int index) { Owner = owner; Kind = kind; Index = index; }
        public int Owner { get; }
        public int Kind { get; }
        public int Index { get; }
    }

    private List<Step> PathOf(Block target)
    {
        if (target == null || _ctrl == null) return null;
        var root = _ctrl.RootListOf(_scopeTab);
        if (root == null) return null;
        var path = new List<Step>();
        return Walk(root, target, path) ? path : null;
    }

    private static bool Walk(List<Block> list, Block target, List<Step> path)
    {
        if (list == null) return false;
        for (int i = 0; i < list.Count; i++)
        {
            var b = list[i];
            if (b == null) continue;
            if (ReferenceEquals(b, target)) { path.Add(new Step(i, 0, i)); return true; }
            if (b.Stacks != null && b.Stacks.Count > 0)
            {
                path.Add(new Step(i, 1, 0));
                if (Walk(b.Stacks, target, path)) return true;
                path.RemoveAt(path.Count - 1);
            }
            if (b.ElseStacks != null && b.ElseStacks.Count > 0)
            {
                path.Add(new Step(i, 2, 0));
                if (Walk(b.ElseStacks, target, path)) return true;
                path.RemoveAt(path.Count - 1);
            }
            if (b.Branches != null)
            {
                for (int k = 0; k < b.Branches.Count; k++)
                {
                    var br = b.Branches[k];
                    if (br == null || br.Count == 0) continue;
                    path.Add(new Step(i, 3, k));
                    if (Walk(br, target, path)) return true;
                    path.RemoveAt(path.Count - 1);
                }
            }
        }
        return false;
    }

    private Block ResolvePath(CardBlockWorkspace ws, List<Step> path)
    {
        var cur = _ctrl?.RootListOf(_scopeTab);
        if (cur == null || path == null || path.Count == 0) return null;
        Block chosen = null;
        foreach (var st in path)
        {
            if (st.Kind == 0)
            {
                if (cur == null || st.Index < 0 || st.Index >= cur.Count) return null;
                chosen = cur[st.Index];
                continue;
            }
            if (cur == null || st.Owner < 0 || st.Owner >= cur.Count) return null;
            var owner = cur[st.Owner];
            if (owner == null) return null;
            if (st.Kind == 1) cur = owner.Stacks;
            else if (st.Kind == 2) cur = owner.ElseStacks;
            else
            {
                if (owner.Branches == null || st.Index < 0 || st.Index >= owner.Branches.Count) return null;
                cur = owner.Branches[st.Index];
            }
        }
        return chosen;
    }

    private static void BuildIntEditor(VBoxContainer col, string label, int cur, Action<int> setter)
    {
        var row = new HBoxContainer();
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(92, 0) };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);
        var spin = new SpinBox
        {
            MinValue = -999, MaxValue = 999, Step = 1, Value = cur,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        spin.ValueChanged += v => setter((int)v);
        row.AddChild(spin);
        col.AddChild(row);
    }

    private static void BuildTextEditor(VBoxContainer col, string label, string cur, Action<string> setter)
    {
        var row = new HBoxContainer();
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(92, 0) };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);
        var edit = new LineEdit { Text = cur ?? "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        edit.TextSubmitted += v => setter(v);
        edit.FocusExited += () => setter(edit.Text);
        row.AddChild(edit);
        col.AddChild(row);
    }

    private static void BuildInlineText(VBoxContainer col, string label, string cur, Action<string> setter)
    {
        var row = new HBoxContainer();
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(76, 0) };
        lbl.AddThemeFontSizeOverride("font_size", 10);
        row.AddChild(lbl);
        var edit = new LineEdit { Text = cur ?? "", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        edit.TextSubmitted += v => setter(v);
        edit.FocusExited += () => setter(edit.Text);
        row.AddChild(edit);
        col.AddChild(row);
    }

    private static void BuildInlineInt(VBoxContainer col, string label, int cur, Action<int> setter)
    {
        var row = new HBoxContainer();
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(76, 0) };
        lbl.AddThemeFontSizeOverride("font_size", 10);
        row.AddChild(lbl);
        var spin = new SpinBox
        {
            MinValue = -99, MaxValue = 99, Step = 1, Value = cur,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        spin.ValueChanged += v => setter((int)v);
        row.AddChild(spin);
        col.AddChild(row);
    }

    private static readonly string[] TargetOps = { "tgt_slot", "tgt_leader", "tgt_query" };

    private void BuildTargetSlotEditor(VBoxContainer col, string label, Block cur, Action<Block> setter)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("6EE7B7"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        opts.AddItem("（无）");
        foreach (var o in TargetOps) opts.AddItem(o);
        opts.Selected = cur == null ? 0 : Math.Max(0, Array.IndexOf(TargetOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { setter(null); return; }
            var op = TargetOps[(int)idx - 1];
            if (cur != null && cur.Opcode == op) return;
            var made = new Block { Opcode = op };
            if (op == "tgt_slot") made.Text = TargetSpec.SlotSelf;
            else if (op == "tgt_leader") made.EnumA = "Self";
            else
            {
                made.IntA = 0; made.IntB = 1; made.Text = "Follower"; made.EnumA = "Choose";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
            }
            setter(made);
        };
        box.AddChild(opts);

        if (cur != null)
        {
            if (cur.Opcode == "tgt_slot")
                BuildInlineText(box, "槽位", cur.Text, v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "tgt_leader")
                BuildInlineText(box, "归属", cur.EnumA, v => { cur.EnumA = v; setter(cur); });
            else if (cur.Opcode == "tgt_query")
            {
                BuildInlineInt(box, "区域(0场1手2牌3墓)", cur.IntA, v => { cur.IntA = v; setter(cur); });
                BuildInlineInt(box, "归属(0我1敌2双)", cur.IntB, v => { cur.IntB = v; setter(cur); });
                BuildInlineText(box, "卡种", cur.Text, v => { cur.Text = v; setter(cur); });
                BuildInlineText(box, "选法", cur.EnumA, v => { cur.EnumA = v; setter(cur); });
                var nb = new VBoxContainer();
                box.AddChild(nb);
                BuildNumberSlotEditor(nb, "选取数量", cur.Number, v => { cur.Number = v; setter(cur); });
            }
        }
        col.AddChild(box);
    }

    private static readonly string[] NumOps =
        { "num_const", "num_rand", "num_counter", "num_prop", "num_count", "num_arith" };

    private void BuildNumberSlotEditor(VBoxContainer col, string label, Block cur, Action<Block> setter)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("6EE7B7"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        opts.AddItem("（无）");
        foreach (var o in NumOps) opts.AddItem(o);
        opts.Selected = cur == null ? 0 : Math.Max(0, Array.IndexOf(NumOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { setter(null); return; }
            var op = NumOps[(int)idx - 1];
            if (cur != null && cur.Opcode == op) return;
            var made = new Block { Opcode = op };
            if (op == "num_const") made.IntA = cur?.IntA ?? 1;
            else if (op == "num_rand") { made.IntA = 2; made.IntB = 10; made.IntC = 0; }
            else if (op == "num_counter") made.Text = "pp";
            else if (op == "num_prop") made.EnumA = "Attack";
            else if (op == "num_arith")
            {
                made.EnumA = "Add";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
                made.Number2 = new Block { Opcode = "num_const", IntA = 1 };
            }
            setter(made);
        };
        box.AddChild(opts);

        if (cur != null)
        {
            if (cur.Opcode == "num_const")
                BuildInlineInt(box, "常量", cur.IntA, v => { cur.IntA = v; setter(cur); });
            else if (cur.Opcode == "num_rand")
            {
                BuildInlineInt(box, "下界", cur.IntA, v => { cur.IntA = v; setter(cur); });
                BuildInlineInt(box, "上界", cur.IntB, v => { cur.IntB = v; setter(cur); });
                BuildInlineInt(box, "变量槽", cur.IntC, v => { cur.IntC = v; setter(cur); });
            }
            else if (cur.Opcode == "num_counter")
                BuildInlineText(box, "计数器", cur.Text, v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "num_prop")
                BuildInlineText(box, "属性", cur.EnumA, v => { cur.EnumA = v; setter(cur); });
            else if (cur.Opcode == "num_arith")
            {
                BuildInlineText(box, "运算符", cur.EnumA, v => { cur.EnumA = v; setter(cur); });
                var n1 = new VBoxContainer(); box.AddChild(n1);
                BuildNumberSlotEditor(n1, "左值", cur.Number, v => { cur.Number = v; setter(cur); });
                var n2 = new VBoxContainer(); box.AddChild(n2);
                BuildNumberSlotEditor(n2, "右值", cur.Number2, v => { cur.Number2 = v; setter(cur); });
            }
        }
        col.AddChild(box);
    }

    private static readonly string[] CondOps =
        { "bool_compare", "bool_exists", "bool_has_kw", "bool_has_trait",
          "bool_has_clan", "bool_has_state", "bool_and", "bool_or", "bool_not" };

    private void BuildConditionSlotEditor(VBoxContainer col, string label, Block cur, Action<Block> setter)
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("C4B5FD"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        opts.AddItem("（无条件）");
        foreach (var o in CondOps) opts.AddItem(o);
        opts.Selected = cur == null ? 0 : Math.Max(0, Array.IndexOf(CondOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { setter(null); return; }
            var op = CondOps[(int)idx - 1];
            if (cur != null && cur.Opcode == op) return;
            var made = new Block { Opcode = op };
            if (op == "bool_compare")
            {
                made.EnumA = "Ge";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
                made.Number2 = new Block { Opcode = "num_const", IntA = 1 };
            }
            else if (op == "bool_has_kw") made.Text = "ward";
            else if (op == "bool_has_trait") made.Text = "Fairy";
            else if (op == "bool_has_clan") made.Text = "Forestcraft";
            else if (op == "bool_has_state") made.Text = "Evolved";
            else if (op == "bool_and" || op == "bool_or") made.Stacks = new List<Block>();
            else if (op == "bool_not") made.Condition = new Block { Opcode = "bool_true" };
            setter(made);
        };
        box.AddChild(opts);

        if (cur != null)
        {
            if (cur.Opcode == "bool_compare")
            {
                BuildInlineText(box, "比较符", cur.EnumA, v => { cur.EnumA = v; setter(cur); });
                var n1 = new VBoxContainer(); box.AddChild(n1);
                BuildNumberSlotEditor(n1, "左值", cur.Number, v => { cur.Number = v; setter(cur); });
                var n2 = new VBoxContainer(); box.AddChild(n2);
                BuildNumberSlotEditor(n2, "右值", cur.Number2, v => { cur.Number2 = v; setter(cur); });
            }
            else if (cur.Opcode == "bool_has_kw" || cur.Opcode == "bool_has_trait"
                  || cur.Opcode == "bool_has_clan" || cur.Opcode == "bool_has_state")
                BuildInlineText(box, "取值", cur.Text, v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "bool_not")
            {
                var nb = new VBoxContainer(); box.AddChild(nb);
                BuildConditionSlotEditor(nb, "内层条件", cur.Condition, v => { cur.Condition = v; setter(cur); });
            }
            else if (cur.Opcode == "bool_and" || cur.Opcode == "bool_or")
            {
                var add = new Button { Text = "+ 添加子条件", CustomMinimumSize = new Vector2(0, 24) };
                SvWbTheme.StyleButton(add);
                add.Pressed += () =>
                {
                    cur.Stacks ??= new List<Block>();
                    cur.Stacks.Add(new Block { Opcode = "bool_true" });
                    setter(cur);
                };
                box.AddChild(add);
            }
        }
        col.AddChild(box);
    }

    private void BuildReadOnly(CardBlockWorkspace ws)
    {
        if (ws.DeckModBlocks != null)
            foreach (var b in ws.DeckModBlocks) AddChild(BlockLeaf(b, 0));
        if (ws.Keywords != null && ws.Keywords.Count > 0)
            AddChild(BlockBox(BlockShape.Pill, "关键词：" + string.Join("、", ws.Keywords), null));
        if (ws.Hats != null)
            foreach (var h in ws.Hats) AddChild(BlockLeaf(h, 0));
    }

    private Control BlockLeaf(Block blk, int depth)
    {
        if (blk == null) return new Control();
        var wrap = new VBoxContainer();
        wrap.AddThemeConstantOverride("separation", 4);
        wrap.AddChild(BlockBox(blk.Shape, BuildLabel(blk), null, depth));
        foreach (var inner in blk.Stacks ?? new List<Block>()) wrap.AddChild(BlockLeaf(inner, depth + 1));
        foreach (var inner in blk.ElseStacks ?? new List<Block>()) wrap.AddChild(BlockLeaf(inner, depth + 1));
        if (blk.Branches != null)
            foreach (var inner in blk.Branches)
                if (inner != null)
                    foreach (var b in inner) wrap.AddChild(BlockLeaf(b, depth + 1));
        return wrap;
    }

    /// <summary>目标的中文速写：让画布上每颗积木都能看清「打谁」。</summary>
    private static string DescribeTargetShort(Block t)
    {
        if (t == null) return "（未指定）";
        switch (t.Opcode)
        {
            case "tgt_slot":
                return t.Text switch
                {
                    "$self" => "本卡",
                    "$selected" => "已选目标",
                    "$created" => "刚生成的",
                    "$trigger" => "触发者",
                    "$opposing" => "对阵者",
                    _ => t.Text ?? "槽位",
                };
            case "tgt_leader":
                return t.EnumA == "Opponent" ? "敌方主战者"
                     : t.EnumA == "Any" ? "双方主战者" : "我方主战者";
            case "tgt_query":
            {
                string owner = t.IntA == 0 ? "我方" : t.IntA == 1 ? "敌方" : "双方";
                string zone = (t.IntB == 0 ? "战场" : t.IntB == 1 ? "手牌"
                             : t.IntB == 2 ? "牌组" : t.IntB == 3 ? "墓场" : "区域");
                string mode = t.EnumA == "Choose" ? "手选"
                              : t.EnumA == "Random" ? "随机"
                              : t.EnumA == "First" ? "第一个" : "全部";
                string kind = string.IsNullOrEmpty(t.Text) ? "" : (t.Text == "Follower" ? "随从" : t.Text);
                return owner + zone + "·" + mode + (kind.Length > 0 ? "·" + kind : "");
            }
            default:
                return "目标";
        }
    }

    private static string NumText(Block numBlk, int fallback = 1)
    {
        if (numBlk == null) return fallback.ToString();
        return numBlk.Opcode switch
        {
            "num_const" => numBlk.IntA.ToString(),
            "num_counter" => "【" + (numBlk.Text ?? "计数") + "】",
            "num_count" => "统计数量",
            "num_prop" => (numBlk.EnumA ?? "属性"),
            "num_rand" => "随机" + numBlk.IntA + "~" + numBlk.IntB,
            "num_arith" => "(" + (numBlk.EnumA ?? "+") + ")",
            _ => string.IsNullOrEmpty(numBlk.Opcode) ? fallback.ToString() : numBlk.Opcode,
        };
    }

    private static string BuildLabel(Block blk)
    {
        if (blk == null) return "";
        string op = blk.Opcode ?? "";
        switch (op)
        {
            case "hat_self_event": return "当本卡" + DiyZh.Zh(DiyZh.Events, blk.Text) + "";
            case "hat_watch_event": return "每当【" + DescribeTargetShort(blk.Target) + "】" + DiyZh.Zh(DiyZh.Events, blk.Text) + "";
            case "hat_passive": return "常驻光环";
            case "c_if_else": return "如果满足条件，则…否则…";
            case "c_repeat": return "重复 " + NumText(blk.Number, 2) + " 次";
            case "c_for_each": return "对每个目标";
            case "c_mode": return "模式抉择（选 " + NumText(blk.Number, blk.IntA) + " 个）";
            case "act_damage": return "对【" + DescribeTargetShort(blk.Target) + "】造成【" + NumText(blk.Number, 2) + "】点伤害";
            case "act_split_damage": return "向【" + DescribeTargetShort(blk.Target) + "】分配【" + NumText(blk.Number, 2) + "】点伤害";
            case "act_restore": return "为【" + DescribeTargetShort(blk.Target) + "】回复【" + NumText(blk.Number, 2) + "】点生命";
            case "act_full_restore": return "完全回复生命";
            case "act_draw": return "抽取 " + NumText(blk.Number, 1) + " 张牌";
            case "act_summon": return "召唤「" + (blk.Text ?? "?") + "」";
            case "act_add_hand": return "生成「" + (blk.Text ?? "?") + "」加入手牌";
            case "act_destroy": return "破坏【" + DescribeTargetShort(blk.Target) + "】";
            case "act_banish": return "使【" + DescribeTargetShort(blk.Target) + "】消失";
            case "act_bounce": return "将【" + DescribeTargetShort(blk.Target) + "】返回手牌";
            case "act_return_deck": return "将目标洗回牌组";
            case "act_discard": return "舍弃手牌";
            case "act_silence": return "沉默目标";
            case "act_buff": return "使【" + DescribeTargetShort(blk.Target) + "】获得【" + NumText(blk.Number, 1) + "/" + NumText(blk.Number2, 1) + "】";
            case "act_set_stats": return "设定攻防为 " + NumText(blk.Number, 1) + "/" + NumText(blk.Number2, 1);
            case "act_swap_stats": return "交换攻防";
            case "act_mod_cost": return "使【" + DescribeTargetShort(blk.Target) + "】的费用【增减】【" + NumText(blk.Number, 1) + "】";
            case "act_grant_kw": return "给予【" + DescribeTargetShort(blk.Target) + "】【" + DiyZh.Zh(DiyZh.Keywords, blk.Text) + "】";
            case "act_remove_kw": return "移除【" + DescribeTargetShort(blk.Target) + "】的【" + DiyZh.Zh(DiyZh.Keywords, blk.Text) + "】";
            case "act_evolve": return "使目标进化";
            case "act_super_evolve": return "使目标超进化";
            case "act_reveal": return "公开手牌";
            case "act_mod_counter": return "修改计数器【" + (blk.Text ?? "?") + "】";
            case "act_gain_crest": return "获得纹章「" + (blk.Text ?? "?") + "」";
            case "act_copy": return "复制目标";
            case "act_discover": return "从卡池发现";
            case "act_reanimate": return "亡者召还";
            case "act_transform": return "使目标变身为「" + (blk.Text ?? "?") + "」";
            case "act_replace_deck": return "替换整副牌组";
            case "act_damage_aura": return "护盾光环：使【" + DescribeTargetShort(blk.Target) + "】" + DiyZh.Zh(DiyZh.DamageAuraKinds, blk.EnumA) + "【" + NumText(blk.Number, 3) + "】";
            case "act_grant_entity_ability": return "赋予目标一段能力";
            case "act_grant_ability": return "赋予常驻能力";
            case "act_invoke": return "复刻发动时机能力";
            case "act_deck_mod": return "构筑规则修正";
            case "act_move_to_board": return "移动到战场";
            case "act_select": return "显式选择目标";
            default: return string.IsNullOrEmpty(op) ? "(空积木)" : op;
        }
    }

    private static Control BlockBox(BlockShape shape, string text, string pill, int depth = 0)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 8 + depth * 12);
        margin.AddThemeConstantOverride("margin_right", 8);
        margin.AddThemeConstantOverride("margin_top", shape == BlockShape.Pill ? 3 : 6);
        margin.AddThemeConstantOverride("margin_bottom", shape == BlockShape.Pill ? 3 : 6);

        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            ShapeColor(shape), new Color(1, 1, 1, 0.28f), 6, 1, 4));
        margin.AddChild(box);

        var lbl = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        lbl.AddThemeFontSizeOverride("font_size", shape == BlockShape.Pill ? 11 : 12);
        lbl.AddThemeColorOverride("font_color", Colors.White);
        box.AddChild(lbl);

        if (!string.IsNullOrEmpty(pill))
        {
            var p = new Label { Text = pill };
            p.AddThemeFontSizeOverride("font_size", 10);
            p.AddThemeColorOverride("font_color", new Color("FEF08A"));
            box.AddChild(p);
        }

        row.AddChild(margin);
        return row;
    }
}

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
        SizeFlagsVertical = SizeFlags.ExpandFill;
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        AddThemeConstantOverride("separation", 6);
        AddTitle();
        AddChild(BuildScopeTabs());

        var wb = new HBoxContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        wb.AddThemeConstantOverride("separation", 8);
        AddChild(wb);

        // ① 左栏：调色盘（独立纵向滚动）
        wb.AddChild(BuildPaletteColumn());

        // ② 中栏：积木画布（独立纵向滚动，禁横向）
        var canvasScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(canvasScroll);
        wb.AddChild(canvasScroll);

        _canvasCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ClipContents = true,
        };
        _canvasCol.AddThemeConstantOverride("separation", 4);
        canvasScroll.AddChild(_canvasCol);

        // ③ 右栏：属性检视器 —— 钉死在视口右侧，绝不随中间画布滚走。
        // 早先三栏共用同一个外层 ScrollContainer，滚到底部点积木时
        // 属性面板停在画布顶部（Y=0）早已滚出视野，逼玩家再拉回顶部。
        var inspectorPanel = new PanelContainer
        {
            CustomMinimumSize = new Vector2(265, 0),
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        inspectorPanel.AddThemeStyleboxOverride("panel",
            SvWbTheme.MakePanelBox(SvWbTheme.BgSurface, new Color(1, 1, 1, 0.10f), 8, 1, 6));
        wb.AddChild(inspectorPanel);

        var inspScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(inspScroll);
        inspectorPanel.AddChild(inspScroll);

        _inspectorCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // 必须纵向填满：外层 ScrollContainer 按最小尺寸定高，
            // 不给 ExpandFill 的话内容会被压扁到看不见。
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _inspectorCol.AddThemeConstantOverride("separation", 4);
        inspScroll.AddChild(_inspectorCol);

        RedrawCanvas();
        RedrawInspector();
    }

    /// <summary>
    /// 确认插入点指向**当前这棵树**上的列表。
    ///
    /// 不能只在 null 时兜底：CompileFromBlocks 会整棵重建 BlockModel，
    /// 旧树的 List<Block> 就成了野指针，再往里加积木玩家什么也看不到。
    /// 所以每次都要校验它是否还属于当前树，不属于就重新定位。
    /// </summary>
    private void EnsureInsertList()
    {
        var root = _ctrl?.RootListOf(_scopeTab) ?? ScopeRootFallback();
        if (root == null) return;

        // 插入点还在当前树上、且不是域根（域根只收事件帽）-> 保持不动。
        // 必须先判 ReferenceEquals(_insertList, root)：BelongsToTree 对旧树引用
        // 必然返回 false，不判 root 相等的话，每次改属性都会把玩家设的
        // 插入点（比如 if-else 的否则分支）重置回 Hats 根。
        if (_insertList != null && !ReferenceEquals(_insertList, root) && BelongsToTree(_insertList))
            return;

        if (_scopeTab == 5)
        {
            // 构筑域本来就是平铺列表，直接用根。
            _insertList = root;
        }
        else if (root.Count > 0 && (root[^1].Opcode ?? "").StartsWith("hat_"))
        {
            // 动作积木绝不能落进 Hats 根列表 —— 那里只收事件帽，
            // 塞进去编译会报「不是事件帽积木」。
            root[^1].Stacks ??= new List<Block>();
            _insertList = root[^1].Stacks;
        }
        else
        {
            _insertList = root;
        }
        _ctrl?.SetActiveInsertList(_insertList);
    }


    /// <summary>这个列表还是当前树的一部分吗（防止重编译后的野指针）。</summary>
    private bool BelongsToTree(List<Block> list)
    {
        if (list == null || _ws == null) return false;
        return ContainsList(_ws, list);
    }

    private static bool ContainsList(CardBlockWorkspace ws, List<Block> target)
    {
        if (ws == null || target == null) return false;
        bool hit = false;
        void Walk(List<Block> lst)
        {
            if (hit || lst == null) return;
            if (ReferenceEquals(lst, target)) { hit = true; return; }
            foreach (var b in lst) WalkBlock(b);
        }
        void WalkBlock(Block b)
        {
            if (hit || b == null) return;
            Walk(b.Stacks);
            Walk(b.ElseStacks);
            if (b.Branches != null) foreach (var br in b.Branches) Walk(br);
        }
        Walk(ws.Hats); Walk(ws.CrestHats); Walk(ws.FaithHats);
        Walk(ws.AccelerateHats); Walk(ws.CrystallizeHats); Walk(ws.DeckModBlocks);
        return hit;
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
        // HFlowContainer：6 个能力域页签在窄屏下自动折行，绝不横向撑爆右半屏。
        var row = new HFlowContainer();
        row.AddThemeConstantOverride("h_separation", 6);
        row.AddThemeConstantOverride("v_separation", 4);
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
            ("c_repeat_until", "重复直到…"),
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

    /// <summary>【关联纹章】页签的引导栏：说明纹章机制并提供一键初始化。</summary>
    private void BuildCrestHelperBanner()
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.AccentCyan, 8, 1, 2));
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        box.AddChild(col);

        var hint = new Label
        {
            Text = "💡【关联纹章说明】：此处定义的事件帽属于本卡关联的【纹章】能力。" +
                   "别忘了在「本体能力」里加一块「获得纹章」积木来授予它！",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color("7DD3FC"));
        col.AddChild(hint);

        var quickBtn = new Button
        {
            Text = "⚡ 一键初始化：本体加「入场曲：获得此纹章」+ 此处创建纹章能力帽",
            CustomMinimumSize = new Vector2(0, 28),
        };
        SvWbTheme.StyleButton(quickBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        quickBtn.AddThemeFontSizeOverride("font_size", 11);
        quickBtn.Pressed += () =>
        {
            if (_ws == null || _ctrl == null) return;
            string crestName = !string.IsNullOrWhiteSpace(_ws.Name) ? _ws.Name : "DIY Crest";

            // 1. 本体：没有事件帽就补一顶，然后追加「获得纹章」积木。
            _ws.Hats ??= new List<Block>();
            if (_ws.Hats.Count == 0)
                _ws.Hats.Add(DiyBlockTree.CreateDefaultBlock("hat_self_event"));
            _ws.Hats[0].Stacks ??= new List<Block>();
            _ws.Hats[0].Stacks.Add(new Block { Opcode = "act_gain_crest", Text = crestName, EnumA = "Self" });

            // 2. 纹章域：空的话补一顶常驻帽与一顶回合末帽。
            _ws.CrestHats ??= new List<Block>();
            if (_ws.CrestHats.Count == 0)
            {
                _ws.CrestHats.Add(new Block
                {
                    Opcode = "hat_passive", Text = "PassiveAura",
                    Costs = new List<(string, int)> { ("countdown", 3) },
                });
                var turnEndHat = new Block { Opcode = "hat_self_event", Text = "TurnEnd", DuringTurn = "Self" };
                turnEndHat.Stacks ??= new List<Block>();
                turnEndHat.Stacks.Add(DiyBlockTree.CreateDefaultBlock("act_draw"));
                _ws.CrestHats.Add(turnEndHat);
            }

            _insertList = _ws.CrestHats[^1].Stacks;
            _ctrl.SetActiveInsertList(_insertList);
            Apply();
            Refresh();
        };
        col.AddChild(quickBtn);

        _canvasCol.AddChild(box);
    }

    /// <summary>【关联信仰】页签的引导栏：说明信仰机制并给涨点模板。</summary>
    private void BuildFaithHelperBanner()
    {
        var box = new PanelContainer();
        box.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgSurface, SvWbTheme.AccentGold, 8, 1, 2));
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 6);
        box.AddChild(col);

        var hint = new Label
        {
            Text = "💡【关联信仰说明】：只要此页签非空，开局时就会自动在我方纹章区生成本卡的【信仰】" +
                   "（初始值 0）。此页签配置的是「信仰怎么涨点」。",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", new Color("FEF08A"));
        col.AddChild(hint);

        var quickBtn = new Button
        {
            Text = "⚡ 一键生成信仰模板（初始值 0 + 每当友方随从进化时信仰 +1）",
            CustomMinimumSize = new Vector2(0, 28),
        };
        SvWbTheme.StyleButton(quickBtn, isPrimary: true, customAccent: SvWbTheme.AccentGold);
        quickBtn.AddThemeFontSizeOverride("font_size", 11);
        quickBtn.Pressed += () =>
        {
            if (_ws == null || _ctrl == null) return;
            _ws.FaithHats ??= new List<Block>();
            _ws.FaithHats.Clear();

            // 1. 初始信仰值 = 0
            var initHat = new Block { Opcode = "hat_passive", Text = "PassiveAura" };
            initHat.Stacks ??= new List<Block>();
            initHat.Stacks.Add(new Block
            {
                Opcode = "act_mod_counter", Text = "faith", EnumA = "Set",
                Number = new Block { Opcode = "num_const", IntA = 0 },
            });
            _ws.FaithHats.Add(initHat);

            // 2. 每当友方随从进化时，信仰 +1
            var watchHat = DiyBlockTree.CreateDefaultBlock("hat_watch_event");
            watchHat.Text = "OnEvolve";
            watchHat.Stacks ??= new List<Block>();
            watchHat.Stacks.Add(new Block
            {
                Opcode = "act_mod_counter", Text = "faith", EnumA = "Add",
                Number = new Block { Opcode = "num_const", IntA = 1 },
            });
            _ws.FaithHats.Add(watchHat);

            _insertList = watchHat.Stacks;
            _ctrl.SetActiveInsertList(_insertList);
            Apply();
            Refresh();
        };
        col.AddChild(quickBtn);

        _canvasCol.AddChild(box);
    }
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

        // 167 §3.5：融合配置。HasFuse 是编译器认的字段，
        // 但积木端从来不给入口 —— 一张融合卡在积木端等于做不出来。
        {
            var fuseBox = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            fuseBox.AddThemeConstantOverride("separation", 4);
            var fuseTitle = new Label { Text = "融合（Fuse）" };
            fuseTitle.AddThemeFontSizeOverride("font_size", 13);
            fuseTitle.AddThemeColorOverride("font_color", new Color("FDE68A"));
            fuseBox.AddChild(fuseTitle);

            BuildBoolCheck(fuseBox, "启用【融合】", _ws?.HasFuse == true, v =>
            {
                if (_ws == null || _ctrl == null) return;
                _ws.HasFuse = v;
                _ctrl.CompileFromBlocks(_ws);
                _ws = _ctrl.BlockModel ?? _ws;
                Refresh();
            });
            if (_ws?.HasFuse == true)
                BuildTargetSlotEditor(fuseBox, "融合材料条件", _ws.FuseFilter, v =>
                {
                    if (_ws == null || _ctrl == null) return;
                    _ws.FuseFilter = v;
                    _ctrl.CompileFromBlocks(_ws);
                    _ws = _ctrl.BlockModel ?? _ws;
                });
            col.AddChild(fuseBox);
        }

        foreach (var (key, zh) in DiyZh.Keywords)
        {
            bool isParam = IsParameterizedKw(key);
            string matched = _ws?.Keywords?.FirstOrDefault(x => x == key || x.StartsWith(key + "("));
            int initN = 2;
            if (matched != null && matched.Contains('('))
            {
                string inner = matched.Substring(matched.IndexOf('(') + 1).TrimEnd(')');
                if (int.TryParse(inner, out int parsedN)) initN = parsedN;
            }

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 6);

            var cb = new CheckBox { Text = zh, ButtonPressed = matched != null };
            row.AddChild(cb);

            // 带参关键词要露出次数输入框：写死 (2) 的话玩家改不了「攻击 3 次」。
            SpinBox spin = null;
            if (isParam)
            {
                spin = new SpinBox
                {
                    MinValue = 1, MaxValue = 99, Value = initN,
                    Visible = matched != null,
                    CustomMinimumSize = new Vector2(64, 0),
                };
                row.AddChild(spin);
            }

            string captured = key;
            void UpdateKw()
            {
                if (_ws == null || _ctrl == null) return;
                _ws.Keywords ??= new List<string>();
                _ws.Keywords.RemoveAll(x => x == captured || x.StartsWith(captured + "("));
                if (cb.ButtonPressed)
                {
                    int n = spin != null ? (int)spin.Value : 2;
                    _ws.Keywords.Add(isParam ? captured + "(" + n + ")" : captured);
                }
                if (spin != null) spin.Visible = cb.ButtonPressed;

                _ctrl.CompileFromBlocks(_ws);
                _ws = _ctrl.BlockModel ?? _ws;
            }

            cb.Toggled += _ => UpdateKw();
            if (spin != null) spin.ValueChanged += _ => { if (cb.ButtonPressed) UpdateKw(); };

            col.AddChild(row);
        }

        _canvasCol.AddChild(col);
    }
    private Control BuildPaletteColumn()
    {
        // 167 §1.1：调色盘收窄到 165px，把空间让给中间画布。
        // 原来 185 + 检视器 265 一起吃掉了右半屏，画布只剩 400 出头，
        // 嵌套两层 if/else 后文字全变成「▸ 对【敌方...」。
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(165, 0), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
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
        // 把当前选中传下去：动作积木会插在它同级正下方，连续点击就能一路往下搭。
        if (!_ctrl.InsertBlock(opcode, _scopeTab, _selected)) return;
        _ws = _ctrl.BlockModel;

        // 167 §2.1：选中推进到新插入的那颗积木。
        // 不推进的话第二次点击仍然插在原来那颗下面，顺序会是倒的，
        // 玩家得每加一颗都重新点一次目标位置。
        // LastInsertedBlock 是 InsertBlock 内部插完才赋的引用，直接用它。
        if (_ctrl.LastInsertedBlock != null) _selected = _ctrl.LastInsertedBlock;

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

        // 这两个面板必须在「域为空」的提前返回**之前**渲染：
        // 激奏/结晶费用框正是用来把空域开启的 —— 藏在空域提示后面就等于永远看不见。
        if (_scopeTab == 5) BuildKeywordsPanel();
        if (_scopeTab == 1) BuildCrestHelperBanner();
        if (_scopeTab == 2) BuildFaithHelperBanner();
        if (_scopeTab == 3 || _scopeTab == 4) BuildAcceleratePanel();

        if (root.Count == 0)
        {
            var hint = new Label { Text = "（此能力域为空：从左侧调色盘点「事件帽」开始）", AutowrapMode = TextServer.AutowrapMode.WordSmart };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _canvasCol.AddChild(hint);
            return;
        }

        foreach (var b in root.ToList()) _canvasCol.AddChild(RenderBlock(b, 0));
    }

    private Control RenderBlock(Block blk, int depth)
    {
        if (blk == null) return new Control();
        bool isSelected = ReferenceEquals(_selected, blk);
        bool isHat = (blk.Opcode ?? "").StartsWith("hat_");
        bool isCBlock = blk.Shape == BlockShape.C
                        || blk.Opcode is "act_grant_entity_ability" or "act_grant_ability";

        var wrap = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        wrap.AddThemeConstantOverride("separation", 0);

        // ── 积木头部横条：固定 36px 高，绝不随父容器纵向拉成柱子 ──
        var headerPanel = new PanelContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 36),
            MouseFilter = Control.MouseFilterEnum.Stop,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
        };
        Color baseCol = ShapeColor(blk.Shape);
        headerPanel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            isSelected ? baseCol.Lightened(0.18f) : baseCol,
            isSelected ? new Color("FDE047") : new Color(1, 1, 1, 0.32f),
            isHat ? 10 : 6, isSelected ? 3 : 1, isSelected ? 8 : 2));

        headerPanel.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _selected = blk;
                RedrawCanvas();
                RedrawInspector();
                headerPanel.AcceptEvent();
            }
        };

        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 6);
        headerPanel.AddChild(row);

        var icon = new Label
        {
            Text = isHat ? "🎩" : isCBlock ? "🔀" : "▸",
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.AddThemeFontSizeOverride("font_size", 13);
        row.AddChild(icon);

        var lbl = new Label
        {
            Text = BuildLabel(blk),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
            ClipText = true,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        lbl.AddThemeFontSizeOverride("font_size", 13);
        lbl.AddThemeColorOverride("font_color", Colors.White);
        row.AddChild(lbl);

        if (_editable && _ctrl != null)
        {
            AddMiniBtn(row, "▲", "上移", () => { _ctrl.MoveBlockInTree(blk, -1); _ws = _ctrl.BlockModel; Refresh(); });
            AddMiniBtn(row, "▼", "下移", () => { _ctrl.MoveBlockInTree(blk, 1); _ws = _ctrl.BlockModel; Refresh(); });
            AddMiniBtn(row, "⧉", "复制", () => { _ctrl.DuplicateBlockInTree(blk); _ws = _ctrl.BlockModel; Refresh(); });
            AddMiniBtn(row, "✕", "删除", () =>
            {
                _ctrl.RemoveBlockFromTree(blk);
                if (ReferenceEquals(_selected, blk)) _selected = null;
                _ws = _ctrl.BlockModel;
                Refresh();
            }, SvWbTheme.StatusDanger);
        }

        wrap.AddChild(headerPanel);

        // ── 只有事件帽与控制流块才展开子槽；普通动作块绝不显示 Then 流 ──
        if (isHat)
        {
            blk.Stacks ??= new List<Block>();
            wrap.AddChild(RenderBracketSlot("⚡ 触发后依次执行", blk.Stacks, baseCol, depth));
        }
        else if (blk.Opcode == "c_if_else")
        {
            blk.Stacks ??= new List<Block>();
            blk.ElseStacks ??= new List<Block>();
            wrap.AddChild(RenderBracketSlot("┣ 【那么 (满足条件时)】", blk.Stacks, baseCol, depth));
            wrap.AddChild(RenderBracketSlot("┗ 【否则 (不满足时)】", blk.ElseStacks, baseCol.Darkened(0.2f), depth));
        }
        else if (blk.Opcode is "c_repeat" or "c_repeat_until" or "c_for_each"
                 or "act_grant_entity_ability" or "act_grant_ability")
        {
            blk.Stacks ??= new List<Block>();
            wrap.AddChild(RenderBracketSlot("🔁 内部执行的动作", blk.Stacks, baseCol, depth));
        }
        else if (blk.Opcode == "c_mode" && blk.Branches != null)
        {
            for (int i = 0; i < blk.Branches.Count; i++)
            {
                int idx = i;
                var br = blk.Branches[i];
                wrap.AddChild(RenderBracketSlot($"❖ 模式选项 ({idx + 1})", br, baseCol, depth,
                    onRemoveBranch: blk.Branches.Count > 1 ? () =>
                    {
                        blk.Branches.RemoveAt(idx);
                        Apply();
                    } : null));
            }
        }

        return wrap;
    }

    /// <summary>渲染 C 型夹子凹槽：左侧贯穿色带 + 缩进子积木 + 设为插入点。</summary>
    private Control RenderBracketSlot(string slotTitle, List<Block> list, Color spineCol, int depth,
                                      Action onRemoveBranch = null)
    {
        bool isFocus = ReferenceEquals(_insertList, list);
        var hbox = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        hbox.AddThemeConstantOverride("separation", 0);

        var spine = new ColorRect
        {
            Color = isFocus ? new Color("FDE047") : spineCol,
            CustomMinimumSize = new Vector2(8, 0),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        hbox.AddChild(spine);

        var innerMargin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        innerMargin.AddThemeConstantOverride("margin_left", 10);
        innerMargin.AddThemeConstantOverride("margin_top", 4);
        innerMargin.AddThemeConstantOverride("margin_bottom", 6);
        hbox.AddChild(innerMargin);

        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 4);
        innerMargin.AddChild(col);

        var bar = new HBoxContainer();
        bar.AddThemeConstantOverride("separation", 6);
        var titleLbl = new Label
        {
            Text = slotTitle + $"（{list?.Count ?? 0} 步）",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        titleLbl.AddThemeFontSizeOverride("font_size", 11);
        titleLbl.AddThemeColorOverride("font_color", isFocus ? new Color("FEF08A") : SvWbTheme.TextSecondary);
        bar.AddChild(titleLbl);

        var focusBtn = new Button
        {
            Text = isFocus ? "● 当前插入点" : "⊕ 设为插入点",
            CustomMinimumSize = new Vector2(0, 24),
        };
        SvWbTheme.StyleButton(focusBtn, isPrimary: isFocus,
                              customAccent: isFocus ? SvWbTheme.AccentGold : (Color?)null);
        focusBtn.AddThemeFontSizeOverride("font_size", 11);
        focusBtn.Pressed += () =>
        {
            _insertList = list;
            _ctrl?.SetActiveInsertList(list);
            RedrawCanvas();
        };
        bar.AddChild(focusBtn);

        if (onRemoveBranch != null)
        {
            var rmBtn = new Button { Text = "🗑 删分支", CustomMinimumSize = new Vector2(0, 24) };
            SvWbTheme.StyleButton(rmBtn, customAccent: SvWbTheme.StatusDanger);
            rmBtn.AddThemeFontSizeOverride("font_size", 11);
            rmBtn.Pressed += onRemoveBranch;
            bar.AddChild(rmBtn);
        }
        col.AddChild(bar);

        if (list != null && list.Count > 0)
        {
            foreach (var child in list.ToList()) col.AddChild(RenderBlock(child, depth + 1));
        }
        else
        {
            var emptyHint = new Label { Text = "    （空槽位：点「⊕ 设为插入点」后从左侧点选动作积木）" };
            emptyHint.AddThemeFontSizeOverride("font_size", 11);
            emptyHint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            col.AddChild(emptyHint);
        }

        return hbox;
    }

    /// <summary>28×28 的迷你操作钮。必须 ShrinkCenter —— 否则会随父容器纵向拉成高柱。</summary>
    private static void AddMiniBtn(HBoxContainer row, string txt, string tip, Action onClick, Color? accent = null)
    {
        var b = new Button
        {
            Text = txt,
            TooltipText = tip,
            CustomMinimumSize = new Vector2(28, 28),
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        b.AddThemeFontSizeOverride("font_size", 12);
        SvWbTheme.StyleButton(b, customAccent: accent);
        b.Pressed += onClick;
        row.AddChild(b);
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

            // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

            SvWbTheme.StyleOptionPopup(opt);
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

        // 不要再套一层 ScrollContainer！
        //
        // 外层 inspScroll 会按子节点的**最小尺寸**给 _inspectorCol 定高，
        // 而内层 ScrollContainer 的最小高度恒为 0 且自带裁剪 ——
        // 结果内层高度永远是 0px，下面所有下拉框/数值框全被裁掉，
        // 玩家只看到一行标题，什么都改不了。
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 5);
        _inspectorCol.AddChild(col);

        // 按 opcode 定制：每种积木只渲染它自己真正拥有的孔位。
        // 原来不管点中什么都列出「整数A / 文本 / 枚举A」七个裸字段，
        // 玩家想把入场曲改成谢幕曲得自己猜着敲 OnDestroy —— 那不是 Scratch。
        BuildScriptSpecificInspector(col, _selected);
    }

    /// <summary>按积木语义定制的中文表单。</summary>

    /// <summary>
    /// 可选计数器：内置词表 + 当前工作区 Schema 里注册的自定义计数器。
    ///
    /// 只列 DiyZh.Counters 的话，玩家在卡池里自定义的计数器在积木端根本选不到，
    /// 而效果里写 ctr:my_charge 又是合法的 —— 表现为「功能存在但没法用」。
    /// </summary>
    private (string Key, string Zh)[] AvailableCounters()
    {
        var list = new List<(string, string)>(DiyZh.Counters);
        var seen = new HashSet<string>(DiyZh.Counters.Select(x => x.Key), StringComparer.OrdinalIgnoreCase);
        try
        {
            var schema = _ctrl?.Workspace?.Schema;
            if (schema != null)
            {
                foreach (var c in schema.Counters.All)
                {
                    if (string.IsNullOrWhiteSpace(c.Name) || seen.Contains(c.Name)) continue;
                    var gl = _ctrl.Workspace.Glossary;
                    string zh = gl != null ? gl.ToChinese(c.Name) : c.Name;
                    list.Add((c.Name, zh));
                    seen.Add(c.Name);
                }
            }
        }
        catch (Exception) { }
        return list.ToArray();
    }


    /// <summary>编辑 Block 上的 int 字段（IntA / IntB 这类，不是积木槽位）。</summary>

    /// <summary>布尔勾选项：直接写 Block 上的 bool 字段（FlagA / FlagB 这类）。</summary>

    /// <summary>可选兵种：从当前工作区 Schema 读（含卡池自定义）。</summary>
    private (string Key, string Zh)[] AvailableTraits()
    {
        var list = new List<(string, string)>();
        try
        {
            var schema = _ctrl?.Workspace?.Schema;
            if (schema != null)
                foreach (var t in schema.Traits.All)
                    if (!string.IsNullOrWhiteSpace(t.Name)) list.Add((t.Name, t.Name));
        }
        catch (Exception) { }
        return list.ToArray();
    }

    /// <summary>可选职业：从当前工作区 Schema 读（含卡池自定义）。</summary>
    private (string Key, string Zh)[] AvailableClans()
    {
        var list = new List<(string, string)>();
        try
        {
            var schema = _ctrl?.Workspace?.Schema;
            if (schema != null)
                foreach (var c in schema.Clans.All)
                    if (!string.IsNullOrWhiteSpace(c.Name)) list.Add((c.Name, c.Name));
        }
        catch (Exception) { }
        return list.ToArray();
    }

    private void BuildBoolCheck(VBoxContainer col, string label, bool value, Action<bool> setter)
    {
        var cb = new CheckBox { Text = label, ButtonPressed = value };
        cb.AddThemeFontSizeOverride("font_size", 11);
        cb.Toggled += on => setter(on);
        col.AddChild(cb);
    }

    private void BuildIntSpin(VBoxContainer col, string label, int value, Action<int> setter)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var lbl = new Label { Text = label, CustomMinimumSize = new Vector2(120, 0) };
        lbl.AddThemeFontSizeOverride("font_size", 11);
        row.AddChild(lbl);
        // 上限必须能装下卡号：游戏里卡号是 6~8 位数（妖精 90011110），
        // 写 99 会让卡号一打开就被截断成 99，而且大于 99 的根本敲不进去。
        var spin = new SpinBox { MinValue = 0, MaxValue = 999999999, Value = value, CustomMinimumSize = new Vector2(130, 0) };
        spin.ValueChanged += v => setter((int)v);
        row.AddChild(spin);
        col.AddChild(row);
    }

    /// <summary>构筑修正的规则种类。键必须与 DiyBlockModel 的 DeckModifier 编译分支一致。</summary>
    static (string Key, string Zh)[] DeckModKinds() => new[]
    {
        ("size", "牌组张数上下限增减"),
        ("leader_life", "主战者初始生命增减"),
        ("hand", "起手抽牌数增减"),
        ("ep", "初始进化点 (EP) 增减"),
        ("sep", "初始超进化点 (SEP) 增减"),
        ("self_copy", "本卡投入张数上限覆盖"),
        ("allow_clan_fixed", "允许混组指定职业"),
        ("allow_clan_choose", "允许任选 N 个额外职业"),
    };

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

                // 167 §3.2：补齐事件帽的 5 个隐藏属性。
                // 这些字段编译器全都认，但检视器从来不给玩家改 ——
                // 于是「每回合限一次」「仅我方回合」「受入场曲封印」这些
                // 关键语义只能靠手改脚本，积木端等于做不到。

                // 1) 触发前置条件
                BuildConditionSlotEditor(col, "触发前置条件", b.Condition,
                                         v => { b.Condition = v; Apply(); });

                // 2) 回合限制
                BuildEnumCombo(col, "回合限制",
                    new[] { ("Any", "任意回合"), ("Self", "仅我方回合"), ("Opponent", "仅敌方回合") },
                    b.DuringTurn ?? "Any", v => { b.DuringTurn = v; Apply(); });

                // 3) 每回合触发上限（0 = 不限）
                BuildIntSpin(col, "每回合限次(0=不限)", b.IntA,
                             v => { b.IntA = v; Apply(); });

                // 4) 进化 / 入场曲细分
                if (b.Text == "OnEvolve" || b.Text == "OnSuperEvolve")
                {
                    BuildBoolCheck(col, "仅限手动消耗 EP/SEP 进化触发", b.FlagA,
                                   v => { b.FlagA = v; Apply(); });
                }
                else if (b.Text == "OnPlay")
                {
                    BuildBoolCheck(col, "属于【入场曲】（受入场曲封印影响）", b.FlagB,
                                   v => { b.FlagB = v; Apply(); });
                }

                // 5) 并列触发事件（如「入场曲或融合时」）
                BuildEnumCombo(col, "并列事件",
                    new[] { ("", "无"), ("OnFuse", "融合时"), ("OnDestroy", "谢幕曲"), ("OnAttack", "攻击时") },
                    b.EnumB ?? "", v => { b.EnumB = string.IsNullOrEmpty(v) ? null : v; Apply(); });
                BuildHatCostPanel(col, b);
                break;

            case "c_if_else":
                BuildConditionSlotEditor(col, "成立条件", b.Condition,
                                         v => { b.Condition = v; Apply(); });
                break;
            case "c_repeat_until":
                BuildConditionSlotEditor(col, "停止条件（直到…）", b.Condition,
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

            // 修改计数器（167 §3.1）：原来落进 default 分支，
            // 玩家只能看到「计数器 + 变化量」两项，操作方式、方向、作用对象全都没有，
            // 效果永远只能是「pp +1」。这里补齐全部孔位。
            case "act_mod_counter":
            {
                BuildEnumCombo(col, "计数器", AvailableCounters(), b.Text ?? "pp",
                               v => { b.Text = v; Apply(); });

                var counterOps = new (string Key, string Zh)[]
                {
                    ("Add", "增减变化 (+ / -)"),
                    ("Set", "直接设为 (=)"),
                    ("Fill", "回满上限 (Fill)"),
                };
                BuildEnumCombo(col, "操作方式", counterOps, b.EnumA ?? "Add",
                               v => { b.EnumA = v; Apply(rebuildInspector: true); });

                if ((b.EnumA ?? "Add") == "Add")
                {
                    var signOps = new (string Key, string Zh)[] { ("0", "增加 (+)"), ("1", "减少 (-)") };
                    BuildEnumCombo(col, "增减方向", signOps, b.FlagA ? "1" : "0",
                                   v => { b.FlagA = (v == "1"); Apply(); });
                }

                BuildNumberSlotEditor(col, "数值", b.Number, v => { b.Number = v; Apply(); });

                // 留空 = 我方主战者；指定目标 = 该实体的计数器（手牌魔力增幅 / 护符倒计数）
                BuildTargetSlotEditor(col, "作用实体（留空=我方主战者）", b.Target,
                                      v => { b.Target = v; Apply(); });
                break;
            }
            case "act_grant_kw":
            case "act_remove_kw":
                BuildEnumCombo(col, "关键词", DiyZh.Keywords, b.Text, v =>
                {
                    b.Text = v;
                    // 带参关键词（1回合可攻击N次 / 受到伤害上限N）默认补一个数值，
                    // 否则玩家只能填到关键词、填不到 N，实战退化成默认次数。
                    if (IsParameterizedKw(v) && b.Number == null)
                        b.Number = new Block { Opcode = "num_const", IntA = 2 };
                    Apply();
                });
                BuildTargetSlotEditor(col, "给予谁", b.Target, v => { b.Target = v; Apply(); });
                if (IsParameterizedKw(b.Text))
                    BuildNumberSlotEditor(col, "参数数值 (N)", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_mod_cost":
                BuildTargetSlotEditor(col, "对谁", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "方式", DiyZh.CostOps, b.EnumA, v => { b.EnumA = v; Apply(); });
                BuildNumberSlotEditor(col, "费用变化", b.Number, v => { b.Number = v; Apply(); });
                break;

            // ---- 167 §3.3：补齐落进 default 黑洞的 10 个动作积木 ----

            // 复制卡牌：复制源 + 是否完整复制 + 去向 + 份数
            case "act_copy":
                BuildTargetSlotEditor(col, "复制源", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "复制方式",
                    new[] { ("0", "初始白板复制"), ("1", "完整复制 (exact copy)") },
                    b.FlagA ? "1" : "0", v => { b.FlagA = (v == "1"); Apply(); });
                BuildEnumCombo(col, "去向",
                    new[] { ("Board", "战场"), ("Hand", "手牌") },
                    b.EnumA ?? "Board", v => { b.EnumA = v; Apply(); });
                BuildNumberSlotEditor(col, "份数", b.Number, v => { b.Number = v; Apply(); });
                break;

            // 从卡池发现：筛选条件 + 候选展示数 + 选取张数 + 去向
            case "act_discover":
                BuildTargetSlotEditor(col, "筛选条件", b.Target, v => { b.Target = v; Apply(); });
                // IntA / IntB 是 int 字段，不是积木槽位，用 SpinBox 直接编辑。
                BuildIntSpin(col, "候选展示数", b.IntB > 0 ? b.IntB : 3,
                             v => { b.IntB = v; Apply(); });
                BuildIntSpin(col, "选取张数", b.IntA > 0 ? b.IntA : 1,
                             v => { b.IntA = v; Apply(); });
                BuildEnumCombo(col, "去向",
                    new[] { ("hand", "手牌"), ("board", "战场"), ("deck", "牌组") },
                    b.EnumA ?? "hand", v => { b.EnumA = v; Apply(); });
                break;

            // 亡者召还：只需费用上限 N（原实现还给了无意义的卡名框）
            case "act_reanimate":
                BuildNumberSlotEditor(col, "费用上限", b.Number, v => { b.Number = v; Apply(); });
                break;

            // 使目标变身：变谁 + 变成哪张卡
            case "act_transform":
                BuildTargetSlotEditor(col, "使谁变身", b.Target, v => { b.Target = v; Apply(); });
                // 卡牌一律按**卡 ID**指定，不认卡名 —— 卡名是可变显示文本，
                // 玩家改了名或换了语言，按名查就查不到，效果静默失效。
                BuildCardIdPicker(col, "变成", b.IntB, (id, nm) => { b.IntB = id; if (!string.IsNullOrEmpty(nm)) b.Text = nm; Apply(); });
                break;

            // 获得纹章：纹章名 + 授予对象
            case "act_gain_crest":
                // 纹章是独立实体：卡 ID 非 0 时按 ID 建，否则按 Text 找。
                // 优先 ID，卡名只作显示。
                BuildCardIdPicker(col, "纹章", b.IntB, (id, nm) => { b.IntB = id; if (!string.IsNullOrEmpty(nm)) b.Text = nm; Apply(); });
                BuildTextEditor(col, "纹章名（显示用）", b.Text, v => { b.Text = v; Apply(); });
                BuildEnumCombo(col, "授予对象",
                    new[] { ("Self", "我方"), ("Opponent", "敌方"), ("Any", "双方") },
                    b.EnumA ?? "Self", v => { b.EnumA = v; Apply(); });
                break;

            // 赋予目标子能力：赋予谁 + 持续时间（子能力本身在画布凹槽里编辑）
            case "act_grant_entity_ability":
                BuildTargetSlotEditor(col, "赋予谁", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "持续时间", DiyZh.Durations, b.EnumB ?? "Permanent",
                               v => { b.EnumB = v; Apply(); });
                break;

            // 赋予计数器常驻能力：选计数器，子能力在画布凹槽里编辑
            case "act_grant_ability":
                BuildEnumCombo(col, "计数器", AvailableCounters(), b.Text ?? "faith",
                               v => { b.Text = v; Apply(); });
                break;

            // 复刻时机能力：复刻谁 + 复刻哪个时机
            case "act_invoke":
                BuildTargetSlotEditor(col, "复刻谁", b.Target, v => { b.Target = v; Apply(); });
                BuildEnumCombo(col, "复刻时机",
                    new[] { ("OnPlay", "入场曲"), ("OnDestroy", "谢幕曲"), ("OnEvolve", "进化时"),
                            ("OnAttack", "攻击时"), ("OnClash", "交战时"), ("OnSuperEvolve", "超进化时") },
                    b.Text ?? "OnPlay", v => { b.Text = v; Apply(); });
                break;

            // 替换牌组：选当前卡池定义的具名牌组
            case "act_replace_deck":
                BuildTextEditor(col, "牌组名", b.Text, v => { b.Text = v; Apply(); });
                break;

            // 弃牌：弃谁 + 张数
            case "act_discard":
                BuildTargetSlotEditor(col, "弃谁的牌", b.Target, v => { b.Target = v; Apply(); });
                BuildNumberSlotEditor(col, "张数", b.Number, v => { b.Number = v; Apply(); });
                break;

            case "act_summon":
            case "act_add_hand":
                BuildCardIdPicker(col, "卡牌", b.IntB, (id, nm) => { b.IntB = id; if (!string.IsNullOrEmpty(nm)) b.Text = nm; Apply(); });
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
            {
                // 构筑修正支持 8 种规则，还要能配职业参数 / 叠加 / 互斥组。
                // 只给「规则项 + 数值」两个裸框，玩家得自己记住 size / leader_life
                // 这些内码，等于做不到。
                BuildEnumCombo(col, "规则种类", DeckModKinds(), b.Text ?? "size",
                               v => { b.Text = v; Apply(rebuildInspector: true); });

                BuildIntEditor(col, "修正数值", b.IntA, v => { b.IntA = v; Apply(); });

                if ((b.Text ?? "") == "allow_clan_fixed")
                    BuildEnumCombo(col, "指定混组职业", AvailableClans(), b.CardNameA ?? "",
                                   v => { b.CardNameA = v; Apply(); });

                BuildBoolCheck(col, "按投入张数成倍叠加", b.FlagA, v => { b.FlagA = v; Apply(); });
                BuildInlineText(col, "互斥组标识（留空=无）", b.Text2 ?? "",
                                v => { b.Text2 = v; Apply(); });
                break;
            }

            default:
                // 兜底：确实需要手填的少数积木。卡牌一律用卡 ID，不认卡名。
                BuildCardIdPicker(col, "卡牌", b.IntB, (id, nm) => { b.IntB = id; if (!string.IsNullOrEmpty(nm)) b.Text = nm; Apply(); });
                BuildNumberSlotEditor(col, "数值", b.Number, v => { b.Number = v; Apply(); });
                break;
        }
    }

    /// <summary>卡号的画布显示名：《中文名》，退回卡名，再退回 #卡号。</summary>
    static string FormatCardTitle(int cardId, string text)
    {
        if (cardId > 0)
        {
            // 先查权威卡库（官方卡）；自制卡不在里面，落到下面的 text 兜底 ——
            // 编译期已按卡号把卡名写进了 CardName，反编译回来就是 blk.Text。
            string zh = ShadowverseFan.KernelV3.Packs.AuthoritativeNames.ChineseOf(cardId);
            if (!string.IsNullOrEmpty(zh)) return "《" + zh + "》";
        }
        if (!string.IsNullOrEmpty(text)) return "《" + text + "》";
        return cardId > 0 ? "#" + cardId : "#?";
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

        // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

        SvWbTheme.StyleOptionPopup(opt);
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
    /// <summary>
    /// 把当前积木模型重新编译一遍，并刷新画布。
    ///
    /// rebuildInspector 只在**结构变了**（换了子槽类型、增删了槽位）时才传 true。
    /// 普通数值/文本改动绝不能重画检视器：那会在 SpinBox.ValueChanged 回调里
    /// 把正在触发信号的控件自己 QueueFree 掉，玩家一按键焦点就没了，
    /// 表现为「输入框打不进去字」。
    /// </summary>
    private void Apply(bool rebuildInspector = false)
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

        EnsureInsertList();
        RedrawCanvas();
        // 画布上的 Label 靠 RedrawCanvas 更新；检视器只在结构变化时重建。
        if (rebuildInspector) RedrawInspector();
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



    /// <summary>
    /// 智能卡牌 ID 选择器（168 验收要求）：
    ///
    /// 底层只认卡 ID —— 卡名是可变的显示文本，按名查在改名/换语言后必然失效，
    /// 而且编译端 DoSpawn / DoTransform 只读 CardId。
    /// 但交互上允许输入卡号或中/英卡名检索，点选后立刻转成 ID。
    /// </summary>
    private void BuildCardIdPicker(VBoxContainer col, string label, int currentId, Action<int, string> onPicked)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 4);

        var all = GetSearchableCards();
        var hit0 = all.FirstOrDefault(c => c.Id == currentId);
        string title = currentId > 0
            ? (hit0.Id > 0 ? "#" + hit0.Id + " 《" + hit0.Zh + "》" : "#" + currentId)
            : "（未指定卡 ID）";

        var head = new Label { Text = label + "：" + title, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", currentId > 0 ? new Color("6EE7B7") : SvWbTheme.StatusWarn);
        box.AddChild(head);

        // 直接填卡号
        var idSpin = new SpinBox
        {
            MinValue = 0, MaxValue = 999999999, Value = currentId,
            CustomMinimumSize = new Vector2(140, 0),
        };
        idSpin.ValueChanged += v =>
        {
            int id = (int)v;
            var hit = all.FirstOrDefault(c => c.Id == id);
            head.Text = label + "：" + (id > 0 ? (hit.Id > 0 ? "#" + hit.Id + " 《" + hit.Zh + "》" : "#" + id) : "（未指定卡 ID）");
            // 同时回传卡名：act_gain_crest 在编译器里靠 Text 匹配纹章名，
            // 只给卡号会让纹章变成空名实体。
            onPicked(id, hit.Zh ?? hit.En ?? "");
        };
        box.AddChild(idSpin);

        // 按卡号 / 中英名检索
        var search = new LineEdit
        {
            PlaceholderText = "🔍 输入卡号或中/英卡名检索…",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        search.AddThemeFontSizeOverride("font_size", 11);
        box.AddChild(search);

        var list = new ItemList
        {
            CustomMinimumSize = new Vector2(0, 110),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        list.AddThemeFontSizeOverride("font_size", 11);
        box.AddChild(list);

        var filtered = new List<(int Id, string Zh, string En, bool IsToken)>();

        void Populate(string q)
        {
            list.Clear();
            filtered.Clear();
            string key = (q ?? "").Trim();
            IEnumerable<(int Id, string Zh, string En, bool IsToken)> src = all;
            if (key.Length > 0)
                src = src.Where(c =>
                    c.Id.ToString().Contains(key, StringComparison.Ordinal) ||
                    (!string.IsNullOrEmpty(c.Zh) && c.Zh.Contains(key, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(c.En) && c.En.Contains(key, StringComparison.OrdinalIgnoreCase)));
            foreach (var it in src.Take(40))
            {
                filtered.Add(it);
                string tag = it.IsToken ? " [衍生]" : "";
                string disp = (!string.IsNullOrEmpty(it.En) && it.Zh != it.En)
                    ? "#" + it.Id + " " + it.Zh + " (" + it.En + ")" + tag
                    : "#" + it.Id + " " + it.Zh + tag;
                int idx = list.AddItem(disp);
                if (it.Id == currentId) list.Select(idx);
            }
        }

        Populate("");
        search.TextChanged += Populate;

        // 回车：纯数字直接当卡号，否则取第一条匹配
        search.TextSubmitted += q =>
        {
            string t = (q ?? "").Trim().TrimStart('#');
            if (int.TryParse(t, out int raw) && raw > 0)
            {
                idSpin.Value = raw;
            }
            else if (filtered.Count > 0)
            {
                idSpin.Value = filtered[0].Id;
            }
        };

        list.ItemSelected += idx =>
        {
            int i = (int)idx;
            if (i < 0 || i >= filtered.Count) return;
            var pick = filtered[i];
            head.Text = label + "：#" + pick.Id + " 《" + pick.Zh + "》";
            head.AddThemeColorOverride("font_color", new Color("6EE7B7"));
            idSpin.Value = pick.Id;   // 触发 ValueChanged，统一走一条写入路径
        };

        col.AddChild(box);
    }

    /// <summary>可检索卡池：当前卡池 + 全局总表 + 内置衍生 Token，按卡号去重。</summary>
    private List<(int Id, string Zh, string En, bool IsToken)> GetSearchableCards()
    {
        var map = new Dictionary<int, (int Id, string Zh, string En, bool IsToken)>();

        try
        {
            // 衍生卡与普通卡同源：直接遍历卡池的卡表，is_token 标记的就是衍生物。
            var store = _ctrl?.Store;
            var all = store?.Catalog?.AllCards?.Values;
            if (all != null)
                foreach (var cd in all)
                {
                    if (cd == null || cd.CardId <= 0 || !cd.IsToken) continue;
                    string tzh = !string.IsNullOrWhiteSpace(cd.NameChs) ? cd.NameChs : (cd.Name ?? "");
                    map[cd.CardId] = (cd.CardId, tzh, cd.Name ?? "", true);
                }
        }
        catch (Exception) { }

        try
        {
            var cards = _ctrl?.Store?.Catalog?.AllCards?.Values;
            if (cards != null)
                foreach (var c in cards)
                {
                    if (c == null || c.CardId <= 0) continue;
                    string zh = !string.IsNullOrWhiteSpace(c.NameChs) ? c.NameChs : (c.Name ?? "");
                    map[c.CardId] = (c.CardId, zh, c.Name ?? "", c.IsToken);
                }
        }
        catch (Exception) { }

        return map.Values.OrderBy(x => x.IsToken ? 0 : 1).ThenBy(x => x.Id).ToList();
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
        // 槽位类型下拉框会改变子控件结构（固定数值 → 计数器 / 区域筛选 → 槽位…）。
        // Apply 现在默认不重绘检视器（防 SpinBox 丢焦点），所以槽位设值后必须
        // 显式重绘一次，否则下方的子控件还停留在旧的形态，玩家以为点了没反应。
        var structuralSetter = new Action<Block>(v => { setter(v); RedrawInspector(); });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("6EE7B7"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

        SvWbTheme.StyleOptionPopup(opts);
        opts.AddItem("（无）");
        foreach (var o in DiyZh.TargetOps) opts.AddItem(o.Zh);
        opts.Selected = cur == null ? 0 : Math.Max(0, DiyZh.IndexOf(DiyZh.TargetOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { structuralSetter(null); return; }
            var op = DiyZh.TargetOps[(int)idx - 1].Key;
            if (cur != null && cur.Opcode == op) return;
            var made = new Block { Opcode = op };
            if (op == "tgt_slot") made.Text = TargetSpec.SlotSelf;
            else if (op == "tgt_leader") made.EnumA = "Self";
            else
            {
                made.IntA = 0; made.IntB = 1; made.Text = "Follower"; made.EnumA = "Choose";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
            }
            structuralSetter(made);
        };
        box.AddChild(opts);

        if (cur != null)
        {
            if (cur.Opcode == "tgt_slot")
                BuildEnumCombo(box, "引用槽位", DiyZh.Slots, cur.Text ?? "$self", v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "tgt_leader")
                BuildEnumCombo(box, "主战者归属", DiyZh.Owners, cur.EnumA ?? "Self", v => { cur.EnumA = v; setter(cur); });
            else if (cur.Opcode == "tgt_query")
            {
                // tgt_query 的 IntA 是区域码、IntB 是归属码。
                // 早先是「手填数字」的输入框，玩家得记住 0=战场 1=手牌… 现在换中文下拉。
                var zoneOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。
                SvWbTheme.StyleOptionPopup(zoneOpt);
                for (int zi = 0; zi < DiyZh.Zones.Length; zi++) zoneOpt.AddItem(DiyZh.Zones[zi].Zh, zi);
                zoneOpt.Selected = Mathf.Clamp(cur.IntA, 0, DiyZh.Zones.Length - 1);
                zoneOpt.ItemSelected += s2 =>
                {
                    cur.IntA = Mathf.Clamp((int)s2, 0, DiyZh.Zones.Length - 1);
                    setter(cur);
                };
                box.AddChild(zoneOpt);

                var ownerOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };

                // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

                SvWbTheme.StyleOptionPopup(ownerOpt);
                for (int oi = 0; oi < DiyZh.Owners.Length; oi++) ownerOpt.AddItem(DiyZh.Owners[oi].Zh, oi);
                ownerOpt.Selected = Mathf.Clamp(cur.IntB, 0, DiyZh.Owners.Length - 1);
                ownerOpt.ItemSelected += s3 =>
                {
                    cur.IntB = Mathf.Clamp((int)s3, 0, DiyZh.Owners.Length - 1);
                    setter(cur);
                };
                box.AddChild(ownerOpt);

                var kindOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                SvWbTheme.StyleOptionPopup(kindOpt);
                // 增加“不限卡种”选项（178 号文档 5.2）
                kindOpt.AddItem("（不限卡种）", 0);
                for (int ki = 0; ki < DiyZh.Kinds.Length; ki++) kindOpt.AddItem(DiyZh.Kinds[ki].Zh, ki + 1);

                int kIdx = DiyZh.IndexOf(DiyZh.Kinds, cur.Text);
                kindOpt.Selected = kIdx >= 0 ? kIdx + 1 : 0;
                kindOpt.ItemSelected += s4 =>
                {
                    int ki = (int)s4 - 1;
                    cur.Text = (ki >= 0 && ki < DiyZh.Kinds.Length) ? DiyZh.Kinds[ki].Key : null;
                    setter(cur);
                };
                box.AddChild(kindOpt);

                var modeOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };

                // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

                SvWbTheme.StyleOptionPopup(modeOpt);
                for (int mi = 0; mi < DiyZh.SelectModes.Length; mi++) modeOpt.AddItem(DiyZh.SelectModes[mi].Zh, mi);
                int mIdx = DiyZh.IndexOf(DiyZh.SelectModes, cur.EnumA);
                if (mIdx >= 0) modeOpt.Selected = mIdx;
                modeOpt.ItemSelected += s5 =>
                {
                    int mi = (int)s5;
                    if (mi < 0 || mi >= DiyZh.SelectModes.Length) return;
                    cur.EnumA = DiyZh.SelectModes[mi].Key;
                    setter(cur);
                };
                box.AddChild(modeOpt);
                var nb = new VBoxContainer();
                box.AddChild(nb);
                BuildNumberSlotEditor(nb, "选取数量", cur.Number, v => { cur.Number = v; setter(cur); });
                // 167 §3.4：暴露 tgt_query 的隐藏筛选能力。
                // 这些字段编译器全都支持，但没有 UI 就等于不存在 ——
                // 玩家做不出「攻击力 ≤ 3 的敌方随从」这种最基本的筛选。

                // 筛选条件（Where）
                BuildConditionSlotEditor(nb, "筛选条件 (Where)", cur.Condition,
                                         v => { cur.Condition = v; setter(cur); });

                // 常用修饰
                BuildBoolCheck(nb, "排除本卡/触发源自身", cur.FlagA, v => { cur.FlagA = v; setter(cur); });
                BuildBoolCheck(nb, "同时包含主战者", cur.FlagB, v => { cur.FlagB = v; setter(cur); });
                BuildBoolCheck(nb, "不同名卡牌 (distinct)", cur.FlagE, v => { cur.FlagE = v; setter(cur); });
                BuildBoolCheck(nb, "仅限重复卡牌", cur.FlagC, v => { cur.FlagC = v; setter(cur); });

                // 历史池
                BuildEnumCombo(nb, "取牌范围",
                    new[] { ("", "当前区域实时"), ("DestroyedThisMatch", "本局已破坏"),
                            ("DestroyedThisTurn", "本回合已破坏"), ("EnteredThisMatch", "本局已进场") },
                    cur.EnumB ?? "", v => { cur.EnumB = string.IsNullOrEmpty(v) ? null : v; setter(cur); });

                // 排序与截断
                BuildEnumCombo(nb, "排序属性",
                    new[] { ("", "不排序"), ("Attack", "攻击力"), ("Life", "生命值"),
                            ("Cost", "费用"), ("BaseCost", "基础费用") },
                    cur.EnumC ?? "", v => { cur.EnumC = string.IsNullOrEmpty(v) ? null : v; setter(cur); });
                BuildBoolCheck(nb, "从高到低排序", cur.FlagD, v => { cur.FlagD = v; setter(cur); });
                BuildIntSpin(nb, "取前 N 项(0=全部)", cur.IntC, v => { cur.IntC = v; setter(cur); });
            }
        }
        col.AddChild(box);
    }

    private static readonly string[] NumOps =
        { "num_const", "num_rand", "num_counter", "num_prop", "num_count", "num_arith" };

    private void BuildNumberSlotEditor(VBoxContainer col, string label, Block cur, Action<Block> setter)
    {
        // 槽位类型下拉框会改变子控件结构（固定数值 → 计数器 / 区域筛选 → 槽位…）。
        // Apply 现在默认不重绘检视器（防 SpinBox 丢焦点），所以槽位设值后必须
        // 显式重绘一次，否则下方的子控件还停留在旧的形态，玩家以为点了没反应。
        var structuralSetter = new Action<Block>(v => { setter(v); RedrawInspector(); });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("6EE7B7"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

        SvWbTheme.StyleOptionPopup(opts);
        opts.AddItem("（无）");
        foreach (var o in DiyZh.NumOps) opts.AddItem(o.Zh);
        opts.Selected = cur == null ? 0 : Math.Max(0, DiyZh.IndexOf(DiyZh.NumOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { structuralSetter(null); return; }
            var op = DiyZh.NumOps[(int)idx - 1].Key;
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
            // 179 号文档 C1：新增四种数值孔位的创建默认值。
            else if (op == "num_sum")
            {
                made.EnumA = "Attack";
                made.Target = null;
            }
            else if (op == "num_distinct") made.Target = null;
            else if (op == "num_cond")
            {
                made.Condition = new Block { Opcode = "bool_true" };
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
                made.Number2 = new Block { Opcode = "num_const", IntA = 0 };
            }
            else if (op == "num_extreme")
            {
                made.EnumA = "Max";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
                made.Number2 = new Block { Opcode = "num_const", IntA = 1 };
            }
            structuralSetter(made);
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
                // 必须用 AvailableCounters()：只列 DiyZh.Counters 的话，
                // 卡池里自定义的计数器在「读取」这一侧永远选不到 ——
                // 能做「使电能+1」，却写不出「若电能 >= 5」。
                BuildEnumCombo(box, "计数器", AvailableCounters(), cur.Text ?? "pp", v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "num_prop")
            {
                BuildEnumCombo(box, "读取属性", DiyZh.Stats, cur.EnumA ?? "Attack", v => { cur.EnumA = v; setter(cur); });
                // 167 §3.5：必须指定「读谁的属性」。
                // 没有这个孔位，「读取攻击力」读的是谁完全由默认值决定，
                // 玩家写「攻击力最高的随从」时永远拿到的不是他以为的那一个。
                BuildTargetSlotEditor(box, "读取谁的属性", cur.Target, v => { cur.Target = v; setter(cur); });
            }
            else if (cur.Opcode == "num_count")
            {
                // 统计目标数量同样必须指定集合。
                BuildTargetSlotEditor(box, "统计哪个集合", cur.Target, v => { cur.Target = v; setter(cur); });
            }
            else if (cur.Opcode == "num_sum")
            {
                BuildEnumCombo(box, "统计属性", DiyZh.Stats, cur.EnumA ?? "Attack", v => { cur.EnumA = v; setter(cur); });
                BuildTargetSlotEditor(box, "统计哪个集合", cur.Target, v => { cur.Target = v; setter(cur); });
            }
            else if (cur.Opcode == "num_distinct")
                BuildTargetSlotEditor(box, "统计哪个集合（按卡名去重）", cur.Target, v => { cur.Target = v; setter(cur); });
            else if (cur.Opcode == "num_cond")
            {
                var cbx = new VBoxContainer(); box.AddChild(cbx);
                BuildConditionSlotEditor(cbx, "条件", cur.Condition, v => { cur.Condition = v; setter(cur); });
                var tbx = new VBoxContainer(); box.AddChild(tbx);
                BuildNumberSlotEditor(tbx, "满足时取", cur.Number, v => { cur.Number = v; setter(cur); });
                var fbx = new VBoxContainer(); box.AddChild(fbx);
                BuildNumberSlotEditor(fbx, "否则取", cur.Number2, v => { cur.Number2 = v; setter(cur); });
            }
            else if (cur.Opcode == "num_extreme")
            {
                var exOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                SvWbTheme.StyleOptionPopup(exOpt);
                exOpt.AddItem("取最大值", 0);
                exOpt.AddItem("取最小值", 1);
                exOpt.Selected = (cur.EnumA ?? "Max") == "Min" ? 1 : 0;
                exOpt.ItemSelected += s7 =>
                {
                    cur.EnumA = (int)s7 == 1 ? "Min" : "Max";
                    setter(cur);
                };
                box.AddChild(exOpt);
                var a1 = new VBoxContainer(); box.AddChild(a1);
                BuildNumberSlotEditor(a1, "值 A", cur.Number, v => { cur.Number = v; setter(cur); });
                var a2 = new VBoxContainer(); box.AddChild(a2);
                BuildNumberSlotEditor(a2, "值 B", cur.Number2, v => { cur.Number2 = v; setter(cur); });
            }
            else if (cur.Opcode == "num_arith")
            {
                BuildEnumCombo(box, "运算符", DiyZh.ArithOps, cur.EnumA ?? "Add", v => { cur.EnumA = v; setter(cur); });
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
        // 槽位类型下拉框会改变子控件结构（固定数值 → 计数器 / 区域筛选 → 槽位…）。
        // Apply 现在默认不重绘检视器（防 SpinBox 丢焦点），所以槽位设值后必须
        // 显式重绘一次，否则下方的子控件还停留在旧的形态，玩家以为点了没反应。
        var structuralSetter = new Action<Block>(v => { setter(v); RedrawInspector(); });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        var head = new Label { Text = label };
        head.AddThemeFontSizeOverride("font_size", 11);
        head.AddThemeColorOverride("font_color", new Color("C4B5FD"));
        box.AddChild(head);

        var opts = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

        // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。

        SvWbTheme.StyleOptionPopup(opts);
        opts.AddItem("（无条件）");
        foreach (var o in DiyZh.CondOps) opts.AddItem(o.Zh);
        opts.Selected = cur == null ? 0 : Math.Max(0, DiyZh.IndexOf(DiyZh.CondOps, cur.Opcode) + 1);
        opts.ItemSelected += idx =>
        {
            if (idx <= 0) { structuralSetter(null); return; }
            var op = DiyZh.CondOps[(int)idx - 1].Key;
            if (cur != null && cur.Opcode == op) return;
            var made = new Block { Opcode = op };
            if (op == "bool_compare")
            {
                made.EnumA = "Ge";
                made.Number = new Block { Opcode = "num_const", IntA = 1 };
                made.Number2 = new Block { Opcode = "num_const", IntA = 1 };
            }
            else if (op == "bool_has_kw") made.Text = "ward";
            else if (op == "bool_has_trait")
            {
                // 兵种数据全是中文，写死 "Fairy" 会让下拉框空白、卡面印英文、
                // 而且实战永远匹配不到兵种为「妖精」的随从。
                var ts = AvailableTraits();
                made.Text = ts.Length > 0 ? ts[0].Key : "妖精";
            }
            else if (op == "bool_has_clan")
            {
                // 同 bool_has_trait：职业名也来自卡池数据，写死会让自定义职业池选不到。
                var cs = AvailableClans();
                made.Text = cs.Length > 0 ? cs[0].Key : "中立";
            }
            // 编译器读的是 EnumA（DiyBlockCompiler.CompileCond），不是 Text。
            // 早先写进 Text 会让积木端拼出的条件全落进默认的 Damaged。
            else if (op == "bool_has_state") made.EnumA = "Evolved";
            else if (op == "bool_and" || op == "bool_or") made.Stacks = new List<Block>();
            else if (op == "bool_not") made.Condition = new Block { Opcode = "bool_true" };
            structuralSetter(made);
        };
        box.AddChild(opts);

        if (cur != null)
        {
            if (cur.Opcode == "bool_compare")
            {
                // 比较符也是手填英文 Ge/Le 的重灾区，换中文下拉。
                var cmpOpt = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                // OptionButton 默认 FitToLongestItem=true，长条目会把整栏撑破屏幕右缘。
                SvWbTheme.StyleOptionPopup(cmpOpt);
                for (int ci = 0; ci < DiyZh.Compares.Length; ci++) cmpOpt.AddItem(DiyZh.Compares[ci].Zh, ci);
                int cIdx = DiyZh.IndexOf(DiyZh.Compares, cur.EnumA);
                if (cIdx >= 0) cmpOpt.Selected = cIdx;
                cmpOpt.ItemSelected += s6 =>
                {
                    int ci = (int)s6;
                    if (ci < 0 || ci >= DiyZh.Compares.Length) return;
                    cur.EnumA = DiyZh.Compares[ci].Key;
                    setter(cur);
                };
                box.AddChild(cmpOpt);
                var n1 = new VBoxContainer(); box.AddChild(n1);
                BuildNumberSlotEditor(n1, "左值", cur.Number, v => { cur.Number = v; setter(cur); });
                var n2 = new VBoxContainer(); box.AddChild(n2);
                BuildNumberSlotEditor(n2, "右值", cur.Number2, v => { cur.Number2 = v; setter(cur); });
            }
            // 四种「拥有 X」条件字段位置各不相同，不能共用一个手填框：
            //   bool_has_kw / bool_has_trait 读 Text；
            //   bool_has_clan 读 Clan（本编辑器暂留文本）；
            //   bool_has_state 读 **EnumA**（写 Text 会静默落进默认 Damaged）。
            else if (cur.Opcode == "bool_has_kw")
                BuildEnumCombo(box, "关键词", DiyZh.Keywords, cur.Text ?? "ward", v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "bool_has_state")
                BuildEnumCombo(box, "状态", DiyZh.States, cur.EnumA ?? "Evolved", v => { cur.EnumA = v; setter(cur); });
            else if (cur.Opcode == "bool_has_trait")
                // 兵种走下拉，不让玩家手敲：写错一个字母就是「永远不成立」，
                // 而界面上看不出任何异常。
                BuildEnumCombo(box, "兵种", AvailableTraits(), cur.Text ?? "", v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "bool_has_clan")
                BuildEnumCombo(box, "职业", AvailableClans(), cur.Text ?? "", v => { cur.Text = v; setter(cur); });
            // 179 号文档 C1：这两项在 DiyZh.CondOps 里已加了下拉入口，
            // 但检视器缺 UI —— 玩家选中后下方一片空白，等于点了没反应。
            else if (cur.Opcode == "bool_has_card_name")
            {
                BuildCardIdPicker(box, "匹配卡牌（卡号）", cur.IntA,
                    (id, nm) => { cur.IntA = id; cur.Text = nm; setter(cur); });
                var cb2 = new VBoxContainer(); box.AddChild(cb2);
                BuildTargetSlotEditor(cb2, "或是与谁同名", cur.Target,
                    v => { cur.Target = v; setter(cur); });
            }
            else if (cur.Opcode == "bool_has_pack")
                BuildInlineText(box, "卡包 ID（多个用 | 分隔）", cur.Text ?? "",
                    v => { cur.Text = v; setter(cur); });
            else if (cur.Opcode == "bool_not")
            {
                var nb = new VBoxContainer(); box.AddChild(nb);
                BuildConditionSlotEditor(nb, "内层条件", cur.Condition, v => { cur.Condition = v; setter(cur); });
            }
            else if (cur.Opcode == "bool_and" || cur.Opcode == "bool_or")
            {
                // 已加的子条件必须渲染出来，否则点了「+ 添加子条件」列表是个黑盒 ——
                // 玩家看不到自己加了什么，也删不掉。
                cur.Stacks ??= new List<Block>();
                for (int si = 0; si < cur.Stacks.Count; si++)
                {
                    var rowBox = new HBoxContainer();
                    rowBox.AddThemeConstantOverride("separation", 4);
                    var inner = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
                    rowBox.AddChild(inner);
                    // C# 的 for 索引变量在所有迭代间共享同一个位置，
                    // 闭包直接捕获 si 的话，回调执行时 si 已等于 Count，
                    // idx < Count 恒为 false —— 子条件怎么改都会弹回原样。
                    int siLocal = si;
                    BuildConditionSlotEditor(inner, "子条件 " + (siLocal + 1), cur.Stacks[siLocal],
                                             v => { if (siLocal < cur.Stacks.Count) { cur.Stacks[siLocal] = v; structuralSetter(cur); } });
                    int idx = siLocal;
                    var del = new Button { Text = "✕", CustomMinimumSize = new Vector2(26, 26) };
                    SvWbTheme.StyleButton(del, customAccent: SvWbTheme.StatusDanger);
                    del.Pressed += () =>
                    {
                        if (idx < cur.Stacks.Count) cur.Stacks.RemoveAt(idx);
                        // 删掉一条子条件改变了列表结构，必须重绘才看得到。
                        structuralSetter(cur);
                    };
                    rowBox.AddChild(del);
                    box.AddChild(rowBox);
                }

                var add = new Button { Text = "+ 添加子条件", CustomMinimumSize = new Vector2(0, 24) };
                SvWbTheme.StyleButton(add);
                add.Pressed += () =>
                {
                    cur.Stacks ??= new List<Block>();
                    cur.Stacks.Add(new Block { Opcode = "bool_true" });
                    // 新增一条子条件同样改变结构。
                    structuralSetter(cur);
                };
                box.AddChild(add);
            }
            else if (cur.Opcode == "bool_exists")
            {
                // 167 §3.5：bool_exists 原本一个孔位都没有，
                // 「至少存在 N 个 X」根本做不出来。
                BuildTargetSlotEditor(box, "存在的目标集合", cur.Target, v => { cur.Target = v; setter(cur); });
                var nb = new VBoxContainer(); box.AddChild(nb);
                BuildNumberSlotEditor(nb, "至少有几个", cur.Number, v => { cur.Number = v; setter(cur); });
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
                // 编译器里 IntA = ZoneCode(区域)、IntB = OwnerCode(归属)，
                // 早先这里写反了：「敌方战场」在标签上会显示成「我方手牌」。
                string zone = t.IntA switch
                {
                    0 => "战场", 1 => "手牌", 2 => "牌组", 3 => "墓场",
                    // 序号必须与 DiyBlockModel.ZoneCode 一致：
                    // 5=主战者 6=纹章区 7=额外区 8=当前卡池 9=全部卡池。
                    // 早先 5/7 写反，选「主战者」画布显示「额外区」，反之亦然。
                    4 => "放逐区", 5 => "主战者", 6 => "纹章区", 7 => "额外区",
                    8 => "当前卡池", 9 => "全部卡池",
                    _ => "区域",
                };
                string owner = t.IntB switch
                {
                    0 => "我方", 1 => "敌方", 2 => "双方", _ => "",
                };
                string mode = t.EnumA switch
                {
                    "Choose" => "手选", "Random" => "随机",
                    "First" => "第一个", "All" => "全部", _ => "全部",
                };
                string kind = string.IsNullOrEmpty(t.Text) ? ""
                    : t.Text == "Follower" ? "随从"
                    : t.Text == "Spell" ? "法术"
                    : t.Text == "Amulet" ? "护符" : t.Text;
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
            case "c_repeat_until": return "重复直到条件成立";
            case "c_for_each": return "对每个目标";
            case "c_mode": return "模式抉择（选 " + NumText(blk.Number, blk.IntA) + " 个）";
            case "act_damage": return "对【" + DescribeTargetShort(blk.Target) + "】造成【" + NumText(blk.Number, 2) + "】点伤害";
            case "act_split_damage": return "向【" + DescribeTargetShort(blk.Target) + "】分配【" + NumText(blk.Number, 2) + "】点伤害";
            case "act_restore": return "为【" + DescribeTargetShort(blk.Target) + "】回复【" + NumText(blk.Number, 2) + "】点生命";
            case "act_full_restore": return "完全回复生命";
            case "act_draw": return "抽取 " + NumText(blk.Number, 1) + " 张牌";
            case "act_summon": return "召唤 " + FormatCardTitle(blk.IntB, blk.Text);
            case "act_add_hand": return "生成 " + FormatCardTitle(blk.IntB, blk.Text) + " 加入手牌";
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
            case "act_mod_counter":
            {
                // 完整摘要：作用对象 + 计数器 + 操作 + 数值。
                // 只写「修改计数器【pp】」的话，玩家在画布上看不出这是加还是减、加几点。
                string who = blk.Target != null ? DescribeTargetShort(blk.Target) : "我方主战者";
                string ctrZh = DiyZh.Counters.FirstOrDefault(x => x.Key == (blk.Text ?? "")).Zh ?? (blk.Text ?? "pp");
                string ctrOp = blk.EnumA == "Set" ? "设为" : blk.EnumA == "Fill" ? "回满" : (blk.FlagA ? "减少" : "增加");
                string ctrNum = blk.EnumA == "Fill" ? "" : NumText(blk.Number, 1);
                return "使【" + who + "】的【" + ctrZh + "】" + ctrOp + ctrNum;
            }
            case "act_gain_crest":
                // 纹章是独立合成实体，编译时 CardId 恒为 0（靠卡名存），
                // 只看 IntB 会永远显示 #?。
                return "获得纹章【" + (!string.IsNullOrEmpty(blk.Text) ? blk.Text
                       : (blk.IntB == 0 ? "本卡纹章" : "#" + blk.IntB)) + "】";
            case "act_copy": return "复制目标";
            case "act_discover": return "从卡池发现";
            case "act_reanimate": return "亡者召还";
            case "act_transform": return "使目标变身为 " + FormatCardTitle(blk.IntB, blk.Text);
            case "act_replace_deck": return "替换整副牌组";
            case "act_damage_aura": return "护盾光环：使【" + DescribeTargetShort(blk.Target) + "】" + DiyZh.Zh(DiyZh.DamageAuraKinds, blk.EnumA) + "【" + NumText(blk.Number, 3) + "】";
            case "act_grant_entity_ability": return "赋予目标一段能力";
            case "act_grant_ability": return "赋予常驻能力";
            case "act_invoke": return "复刻发动时机能力";
            case "act_deck_mod":
            {
                // 画出具体规则，而不是干瘪的一行「构筑规则修正」——
                // 玩家在一屏里分不清三张同名的构筑修正积木各改了什么。
                string sign = blk.IntA >= 0 ? "+" : "";
                string detail = (blk.Text ?? "size") switch
                {
                    "size" => "牌组容量 " + sign + blk.IntA,
                    "leader_life" => "主战者生命 " + sign + blk.IntA,
                    "hand" => "起手牌数 " + sign + blk.IntA,
                    "ep" => "初始EP " + sign + blk.IntA,
                    "sep" => "初始SEP " + sign + blk.IntA,
                    "self_copy" => "本卡上限设为 " + blk.IntA + " 张",
                    "allow_clan_fixed" => "允许投入【" + (blk.CardNameA ?? "?") + "】职业卡",
                    "allow_clan_choose" => "允许任选 " + blk.IntA + " 个额外职业",
                    _ => "构筑规则修正",
                };
                return "★ 构筑修正：" + detail + (blk.FlagA ? "（按张数叠加）" : "");
            }
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

    /// <summary>
    /// 该关键词是否带数值参数。
    ///
    /// 带参的（1回合可攻击N次 / 受到伤害上限N / 受到伤害+N）必须露出数值孔位，
    /// 否则玩家只能选到关键词、填不到 N，实战会退化成默认值。
    /// </summary>
    private static bool IsParameterizedKw(string kw)
        => kw is "max_attacks_per_turn" or "damage_cap" or "damage_taken_bonus";
}

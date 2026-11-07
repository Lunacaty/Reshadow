using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 书页按钮弹出的对局信息统计界面（四页签：日志 / 使用 / 破坏 / 计数）。
///
/// 全部数据来自 BattleState 的公开字段，UI 不做任何推测。
/// 日志页读 BattleLogs（内核在 EmitEvent 里逐条翻译成中文），
/// 使用页读 PlayerState.PlayedThisMatch，破坏页读 DestroyedThisMatch。
/// </summary>
public partial class BattleInfoModal : Control
{
    private BattleSessionController _ctrl;
    private int _activeTab;
    private HBoxContainer _tabRow;
    private VBoxContainer _body;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 82;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                QueueFree();
        };
        AddChild(dim);

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(820, 560));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, new Color("D4AF37"), 12, 2, 18));
        AddChild(panel);

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        panel.AddChild(root);

        var head = new HBoxContainer();
        root.AddChild(head);

        var title = new Label { Text = "对局信息统计", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        head.AddChild(title);

        var close = new Button { Text = "X" };
        SvWbTheme.StyleButton(close);
        close.Pressed += QueueFree;
        head.AddChild(close);

        _tabRow = new HBoxContainer();
        _tabRow.AddThemeConstantOverride("separation", 8);
        root.AddChild(_tabRow);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        SvWbTheme.StyleScroll(scroll);

        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_body);

        SwitchTab(0);
    }

    private void SwitchTab(int tabIndex)
    {
        _activeTab = tabIndex;
        foreach (var c in _tabRow.GetChildren())
        {
            _tabRow.RemoveChild(c);
            c.QueueFree();
        }

        string[] tabs = { "1 日志", "2 使用", "3 破坏", "4 计数" };
        for (int i = 0; i < tabs.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Text = tabs[i],
                CustomMinimumSize = new Vector2(180, 38),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            SvWbTheme.StyleButton(btn, isPrimary: _activeTab == idx,
                                  customAccent: _activeTab == idx ? SvWbTheme.AccentGold : (Color?)null);
            btn.Pressed += () => SwitchTab(idx);
            _tabRow.AddChild(btn);
        }

        RebuildBody();
    }

    private void RebuildBody()
    {
        foreach (var c in _body.GetChildren())
        {
            _body.RemoveChild(c);
            c.QueueFree();
        }
        if (_ctrl?.State == null) return;

        switch (_activeTab)
        {
            case 0: BuildLogTab(); break;
            case 1: BuildPlayedCardsTab(); break;
            case 2: BuildDestroyedCardsTab(); break;
            case 3: BuildCountersTab(); break;
        }
    }

    // ---------------- Tab 1：日志 ----------------
    private void BuildLogTab()
    {
        var logs = _ctrl.State.BattleLogs;
        if (logs == null || logs.Count == 0)
        {
            _body.AddChild(EmptyHint("（暂无对局日志）"));
            return;
        }
        foreach (var line in logs)
        {
            bool isTurnHeader = line.StartsWith("\u2014\u2014");
            var lbl = new RichTextLabel
            {
                BbcodeEnabled = true,
                Text = isTurnHeader
                    ? "[color=#FDE047][b]" + line + "[/b][/color]"
                    : SvWbTheme.FormatRichCardText(line),
                FitContent = true,
                ScrollActive = false,
            };
            lbl.AddThemeFontSizeOverride("normal_font_size", isTurnHeader ? 13 : 12);
            _body.AddChild(lbl);
        }
    }

    // ---------------- Tab 2：使用 ----------------
    private void BuildPlayedCardsTab()
    {
        var p0 = _ctrl.State.Players[0].PlayedThisMatch;
        var p1 = _ctrl.State.Players[1].PlayedThisMatch;
        _body.AddChild(BuildTwoColumnCardTally(
            "我方本局已使用卡牌", p0, "敌方本局已使用卡牌", p1));
    }

    // ---------------- Tab 3：破坏 ----------------
    private void BuildDestroyedCardsTab()
    {
        var d0 = _ctrl.State.Players[0].DestroyedThisMatch
            .Select(s => s.Def ?? _ctrl.State.CardDefOf(s.CardId))
            .Where(d => d != null).ToList();
        var d1 = _ctrl.State.Players[1].DestroyedThisMatch
            .Select(s => s.Def ?? _ctrl.State.CardDefOf(s.CardId))
            .Where(d => d != null).ToList();
        _body.AddChild(BuildTwoColumnCardTally(
            "我方本局被破坏卡牌", d0, "敌方本局被破坏卡牌", d1));
    }

    private Control BuildTwoColumnCardTally(
        string leftTitle, IEnumerable<CardDef> leftCards,
        string rightTitle, IEnumerable<CardDef> rightCards)
    {
        var cols = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cols.AddThemeConstantOverride("separation", 18);
        cols.AddChild(BuildCardTallyColumn(leftTitle, leftCards, new Color("38BDF8")));
        cols.AddChild(BuildCardTallyColumn(rightTitle, rightCards, new Color("F87171")));
        return cols;
    }

    private Control BuildCardTallyColumn(string title, IEnumerable<CardDef> cards, Color headerColor)
    {
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 6);

        var head = new Label { Text = "-- " + title + " --" };
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AddThemeColorOverride("font_color", headerColor);
        col.AddChild(head);

        var grouped = (cards ?? Enumerable.Empty<CardDef>())
            .Where(c => c != null)
            .GroupBy(c => DeckEditorViewModel.DisplayName(c))
            .Select(g => (Def: g.First(), Name: g.Key, Count: g.Count()))
            .OrderBy(x => x.Def.BaseCost)
            .ThenBy(x => x.Def.CardId)
            .ToList();

        if (grouped.Count == 0)
        {
            col.AddChild(EmptyHint("（无记录）"));
            return col;
        }

        foreach (var item in grouped)
        {
            var bar = new ArtCenterBannerBox
            {
                CardId = item.Def.CardId,
                ArtCardId = item.Def.ArtCardId,
                DimAlpha = 0.68f,
                CustomMinimumSize = new Vector2(0, 36),
            };
            bar.AddThemeStyleboxOverride("panel", new StyleBoxEmpty());

            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var margin = new MarginContainer();
            margin.AddThemeConstantOverride("margin_left", 10);
            margin.AddThemeConstantOverride("margin_right", 12);
            margin.AddChild(row);
            bar.AddChild(margin);

            var costLbl = new Label
            {
                Text = "[" + item.Def.BaseCost + "费]",
                CustomMinimumSize = new Vector2(42, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            costLbl.AddThemeFontSizeOverride("font_size", 12);
            costLbl.AddThemeColorOverride("font_color", new Color("4ADE80"));
            row.AddChild(costLbl);

            var nameLbl = new Label
            {
                Text = item.Name,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                VerticalAlignment = VerticalAlignment.Center,
                ClipText = true,
            };
            nameLbl.AddThemeFontSizeOverride("font_size", 13);
            nameLbl.AddThemeColorOverride("font_color", Colors.White);
            nameLbl.AddThemeConstantOverride("outline_size", 3);
            nameLbl.AddThemeColorOverride("font_outline_color", Colors.Black);
            row.AddChild(nameLbl);

            var cntLbl = new Label
            {
                Text = "* " + item.Count,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cntLbl.AddThemeFontSizeOverride("font_size", 14);
            cntLbl.AddThemeColorOverride("font_color", new Color("FEF08A"));
            row.AddChild(cntLbl);

            col.AddChild(bar);
        }
        return col;
    }

    // ---------------- Tab 4：计数（过滤 0） ----------------
    private void BuildCountersTab()
    {
        string[] counters = { "rally", "combo", "evolve_count", "shadow", "faith",
                              "earth_sigil", "clues", "wrath" };
        string[] zh = { "协作数 (Rally)", "本回合连击 (Combo)", "本局进化次数", "墓场数 (Shadows)",
                        "信仰值 (Faith)", "土之印层数", "线索层数", "血怒次数 (Wrath)" };

        int shown = 0;
        for (int i = 0; i < counters.Length; i++)
        {
            int mine = _ctrl.ReadCounter(0, counters[i]);
            int foe = _ctrl.ReadCounter(1, counters[i]);
            if (mine == 0 && foe == 0) continue;
            _body.AddChild(CounterRow(zh[i], mine, foe));
            shown++;
        }

        // 卡池自定义主战者计数器（如【剑意】sword_intent）。
        // 只遍历上面那 8 个内置项的话，玩家自定义的计数器哪怕涨到 10，
        // 点开书页统计也完全看不到（180 号文档 D2）。
        foreach (var pc in _ctrl.State.PoolCounterDefs
                     ?? Array.Empty<ShadowverseFan.KernelV3.Packs.PoolCounterDef>())
        {
            if (pc == null || pc.IsBuiltin || string.IsNullOrWhiteSpace(pc.Name)) continue;
            int mine = _ctrl.ReadCounter(0, pc.Name);
            int foe = _ctrl.ReadCounter(1, pc.Name);
            if (mine == 0 && foe == 0) continue;
            string label = string.IsNullOrWhiteSpace(pc.NameChs)
                ? pc.Name : pc.NameChs + " (" + pc.Name + ")";
            _body.AddChild(CounterRow(label, mine, foe));
            shown++;
        }

        if (shown == 0)
            _body.AddChild(EmptyHint("（当前所有特殊计数器均为 0）"));
    }

    private static Control CounterRow(string label, int mine, int foe)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 16);

        var a = new Label { Text = label, CustomMinimumSize = new Vector2(200, 30), VerticalAlignment = VerticalAlignment.Center };
        a.AddThemeFontSizeOverride("font_size", 13);
        a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(a);

        var b = new Label { Text = "我方：" + mine, CustomMinimumSize = new Vector2(140, 30), VerticalAlignment = VerticalAlignment.Center };
        b.AddThemeFontSizeOverride("font_size", 14);
        b.AddThemeColorOverride("font_color", new Color("38BDF8"));
        row.AddChild(b);

        var c = new Label { Text = "敌方：" + foe, VerticalAlignment = VerticalAlignment.Center };
        c.AddThemeFontSizeOverride("font_size", 14);
        c.AddThemeColorOverride("font_color", new Color("F87171"));
        row.AddChild(c);

        return row;
    }

    private static Label EmptyHint(string t)
    {
        var l = new Label { Text = t };
        l.AddThemeFontSizeOverride("font_size", 13);
        l.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        return l;
    }
}

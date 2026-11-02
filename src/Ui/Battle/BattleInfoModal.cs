using System;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 最左侧书页按钮弹出的对局信息统计界面。
///
/// 集中展示双方公开统计（协作/连击/进化/墓场/信仰等）、
/// 已入场的卡牌与兵种种类、已破坏列表、以及战况日志。
/// 全部数据来自 BattleState 的公开字段 —— UI 不做任何推测。
/// </summary>
public partial class BattleInfoModal : Control
{
    private BattleSessionController _ctrl;
    private VBoxContainer _body;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        dim.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                QueueFree();
        };
        AddChild(dim);

        var panel = new PanelContainer();
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.Size = new Vector2(760, 560);
        panel.Position = new Vector2(260, 80);
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, new Color("D4AF37"), 12, 2, 18));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 8);
        panel.AddChild(root);

        var head = new HBoxContainer();
        root.AddChild(head);

        var title = new Label { Text = "[ 书页 ] 对局信息统计", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        head.AddChild(title);

        var close = new Button { Text = "✕" };
        SvWbTheme.StyleButton(close);
        close.Pressed += QueueFree;
        head.AddChild(close);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        root.AddChild(scroll);
        SvWbTheme.StyleScroll(scroll);

        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 6);
        scroll.AddChild(_body);

        Rebuild();
    }

    private void Rebuild()
    {
        if (_ctrl?.State == null) return;

        _body.AddChild(Section("公开计数"));
        string[] counters = { "rally", "combo", "evolve_count", "shadow", "faith",
                              "earth_sigil", "clues", "wrath" };
        string[] zh = { "协作", "连击", "进化数", "墓场", "信仰",
                        "土之印", "线索", "血怒" };

        for (int i = 0; i < counters.Length; i++)
        {
            int mine = _ctrl.ReadCounter(0, counters[i]);
            int foe = _ctrl.ReadCounter(1, counters[i]);
            if (mine == 0 && foe == 0) continue;
            _body.AddChild(Row($"{zh[i]}", $"我方 {mine}", $"敌方 {foe}"));
        }

        _body.AddChild(Section("区域规模"));
        _body.AddChild(Row("手牌", $"{_ctrl.Hand(0).Count}", $"{_ctrl.Hand(1).Count}"));
        _body.AddChild(Row("牌库",
            $"{_ctrl.State.EnumerateZone(ZoneRef.Deck, 0).Count()}",
            $"{_ctrl.State.EnumerateZone(ZoneRef.Deck, 1).Count()}"));
        _body.AddChild(Row("墓场",
            $"{_ctrl.State.EnumerateZone(ZoneRef.Cemetery, 0).Count()}",
            $"{_ctrl.State.EnumerateZone(ZoneRef.Cemetery, 1).Count()}"));
        _body.AddChild(Row("纹章", $"{_ctrl.Crests(0).Count}", $"{_ctrl.Crests(1).Count}"));

        var leader0 = _ctrl.Leader(0);
        var leader1 = _ctrl.Leader(1);
        if (leader0 != null && leader1 != null)
        {
            _body.AddChild(Section("主战者"));
            _body.AddChild(Row("生命", $"{leader0.Life} / {leader0.MaxLife}",
                                      $"{leader1.Life} / {leader1.MaxLife}"));
        }

        _body.AddChild(Section("战场"));
        _body.AddChild(Row("随从 / 护符", $"{_ctrl.Board(0).Count}", $"{_ctrl.Board(1).Count}"));

        if (_ctrl.IsFinished)
        {
            _body.AddChild(Section("结果"));
            int w = _ctrl.Winner;
            _body.AddChild(Row("胜者", w < 0 ? "未决" : (w == 0 ? "我方" : "敌方"), ""));
        }
    }

    private static Label Section(string t)
    {
        var l = new Label { Text = "—— " + t };
        l.AddThemeFontSizeOverride("font_size", 14);
        l.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        return l;
    }

    private static Control Row(string label, string mine, string foe)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);

        var a = new Label { Text = label, CustomMinimumSize = new Vector2(120, 0) };
        a.AddThemeFontSizeOverride("font_size", 12);
        a.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        row.AddChild(a);

        var b = new Label { Text = mine, CustomMinimumSize = new Vector2(140, 0) };
        b.AddThemeFontSizeOverride("font_size", 12);
        b.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
        row.AddChild(b);

        var c = new Label { Text = foe };
        c.AddThemeFontSizeOverride("font_size", 12);
        c.AddThemeColorOverride("font_color", new Color("F87171"));
        row.AddChild(c);

        return row;
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Decks;

/// <summary>
/// 界面 2：三步引导创建向导弹窗（880×540）。
///
/// Step1 选赛制卡池 → Step2 选主战者职业 → Step3 输入名称并创建。
/// 卡池卡片下方列出的规则摘要全部从 MatchRules 读，不写死。
/// </summary>
public partial class CreateDeckWizardModal : Control
{
    public event Action<ConstructedDeck> OnCreated;
    public event Action OnCancelled;

    private readonly GameSessionStore _store;
    private int _step = 1;
    private string _poolId;
    private string _clan = "Neutral";

    private VBoxContainer _body;
    private Label _stepLabel;
    private Button _nextBtn;
    private LineEdit _nameEdit;

    private static readonly string[] Clans =
    {
        "Forestcraft", "Swordcraft", "Runecraft", "Dragoncraft",
        "Abysscraft", "Havencraft", "Portalcraft", "Neutral",
    };

    public CreateDeckWizardModal(GameSessionStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.72f) };
        dim.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.AccentCyan, 12, 2, 18));
        AddChild(panel);

        // 必须先 AddChild 再定位：Position 在未入树时会被父级布局覆盖。
        // 且要用 Size 显式指定尺寸 —— 只设 CustomMinimumSize 时
        // PanelContainer 会按锚点拉伸填满父容器（这里就是整屏）。
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(880, 540));
        panel.Position = new Vector2(
            Mathf.Max(0, (GetViewportRect().Size.X - 880) / 2),
            Mathf.Max(0, (GetViewportRect().Size.Y - 540) / 2));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 12);
        panel.AddChild(root);

        _stepLabel = new Label { Text = "Step 1 / 3  选择所属赛制卡池" };
        _stepLabel.AddThemeFontSizeOverride("font_size", 20);
        _stepLabel.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        root.AddChild(_stepLabel);

        // _body 必须包在 ScrollContainer 里：
        // 卡池多起来时规则文字换行会把内容撑到 700px+，
        // 底部的「取消 / 下一步」栏就被挤出弹窗下边缘。
        var bodyScroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 380),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        SvWbTheme.StyleScroll(bodyScroll);
        root.AddChild(bodyScroll);

        _body = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _body.AddThemeConstantOverride("separation", 10);
        bodyScroll.AddChild(_body);

        var bottom = new HBoxContainer();
        bottom.AddThemeConstantOverride("separation", 10);
        root.AddChild(bottom);

        var cancel = new Button { Text = "取消" };
        SvWbTheme.StyleButton(cancel);
        cancel.Pressed += () => { OnCancelled?.Invoke(); QueueFree(); };
        bottom.AddChild(cancel);

        bottom.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });

        _nextBtn = new Button { Text = "下一步 →", Disabled = true };
        SvWbTheme.StyleButton(_nextBtn, isPrimary: true);
        _nextBtn.Pressed += OnNext;
        bottom.AddChild(_nextBtn);

        RenderStep();
    }

    private void RenderStep()
    {
        foreach (var c in _body.GetChildren()) c.QueueFree();
        _nextBtn.Disabled = true;

        switch (_step)
        {
            case 1: RenderPoolStep(); break;
            case 2: RenderClanStep(); break;
            case 3: RenderNameStep(); break;
        }
    }

    private void RenderPoolStep()
    {
        _stepLabel.Text = "Step 1 / 3  选择所属赛制卡池";
        _nextBtn.Text = "下一步 →";

        // 卡池多时自动折行，不再横向撑爆弹窗。
        var row = new HFlowContainer
        {
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        row.AddThemeConstantOverride("separation", 12);
        _body.AddChild(row);

        foreach (var kv in _store.PoolConfigs)
        {
            var cfg = kv.Value;
            var rules = cfg.Rules;
            bool selected = string.Equals(_poolId, cfg.PoolId, StringComparison.OrdinalIgnoreCase);

            // 高度只到内容所需即可 —— 撑到 320 会在下方留一大片空白。
            var card = new PanelContainer
            {
                CustomMinimumSize = new Vector2(260, 0),
                SizeFlagsVertical = SizeFlags.ShrinkBegin,
            };
            card.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                selected ? new Color(0.05f, 0.18f, 0.24f, 0.95f) : SvWbTheme.BgSurface,
                selected ? SvWbTheme.AccentCyan : SvWbTheme.BorderSubtle,
                10, selected ? 2 : 1, selected ? 10 : 2));
            card.MouseFilter = MouseFilterEnum.Stop;
            row.AddChild(card);

            var v = new VBoxContainer();
            v.AddThemeConstantOverride("separation", 6);
            card.AddChild(v);

            var nm = new Label { Text = cfg.Name };
            nm.AddThemeFontSizeOverride("font_size", 16);
            nm.AddThemeColorOverride("font_color", SvWbTheme.TextPrimary);
            v.AddChild(nm);

            var id = new Label { Text = $"({cfg.PoolId})" };
            id.AddThemeFontSizeOverride("font_size", 11);
            id.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            v.AddChild(id);

            // 规则摘要：全部来自 MatchRules
            v.AddChild(RuleLine($"主战者初始生命: {rules.LeaderInitialLife}"));
            v.AddChild(RuleLine($"能量上限: {rules.MaxPpCap} (每回合+{rules.PpGainPerTurn})"));
            v.AddChild(RuleLine($"基础构筑: {rules.MinDeckSize}~{rules.MaxDeckSize}张 (同名限{rules.MaxCopiesPerCard}张)"));
            v.AddChild(RuleLine($"起手抽牌: {rules.StartingHandSize} 张"));
            v.AddChild(RuleLine($"进化解锁: 先手{rules.EvolveUnlockTurnFirstPlayer} / 后手{rules.EvolveUnlockTurnSecondPlayer} 回合"));

            string pid = cfg.PoolId;
            card.GuiInput += (InputEvent e) =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    _poolId = pid;
                    RenderStep();
                }
            };
        }

        if (!string.IsNullOrEmpty(_poolId)) _nextBtn.Disabled = false;
    }

    private void RenderClanStep()
    {
        _stepLabel.Text = "Step 2 / 3  选择主战者职业";
        _nextBtn.Text = "下一步 →";

        var grid = new GridContainer
        {
            Columns = 8,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
        };
        grid.AddThemeConstantOverride("h_separation", 10);
        _body.AddChild(grid);

        foreach (var clan in Clans)
        {
            Color col = SvWbTheme.ClanColor(clan);
            bool selected = string.Equals(_clan, clan, StringComparison.OrdinalIgnoreCase);

            var card = new PanelContainer { CustomMinimumSize = new Vector2(96, 140) };
            card.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                col.Darkened(0.65f), selected ? col : SvWbTheme.BorderSubtle,
                8, selected ? 3 : 1, selected ? 10 : 0));
            // 选中时向上浮起 8px（对齐文档要求）
            card.Position = new Vector2(0, selected ? -8 : 0);
            card.MouseFilter = MouseFilterEnum.Stop;
            grid.AddChild(card);

            var center = new CenterContainer();
            card.AddChild(center);
            var lbl = new Label
            {
                Text = SvWbTheme.ClanDisplayZh(clan),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            lbl.AddThemeFontSizeOverride("font_size", 14);
            lbl.AddThemeColorOverride("font_color", selected ? Colors.White : SvWbTheme.TextSecondary);
            center.AddChild(lbl);

            string c = clan;
            card.GuiInput += (InputEvent e) =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                {
                    _clan = c;
                    RenderStep();
                }
            };
        }

        if (!string.IsNullOrEmpty(_clan)) _nextBtn.Disabled = false;
    }

    private void RenderNameStep()
    {
        _stepLabel.Text = "Step 3 / 3  确认牌组名称";
        _nextBtn.Text = "确认并进入构筑 →";

        _nameEdit = new LineEdit
        {
            Text = $"新建{SvWbTheme.ClanDisplayZh(_clan)}牌组",
            CustomMinimumSize = new Vector2(0, 40),
        };
        _nameEdit.AddThemeFontSizeOverride("font_size", 15);
        _body.AddChild(_nameEdit);
        _nextBtn.Disabled = false;
    }

    private void OnNext()
    {
        if (_step < 3) { _step++; RenderStep(); return; }

        string name = _nameEdit != null && !string.IsNullOrWhiteSpace(_nameEdit.Text)
            ? _nameEdit.Text.Trim()
            : $"新建{SvWbTheme.ClanDisplayZh(_clan)}牌组";

        var deck = _store.CreateDeck(_poolId, _clan, name);
        OnCreated?.Invoke(deck);
        QueueFree();
    }

    private static Label RuleLine(string t)
    {
        var l = new Label { Text = "· " + t, AutowrapMode = TextServer.AutowrapMode.WordSmart };
        l.AddThemeFontSizeOverride("font_size", 11);
        l.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        return l;
    }
}

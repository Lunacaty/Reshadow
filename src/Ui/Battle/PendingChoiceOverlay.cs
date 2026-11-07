using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// VM 挂起交互总控（SelectTarget / SelectMode / DiscoverCard 三合一）。
///
/// 三种挂起的**交互形态刻意不同**：
///   · SelectTarget 若候选都在场上/手牌 → 不遮挡战场，只挂顶部横幅，
///     玩家直接点高亮的棋子；
///   · SelectMode / DiscoverCard 是「看选项做决定」，才用居中浮层，
///     并都带 [查看战场] 折叠开关。
/// 把三者统一成遮罩弹窗会把「点一个目标」变成「先弹窗再选」，手感完全不同。
/// </summary>
public partial class PendingChoiceOverlay : Control
{
    public event Action OnChoiceSubmitted;

    private BattleSessionController _ctrl;
    private int _boundChoiceId = -1;
    private readonly List<int> _selected = new();
    private bool _peekingField;

    private Control _modalContent;

    /// <summary>
    /// 该全局坐标是否落在挂起选择浮层的交互控件上
    /// （模式弹窗、查看战场按钮、多选确认按钮）。
    ///
    /// _Input 先于所有控件的 GuiInput 执行，不先问一句就把点击当成
    /// 「点空白取消」，模式弹窗会一点就消失。
    /// </summary>
    public bool IsPointOnOverlayUi(Vector2 globalPos)
    {
        if (!Visible) return false;
        if (_modalContent != null && IsInstanceValid(_modalContent) && _modalContent.Visible
            && _modalContent.GetGlobalRect().HasPoint(globalPos))
            return true;
        if (_peekBtn != null && IsInstanceValid(_peekBtn) && _peekBtn.Visible
            && _peekBtn.GetGlobalRect().HasPoint(globalPos))
            return true;
        if (_bannerConfirmBtn != null && IsInstanceValid(_bannerConfirmBtn) && _bannerConfirmBtn.Visible
            && _bannerConfirmBtn.GetGlobalRect().HasPoint(globalPos))
            return true;
        return false;
    }
    private Button _peekBtn;
    private Label _bannerLabel;
    private Button _bannerConfirmBtn;

    public IReadOnlyList<int> SelectedItems => _selected;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        ZIndex = 70;
        Visible = false;
    }

    /// <summary>战场棋子 / 主战者 / 手牌被点击时调用，尝试作为 SelectTarget 候选响应。</summary>
    public bool TryPickBoardOrHandEntity(int entityId)
    {
        // 客机模式下 Runner.PendingChoice 恒为 null，必须走 EffectivePendingChoice。
        var p = _ctrl?.EffectivePendingChoice;
        if (p == null || p.Kind != ChoiceKind.SelectTarget) return false;
        if (!p.Candidates.Contains(entityId)) return false;

        if (_selected.Contains(entityId))
        {
            _selected.Remove(entityId);
        }
        else
        {
            if (p.MaxSelect == 1)
            {
                Submit(p.ChoiceId, entityId);
                return true;
            }
            if (_selected.Count < p.MaxSelect) _selected.Add(entityId);
        }

        RefreshState();
        return true;
    }

    public void RefreshState()
    {
        // 客机不跑本地 VM，Runner 里永远没有挂起选择；
        // 必须读 EffectivePendingChoice（远端快照带来的那一份）。
        var p = _ctrl?.EffectivePendingChoice;
        if (p == null || !_ctrl.IsWaitingForInput || p.PlayerId != _ctrl.MyPlayerId)
        {
            _boundChoiceId = -1;
            _selected.Clear();
            Visible = false;
            return;
        }

        if (_boundChoiceId != p.ChoiceId)
        {
            _boundChoiceId = p.ChoiceId;
            _selected.Clear();
            _peekingField = false;
        }

        Visible = true;
        foreach (var c in GetChildren()) c.QueueFree();

        switch (p.Kind)
        {
            case ChoiceKind.SelectTarget: BuildSelectTargetUi(p); break;
            case ChoiceKind.SelectMode:   BuildSelectModeUi(p);   break;
            case ChoiceKind.DiscoverCard: BuildDiscoverUi(p);     break;
        }
    }

    // ---------------------------------------------------------------- SelectTarget
    private void BuildSelectTargetUi(PendingChoice p)
    {
        bool onFieldOrHand = p.Candidates.All(id =>
            _ctrl.Board(0).Any(e => e.EntityId == id) ||
            _ctrl.Board(1).Any(e => e.EntityId == id) ||
            _ctrl.Leader(0)?.EntityId == id ||
            _ctrl.Leader(1)?.EntityId == id ||
            _ctrl.Hand(0).Any(e => e.EntityId == id));

        if (onFieldOrHand)
        {
            MouseFilter = MouseFilterEnum.Ignore;

            var banner = new PanelContainer
            {
                Size = new Vector2(520, 48),
                MouseFilter = MouseFilterEnum.Stop,
            };
            SvWbTheme.KeepCentered(this, banner, new Vector2(520, 48),
                new Vector2(0f, -252f));
            banner.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                new Color(0.06f, 0.10f, 0.18f, 0.94f), new Color("FDE047"), 10, 2, 10));
            AddChild(banner);

            var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
            row.AddThemeConstantOverride("separation", 14);
            banner.AddChild(row);

            var srcEnt = _ctrl.GetPendingSourceEntity();
            string srcName = srcEnt?.Def != null
                ? "《" + DeckEditorViewModel.DisplayName(srcEnt.Def) + "》" : "";
            string needTxt = p.MinSelect == p.MaxSelect
                ? p.MaxSelect.ToString()
                : p.MinSelect + "~" + p.MaxSelect;

            _bannerLabel = new Label
            {
                Text = "🎯 " + srcName + " 请在场上或手牌点选目标 ("
                       + _selected.Count + " / " + needTxt + ")",
            };
            _bannerLabel.AddThemeFontSizeOverride("font_size", 15);
            _bannerLabel.AddThemeColorOverride("font_color", new Color("FEF08A"));
            row.AddChild(_bannerLabel);

            if (p.MaxSelect > 1)
            {
                _bannerConfirmBtn = new Button
                {
                    Text = "✔ 确认 (" + _selected.Count + ")",
                    Disabled = _selected.Count < p.MinSelect,
                };
                SvWbTheme.StyleButton(_bannerConfirmBtn, isPrimary: true,
                                      customAccent: SvWbTheme.StatusValid);
                var boundChoice = p.ChoiceId;
                _bannerConfirmBtn.Pressed += () => Submit(boundChoice, _selected.ToArray());
                row.AddChild(_bannerConfirmBtn);
            }
            return;
        }

        // 候选在牌库/墓场等不可直点区域 -> 弹卡片选择浮层
        BuildModalShell("🎯 请选择目标 (" + _selected.Count + " / " + p.MaxSelect + ")",
                        out var content);

        var hbox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        hbox.AddThemeConstantOverride("separation", 14);
        content.AddChild(hbox);

        foreach (int eid in p.Candidates)
        {
            var ent = FindEntityAnyZone(eid);
            if (ent?.Def == null) continue;
            bool picked = _selected.Contains(eid);

            var w = new CardVisualWidget();
            w.Setup(ent.Def, _ctrl.Store.CardPrinter, picked ? 1 : 0, 1, showCopyPips: true);
            w.SetCardSize(156, 218);
            int boundId = eid;
            w.OnLeftClick += _ => ToggleModalItem(p, boundId);
            hbox.AddChild(w);
        }
    }

    // ---------------------------------------------------------------- SelectMode
    private void BuildSelectModeUi(PendingChoice p)
    {
        var srcEnt = _ctrl.GetPendingSourceEntity();
        string srcTitle = srcEnt?.Def != null
            ? "《" + DeckEditorViewModel.DisplayName(srcEnt.Def) + "》" : "卡牌能力";
        BuildModalShell("❖ " + srcTitle + " — 请选择 " + p.MaxSelect + " 个【模式】发动",
                        out var content);

        var bodyRow = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        bodyRow.AddThemeConstantOverride("separation", 20);
        content.AddChild(bodyRow);

        if (srcEnt?.Def != null)
        {
            var leftCard = new CardVisualWidget();
            leftCard.Setup(srcEnt.Def, _ctrl.Store.CardPrinter, 0, 3, showCopyPips: false);
            leftCard.SetCardSize(192, 254);
            bodyRow.AddChild(leftCard);
        }

        var optCol = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        optCol.AddThemeConstantOverride("separation", 12);
        bodyRow.AddChild(optCol);

        var branch = _ctrl.GetPendingBranchModeNode();
        foreach (int modeIdx in p.Candidates)
        {
            bool picked = _selected.Contains(modeIdx);
            string optDesc = (branch != null && modeIdx >= 0 && modeIdx < branch.Options.Count)
                ? _ctrl.Store.CardPrinter.PrintNodes(branch.Options[modeIdx])
                : "模式选项 " + (modeIdx + 1);

            var optBox = new PanelContainer
            {
                CustomMinimumSize = new Vector2(520, 68),
                MouseFilter = MouseFilterEnum.Stop,
                MouseDefaultCursorShape = CursorShape.PointingHand,
            };
            optBox.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                picked ? new Color(0.14f, 0.24f, 0.36f, 0.96f) : SvWbTheme.BgSurface,
                picked ? new Color("FDE047") : new Color("D4AF37"),
                10, picked ? 3 : 1, picked ? 10 : 4));

            var h = new HBoxContainer();
            h.AddThemeConstantOverride("separation", 14);
            optBox.AddChild(h);

            var numLbl = new Label
            {
                Text = picked ? "✔ 模式 " + (modeIdx + 1) : "【模式 " + (modeIdx + 1) + "】",
                CustomMinimumSize = new Vector2(92, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            numLbl.AddThemeFontSizeOverride("font_size", 16);
            numLbl.AddThemeColorOverride("font_color",
                picked ? new Color("FEF08A") : SvWbTheme.AccentCyan);
            h.AddChild(numLbl);

            var rich = new RichTextLabel
            {
                BbcodeEnabled = true,
                Text = SvWbTheme.FormatRichCardText(optDesc),
                FitContent = true,
                ScrollActive = false,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            rich.AddThemeFontSizeOverride("normal_font_size", 14);
            rich.AddThemeFontSizeOverride("bold_font_size", 14);
            h.AddChild(rich);

            int boundIdx = modeIdx;
            optBox.GuiInput += e =>
            {
                if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
                    ToggleModalItem(p, boundIdx);
            };
            optCol.AddChild(optBox);
        }

        if (p.MaxSelect > 1)
        {
            var confirm = new Button
            {
                Text = "✔ 确认发动已选模式 (" + _selected.Count + " / " + p.MaxSelect + ")",
                Disabled = _selected.Count < p.MinSelect,
                CustomMinimumSize = new Vector2(240, 42),
            };
            SvWbTheme.StyleButton(confirm, isPrimary: true, customAccent: SvWbTheme.AccentGold);
            var boundChoice2 = p.ChoiceId;
            confirm.Pressed += () => Submit(boundChoice2, _selected.ToArray());
            optCol.AddChild(confirm);
        }
    }

    // ---------------------------------------------------------------- DiscoverCard
    private void BuildDiscoverUi(PendingChoice p)
    {
        BuildModalShell(
            "✦ 发现卡牌 — 请选择 " + p.MaxSelect + " 张 (" + _selected.Count + "/" + p.MaxSelect + ")",
            out var content);

        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 16);
        content.AddChild(row);

        foreach (int cardId in p.Candidates)
        {
            var def = _ctrl.State.CardDefOf(cardId) ?? _ctrl.Store.Catalog.TryGetCard(cardId);
            if (def == null) continue;
            bool picked = _selected.Contains(cardId);

            var col = new VBoxContainer { CustomMinimumSize = new Vector2(188, 0) };
            col.AddThemeConstantOverride("separation", 6);
            row.AddChild(col);

            var cardWdg = new CardVisualWidget();
            cardWdg.Setup(def, _ctrl.Store.CardPrinter, picked ? 1 : 0, 1, showCopyPips: false);
            cardWdg.SetCardSize(184, 242);
            int boundCid = cardId;
            cardWdg.OnLeftClick += _ => ToggleModalItem(p, boundCid);
            col.AddChild(cardWdg);

            // 卡下直接印效果：一屏看完 X 张，不用挨个悬停
            var descBox = new PanelContainer { CustomMinimumSize = new Vector2(184, 88) };
            descBox.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
                picked ? new Color(0.16f, 0.26f, 0.38f, 0.95f) : SvWbTheme.BgSurface,
                picked ? new Color("FDE047") : SvWbTheme.BorderSubtle, 8, picked ? 2 : 1, 2));

            var rich = new RichTextLabel
            {
                BbcodeEnabled = true,
                Text = SvWbTheme.FormatRichCardText(_ctrl.Store.CardPrinter.PrintCard(def)),
                FitContent = true,
                ScrollActive = false,
                MouseFilter = MouseFilterEnum.Ignore,
            };
            rich.AddThemeFontSizeOverride("normal_font_size", 11);
            rich.AddThemeFontSizeOverride("bold_font_size", 11);
            descBox.AddChild(rich);
            col.AddChild(descBox);
        }

        if (p.MaxSelect > 1)
        {
            var confirm = new Button
            {
                Text = "✔ 确认选择 (" + _selected.Count + " / " + p.MaxSelect + ")",
                Disabled = _selected.Count < p.MinSelect,
                CustomMinimumSize = new Vector2(220, 42),
            };
            SvWbTheme.StyleButton(confirm, isPrimary: true, customAccent: SvWbTheme.AccentGold);
            var boundChoice3 = p.ChoiceId;
            confirm.Pressed += () => Submit(boundChoice3, _selected.ToArray());
            content.AddChild(confirm);
        }
    }

    // ---------------------------------------------------------------- 公共外壳与提交
    private void BuildModalShell(string titleText, out VBoxContainer contentBox)
    {
        MouseFilter = _peekingField ? MouseFilterEnum.Ignore : MouseFilterEnum.Stop;

        if (!_peekingField)
        {
            var dim = new ColorRect { Color = new Color(0.02f, 0.04f, 0.09f, 0.76f) };
            dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            AddChild(dim);
        }

        _modalContent = new PanelContainer
        {
            Size = new Vector2(1000, 490),
            Visible = !_peekingField,
        };
        _modalContent.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, new Color("D4AF37"), 12, 2, 16));
        AddChild(_modalContent);

        contentBox = new VBoxContainer();
        contentBox.AddThemeConstantOverride("separation", 12);
        _modalContent.AddChild(contentBox);

        var head = new Label { Text = titleText, HorizontalAlignment = HorizontalAlignment.Center };
        head.AddThemeFontSizeOverride("font_size", 20);
        head.AddThemeColorOverride("font_color", new Color("FEF08A"));
        contentBox.AddChild(head);

        _peekBtn = new Button
        {
            Text = _peekingField ? "↩ 返回选择窗口" : "👁 查看战场",

            Size = new Vector2(130, 38),
            CustomMinimumSize = new Vector2(130, 38),
        };
        SvWbTheme.StyleButton(_peekBtn, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
        _peekBtn.Pressed += () => { _peekingField = !_peekingField; RefreshState(); };
        AddChild(_peekBtn);

        // 不再写死 (140,92) / (1010,42)：那两个坐标是按 1280x720 算的，
        // 全屏下弹窗会跑到左上、按钮会飘到屏幕中间。
        SvWbTheme.KeepCentered(this, _modalContent, new Vector2(1000, 490), new Vector2(0, -16));
        SvWbTheme.KeepCentered(this, _peekBtn, new Vector2(130, 38), new Vector2(435, -295));
    }

    private void ToggleModalItem(PendingChoice p, int val)
    {
        if (p.MaxSelect == 1) { Submit(p.ChoiceId, val); return; }
        if (!_selected.Remove(val) && _selected.Count < p.MaxSelect) _selected.Add(val);
        RefreshState();
    }

    /// <summary>远程模式下取代本地 SubmitChoice（客机只发意图）。</summary>
    public Action<int, int[]> RemoteSubmit { get; set; }

    private void Submit(int choiceId, params int[] chosen)
    {
        _boundChoiceId = -1;
        _selected.Clear();
        Visible = false;
        // 客机模式：选择也必须上行给权威端，本地不结算；先乐观清空挂起防止闪回（178 号文档 2.2）
        if (RemoteSubmit != null)
        {
            _ctrl?.ClearRemotePendingChoice();
            RemoteSubmit(choiceId, chosen);
            OnChoiceSubmitted?.Invoke();
            return;
        }
        _ctrl.SubmitChoice(choiceId, chosen);
        OnChoiceSubmitted?.Invoke();
    }

    private RuntimeEntity FindEntityAnyZone(int entityId)
    {
        foreach (ZoneRef z in new[]
        {
            ZoneRef.Board, ZoneRef.Hand, ZoneRef.Cemetery, ZoneRef.Deck,
            ZoneRef.Banished, ZoneRef.CrestArea, ZoneRef.Leader,
        })
        {
            foreach (var e in _ctrl.State.EnumerateZone(z, -1))
                if (e != null && e.EntityId == entityId) return e;
        }
        return null;
    }
}
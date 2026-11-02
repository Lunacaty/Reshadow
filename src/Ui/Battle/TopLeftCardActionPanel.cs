using System;
using Godot;
using ShadowverseFan.KernelV3.Ast;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 左上角卡牌详情与操作弹窗（X:18, Y:82, 宽 336）。
///
/// 单击任意手牌 / 场上棋子 / 纹章时在这里展示效果，
/// 并在底部给出【进化】【超进化】【启动】【融合】按钮 ——
/// 原版战斗界面的核心操作入口就是这个左上角窗，不是右键菜单。
/// </summary>
public partial class TopLeftCardActionPanel : PanelContainer
{
    private BattleSessionController _ctrl;
    private RuntimeEntity _bound;

    /// <summary>
    /// 由 BattleScreenRoot 注入的阻塞门禁判定。
    ///
    /// 三层动画架构里，进化/超进化属于「阻塞段」——
    /// 演出没播完之前玩家不该改状态。左上角弹窗最初没有查这个门禁，
    /// 于是当场上有阻塞动画时，玩家仍能从这里强行点进化，
    /// 后端立刻结算而演出还排着队，视觉与状态彻底脱节。
    /// </summary>
    public Func<bool> IsInputBlocked { get; set; }

    private Label _nameLbl;
    private Label _metaLbl;
    private RichTextLabel _effectRich;
    private HBoxContainer _actionRow;

    public event Action<RuntimeEntity> OnEntityChanged;

    public void Setup(BattleSessionController ctrl)
    {
        _ctrl = ctrl;
        Visible = false;
        Position = new Vector2(18, 82);
        Size = new Vector2(336, 0);
        CustomMinimumSize = new Vector2(336, 0);
        ZIndex = 65;

        AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.06f, 0.09f, 0.16f, 0.95f), new Color("D4AF37"), 10, 2, 12));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 6);
        AddChild(root);

        var head = new HBoxContainer();
        root.AddChild(head);

        _nameLbl = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _nameLbl.AddThemeFontSizeOverride("font_size", 16);
        _nameLbl.AddThemeColorOverride("font_color", new Color("FEF08A"));
        head.AddChild(_nameLbl);

        var close = new Button { Text = "✕" };
        SvWbTheme.StyleButton(close);
        close.Pressed += HidePanel;
        head.AddChild(close);

        _metaLbl = new Label();
        _metaLbl.AddThemeFontSizeOverride("font_size", 12);
        _metaLbl.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        root.AddChild(_metaLbl);

        _effectRich = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            ScrollActive = false,
            CustomMinimumSize = new Vector2(310, 0),
        };
        _effectRich.AddThemeFontSizeOverride("normal_font_size", 12);
        _effectRich.AddThemeFontSizeOverride("bold_font_size", 12);
        root.AddChild(_effectRich);

        _actionRow = new HBoxContainer();
        _actionRow.AddThemeConstantOverride("separation", 8);
        root.AddChild(_actionRow);
    }

    /// <summary>
    /// 延迟到下一帧再重建面板与战场。
    ///
    /// 进化在按钮自己的 Pressed 回调里执行，若当场重建，
    /// 正在派发信号的按钮会被 QueueFree —— 界面卡死。
    /// </summary>
    private RuntimeEntity _pendingRefresh;

    private void DeferRefresh(RuntimeEntity entity)
    {
        // CallDeferred 只能传 Godot 的 Variant 参数，RuntimeEntity 是纯 C# 对象，
        // 因此把待刷新实体存在字段里，延迟调用自己不带参数。
        _pendingRefresh = entity;
        CallDeferred(nameof(DoRefresh));
    }

    private void DoRefresh()
    {
        var entity = _pendingRefresh;
        _pendingRefresh = null;
        if (entity == null) { HidePanel(); return; }
        ShowForEntity(entity);
        OnEntityChanged?.Invoke(entity);
    }

    /// <summary>当前是否处于阻塞状态（进化演出 / Choose 挂起 / 终局）。</summary>
    private bool Blocked => IsInputBlocked != null && IsInputBlocked();

    public void ShowForEntity(RuntimeEntity entity)
    {
        if (entity?.Def == null || _ctrl == null) { HidePanel(); return; }
        _bound = entity;
        var def = entity.Def;

        _nameLbl.Text = DeckEditorViewModel.DisplayName(def)
            + (entity.IsSuperEvolved ? " 【超进化】" : entity.IsEvolved ? " 【进化】" : "");

        string stat = def.Kind == CardKind.Follower
            ? $"费用 {entity.Cost}  |  ⚔ {entity.Attack}  |  【盾】{entity.Life}/{entity.MaxLife}"
            : $"费用 {entity.Cost}  |  {def.Kind}";
        _metaLbl.Text = $"【{SvWbTheme.ClanDisplayZh(def.Clan.IsValid ? def.Clan.Name : null)}】 {stat}";

        _effectRich.Text = SvWbTheme.FormatRichCardText(_ctrl.Store.CardPrinter.PrintCard(def));

        // 不能在按钮自己的 Pressed 回调里销毁按钮本身。
        // Godot 的信号派发持有当前控件的引用，边派发边 QueueFree
        // 会把「信号发出者」在回调中途拆掉 —— 表现为点击后整个界面卡死。
        // 这里延到下一帧再清，回调结束时控件仍然有效。
        foreach (var c in _actionRow.GetChildren())
        {
            _actionRow.RemoveChild(c);
            c.QueueFree();
        }

        if (_ctrl.CanEvolveUnit(entity))
        {
            var b = new Button { Text = "✦ 进化 (+2/+2)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentGold);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEvolve(entity, false)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (_ctrl.CanSuperEvolveUnit(entity))
        {
            var b = new Button { Text = "★ 超进化 (+3/+3)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEvolve(entity, true)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (_ctrl.CanEngageUnit(entity, out int cost))
        {
            var b = new Button { Text = $"⚡ 启动 ({cost}费)" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentCyan);
            b.Pressed += () => { if (Blocked) return; if (_ctrl.TryEngage(entity)) DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }
        if (entity.Zone == ZoneRef.Hand && _ctrl.CanStartFuse(entity))
        {
            var b = new Button { Text = "◆ 进行融合" };
            SvWbTheme.StyleButton(b, isPrimary: true, customAccent: SvWbTheme.AccentPurple);
            b.Pressed += () => { if (Blocked) return; _ctrl.StartFuseMode(entity); DeferRefresh(entity); };
            _actionRow.AddChild(b);
        }

        if (_ctrl.IsInFuseMode && ReferenceEquals(_ctrl.FuseBaseCard, entity))
        {
            var ok = new Button
            {
                Text = $"✔ 确认融合 (已选 {_ctrl.SelectedFuseMaterials.Count} 张)",
                Disabled = _ctrl.SelectedFuseMaterials.Count == 0,
            };
            SvWbTheme.StyleButton(ok, isPrimary: true, customAccent: SvWbTheme.StatusValid);
            ok.Pressed += () => { _ctrl.ConfirmFuse(); DeferRefresh(entity); };
            _actionRow.AddChild(ok);

            var cancel = new Button { Text = "取消融合" };
            SvWbTheme.StyleButton(cancel);
            cancel.Pressed += () => { _ctrl.CancelFuseMode(); DeferRefresh(entity); };
            _actionRow.AddChild(cancel);
        }

        Visible = true;
    }

    public void HidePanel()
    {
        _bound = null;
        Visible = false;
    }

    public RuntimeEntity BoundEntity => _bound;
}
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Core;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.KernelV3.Vm;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Battle;

/// <summary>
/// 战斗桌面根节点：把 6 个组件与拖拽状态机接起来。
///
/// 交互铁律（全部靠拖拽，不用「先选中再点按钮」）：
///   ① 点手牌区展开 / 点战场空白收起
///   ② 按住手牌向上拖过 Y&lt;500 松手即打出
///   ③ 按住场上随从拖出箭头到目标松手即攻击
///   ④ 单击任意卡/棋子/纹章 → 左上角弹出详情与操作按钮
/// </summary>
public partial class BattleScreenRoot : Control
{
    private GameSessionStore _store;
    private BattleSessionController _ctrl;

    private BattlefieldCanvas _field;
    private LeaderMedallionWidget _allyLeader;
    private LeaderMedallionWidget _foeLeader;
    private RightCommandColumnWidget _rightCol;
    private TopLeftCardActionPanel _actionPanel;
    private Control _handLayer;
    private VBoxContainer _boardAlly;
    private VBoxContainer _boardFoe;
    private Button _pageBtn;

    private bool _handExpanded;
    private RuntimeEntity _attacker;
    private bool _draggingPlay;
    private RuntimeEntity _draggingCard;

    public event Action OnExitToDecks;

    public void Boot(GameSessionStore store, ConstructedDeck mine, ConstructedDeck foe)
    {
        _store = store;
        _ctrl = new BattleSessionController(store, mine, foe, seed: 20260101);
        BoardUnitWidget.CurrentController = _ctrl;
        BuildUi();
        Rebuild();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _field = new BattlefieldCanvas { Controller = _ctrl };
        _field.OnCrestClicked += e => _actionPanel.ShowForEntity(e);
        AddChild(_field);

        _foeLeader = new LeaderMedallionWidget();
        _foeLeader.Setup(_ctrl, 1, true);
        _foeLeader.Position = new Vector2(430, 10);
        _foeLeader.OnLeaderClicked += () => _actionPanel.ShowForEntity(_ctrl.Leader(1));
        AddChild(_foeLeader);

        _allyLeader = new LeaderMedallionWidget();
        _allyLeader.Setup(_ctrl, 0, false);
        _allyLeader.Position = new Vector2(520, 480);
        _allyLeader.OnLeaderClicked += () => _actionPanel.ShowForEntity(_ctrl.Leader(0));
        AddChild(_allyLeader);

        _rightCol = new RightCommandColumnWidget();
        _rightCol.Setup(_ctrl);
        _rightCol.Position = new Vector2(1035, 115);
        _rightCol.OnEndTurnClicked += () => { _ctrl.EndTurn(); Rebuild(); };
        _rightCol.OnExtraPpClicked += () => { _ctrl.TryUseExtraPp(); Rebuild(); };
        AddChild(_rightCol);

        // 棋盘容器（上敌下我，各 5 格）
        _boardFoe = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _boardFoe.Position = new Vector2(300, 165);
        AddChild(_boardFoe);

        _boardAlly = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        _boardAlly.Position = new Vector2(300, 350);
        AddChild(_boardAlly);

        _handLayer = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_handLayer);

        _actionPanel = new TopLeftCardActionPanel();
        _actionPanel.Setup(_ctrl);
        AddChild(_actionPanel);

        _pageBtn = new Button { Text = "[ 书页 ]", Position = new Vector2(46, 292) };
        _pageBtn.CustomMinimumSize = new Vector2(52, 52);
        SvWbTheme.StyleButton(_pageBtn);
        _pageBtn.Pressed += OpenInfoModal;
        AddChild(_pageBtn);

        // 点战场空白处收起手牌
        _field.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                if (_handExpanded) { _handExpanded = false; RebuildHand(); }
                _actionPanel.HidePanel();
            }
        };
    }

    public override void _Process(double delta)
    {
        if (_draggingPlay && _draggingCard != null)
        {
            var mp = GetGlobalMousePosition();
            _field.IsDraggingPlayCard = mp.Y < 500f;
            _field.QueueRedraw();
        }
    }

    // ---------------------------------------------------------------- 重建

    public void Rebuild()
    {
        RebuildBoards();
        RebuildHand();
        _allyLeader.QueueRedraw();
        _foeLeader.QueueRedraw();
        _rightCol.QueueRedraw();
        _field.QueueRedraw();
    }

    private void RebuildBoards()
    {
        foreach (var c in _boardAlly.GetChildren()) c.QueueFree();
        foreach (var c in _boardFoe.GetChildren()) c.QueueFree();

        AddBoardRow(_boardFoe, 1);
        AddBoardRow(_boardAlly, 0);
    }

    private void AddBoardRow(VBoxContainer host, int playerId)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        host.AddChild(row);

        foreach (var e in _ctrl.Board(playerId).Take(5))
        {
            var w = new BoardUnitWidget();
            bool canAtk = playerId == 0 && _ctrl.CanAttackWith(e);
            bool selected = _actionPanel.BoundEntity != null
                            && _actionPanel.BoundEntity.EntityId == e.EntityId;
            w.Setup(e, selected, canAtk);

            w.OnClicked += ent =>
            {
                _actionPanel.ShowForEntity(ent);
                if (_ctrl.IsInFuseMode) _ctrl.ToggleFuseMaterial(ent);
                Rebuild();
            };
            w.OnRightClicked += ent => _actionPanel.ShowForEntity(ent);

            w.OnDragAttackStarted += (ent, pos) =>
            {
                _attacker = ent;
                _field.IsDraggingAttack = true;
                _field.AttackDragStart = pos;
                _field.AttackDragCurrent = pos;
                _field.QueueRedraw();
            };
            w.OnDragAttackMoved += (ent, pos) =>
            {
                if (_attacker == null) return;
                _field.AttackDragCurrent = pos;
                _field.QueueRedraw();
            };
            w.OnDragAttackReleased += (ent, pos) => ReleaseAttack(pos);

            row.AddChild(w);
        }
    }

    private void ReleaseAttack(Vector2 screenPos)
    {
        _field.IsDraggingAttack = false;
        _field.QueueRedraw();

        if (_attacker == null) return;
        var atk = _attacker;
        _attacker = null;

        // 命中的目标：先问能否打主战者，再看各随从的矩形
        var legal = _ctrl.LegalAttackTargets(atk);

        var foeLeader = _ctrl.Leader(1);
        if (foeLeader != null && legal.Contains(foeLeader.EntityId)
            && _foeLeader.GetGlobalRect().HasPoint(screenPos))
        {
            _ctrl.TryAttack(atk, foeLeader, out _);
            Rebuild();
            return;
        }

        foreach (var w in _boardFoe.GetChildren().OfType<HBoxContainer>()
                     .SelectMany(h => h.GetChildren().OfType<BoardUnitWidget>()))
        {
            if (!w.GetGlobalRect().HasPoint(screenPos)) continue;
            if (legal.Contains(w.Entity.EntityId))
            {
                _ctrl.TryAttack(atk, w.Entity, out _);
                Rebuild();
            }
            return;
        }
    }

    private void RebuildHand()
    {
        foreach (var c in _handLayer.GetChildren()) c.QueueFree();

        var hand = _ctrl.Hand(0);
        var panel = new PanelContainer();
        _handLayer.AddChild(panel);

        // 缩小态要让开主战者拱框（拱框中心约 X=620）。
        // 放在 860 起才不压住立绘；展开态才允许铺到中右。
        Rect2 rect = _handExpanded
            ? new Rect2(430, 516, 830, 200)
            : new Rect2(862, 600, 402, 116);

        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        panel.Position = rect.Position;
        panel.Size = rect.Size;
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color(0.05f, 0.07f, 0.12f, 0.88f), SvWbTheme.AccentCyan, 10, 1, 8));
        panel.MouseFilter = MouseFilterEnum.Stop;

        panel.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _handExpanded = !_handExpanded;
                RebuildHand();
            }
        };

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        panel.AddChild(row);

        float cardW = _handExpanded ? 132f : 92f;
        float cardH = _handExpanded ? 184f : 116f;

        foreach (var card in hand.Take(9))
        {
            var w = new CardVisualWidget();
            w.Setup(card.Def, _store.CardPrinter, 0, 3, showCopyPips: false);
            w.CustomMinimumSize = new Vector2(cardW, cardH);
            w.Size = new Vector2(cardW, cardH);

            var bound = card;
            w.OnLeftClick += _ =>
            {
                _actionPanel.ShowForEntity(bound);
                _draggingCard = bound;
                _draggingPlay = true;
            };
            w.OnRightClick += _ => _actionPanel.ShowForEntity(bound);
            row.AddChild(w);
        }

        if (hand.Count == 0)
        {
            var empty = new Label { Text = "（手牌为空）" };
            empty.AddThemeFontSizeOverride("font_size", 12);
            empty.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            row.AddChild(empty);
        }
    }

    // ---------------------------------------------------------------- 输入

    public override void _Input(InputEvent @event)
    {
        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;

        if (!mb.Pressed && _draggingPlay && _draggingCard != null)
        {
            _draggingPlay = false;
            var card = _draggingCard;
            _draggingCard = null;
            _field.IsDraggingPlayCard = false;
            _field.QueueRedraw();

            // 越过手牌区上沿（Y < 500）才算「拖到战场」
            if (mb.GlobalPosition.Y < 500f)
            {
                _ctrl.TryPlayCard(card, out _);
                Rebuild();
            }
        }
    }

    private void OpenInfoModal()
    {
        var modal = new BattleInfoModal();
        AddChild(modal);
        modal.Setup(_ctrl);
    }

    // ---------------------------------------------------------------- 截图辅助

    public void SetHandExpanded(bool v) { _handExpanded = v; RebuildHand(); }
    public BattleSessionController Controller => _ctrl;
}
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
    private Button _menuBtn;
    private BattleVfxDirector _vfx;
    private int _lastEventIdx;
    private readonly Dictionary<long, Vector2> _unitCenters = new();

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
        _foeLeader.Position = new Vector2(475, 6);
        _foeLeader.OnLeaderClicked += () => _actionPanel.ShowForEntity(_ctrl.Leader(1));
        AddChild(_foeLeader);

        _allyLeader = new LeaderMedallionWidget();
        _allyLeader.Setup(_ctrl, 0, false);
        _allyLeader.Position = new Vector2(475, 558);
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

        _vfx = new BattleVfxDirector();
        AddChild(_vfx);

        _actionPanel = new TopLeftCardActionPanel();
        _actionPanel.Setup(_ctrl);
        AddChild(_actionPanel);

        // 书页按钮：矢量绘制的鎏金暗蓝方框 + 折角书页，不是文字按钮。
        _pageBtn = new Button { Text = "", Position = new Vector2(52, 312) };
        _pageBtn.CustomMinimumSize = new Vector2(52, 52);
        _pageBtn.Size = new Vector2(52, 52);
        SvWbTheme.StyleButton(_pageBtn);
        _pageBtn.Pressed += OpenInfoModal;
        _pageBtn.Draw += () => DrawPageIcon(_pageBtn);
        AddChild(_pageBtn);

        // 左上角菜单按钮 [≡]：点击返回卡组一览
        _menuBtn = new Button { Text = "", Position = new Vector2(52, 24) };
        _menuBtn.CustomMinimumSize = new Vector2(52, 52);
        _menuBtn.Size = new Vector2(52, 52);
        SvWbTheme.StyleButton(_menuBtn);
        _menuBtn.Pressed += () => OnExitToDecks?.Invoke();
        _menuBtn.Draw += () => DrawMenuIcon(_menuBtn);
        AddChild(_menuBtn);

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
        ConsumeNewEventsToVfx();
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

            // 记下实体的屏幕中心：事件到来时要往这里播特效。
            var cap = e;
            CallDeferred(nameof(CaptureCenter), cap.EntityId, w);
        }
    }

    private void CaptureCenter(long entityId, Control w)
    {
        if (w == null || !IsInstanceValid(w)) return;
        _unitCenters[entityId] = w.GlobalPosition + w.Size * 0.5f;
    }

    private Vector2 PosOf(EntityRef? r, int playerId)
    {
        if (r != null && _unitCenters.TryGetValue(r.Value.EntityId, out var p))
            return p;
        // 找不到就用主战者位置兜底（伤害多半打在主战者身上）
        var leader = _ctrl.Leader(playerId >= 0 ? playerId : 1);
        if (leader != null && _unitCenters.TryGetValue(leader.EntityId, out var lp))
            return lp;
        return new Vector2(640f, 300f);
    }

    /// <summary>
    /// 把 BattleState.EventLog 里新增的事件翻译成视觉演出。
    ///
    /// 这是「前后端分离」在这里的体现：内核只管往日志里写发生了什么，
    /// UI 按游标消费日志决定播什么动画 —— 内核完全不知道有特效这回事。
    /// </summary>
    private void ConsumeNewEventsToVfx()
    {
        if (_vfx == null || _ctrl?.State == null) return;
        var log = _ctrl.State.EventLog;

        for (int i = _lastEventIdx; i < log.Count; i++)
        {
            var ev = log[i];
            var pos = PosOf(ev.Subject, ev.PlayerId);

            switch (ev.Kind)
            {
                case CoreEvent.OnDamaged:
                    _vfx.PlayDamage(pos, ev.Amount);
                    break;
                case CoreEvent.OnHealed:
                    _vfx.PlayHeal(pos, ev.Amount);
                    break;
                case CoreEvent.OnDestroy:
                    _vfx.PlayDestroy(pos);
                    break;
                case CoreEvent.OnBanish:
                    _vfx.PlayBanish(pos);
                    break;
                case CoreEvent.OnEnterField:
                case CoreEvent.OnSummon:
                    _vfx.PlayEnterField(pos);
                    break;
                case CoreEvent.OnTransform:
                    _vfx.PlayTransform(pos);
                    break;
                case CoreEvent.OnEvolve:
                    _vfx.PlayEvolve(pos, false);
                    break;
                case CoreEvent.OnSuperEvolve:
                    _vfx.PlayEvolve(pos, true);
                    break;
            }
        }

        _lastEventIdx = log.Count;
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
        int n = hand.Count;

        float cardW = _handExpanded ? 138f : 88f;
        float cardH = _handExpanded ? 182f : 116f;
        float rightEdge = 1266f;

        // 缩小态不越过 750（让开主战者拱框 X=475..715）；展开态可到 420。
        float minLeft = _handExpanded ? 420f : 750f;
        float maxTrayW = rightEdge - minLeft;

        float idealStep = cardW + (_handExpanded ? 6f : 4f);
        float neededW = n <= 0 ? 180f : cardW + (n - 1) * idealStep + 20f;
        float trayW = Mathf.Clamp(neededW, 180f, maxTrayW);
        float trayLeft = rightEdge - trayW;
        float trayTop = _handExpanded ? 528f : 596f;
        float trayH = cardH + 14f;

        // 手牌区**不画外层矩形**。
        // 之前套了一个 Panel 当托盘底板，屏幕上就凭空多出一个暗色方框，
        // 把五张卡圈在里面像张表格 —— 原版是没有这个框的，
        // 手牌本身就是唯一的视觉元素。
        // 这里只用一个透明的 Control 承接点击（点它 = 展开/收起）。
        var tray = new Control
        {
            Position = new Vector2(trayLeft, trayTop),
            Size = new Vector2(trayW, trayH),
            MouseFilter = MouseFilterEnum.Stop,
        };
        tray.GuiInput += e =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                _handExpanded = !_handExpanded;
                RebuildHand();
            }
        };
        _handLayer.AddChild(tray);

        if (n == 0) return;

        // 牌多时自动叠牌：步进被压缩，绝不越过右边界。
        float stepX = n == 1 ? 0f : Mathf.Min(idealStep, (trayW - 20f - cardW) / (n - 1));
        float startX = trayLeft + 10f;

        for (int i = 0; i < n; i++)
        {
            var card = hand[i];
            var w = new CardVisualWidget();
            w.Setup(card.Def, _store.CardPrinter, 0, 3, showCopyPips: false);
            w.Position = new Vector2(startX + i * stepX, trayTop + 7f);
            // 必须走 SetCardSize：单写 Size 会被 CustomMinimumSize 撑回 192x254，
            // 手牌就会全部溢出右边界（截图里被切掉的那张就是这么来的）。
            w.SetCardSize(cardW, cardH);

            var bound = card;
            w.OnLeftClick += _ =>
            {
                if (!_handExpanded) { _handExpanded = true; RebuildHand(); return; }
                _actionPanel.ShowForEntity(bound);
                if (_ctrl.IsInFuseMode && !ReferenceEquals(_ctrl.FuseBaseCard, bound))
                {
                    _ctrl.ToggleFuseMaterial(bound);
                    _actionPanel.ShowForEntity(_ctrl.FuseBaseCard);
                    RebuildHand();
                    return;
                }
                _draggingCard = bound;
                _draggingPlay = true;
            };
            w.OnRightClick += _ => _actionPanel.ShowForEntity(bound);
            _handLayer.AddChild(w);
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

            // 判定逻辑在 BattleSessionController.ShouldPlayOnRelease 里，
            // 这里只负责取坐标 —— 那段逻辑在门禁里被测到。
            if (BattleSessionController.ShouldPlayOnRelease(mb.GlobalPosition.Y, true))
            {
                _ctrl.TryPlayCard(card, out _);
                Rebuild();
            }
        }
    }

    /// <summary>折角书页图标（矢量，零贴图）。</summary>
    private static void DrawPageIcon(Control host)
    {
        var rect = new Rect2(14f, 12f, 24f, 28f);
        // 鎏金暗蓝方框底
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color(0.06f, 0.09f, 0.16f, 0.9f));
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color("D4AF37"), false, 1.5f);

        // 书页主体（右上折角）
        Vector2[] page =
        {
            new(rect.Position.X, rect.Position.Y),
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + 8f),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + rect.Size.Y),
            new(rect.Position.X, rect.Position.Y + rect.Size.Y),
        };
        host.DrawColoredPolygon(page, new Color("F8FAFC"));

        // 折角三角
        Vector2[] fold =
        {
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y),
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y + 8f),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + 8f),
        };
        host.DrawColoredPolygon(fold, new Color("CBD5E1"));

        // 三条文字线
        for (int i = 0; i < 3; i++)
        {
            float y = rect.Position.Y + 12f + i * 6f;
            host.DrawLine(new Vector2(rect.Position.X + 5f, y),
                          new Vector2(rect.Position.X + rect.Size.X - 5f, y),
                          new Color(0.35f, 0.40f, 0.50f), 1.2f);
        }
    }

    /// <summary>三道横线菜单图标（矢量）。</summary>
    private static void DrawMenuIcon(Control host)
    {
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color(0.06f, 0.09f, 0.16f, 0.9f));
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color("D4AF37"), false, 1.5f);
        for (int i = 0; i < 3; i++)
        {
            float y = 19f + i * 7f;
            host.DrawLine(new Vector2(15f, y), new Vector2(37f, y), new Color("F8FAFC"), 2.4f);
        }
    }

    private void OpenInfoModal()
    {
        var modal = new BattleInfoModal();
        AddChild(modal);
        modal.Setup(_ctrl);
    }

    // ---------------------------------------------------------------- 截图辅助

    /// <summary>
    /// 截图辅助：在我方场上摆三张代表不同能力类型的卡，
    /// 用来目视确认「红书页 / 绿旗 / 空」三态真的画对了。
    /// </summary>
    public void SpawnBadgeDemo()
    {
        if (_ctrl == null) return;
        var pool = _ctrl.Store.GetPoolView("all-clans");
        var cards = pool.EnumerateCollectibleCards().ToList();

        // 按能力类型各挑一张
        var none = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.None);
        var lw = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.LastWords);
        var trg = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.Triggered);

        foreach (var def in new[] { lw, trg, none })
        {
            if (def == null) continue;
            var e = _ctrl.State.SpawnCard(def.CardId, ZoneRef.Board, 0);
            if (e != null) e.EnteredTurn = _ctrl.State.TurnNumber - 1;
        }
        Rebuild();
    }

    public void SetHandExpanded(bool v) { _handExpanded = v; RebuildHand(); }
    public BattleSessionController Controller => _ctrl;
}
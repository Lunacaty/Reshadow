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
    /// <summary>浮字/粒子等短命特效层：不随 Rebuild 销毁，自己驱动生命周期。</summary>
    private Control _overlayLayer;
    private VBoxContainer _boardAlly;
    private VBoxContainer _boardFoe;
    private Button _pageBtn;
    private Button _menuBtn;
    private BattleVfxDirector _vfx;
    private MulliganOverlay _mulligan;
    private PendingChoiceOverlay _pending;
    private MatchResultOverlay _result;
    private double _aiTimer;
    private int _lastEventIdx;
    private readonly Dictionary<long, Vector2> _unitCenters = new();

    private bool _handExpanded;
    private RuntimeEntity _attacker;
    private bool _draggingPlay;
    private RuntimeEntity _draggingCard;

    // ---- 拖拽跟随 ----
    /// <summary>跟随用的浮动卡面（独立于手牌层，不随 RebuildHand 销毁）。</summary>
    private CardVisualWidget _dragGhost;
    /// <summary>是否已越过手牌区阈值、开始跟随。</summary>
    private bool _dragFollow;
    /// <summary>跟随卡当前插值位置（缓动用）。</summary>
    private Vector2 _dragPos;
    /// <summary>鼠标当前位置。</summary>
    private Vector2 _dragTarget;

    // 保存双方牌组，供「再战一局」直接重开 ——
    // 之前 OnRematch 接到 OnExitToDecks，点「再战」会直接退回牌组一览。
    private ConstructedDeck _myDeck;
    private ConstructedDeck _foeDeck;

    public event Action OnExitToDecks;

    public void Boot(GameSessionStore store, ConstructedDeck mine, ConstructedDeck foe)
    {
        _store = store;
        _myDeck = mine;
        _foeDeck = foe;
        // startInMulligan: true —— 先停在换牌阶段，由 MulliganOverlay 接管。
        _ctrl = new BattleSessionController(store, mine, foe, seed: 20260101,
                                            startInMulligan: true);
        BoardUnitWidget.CurrentController = _ctrl;

        // 开局前先把游标推到日志末尾。
        // 否则起手抽牌、换牌这些「开局事件」会在第一次 Rebuild 时
        // 被当成刚发生的事故，满屏播一遍伤害与破坏特效。
        _lastEventIdx = _ctrl.State.EventLog.Count;

        BuildUi();
        Rebuild();
        RefreshOverlays();
    }

    /// <summary>
    /// 按当前状态挂载/卸载三块交互层。
    ///
    /// 顺序即 Z 序：换牌层(75) &lt; 挂起层(70) &lt; 结算层(85)，
    /// 所以结算永远盖在最上面，换牌阶段不会被挂起弹窗抢焦点。
    /// </summary>
    private void RefreshOverlays()
    {
        if (_ctrl == null) return;

        // ① 换牌
        //
        // 换牌层必须**显式卸掉**：RefreshOverlays 早先只负责「装」不负责「卸」，
        // 确认换牌后 _mulligan 仍挂在树上盖住整个战场 ——
        // 表现就是「换完牌画面不动，点哪里都没反应」。
        if (_ctrl.IsInMulligan)
        {
            if (_mulligan == null || !IsInstanceValid(_mulligan))
            {
                _mulligan = new MulliganOverlay();
                AddChild(_mulligan);
                _mulligan.Setup(_ctrl);
                _mulligan.OnMulliganConfirmed += () => { _mulligan = null; Rebuild(); };
            }
            return;
        }
        else if (_mulligan != null && IsInstanceValid(_mulligan))
        {
            _mulligan.QueueFree();
            _mulligan = null;
        }

        // ② 终局
        if (_ctrl.IsFinished)
        {
            if (_result == null || !IsInstanceValid(_result))
            {
                int winner = _ctrl.Winner;
                bool victory = winner == _ctrl.MyPlayerId;
                _result = new MatchResultOverlay();
                AddChild(_result);
                _result.Setup(victory, _ctrl.State.TurnNumber);
                // 再战：清掉旧的三层 overlay 后原地重开一局。
                _result.OnRematch += () => RestartMatch();
                _result.OnBackToDecks += () => OnExitToDecks?.Invoke();
            }
            return;
        }

        // ③ VM 挂起
        if (_pending == null || !IsInstanceValid(_pending))
        {
            _pending = new PendingChoiceOverlay();
            AddChild(_pending);
            _pending.Setup(_ctrl);
            _pending.OnChoiceSubmitted += Rebuild;
        }
        _pending.RefreshState();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _field = new BattlefieldCanvas { Controller = _ctrl };
        _field.OnCrestClicked += e => _actionPanel.ShowForEntity(e);
        AddChild(_field);

        _foeLeader = new LeaderMedallionWidget();
        _foeLeader.Setup(_ctrl, 1, true);
        // 主战者要「贴着窗口边、平直边被窗口切掉」。
        //
        // 关键：敌方是翻转的（拱顶朝下），翻转后**平直边落在控件内 y=DomeTopY(4)**，
        // 拱顶落在 y=FlatEdgeY(112)。所以只需把控件放到 Y = -DomeTopY，
        // 平直边就正好压在屏幕顶沿，拱顶留在屏幕内 108 处。
        //
        // 之前错写成 -FlatEdgeY(-112)，把整个框推出屏幕，只剩半个立绘。
        _foeLeader.Position = new Vector2(475, -LeaderMedallionWidget.DomeTopY);
        _foeLeader.OnLeaderClicked += () =>
        {
            var ldr = _ctrl.Leader(1);
            if (_pending != null && IsInstanceValid(_pending)
                && _pending.TryPickBoardOrHandEntity(ldr.EntityId)) return;
            _actionPanel.ShowForEntity(ldr);
        };
        AddChild(_foeLeader);

        _allyLeader = new LeaderMedallionWidget();
        _allyLeader.Setup(_ctrl, 0, false);
        // 我方不翻转：平直边在控件内 y=FlatEdgeY(112)，拱顶在 y=DomeTopY(4)。
        // 放到 Y = 720 - FlatEdgeY，平直边正好压在屏幕底沿。
        _allyLeader.Position = new Vector2(
            475, GetViewportRect().Size.Y - LeaderMedallionWidget.FlatEdgeY);
        _allyLeader.OnLeaderClicked += () =>
        {
            var ldr = _ctrl.Leader(0);
            if (_pending != null && IsInstanceValid(_pending)
                && _pending.TryPickBoardOrHandEntity(ldr.EntityId)) return;
            _actionPanel.ShowForEntity(ldr);
        };

        // EP / SEP 球可直接点击进化 —— 原版就是把进化球镶在主战者拱框上，
        // 玩家会自然去点它。之前事件定义了却没人订阅，点了毫无反应。
        // 和左上角弹窗的【进化】按钮等价：都作用于当前选中的实体。
        _allyLeader.OnEvolveOrbClicked += isSuper =>
        {
            var target = _actionPanel.BoundEntity;
            if (target == null) return;
            if (_ctrl.TryEvolve(target, isSuper)) Rebuild();
        };
        _foeLeader.OnEvolveOrbClicked += isSuper =>
        {
            var target = _actionPanel.BoundEntity;
            if (target == null) return;
            if (_ctrl.TryEvolve(target, isSuper)) Rebuild();
        };
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

        // 浮字层：必须盖在手牌之上，且**不参与 Rebuild 清理** ——
        // 伤害数字要在玩家后续操作时继续飘完。
        _overlayLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlayLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_overlayLayer);

        _vfx = new BattleVfxDirector();
        AddChild(_vfx);

        _actionPanel = new TopLeftCardActionPanel();
        _actionPanel.Setup(_ctrl);

        // 左上角面板里点了进化 / 启动 / 融合后，战场必须整体重画。
        //
        // 这个事件原先**从未被订阅**：进化成功了、引擎那边数值也变了，
        // 但界面不动。玩家看到的是「叮当天使进化后卡住了」——
        // 实际上只要再做一个带 Rebuild() 的操作（出牌、攻击），
        // 画面立刻正常，进化时抽的牌也才显示出来。
        _actionPanel.OnEntityChanged += _ => Rebuild();

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
            _dragTarget = mp;
            _field.IsDraggingPlayCard = mp.Y < 500f;
            _field.QueueRedraw();
        }

        // 跟随插值必须每帧推进：只靠 InputEvent 会在鼠标静止时不更新，
        // 卡就停在半路追不上。
        StepDragFollow((float)delta);

        StepOpponentIfNeeded(delta);
        UpdateAimLine();
    }

    /// <summary>
    /// 挂了 SelectTarget 时，让战场画一条「底部中央 → 鼠标」的虚线瞄准线。
    /// 只在「候选都在场上/手牌」的那种形态下画 —— 弹窗形态本来就有自己的界面，
    /// 再叠一条线只是噪音。
    /// </summary>
    private void UpdateAimLine()
    {
        if (_field == null || _ctrl == null) return;

        var p = _ctrl.Runner?.PendingChoice;
        bool aiming = p != null
                      && p.Kind == ChoiceKind.SelectTarget
                      && p.PlayerId == _ctrl.MyPlayerId
                      && _pending != null && IsInstanceValid(_pending);

        _field.IsAiming = aiming;
        if (aiming)
        {
            _field.AimTarget = GetGlobalMousePosition();
            _field.QueueRedraw();
        }
    }

    /// <summary>
    /// 对手回合的「慢放」推进：每 0.35s 走一步。
    ///
    /// 不这么做的后果是 AI 一帧之内把整个回合打完 —— 玩家只看到
    /// 场面瞬间变样，完全看不出对手出了什么牌、打了谁。
    /// </summary>
    private void StepOpponentIfNeeded(double delta)
    {
        if (_ctrl == null || _ctrl.IsFinished) return;
        if (_ctrl.IsInMulligan) return;
        if (_ctrl.IsMyTurn || _ctrl.IsWaitingForInput) { _aiTimer = 0; return; }

        _aiTimer += delta;
        if (_aiTimer < 0.35) return;
        _aiTimer = 0;

        if (_ctrl.StepOpponentTurn()) Rebuild();
        else Rebuild();
    }

    // ---------------------------------------------------------------- 重建

    public void Rebuild()
    {
        RebuildBoards();
        RebuildHand();
        ConsumeNewEventsToVfx();
        ApplyChoiceHighlightToLeaders();

        _allyLeader.QueueRedraw();
        _foeLeader.QueueRedraw();
        _rightCol.QueueRedraw();
        _field.QueueRedraw();

        RefreshOverlays();
    }

    /// <summary>把挂起选择的候选态同步到双方主战者框。</summary>
    private void ApplyChoiceHighlightToLeaders()
    {
        var p = _ctrl?.Runner?.PendingChoice;
        bool choosing = p != null
                        && p.Kind == ChoiceKind.SelectTarget
                        && p.PlayerId == _ctrl.MyPlayerId;

        ApplyLeader(_allyLeader, _ctrl.Leader(0), choosing, p);
        ApplyLeader(_foeLeader, _ctrl.Leader(1), choosing, p);

        static void ApplyLeader(LeaderMedallionWidget w, RuntimeEntity e,
                                bool choosing, PendingChoice p)
        {
            if (w == null || e == null) return;
            if (!choosing)
            {
                w.SetChoiceState(false, false);
                return;
            }
            bool isCand = p.Candidates.Contains(e.EntityId);
            w.SetChoiceState(isCand, !isCand);
        }
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
        // 用 CenterContainer 包一层：随从不足 5 个时整排居中，
        // 而不是从左往右贴着左边排 —— 原版战场就是这样，
        // 随从越少越靠中间。
        var center = new CenterContainer();
        center.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        host.AddChild(center);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 14);
        center.AddChild(row);

        foreach (var e in _ctrl.Board(playerId).Take(5))
        {
            var w = new BoardUnitWidget();
            bool canAtk = playerId == 0 && _ctrl.CanAttackWith(e);
            bool selected = _actionPanel.BoundEntity != null
                            && _actionPanel.BoundEntity.EntityId == e.EntityId;
            w.Setup(e, selected, canAtk);

            // 挂起选择时：候选棋子亮金圈，其余压暗。
            // 没有这一层，玩家只能靠「点了有没有反应」猜哪些能点。
            var pend = _ctrl.Runner.PendingChoice;
            if (pend != null && pend.Kind == ChoiceKind.SelectTarget
                && pend.PlayerId == _ctrl.MyPlayerId)
            {
                bool isCand = pend.Candidates.Contains(e.EntityId);
                w.SetChoiceState(isCand, !isCand);
            }

            w.OnClicked += ent =>
            {
                // 若正在等玩家点选目标，这一次点击就直接当选择提交，
                // 不要再弹详情窗把交互带偏。
                if (_pending != null && IsInstanceValid(_pending)
                    && _pending.TryPickBoardOrHandEntity(ent.EntityId))
                    return;

                // 只弹左上详情，**绝不在这里 Rebuild()**。
                // OnClicked 是鼠标「按下」瞬间触发的，此刻这个 widget 正准备
                // 进入拖拽攻击（OnDragAttackStarted 紧随其后）。
                // 一旦 Rebuild() 就会把它 QueueFree 掉，拖拽直接断在半路，
                // 玩家永远拉不出瞄准箭头。
                _actionPanel.ShowForEntity(ent);
                if (_ctrl.IsInFuseMode) _ctrl.ToggleFuseMaterial(ent);
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

    /// <summary>
    /// 攻击命中瞬间播一次冲撞动画。
    ///
    /// 必须**在 Rebuild() 之前**调：攻击者控件马上会被 QueueFree，
    /// 之后再拿到它就是一个失效引用，Tween 会打在一个已释放的节点上。
    /// </summary>
    private void PlayLungeBeforeRebuild(RuntimeEntity attacker, Vector2 defenderCenter)
    {
        if (_vfx == null || attacker == null) return;

        var attackerWidget = _boardAlly.GetChildren().OfType<HBoxContainer>()
            .SelectMany(h => h.GetChildren().OfType<BoardUnitWidget>())
            .FirstOrDefault(x => x.Entity != null && x.Entity.EntityId == attacker.EntityId);

        Vector2 attackerCenter = attackerWidget != null
            ? attackerWidget.GetGlobalRect().GetCenter()
            : _unitCenters.TryGetValue(attacker.EntityId, out var c) ? c : defenderCenter;

        _vfx.PlayAttackLunge(attackerWidget, attackerCenter, defenderCenter);
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
                    SpawnDamageFloat(pos, ev.Amount, false);
                    break;
                case CoreEvent.OnHealed:
                    _vfx.PlayHeal(pos, ev.Amount);
                    SpawnDamageFloat(pos, ev.Amount, true);
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

    /// <summary>
    /// 弹一个伤害浮字。
    ///
    /// 挂在 _handLayer 之外的独立层，并且自己驱动生命周期 ——
    /// 如果挂在会随 Rebuild 销毁的容器里，玩家一操作数字就被抹掉了。
    /// 主战者与随从都走这里。
    /// </summary>
    private void SpawnDamageFloat(Vector2 center, int amount, bool heal)
    {
        if (amount == 0) return;

        var f = new DamageFloat();
        _overlayLayer.AddChild(f);
        // 从目标头顶偏上一点浮出
        f.Setup(amount, center + new Vector2(-48f, -34f), heal);
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
            PlayLungeBeforeRebuild(atk, _foeLeader.GetGlobalRect().GetCenter());
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
                PlayLungeBeforeRebuild(atk, w.GetGlobalRect().GetCenter());
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
                // 手牌也可能是 SelectTarget 的候选（「选择 1 张手牌舍弃/融合」这类效果）。
                // 少了这一句，玩家点手牌不会提交选择，挂起就永远卡在那里。
                if (_pending != null && IsInstanceValid(_pending)
                    && _pending.TryPickBoardOrHandEntity(bound.EntityId))
                    return;

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
        // 鼠标移动：更新跟随时目标点（真正的插值在 _Process 里做，
        // 这样速度与帧率无关，也不会因为一次大幅移动而瞬移）
        if (@event is InputEventMouseMotion mm && _draggingPlay && _draggingCard != null)
        {
            _dragTarget = mm.Position;

            // 起跟条件：越过手牌区上沿**再往外一点**才算。
            // 不给阈值的话，按下瞬间手就在原地，卡会立刻弹起 ——
            // 玩家只是想点一下看效果，却被当成要出牌。
            const float PLAY_THRESHOLD = 40f;
            if (!_dragFollow && mm.Position.Y < BattleSessionController.HandZoneTopY - PLAY_THRESHOLD)
                BeginDragFollow();
            return;
        }

        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;

        if (!mb.Pressed && _draggingPlay && _draggingCard != null)
        {
            _draggingPlay = false;
            var card = _draggingCard;
            _draggingCard = null;
            _field.IsDraggingPlayCard = false;
            _field.QueueRedraw();

            // 松手判定必须同时满足：跟已启动（越过了阈值）
            // 且松手时仍在手牌区外。两者缺一不可 ——
            // 只判松手位置的话，鼠标移动回手牌区再松开也会打出。
            bool shouldPlay = _dragFollow
                && BattleSessionController.ShouldPlayOnRelease(mb.GlobalPosition.Y, true);
            EndDragFollow();

            if (shouldPlay)
            {
                _ctrl.TryPlayCard(card, out _);
                Rebuild();
            }
            else
            {
                RebuildHand();
            }
        }
    }

    /// <summary>开始跟随：造一张浮动卡面，从原位快速「提起来」。</summary>
    private void BeginDragFollow()
    {
        if (_draggingCard == null) return;
        _dragFollow = true;

        _dragGhost = new CardVisualWidget();
        // Setup 收的是 CardDef，不是运行时实体。
        _dragGhost.Setup(_draggingCard.Def, _store.CardPrinter, 0, 3, false);
        _dragGhost.SetCardSize(150f, 198f);
        _dragGhost.MouseFilter = MouseFilterEnum.Ignore;
        _dragGhost.ZIndex = 55;
        _dragGhost.Modulate = new Color(1, 1, 1, 0.94f);
        _overlayLayer.AddChild(_dragGhost);

        _dragPos = _dragTarget;
        _dragGhost.Position = _dragPos - new Vector2(75f, 99f);
    }

    /// <summary>结束跟随：销毁浮动卡面。</summary>
    private void EndDragFollow()
    {
        _dragFollow = false;
        if (_dragGhost != null && IsInstanceValid(_dragGhost))
        {
            _dragGhost.QueueFree();
        }
        _dragGhost = null;
    }

    /// <summary>
    /// 拖拽跟随的插值。
    ///
    /// 目标点 = 鼠标位置，当前位置按 EaseOutCubic 每帧逼近。
    /// 不做 1:1 硬跟随：那看起来像卡粘在光标上，没有「被拖动」的重量感。
    /// 速度设为 18/s：约 0.15 秒追平，明显慢于鼠标但不会拖沓。
    /// </summary>
    private void StepDragFollow(float dt)
    {
        if (!_dragFollow || _dragGhost == null || !IsInstanceValid(_dragGhost)) return;

        float k = 1f - Mathf.Pow(0.001f, dt * 1.9f);
        _dragPos = _dragPos.Lerp(_dragTarget, Mathf.Clamp(k, 0f, 1f));
        _dragGhost.Position = _dragPos - new Vector2(75f, 99f);

        // 离开手牌区时微微放大 + 提高不透明度，给出「准备打出」的暗示
        float inHand = Mathf.Clamp((BattleSessionController.HandZoneTopY - _dragPos.Y) / 120f, 0f, 1f);
        _dragGhost.Scale = Vector2.One * (0.96f + 0.10f * inHand);
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

    /// <summary>
    /// 再战一局：拆掉旧的 overlay 与控制器状态，用同一副牌组重新 Boot。
    ///
    /// 不能只把 _result 隐藏 —— 旧 _ctrl 已处于 Finished，
    /// 场面、手牌、AI 定时器全是终局态，必须整体重建。
    /// </summary>
    private void RestartMatch()
    {
        foreach (var ov in new Control[] { _mulligan, _pending, _result })
        {
            if (ov != null && IsInstanceValid(ov)) ov.QueueFree();
        }
        _mulligan = null;
        _pending = null;
        _result = null;
        _aiTimer = 0;
        _lastEventIdx = 0;
        _unitCenters.Clear();

        Boot(_store, _myDeck, _foeDeck);
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

    /// <summary>开发期入口：往双方战场各放几个随从（截图用）。</summary>
    public void SpawnBoardDemo()
    {
        if (_ctrl?.State == null) return;
        var ids = new[] { 10001120, 10001110, 10001130, 10002120 };
        for (int i = 0; i < ids.Length; i++)
        {
            _ctrl.State.SpawnCard(ids[i], ZoneRef.Board, _ctrl.MyPlayerId);
            _ctrl.State.SpawnCard(ids[i], ZoneRef.Board, _ctrl.MyPlayerId == 0 ? 1 : 0);
        }
        Rebuild();
    }

    public void SetHandExpanded(bool v) { _handExpanded = v; RebuildHand(); }
    public BattleSessionController Controller => _ctrl;
}
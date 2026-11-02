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

    /// <summary>战场动画调度器（全局一份，按 EntityId 存状态）。</summary>
    private readonly BoardAnimDirector _anims = new();
    private bool _animBusy;

    /// <summary>
    /// 与真实状态有关的演出队列（先进先出）。
    ///
    /// 玩家在队列播放期间仍可自由操作 —— 拖手牌、选攻击目标、看卡面，
    /// 这些都是纯表现，不改变对局状态。
    /// 但攻击结算、掉血、抽牌、进化、终局这些**会改变状态**的演出
    /// 必须一段播完再播下一段，否则多个结果会糊在一起。
    /// </summary>
    private readonly AnimQueue _seq = new();

    /// <summary>
    /// 本次攻击是否由超进化随从发起。
    ///
    /// OnDestroy 事件本身不带「谁打死的」，而 UI 必须在「被超进化随从攻击破坏」
    /// 时改播击飞动画。所以在 ReleaseAttack 里先记下来，
    /// 事件消费完就清掉 —— 只对紧接着的那一批破坏生效。
    /// </summary>
    private bool _lastHitBySuperEvolved;
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
        BoardUnitWidget.Director = _anims;

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
        //
        // 结算窗必须排在所有表演之后 —— 否则致命一击的动画还没播完，
        // 胜负结果就盖上来，玩家根本看不清最后一击发生了什么。
        if (_ctrl.IsFinished && _seq.Busy)
        {
            return;
        }

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

        // 窗口尺寸变化时重排。
        // project.godot 设的是 stretch/aspect="expand"，视口会随窗口变大，
        // 而这里的坐标全是按 1280x720 设计的 —— 不做自适应，
        // 全屏时我方主战者（y=608）会被推到可视区外，直接看不见。
        Resized += LayoutBattlefield;

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
        // 位置由 LayoutBattlefield 统一计算
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
        _rightCol.OnEndTurnClicked += () => { _ctrl.EndTurn(); Rebuild(); };
        _rightCol.OnExtraPpClicked += () => { _ctrl.TryUseExtraPp(); Rebuild(); };
        AddChild(_rightCol);

        // 棋盘容器（上敌下我，各 5 格）
        _boardFoe = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_boardFoe);

        _boardAlly = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_boardAlly);

        _handLayer = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_handLayer);

        // 浮字层：必须盖在手牌之上，且**不参与 Rebuild 清理** ——
        // 伤害数字要在玩家后续操作时继续飘完。
        _overlayLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlayLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_overlayLayer);

        // 全部控件建完之后再排一次 —— 早先在创建 _foeLeader 之后立刻调，
        // 那时 _allyLeader / _boardAlly 都还不存在，等于什么都没排。
        LayoutBattlefield();
        CallDeferred(nameof(LayoutBattlefield));

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
        _actionPanel.OnEntityChanged += _ =>
        {
            Rebuild();
            // 进化/启动/融合都会改状态：把动画排进队列，播完前锁操作。
            if (_ctrl?.State != null) ConsumeNewEventsToVfx();
        };

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
        _anims.Step((float)delta);

        // 只在真的有动画时写位置 —— 每帧无条件写会和容器布局互相覆盖。
        if (_animBusy)
        {
            ApplyMotionsToWidgets();
            _animBusy = false;
            foreach (var w in BoardUnitsOf(_boardAlly))
                if (w.Entity != null && _anims.IsAnimating(w.Entity.EntityId)) { _animBusy = true; break; }
            if (!_animBusy)
                foreach (var w in BoardUnitsOf(_boardFoe))
                    if (w.Entity != null && _anims.IsAnimating(w.Entity.EntityId)) { _animBusy = true; break; }
        }

        // 表演队列：先进先出，一段播完接下一段
        if (_seq.Step((float)delta))
        {
            // 队列在动时战场每帧都要重画（动画在 _Draw 里应用）
            _boardAlly?.QueueRedraw();
            _boardFoe?.QueueRedraw();
        }

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
        // 整排居中用 HBoxContainer 自己的 Alignment。
        //
        // 之前套了一层 CenterContainer + ExpandFill，实测无效：
        // 棋子位置打印出来是 0 / 110 / 220 / 330，从容器左边开始排，
        // 完全没有居中。CenterContainer 在这种嵌套下不会按内容收窄。
        // HBoxContainer.Alignment = Center 是控件自带的居中，
        // 不需要父级给宽度参照，直接把整排推到中间。
        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 14);
        host.AddChild(row);

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
                // 选目标阶段：棋子稍微上浮并持续上下浮动，表示「已被拿起」。
                // 这一步**不改变任何状态**，所以不进表演队列。
                _anims.SetAiming(ent.EntityId);
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

        var attackerWidget = BoardUnitsOf(_boardAlly)
            .FirstOrDefault(x => x.Entity != null && x.Entity.EntityId == attacker.EntityId);

        Vector2 attackerCenter = attackerWidget != null
            ? attackerWidget.GetGlobalRect().GetCenter()
            : _unitCenters.TryGetValue(attacker.EntityId, out var c) ? c : defenderCenter;

        _vfx.PlayAttackLunge(attackerWidget, attackerCenter, defenderCenter);

        // 棋子本体也真的撞过去再弹回（不只是特效层的一个闪光）。
        // 用 Director 而不是直接给控件做 Tween：攻击结算会立刻 Rebuild，
        // 控件会被销毁，Tween 打在空中；Director 的偏移按 EntityId 存，
        // 重建后的新控件会自然接上。
        _anims.PlayLunge(attacker.EntityId, defenderCenter - attackerCenter);
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
            EnqueueEvent(log[i]);

        _lastEventIdx = log.Count;
        // 只对刚刚这批破坏生效，之后的事件不该再触发击飞。
        _lastHitBySuperEvolved = false;
    }

    /// <summary>
    /// 把一个事件排进表演队列。
    ///
    /// 每个事件 = 一段 Seq，播完才播下一段。这是「先进先出」的落点：
    /// 攻击结算、掉血、抽牌、进化、终局都必须按发生顺序逐个演出，
    /// 同时涌上来会糊成一团，玩家看不出因果。
    ///
    /// 每段开头先 RebuildBoard —— 让**控件位置**立刻对上新的真实状态，
    /// 再用动画解释「是怎么变成这样的」。顺序反了会看到残影。
    /// </summary>
    private void EnqueueEvent(VmEvent ev)
    {
        var pos = PosOf(ev.Subject, ev.PlayerId);
        var center = pos + new Vector2(BoardUnitWidget.UnitW * 0.5f, BoardUnitWidget.UnitH * 0.5f);
        long sid = ev.Subject?.EntityId ?? -1;

        switch (ev.Kind)
        {
            case CoreEvent.OnDamaged:
            {
                var seq = new Seq()
                    .Do(() => { _vfx.PlayDamage(pos, ev.Amount); SpawnDamageFloat(pos, ev.Amount, false); })
                    .Then(new Anim(0.16f, Ease.Linear, v =>
                    {
                        if (sid >= 0) _anims.PlayHitShake(sid);
                    }))
                    .Wait(0.06f);
                _seq.Enqueue(seq);
                break;
            }

            case CoreEvent.OnHealed:
                _seq.Enqueue(new Seq()
                    .Do(() => { _vfx.PlayHeal(pos, ev.Amount); SpawnDamageFloat(pos, ev.Amount, true); })
                    .Wait(0.12f));
                break;

            case CoreEvent.OnDestroy:
            {
                bool bySuper = _lastHitBySuperEvolved;
                int victimPlayer = ev.PlayerId >= 0 ? ev.PlayerId : 1;
                var ownLeader = _ctrl.Leader(victimPlayer);

                if (bySuper && sid >= 0)
                {
                    Vector2 toward = new(0, victimPlayer == 0 ? 210f : -210f);
                    if (ownLeader != null && _unitCenters.TryGetValue(ownLeader.EntityId, out var lp))
                        toward = lp - center;
                    var hitAt = center + toward;

                    _seq.Enqueue(new Seq()
                        .Do(() => { _vfx.PlayDestroy(pos); _anims.PlayKnockToLeader(sid, toward); })
                        .Then(new Anim(0.40f, Ease.Linear, _ => { }))
                        .Do(() => SpawnBurst(hitAt, 18, 130f, new Color("C084FC"), 0.6f))
                        .Then(new Anim(0.28f, Ease.InCubic, _ => { })));
                }
                else
                {
                    _seq.Enqueue(new Seq()
                        .Do(() => { _vfx.PlayDestroy(pos); SpawnBurst(center, 22, 150f, new Color("93C5FD"), 0.75f); })
                        .Then(new Anim(0.30f, Ease.Linear, _ => { if (sid >= 0) _anims.PlayLeave(sid); })));
                }
                break;
            }

            case CoreEvent.OnBanish:
                _seq.Enqueue(new Seq()
                    .Do(() => { _vfx.PlayBanish(pos); SpawnBurst(center, 18, 140f, new Color("A78BFA"), 0.6f); })
                    .Then(new Anim(0.26f, Ease.Linear, _ => { if (sid >= 0) _anims.PlayLeave(sid); })));
                break;

            // 只认 OnEnterField。
            // 召唤一张卡时内核会**同时**发 OnEnterField 与 OnSummon
            // （后者是给「召唤时」能力用的语义事件），
            // 两个都接就会把入场动画播两遍。
            case CoreEvent.OnEnterField:
                _seq.Enqueue(new Seq()
                    .Do(() =>
                    {
                        _vfx.PlayEnterField(pos);
                        SpawnBurst(pos + new Vector2(BoardUnitWidget.UnitW * 0.5f, BoardUnitWidget.UnitH),
                                   10, 90f, new Color("A5B4FC"), 0.45f, -40f, 2.4f);
                        if (sid >= 0) _anims.PlayEnter(sid, BoardUnitWidget.UnitH);
                    })
                    .Then(new Anim(0.34f, Ease.Linear, _ => { })));
                break;

            case CoreEvent.OnDraw:
                // 抽牌：卡从屏幕右侧飞进手牌区。
                _seq.Enqueue(new Seq()
                    .Do(() => PlayDrawFlyIn())
                    .Then(new Anim(0.26f, Ease.Linear, _ => { })));
                break;

            case CoreEvent.TurnStart:
                // 回合开始也要排队：播完才能操作。
                _seq.Enqueue(new Seq()
                    .Do(() => _rightCol?.QueueRedraw())
                    .Wait(0.24f));
                break;

            case CoreEvent.TurnEnd:
                _seq.Enqueue(new Seq()
                    .Do(() => _rightCol?.QueueRedraw())
                    .Wait(0.18f));
                break;

            case CoreEvent.OnTransform:
                _seq.Enqueue(new Seq()
                    .Do(() => _vfx.PlayTransform(pos))
                    .Wait(0.16f));
                break;

            case CoreEvent.OnEvolve:
                _seq.Enqueue(new Seq()
                    .Do(() => { _vfx.PlayEvolve(pos, false); if (sid >= 0) _anims.PlayEvolve(sid, false); })
                    .Then(new Anim(0.62f, Ease.Linear, _ => { })));
                break;

            case CoreEvent.OnSuperEvolve:
                _seq.Enqueue(new Seq()
                    .Do(() => { _vfx.PlayEvolve(pos, true); if (sid >= 0) _anims.PlayEvolve(sid, true); })
                    .Then(new Anim(0.62f, Ease.Linear, _ => { })));
                break;
        }
    }

    /// <summary>抽牌动画：一张卡背从右缘飞向手牌区。</summary>
    private void PlayDrawFlyIn()
    {
        if (_overlayLayer == null) return;

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color("1A2438"), SvWbTheme.AccentCyan, 8, 2, 10));
        card.Size = new Vector2(64, 88);
        card.Position = new Vector2(1300f, 596f);
        card.MouseFilter = MouseFilterEnum.Ignore;
        card.ZIndex = 57;
        _overlayLayer.AddChild(card);

        var from = card.Position;
        var to = new Vector2(1120f, 610f);

        _seq.Enqueue(new Seq()
            .Then(new Anim(0.26f, Ease.OutCubic, v =>
            {
                if (!IsInstanceValid(card)) return;
                card.Position = from.Lerp(to, v);
                card.Modulate = new Color(1, 1, 1, 1f - Mathf.Max(0f, (v - 0.7f) / 0.3f));
            }, () => { if (IsInstanceValid(card)) card.QueueFree(); }))
            .Do(RebuildHand));
    }

    /// <summary>
    /// 弹一个伤害浮字。
    ///
    /// 挂在 _handLayer 之外的独立层，并且自己驱动生命周期 ——
    /// 如果挂在会随 Rebuild 销毁的容器里，玩家一操作数字就被抹掉了。
    /// 主战者与随从都走这里。
    /// </summary>
    /// <summary>
    /// 取出某个战场容器里的全部棋子。
    ///
    /// 容器结构是 _board → CenterContainer → HBoxContainer → 棋子，
    /// 以后若再加一层包装也不会因此取空 —— 这里做深度遍历而不是写死层数。
    /// </summary>
    private static IEnumerable<BoardUnitWidget> BoardUnitsOf(Node container)
    {
        if (container == null) yield break;

        foreach (var child in container.GetChildren())
        {
            if (child is BoardUnitWidget w) { yield return w; continue; }
            foreach (var sub in BoardUnitsOf(child)) yield return sub;
        }
    }

    /// <summary>
    /// 把调度器里的运动状态写到每个棋子控件的真实变换上。
    ///
    /// 这是 UI 层的唯一位置真相：控件的 Position 就是它看起来的位置，
    /// 因此命中判定（GetGlobalRect）与视觉永远一致 ——
    /// 之前用 _Draw 里的 DrawSetTransform 偏移，导致「看得见但点不中」。
    /// </summary>
    private void ApplyMotionsToWidgets()
    {
        ApplyMotionsIn(_boardAlly);
        ApplyMotionsIn(_boardFoe);
    }

    private void ApplyMotionsIn(Node container)
    {
        if (container == null || _anims == null) return;

        foreach (var w in BoardUnitsOf(container))
        {
            if (w.Entity == null) continue;
            w.ApplyMotion(_anims.MotionOf(w.Entity.EntityId));
        }
    }

    /// <summary>在指定位置放一簇光点。坐标是全局坐标，这里转成本层局部坐标。</summary>
    private void SpawnBurst(Vector2 globalPos, int count, float spread, Color tint,
                            float life = 0.7f, float gravity = 60f, float size = 3.2f)
    {
        if (_overlayLayer == null) return;
        var b = new ParticleBurst();
        _overlayLayer.AddChild(b);
        // _overlayLayer 是铺满全屏的 Control，原点与视口一致，
        // 所以全局坐标可以直接当局部坐标用。
        b.Setup(globalPos, count, spread, tint, life, gravity, size);
    }

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
        _anims.ClearAiming();

        if (_attacker == null) return;

        // 队列忙 / 有挂起时不允许真正攻击：取消这次拖拽，不消耗随从。
        if (InputLocked)
        {
            _attacker = null;
            RebuildBoards();
            return;
        }

        var atk = _attacker;
        _attacker = null;

        // 命中的目标：先问能否打主战者，再看各随从的矩形
        var legal = _ctrl.LegalAttackTargets(atk);

        var foeLeader = _ctrl.Leader(1);
        if (foeLeader != null && legal.Contains(foeLeader.EntityId)
            && _foeLeader.GetGlobalRect().HasPoint(screenPos))
        {
            PlayLungeBeforeRebuild(atk, _foeLeader.GetGlobalRect().GetCenter());
            _lastHitBySuperEvolved = atk.IsSuperEvolved;
            _ctrl.TryAttack(atk, foeLeader, out _);
            Rebuild();
            return;
        }

        // 棋子现在藏在 CenterContainer → HBoxContainer 里（为了让不足 5 个时居中）。
        // 之前这里直接 OfType<HBoxContainer>()，中间多一层就取不到任何棋子 ——
        // 循环一次都不执行，表现为「我方随从打不了敌方随从，但能打主战者」。
        foreach (var w in BoardUnitsOf(_boardFoe))
        {
            if (w.Entity == null) continue;
            if (!w.GetGlobalRect().HasPoint(screenPos)) continue;
            if (!legal.Contains(w.Entity.EntityId)) continue;

            PlayLungeBeforeRebuild(atk, w.GetGlobalRect().GetCenter());
            _lastHitBySuperEvolved = atk.IsSuperEvolved;
            _ctrl.TryAttack(atk, w.Entity, out _);
            Rebuild();
            return;
        }
    }

    private void RebuildHand()
    {
        foreach (var c in _handLayer.GetChildren()) c.QueueFree();

        var hand = _ctrl.Hand(0);
        int n = hand.Count;

        // 按实际视口算，不写死 1280x720 的像素值 ——
        // 否则全屏时手牌会停在屏幕中间而不是底部。
        var vp = Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = GetViewportRect().Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = new Vector2(DesignW, DesignH);

        float sx = vp.X / DesignW;

        float cardW = (_handExpanded ? 138f : 88f) * sx;
        float cardH = (_handExpanded ? 182f : 116f) * sx;
        float rightEdge = vp.X - 14f * sx;

        // 缩小态不越过主战者拱框右沿；展开态可以更靠左。
        float minLeft = (_handExpanded ? 420f : 750f) * sx;
        float maxTrayW = rightEdge - minLeft;

        float idealStep = cardW + (_handExpanded ? 6f : 4f);
        float neededW = n <= 0 ? 180f : cardW + (n - 1) * idealStep + 20f;
        float trayW = Mathf.Clamp(neededW, 180f, maxTrayW);
        float trayLeft = rightEdge - trayW;
        // 手牌贴着窗口下沿：底边固定，展开时向上长。
        float bottomY = vp.Y - HandTrayBottomGap;
        float trayTop = _handExpanded ? bottomY - (cardH + 14f) - 56f : bottomY - (cardH + 14f);
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

    /// <summary>
    /// 是否应当拦住玩家的「会改变状态」的操作。
    ///
    /// 两条：
    ///   ① 表演队列还在播 —— 先看清上一个结果再操作；
    ///   ② 有 choose 挂起 —— 必须先把这张卡的选项选完。
    ///
    /// 注意：**选攻击目标**不受此限（那是纯表现，不改状态）。
    /// </summary>
    private bool InputLocked => _seq.Busy || _ctrl?.IsWaitingForInput == true;

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
            // 队列忙时不允许真正打出：先放下，等演出播完再操作。
            if (InputLocked)
            {
                _draggingPlay = false;
                _draggingCard = null;
                _field.IsDraggingPlayCard = false;
                _field.QueueRedraw();
                EndDragFollow();
                RebuildHand();
                return;
            }

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
                // 打出：卡面先化作粒子消散，再真正把牌打出去。
                // 粒子起点用跟随卡的当前位置，视觉上「同一张卡」连得上。
                SpawnBurst(_dragPos, 26, 190f, new Color("FDE68A"), 0.62f, -20f, 3.0f);

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

    /// <summary>设计基准分辨率。战场椭圆的坐标就是在它下面标定的。</summary>
    private const float DesignW = 1280f;
    private const float DesignH = 720f;

    /// <summary>手牌区底部与窗口下沿之间的留白。</summary>
    private const float HandTrayBottomGap = 8f;

    /// <summary>右侧指令列距窗口右沿的距离。</summary>
    private const float RightColumnRightMargin = 245f;

    /// <summary>
    /// 按实际视口重排全部战场元素。
    ///
    /// 唯一的对齐基准是**中央竞技场椭圆**（BattlefieldCanvas.ArenaCenter）。
    /// 早先这里用屏幕中心 vp.X/2，而竞技场中心是 595（设计基准下），
    /// 两者差 45px —— 整排随从偏右、和椭圆对不上。
    ///
    /// 缩放策略：只按宽度等比。
    /// 竞技场是椭圆，纵横比固定；按高度缩放会让椭圆变形，
    /// 而椭圆本身在 _Draw 里是写死尺寸的。
    /// </summary>
    private void LayoutBattlefield()
    {
        var vp = Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = GetViewportRect().Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = new Vector2(DesignW, DesignH);

        float sx = vp.X / DesignW;

        // 竞技场中心在屏幕上的实际位置
        Vector2 arena = new(
            BattlefieldCanvas.ArenaCenter.X * sx,
            BattlefieldCanvas.ArenaCenter.Y * (vp.Y / DesignH));

        // ---- 双方主战者：贴着上/下沿、对齐竞技场中线 ----
        //
        // LeaderMedallionWidget 控件宽 240，拱框中线在控件内 x=120
        // （FrameL 34 + FrameW/2 86）。用竞技场中线而不是屏幕中线。
        const float LeaderHalfW = 120f;

        if (_foeLeader != null)
        {
            _foeLeader.Position = new Vector2(
                arena.X - LeaderHalfW, -LeaderMedallionWidget.DomeTopY);
        }

        if (_allyLeader != null)
        {
            // 主战者必须压在窗口底沿、平直边被窗口切掉（与原版一致）。
            // 之前试过「贴手牌区上沿」，结果主战者被顶到战场中间、压住随从。
            _allyLeader.Position = new Vector2(
                arena.X - LeaderHalfW,
                vp.Y - LeaderMedallionWidget.FlatEdgeY);
        }

        // ---- 右侧指令列 ----
        if (_rightCol != null)
        {
            _rightCol.Position = new Vector2(
                vp.X - RightColumnRightMargin, vp.Y * 0.16f);
        }

        // ---- 两块棋盘：关于竞技场中心上下对称 ----
        //
        // 竞技场竖直半轴 208。敌我各占一侧，中心分别在 ±104 处，
        // 棋子高 124，所以再各让 62 —— 得到的行中心正好落在椭圆内。
        float boardW = 700f * sx;
        float boardX = arena.X - boardW * 0.5f;

        float rowGap = 96f * (vp.Y / DesignH);
        float halfUnit = BoardUnitWidget.UnitH * 0.5f * sx;

        if (_boardFoe != null)
        {
            _boardFoe.Position = new Vector2(boardX, arena.Y - rowGap - halfUnit);
            _boardFoe.Size = new Vector2(boardW, 0);
            _boardFoe.CustomMinimumSize = new Vector2(boardW, 0);
        }

        if (_boardAlly != null)
        {
            _boardAlly.Position = new Vector2(boardX, arena.Y + rowGap - halfUnit);
            _boardAlly.Size = new Vector2(boardW, 0);
            _boardAlly.CustomMinimumSize = new Vector2(boardW, 0);
        }

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
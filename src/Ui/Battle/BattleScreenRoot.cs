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
/// 战斗桌面根节点：实现《影之诗：超凡世界》三层动画与输入管理架构。
///
///   第一层（无条件动画）：拖拽手牌 _dragGhost、随从攻击瞄准浮动 _anims.SetAiming。
///                        在非阻塞事件动画播放期间随时可用、实时响应。
///   第二层（非阻塞事件动画）：出牌、入场、攻击冲撞、受击抖动、伤害飘字、破坏碎裂、超进化击飞、抽牌、回合切换。
///                        按 FIFO 在 _seq 中排队播放，不锁死玩家的后续出牌与攻击指令（动画落后于后端）。
///                        离场随从通过 _dyingProxies 保留视觉替身，直至其死亡/击飞动画播放完毕。
///   第三层（阻塞性动画/状态）：进化/超进化（Seq.IsBlocking = true）、需要 Choose 的挂起交互（PendingChoice）、终局结算。
///                        进入队列即锁定玩家状态操作；Choose 与终局弹窗等待前置动画全部播完后才弹出。
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
    private Control _overlayLayer;

    /// <summary>左下角效果发动浮窗栈。</summary>
    private AbilityActivationToastStack _toastStack;

    private readonly BoardAnimDirector _anims = new();
    private readonly AnimQueue _seq = new();

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
    private readonly Dictionary<long, Vector2> _unitTops = new();
    private readonly Dictionary<long, RuntimeEntity> _lastBoardEntities = new();
    private readonly Dictionary<long, BoardUnitWidget> _dyingProxies = new();

    /// <summary>
    /// 未播伤害账本：entityId -> 需要**补回去**的生命值。
    ///
    /// 后端伤害立即结算，但受击动画还排在 _seq 里。若不记账，
    /// Rebuild 出来的棋子当场就显示扣完血的数值 ——
    /// 玩家看到的是「还没被打，盾牌先掉」。
    /// 账上记为正值，_Draw 用它加回可见血量；
    /// 等 _seq 真正播到那一段 OnDamaged 时才销账，盾牌与飘字同帧跳变。
    /// </summary>
    private readonly Dictionary<long, int> _pendingLifeDelta = new();

    /// <summary>
    /// 未播进化账本：后端已进化但翻面动画还在排队时，
    /// 暂时隐藏金框与进化身材加成。
    /// </summary>
    private readonly HashSet<long> _pendingEvolveVisual = new();

    private bool _handExpanded;

    /// <summary>当前手牌区矩形（全局坐标），由 RebuildHand 更新。</summary>
    private Rect2 _handRect;

    /// <summary>
    /// 按下瞬间命中的那张手牌。
    ///
    /// 缩小态按下会立即展开并重建手牌，此时卡片控件全部换新、位置也变了。
    /// 在重建**之前**按旧布局算一遍，才能保证拖出来的是玩家真正按住的那张。
    /// </summary>
    private RuntimeEntity _pressedCard;

    /// <summary>缩小态下每张手牌的左边界，供按下时的命中判定。</summary>
    private readonly List<(Rect2 Rect, RuntimeEntity Card)> _handHitRects = new();

    /// <summary>
    /// 手牌展开/收起的过渡进度：0 = 常态（缩小），1 = 放大。
    ///
    /// 早先切换是瞬时的：RebuildHand 一重建，卡片直接从 88x116 跳到 138x182，
    /// 图标和位置「啪」地换掉，观感很硬。现在用一个缓动把两者插值出来，
    /// 逻辑等价于 CSS 的 transition。
    /// </summary>
    private float _handAnim;
    private bool _handAnimTarget;
    private RuntimeEntity _attacker;
    private bool _draggingPlay;
    private RuntimeEntity _draggingCard;

    private CardVisualWidget _dragGhost;
    private bool _dragFollow;
    private Vector2 _dragPos;
    private Vector2 _dragTarget;

    private ConstructedDeck _myDeck;
    private ConstructedDeck _foeDeck;

    public event Action OnExitToDecks;

    /// <summary>
    /// 三层输入门禁：
    /// 只有当存在【阻塞性动画】（进化/超进化）、或后端正在等待【Choose 选择】、或【对局已结束】时，才阻塞玩家状态操作。
    /// 普通非阻塞事件动画（入场、攻击、受伤、抽牌）播放期间返回 false，允许玩家连拍疾驰随从并立即操控攻击！
    /// </summary>
    public bool IsInputBlocked
        => _seq.HasBlocking
        || _ctrl?.IsWaitingForInput == true
        || _ctrl?.IsFinished == true;

    public void Boot(GameSessionStore store, ConstructedDeck mine, ConstructedDeck foe)
    {
        _store = store;
        _myDeck = mine;
        _foeDeck = foe;
        _ctrl = new BattleSessionController(store, mine, foe, seed: 20260101,
                                            startInMulligan: true);
        BoardUnitWidget.CurrentController = _ctrl;

        _lastEventIdx = _ctrl.State.EventLog.Count;

        BuildUi();
        Rebuild();
        RefreshOverlays();
    }


    /// <summary>
    /// 客机模式启动：本地 State 只作只读镜像，所有操作上行给权威端。
    ///
    /// 与 Boot 的区别：不跑本地 AI、不做本地换牌 —— 权威端说了算；
    /// 快照到达时把状态铺进镜像再重绘，而不是本地结算。
    /// </summary>
    public void BootRemote(GameSessionStore store, ConstructedDeck mine, ConstructedDeck foe,
                           ShadowverseFan.Ui.Net.RemoteBattleClient client)
    {
        _store = store;
        _myDeck = mine;
        _foeDeck = foe;
        _remoteClient = client;

        _ctrl = new BattleSessionController(store, mine, foe, seed: 20260101,
                                            startInMulligan: false);
        BoardUnitWidget.CurrentController = _ctrl;
        _lastEventIdx = _ctrl.State.EventLog.Count;

        BuildUi();
        Rebuild();
        RefreshOverlays();

        if (_remoteClient != null)
        {
            _remoteClient.SnapshotReceived += OnRemoteSnapshot;
            if (_pending != null)
                _pending.RemoteSubmit = (cid, sel) => RemoteSubmitChoice(cid, sel);
            _remoteClient.Failed += msg => System.Console.WriteLine("[联机] " + msg);
        }
    }

    private ShadowverseFan.Ui.Net.RemoteBattleClient _remoteClient;

    // ---------------------------------------------------------------- 远程模式输入路由

    /// <summary>客机模式下所有操作都必须上行给权威端，绝不在本地结算。</summary>
    private bool IsRemote => _remoteClient != null;

    private void RemotePlayCard(int entityId) { if (_remoteClient != null) _ = _remoteClient.PlayCard(entityId); }
    private void RemoteAttack(int attackerId, int targetId) { if (_remoteClient != null) _ = _remoteClient.Attack(attackerId, targetId); }
    private void RemoteEvolve(int entityId, bool super) { if (_remoteClient != null) _ = _remoteClient.Evolve(entityId, super); }
    private void RemoteEngage(int entityId) { if (_remoteClient != null) _ = _remoteClient.EngageAmulet(entityId); }
    private void RemoteEndTurn() { if (_remoteClient != null) _ = _remoteClient.EndTurn(); }
    private void RemoteSubmitChoice(int choiceId, int[] selected) { if (_remoteClient != null) _ = _remoteClient.SubmitChoice(choiceId, selected); }


    private void OnRemoteSnapshot(ShadowverseFan.KernelV3.Net.NetStateSnapshot snap)
    {
        if (_ctrl == null || snap == null) return;
        _ctrl.ApplyRemoteSnapshot(snap);
        Rebuild();
        RefreshOverlays();
    }

    private void RefreshOverlays()
    {
        if (_ctrl == null) return;

        // ① 换牌阶段
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

        // ② 终局：必须等待前置动画（如最后一击、超进化击飞、主战者掉血飘字）全部播完才弹出结算窗
        if (_ctrl.IsFinished)
        {
            if (_seq.Busy) return;

            if (_result == null || !IsInstanceValid(_result))
            {
                int winner = _ctrl.Winner;
                bool victory = winner == _ctrl.MyPlayerId;
                _result = new MatchResultOverlay();
                AddChild(_result);
                _result.Setup(victory, _ctrl.State.TurnNumber);
                _result.OnRematch += () => RestartMatch();
                _result.OnBackToDecks += () => OnExitToDecks?.Invoke();
            }
            return;
        }

        // ③ VM 挂起（Choose 目标/模式/发现）：
        // 铁律：打出需要 Choose 的卡片或触发 Choose 时，玩家输入已被 IsInputBlocked 锁定，
        // 但必须等此前的动画队列（_seq.Busy）全部播放完毕后，才展示选择界面与金色候选高亮！
        if (_pending == null || !IsInstanceValid(_pending))
        {
            _pending = new PendingChoiceOverlay();
            AddChild(_pending);
            _pending.Setup(_ctrl);
            _pending.OnChoiceSubmitted += Rebuild;
        }

        if (_ctrl.IsWaitingForInput && _seq.Busy)
        {
            _pending.Visible = false;
            return;
        }

        _pending.RefreshState();
    }

    private void BuildUi()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized += LayoutBattlefield;

        _field = new BattlefieldCanvas { Controller = _ctrl };
        _field.OnCrestClicked += e => _actionPanel.ShowForEntity(e);
        AddChild(_field);

        _foeLeader = new LeaderMedallionWidget();
        _foeLeader.Setup(_ctrl, 1, true);
        _foeLeader.OnLeaderClicked += () =>
        {
            var ldr = _ctrl.Leader(1);
            if (!_seq.Busy && _pending != null && IsInstanceValid(_pending)
                && _pending.TryPickBoardOrHandEntity(ldr.EntityId)) return;
            _actionPanel.ShowForEntity(ldr);
        };
        AddChild(_foeLeader);

        _allyLeader = new LeaderMedallionWidget();
        _allyLeader.Setup(_ctrl, 0, false);
        _allyLeader.OnLeaderClicked += () =>
        {
            var ldr = _ctrl.Leader(0);
            if (!_seq.Busy && _pending != null && IsInstanceValid(_pending)
                && _pending.TryPickBoardOrHandEntity(ldr.EntityId)) return;
            _actionPanel.ShowForEntity(ldr);
        };

        _allyLeader.OnEvolveOrbClicked += isSuper =>
        {
            if (IsInputBlocked) return;
            var target = _actionPanel.BoundEntity;
            if (target == null) return;
            if (IsRemote) { RemoteEvolve(target.EntityId, isSuper); return; }
            if (_ctrl.TryEvolve(target, isSuper)) Rebuild();
        };
        _foeLeader.OnEvolveOrbClicked += isSuper =>
        {
            if (IsInputBlocked) return;
            var target = _actionPanel.BoundEntity;
            if (target == null) return;
            if (IsRemote) { RemoteEvolve(target.EntityId, isSuper); return; }
            if (_ctrl.TryEvolve(target, isSuper)) Rebuild();
        };
        AddChild(_allyLeader);

        _rightCol = new RightCommandColumnWidget();
        _rightCol.Setup(_ctrl);
        _rightCol.OnEndTurnClicked += () =>
        {
            if (IsInputBlocked) return;
            if (IsRemote) { RemoteEndTurn(); return; }
            _ctrl.EndTurn();
            Rebuild();
        };
        _rightCol.OnExtraPpClicked += () =>
        {
            if (IsInputBlocked) return;
            if (IsRemote) { _ = _remoteClient.UseExtraPp(); return; }
            _ctrl.TryUseExtraPp();
            Rebuild();
        };
        AddChild(_rightCol);

        _boardFoe = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_boardFoe);

        _boardAlly = new VBoxContainer { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_boardAlly);

        _handLayer = new Control { MouseFilter = MouseFilterEnum.Pass };
        AddChild(_handLayer);

        _overlayLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        _overlayLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_overlayLayer);

        _toastStack = new AbilityActivationToastStack();
        _toastStack.Setup(_ctrl);
        AddChild(_toastStack);

        _vfx = new BattleVfxDirector();
        AddChild(_vfx);

        _actionPanel = new TopLeftCardActionPanel();
        // 左上角面板的进化/超进化/启动/融合按钮同样受阻塞门禁约束。
        _actionPanel.IsInputBlocked = () => IsInputBlocked;
        _actionPanel.Setup(_ctrl);
        _actionPanel.OnEntityChanged += _ => Rebuild();
        AddChild(_actionPanel);

        _pageBtn = new Button { Text = "", Position = new Vector2(52, 312) };
        _pageBtn.CustomMinimumSize = new Vector2(52, 52);
        _pageBtn.Size = new Vector2(52, 52);
        SvWbTheme.StyleButton(_pageBtn);
        _pageBtn.Pressed += OpenInfoModal;
        _pageBtn.Draw += () => DrawPageIcon(_pageBtn);
        AddChild(_pageBtn);

        _menuBtn = new Button { Text = "", Position = new Vector2(52, 24) };
        _menuBtn.CustomMinimumSize = new Vector2(52, 52);
        _menuBtn.Size = new Vector2(52, 52);
        SvWbTheme.StyleButton(_menuBtn);
        _menuBtn.Pressed += () => OnExitToDecks?.Invoke();
        _menuBtn.Draw += () => DrawMenuIcon(_menuBtn);
        AddChild(_menuBtn);

        _field.GuiInput += (InputEvent e) =>
        {
            if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left)
            {
                // 手牌收起统一由 _Input 按「是否在手牌区内」判定，
                // 这里再收一次会与它抢时序（_Input 先跑，这里后跑）。
                _actionPanel.HidePanel();
            }
        };

        LayoutBattlefield();
        CallDeferred(nameof(LayoutBattlefield));
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        if (_draggingPlay && _draggingCard != null)
        {
            var mp = GetGlobalMousePosition();
            _dragTarget = mp;
            _field.IsDraggingPlayCard = mp.Y < PlayLineY;
            _field.QueueRedraw();
        }

        // 0. 手牌展开/收起的过渡：每帧逼近目标，变化时重排手牌。
        //    这就是 CSS transition 的等价物 —— 开关只是一个目标值，
        //    真正的尺寸/位置由 _handAnim 连续插出来。
        {
            float target = _handExpanded ? 1f : 0f;
            if (Mathf.Abs(_handAnim - target) > 0.001f)
            {
                float speed = 6.5f;          // 约 0.15 秒走完
                _handAnim = Mathf.MoveToward(_handAnim, target, speed * dt);
                // 过渡期间只做纯几何重排：这里每帧调 RebuildHand 会把
                // 全部手牌控件销毁重建，0.15 秒内造出上百个对象。
                LayoutHandWidgetsOnly();
            }
            else if (_handAnim != target)
            {
                _handAnim = target;
                LayoutHandWidgetsOnly();
            }
        }

        // 1. 第一层（无条件动画）：手牌拖拽跟随 + 棋子瞄准浮动与轨道推进
        StepDragFollow(dt);
        _anims.Step(dt);
        ApplyMotionsToWidgets();

        // 2. 第二/三层（事件动画队列）：FIFO 推进
        bool wasBusy = _seq.Busy;
        if (_seq.Step(dt))
        {
            _boardAlly?.QueueRedraw();
            _boardFoe?.QueueRedraw();
            foreach (var p in _dyingProxies.Values)
                if (IsInstanceValid(p)) p.QueueRedraw();
        }

        // 当队列刚好从 Busy 变为 Idle 时：立刻唤醒等待中的 Choose 弹窗、候选高亮或终局结算窗！
        if (wasBusy && !_seq.Busy)
        {
            ApplyChoiceHighlightToLeaders();
            ApplyChoiceHighlightToBoards();
            RefreshOverlays();
        }

        StepOpponentIfNeeded(delta);
        UpdateAimLine();
    }

    /// <summary>出牌判定线：距窗口底部恒定 220px，随视口高度走。</summary>
    private float PlayLineY
    {
        get
        {
            var vp = Size;
            if (vp.Y <= 0f) vp = GetViewportRect().Size;
            return BattleSessionController.HandZoneTopYFor(vp.Y);
        }
    }

    private void UpdateAimLine()
    {
        if (_field == null || _ctrl == null) return;

        var p = _ctrl.Runner?.PendingChoice;
        bool aiming = !_seq.Busy
                      && p != null
                      && p.Kind == ChoiceKind.SelectTarget
                      && p.PlayerId == _ctrl.MyPlayerId
                      && _pending != null && IsInstanceValid(_pending);

        if (_field.IsAiming != aiming || aiming)
        {
            _field.IsAiming = aiming;
            if (aiming) _field.AimTarget = GetGlobalMousePosition();
            _field.QueueRedraw();
        }
    }

    private void StepOpponentIfNeeded(double delta)
    {
        if (_ctrl == null || _ctrl.IsFinished) return;
        if (_ctrl.IsInMulligan) return;
        if (_ctrl.IsMyTurn || _ctrl.IsWaitingForInput) { _aiTimer = 0; return; }
        // 对手回合等待当前动画段播完再走下一步，防止动作堆叠
        if (_seq.Busy) return;

        _aiTimer += delta;
        if (_aiTimer < 0.35) return;
        _aiTimer = 0;

        _ctrl.StepOpponentTurn();
        Rebuild();
    }

    // ---------------------------------------------------------------- 重建与坐标计算

    public void Rebuild()
    {
        LayoutBattlefield();
        ConsumeNewEventsToVfx();
        RebuildBoards();
        RebuildHand();
        ApplyChoiceHighlightToLeaders();
        ApplyChoiceHighlightToBoards();

        _allyLeader.QueueRedraw();
        _foeLeader.QueueRedraw();
        _rightCol.QueueRedraw();
        _field.QueueRedraw();

        RefreshOverlays();
    }

    private void ApplyChoiceHighlightToLeaders()
    {
        var p = _ctrl?.Runner?.PendingChoice;
        bool choosing = !_seq.Busy
                        && p != null
                        && p.Kind == ChoiceKind.SelectTarget
                        && p.PlayerId == _ctrl.MyPlayerId;

        ApplyLeader(_allyLeader, _ctrl.Leader(0), choosing, p);
        ApplyLeader(_foeLeader, _ctrl.Leader(1), choosing, p);

        static void ApplyLeader(LeaderMedallionWidget w, RuntimeEntity e,
                                bool choosing, PendingChoice p)
        {
            if (w == null || e == null) return;
            if (!choosing) { w.SetChoiceState(false, false); return; }
            bool isCand = p.Candidates.Contains(e.EntityId);
            w.SetChoiceState(isCand, !isCand);
        }
    }

    private void ApplyChoiceHighlightToBoards()
    {
        var p = _ctrl?.Runner?.PendingChoice;
        bool choosing = !_seq.Busy
                        && p != null
                        && p.Kind == ChoiceKind.SelectTarget
                        && p.PlayerId == _ctrl.MyPlayerId;

        foreach (var w in BoardUnitsOf(_boardAlly).Concat(BoardUnitsOf(_boardFoe)))
        {
            if (w.Entity == null) continue;
            if (!choosing) { w.SetChoiceState(false, false); continue; }
            bool isCand = p.Candidates.Contains(w.Entity.EntityId);
            w.SetChoiceState(isCand, !isCand);
        }
    }

    /// <summary>
    /// 确定性计算某玩家场上第 index/count 颗棋子的左上角全局坐标。
    /// 不依赖 HBoxContainer 的异步延迟排版，哪怕在第 0 帧也能算准每个随从的精确坐标！
    /// </summary>
    private Vector2 ComputeSlotTopLeft(int playerId, int index, int count)
    {
        var vp = GetEffectiveViewportSize();
        float sx = vp.X / DesignW;
        float sy = vp.Y / DesignH;
        Vector2 arena = new(BattlefieldCanvas.ArenaCenter.X * sx,
                            BattlefieldCanvas.ArenaCenter.Y * sy);

        const float sep = 14f;
        float totalW = count <= 0 ? BoardUnitWidget.UnitW
                     : count * BoardUnitWidget.UnitW + (count - 1) * sep;
        float startX = arena.X - totalW * 0.5f;

        float rowGap = 96f * sy;
        float halfUnit = BoardUnitWidget.UnitH * 0.5f;
        float rowY = playerId == 1
            ? arena.Y - rowGap - halfUnit
            : arena.Y + rowGap - halfUnit;

        return new Vector2(startX + index * (BoardUnitWidget.UnitW + sep), rowY);
    }

    private void RebuildBoards()
    {
        foreach (var c in _boardAlly.GetChildren()) c.QueueFree();
        foreach (var c in _boardFoe.GetChildren()) c.QueueFree();

        AddBoardRow(_boardFoe, 1);
        AddBoardRow(_boardAlly, 0);
        ApplyMotionsToWidgets();
    }

    private void AddBoardRow(VBoxContainer host, int playerId)
    {
        var row = new HBoxContainer
        {
            Alignment = BoxContainer.AlignmentMode.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 14);
        host.AddChild(row);

        var boardList = _ctrl.Board(playerId).Take(5).ToList();
        int count = boardList.Count;

        for (int i = 0; i < count; i++)
        {
            var e = boardList[i];
            _lastBoardEntities[e.EntityId] = e;

            // 立即写入精确槽位坐标，绝不等到下一帧
            Vector2 topLeft = ComputeSlotTopLeft(playerId, i, count);
            _unitTops[e.EntityId] = topLeft;
            _unitCenters[e.EntityId] = topLeft + new Vector2(BoardUnitWidget.UnitW * 0.5f,
                                                             BoardUnitWidget.UnitH * 0.5f);

            var w = new BoardUnitWidget();
            w.BindDirector(_anims);
            bool canAtk = playerId == 0 && _ctrl.CanAttackWith(e);
            bool selected = _actionPanel.BoundEntity != null
                            && _actionPanel.BoundEntity.EntityId == e.EntityId;
            w.Setup(e, selected, canAtk);
            w.ApplyMotion(_anims.MotionOf(e.EntityId));

            w.OnClicked += ent =>
            {
                if (!_seq.Busy && _pending != null && IsInstanceValid(_pending)
                    && _pending.TryPickBoardOrHandEntity(ent.EntityId))
                    return;

                _actionPanel.ShowForEntity(ent);
                if (!IsInputBlocked && _ctrl.IsInFuseMode) _ctrl.ToggleFuseMaterial(ent);
            };
            w.OnRightClicked += ent => _actionPanel.ShowForEntity(ent);

            // 第一层（无条件动画）：只要未处于阻塞态，随时可拿起就绪随从进入浮动瞄准状态！
            w.OnDragAttackStarted += (ent, pos) =>
            {
                if (IsInputBlocked) return;
                _anims.SetAiming(ent.EntityId);
                _attacker = ent;
                _field.IsDraggingAttack = true;
                _field.AttackDragStart = _unitCenters.TryGetValue(ent.EntityId, out var c) ? c : pos;
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

    /// <summary>
    /// 为已被破坏/消失、但死亡动画还在 _seq 中排队的随从生成视觉替身棋子（Dying Proxy）。
    /// 这样它能继续在原座位挨刀、抖动、冒伤害数字，然后播放超进化击飞或碎裂消散！
    /// </summary>
    private void EnsureDyingProxy(long entityId, EntityRef? subjRef)
    {
        if (entityId < 0 || _overlayLayer == null) return;
        if (_dyingProxies.TryGetValue(entityId, out var existing) && IsInstanceValid(existing))
            return;

        RuntimeEntity ent = null;
        if (!_lastBoardEntities.TryGetValue(entityId, out ent) && subjRef?.Snapshot != null)
        {
            var sn = subjRef.Value.Snapshot;
            var def = sn.Def ?? _ctrl.State.CardDefOf(sn.CardId);
            if (def != null)
            {
                ent = new RuntimeEntity
                {
                    EntityId = sn.EntityId,
                    EntrySerial = sn.EntrySerial,
                    Def = def,
                    PlayerId = sn.PlayerId,
                    Zone = ZoneRef.Board,
                    Attack = sn.Attack,
                    Life = Math.Max(0, sn.Life),
                    MaxLife = sn.MaxLife,
                    Cost = sn.Cost,
                    IsEvolved = sn.IsEvolved,
                    IsSuperEvolved = sn.IsSuperEvolved,
                };
            }
        }
        if (ent?.Def == null) return;

        int pid = ent.PlayerId >= 0 ? ent.PlayerId : 1;
        Vector2 topLeft = _unitTops.TryGetValue(entityId, out var t0)
            ? t0
            : ComputeSlotTopLeft(pid, 0, 1);

        var proxy = new BoardUnitWidget();
        proxy.BindDirector(_anims);
        proxy.Setup(ent, false, false);
        proxy.MouseFilter = MouseFilterEnum.Ignore;
        proxy.Position = topLeft;
        proxy.ZIndex = 52;
        _overlayLayer.AddChild(proxy);
        _dyingProxies[entityId] = proxy;
    }

    private void RemoveDyingProxy(long entityId)
    {
        if (_dyingProxies.TryGetValue(entityId, out var proxy))
        {
            if (IsInstanceValid(proxy)) proxy.QueueFree();
            _dyingProxies.Remove(entityId);
        }
        _anims.Clear(entityId);
    }

    private Vector2 CenterOf(EntityRef? r, int fallbackPlayerId)
    {
        if (r != null)
        {
            long id = r.Value.EntityId;
            if (_unitCenters.TryGetValue(id, out var c)) return c;
            if (r.Value.Snapshot?.Zone == ZoneRef.Leader)
                return LeaderCenterOf(r.Value.Snapshot.PlayerId);
        }
        return LeaderCenterOf(fallbackPlayerId >= 0 ? fallbackPlayerId : 1);
    }

    private Vector2 LeaderCenterOf(int playerId)
    {
        var leader = _ctrl?.Leader(playerId);
        if (leader != null && _unitCenters.TryGetValue(leader.EntityId, out var lp))
            return lp;
        var w = playerId == 0 ? _allyLeader : _foeLeader;
        if (w != null && IsInstanceValid(w))
            return w.Position + w.Size * 0.5f;
        return new Vector2(595f, playerId == 0 ? 650f : 65f);
    }

    /// <summary>
    /// 消费 BattleState.EventLog 中新增的事件并排入 _seq 队列。
    /// 注意：先在 RebuildBoards 之前消费事件，这样刚阵亡的随从坐标和实体快照仍在，可生成 DyingProxy！
    /// 同时预先计算本轮在场随从的精确坐标，供新入场随从使用。
    /// </summary>
    private void ConsumeNewEventsToVfx()
    {
        if (_vfx == null || _ctrl?.State == null) return;

        // 先为当前已在场的实体预填坐标（供新入场随从的 OnEnterField 读取）
        for (int pid = 0; pid <= 1; pid++)
        {
            var list = _ctrl.Board(pid).Take(5).ToList();
            for (int idx = 0; idx < list.Count; idx++)
            {
                var e = list[idx];
                _lastBoardEntities[e.EntityId] = e;
                Vector2 tl = ComputeSlotTopLeft(pid, idx, list.Count);
                _unitTops[e.EntityId] = tl;
                _unitCenters[e.EntityId] = tl + new Vector2(BoardUnitWidget.UnitW * 0.5f,
                                                             BoardUnitWidget.UnitH * 0.5f);
            }
        }

        var log = _ctrl.State.EventLog;
        for (int i = _lastEventIdx; i < log.Count; i++)
        {
            var ev = log[i];

            // 手动超进化会先后发出 OnEvolve 与 OnSuperEvolve 两条事件；
            // 合并为一次【超进化阻塞动画】，避免同一个随从连翻两次面。
            if (ev.Kind == CoreEvent.OnEvolve
                && i + 1 < log.Count
                && log[i + 1].Kind == CoreEvent.OnSuperEvolve
                && log[i + 1].Subject?.EntityId == ev.Subject?.EntityId)
            {
                continue;
            }

            EnqueueEvent(ev);
        }

        _lastEventIdx = log.Count;
        _lastHitBySuperEvolved = false;
    }

    private void EnqueueEvent(VmEvent ev)
    {
        int ownerPid = ev.Subject?.Snapshot?.PlayerId ?? (ev.PlayerId >= 0 ? ev.PlayerId : 1);
        Vector2 center = CenterOf(ev.Subject, ownerPid);
        long sid = ev.Subject?.EntityId ?? -1;

        bool hitBySuper = ev.KnockedBackBySuperEvolve || _lastHitBySuperEvolved;

        switch (ev.Kind)
        {
            case CoreEvent.OnAttack:
            {
                if (sid >= 0 && ev.Object != null)
                {
                    var subjRef = ev.Subject;
                    var objRef = ev.Object;
                    int atkPid = ownerPid;
                    int defPid = objRef.Value.Snapshot?.PlayerId ?? (1 - ownerPid);

                    _seq.Enqueue(new Seq()
                        .Then(CreateAttackLungeAnim(sid, subjRef, atkPid, objRef, defPid))
                        .Wait(0.26f));
                }
                break;
            }
            case CoreEvent.OnDamaged:
            {
                int dmg = ev.Amount;

                // 先在账上记一笔「已扣但还没播」的血量，让盾牌维持受击前的数值。
                if (sid >= 0 && dmg > 0)
                    _pendingLifeDelta[sid] = _pendingLifeDelta.GetValueOrDefault(sid) + dmg;

                _seq.Enqueue(new Seq()
                    .Do(() =>
                    {
                        Vector2 c = CenterOf(ev.Subject, ownerPid);
                        _vfx.PlayDamage(c, dmg);
                        if (dmg > 0) SpawnDamageFloat(c, dmg, false);
                        if (sid >= 0) _anims.PlayHitShake(sid);

                        // 播到这一帧才销账：盾牌数字与伤害飘字同帧跳变。
                        if (sid >= 0 && dmg > 0)
                        {
                            int left = _pendingLifeDelta.GetValueOrDefault(sid) - dmg;
                            if (left > 0) _pendingLifeDelta[sid] = left;
                            else _pendingLifeDelta.Remove(sid);
                            ApplyMotionsToWidgets();
                        }
                    })
                    .Wait(0.18f));
                break;
            }

            case CoreEvent.OnHealed:
            {
                int heal = ev.Amount;

                // 治疗同理但方向相反：显示治疗前的血量，播到该段才补上。
                if (sid >= 0 && heal > 0)
                    _pendingLifeDelta[sid] = _pendingLifeDelta.GetValueOrDefault(sid) - heal;

                _seq.Enqueue(new Seq()
                    .Do(() =>
                    {
                        Vector2 c = CenterOf(ev.Subject, ownerPid);
                        _vfx.PlayHeal(c, heal);
                        if (heal > 0) SpawnDamageFloat(c, heal, true);

                        if (sid >= 0 && heal > 0)
                        {
                            int left = _pendingLifeDelta.GetValueOrDefault(sid) + heal;
                            if (left < 0) _pendingLifeDelta[sid] = left;
                            else _pendingLifeDelta.Remove(sid);
                            ApplyMotionsToWidgets();
                        }
                    })
                    .Wait(0.16f));
                break;
            }

            case CoreEvent.OnDestroy:
            {
                if (sid >= 0 && ev.Subject?.Snapshot?.Zone == ZoneRef.Board)
                    EnsureDyingProxy(sid, ev.Subject);

                Vector2 unitCenter = center;
                if (sid >= 0 && _unitTops.TryGetValue(sid, out var t0))
                    unitCenter = t0 + new Vector2(BoardUnitWidget.UnitW * 0.5f, BoardUnitWidget.UnitH * 0.5f);

                if (hitBySuper && sid >= 0)
                {
                    Vector2 leaderCenter = LeaderCenterOf(ownerPid);
                    Vector2 toward = leaderCenter - unitCenter;

                    // 超进化击飞演出：替身棋子高速撞向所属主战者 → 主战者处爆开紫金粒子与斩击 → 销毁替身
                    _seq.Enqueue(new Seq()
                        .Do(() =>
                        {
                            _anims.PlayKnockToLeader(sid, toward);
                        })
                        .Wait(0.34f)
                        .Do(() =>
                        {
                            _vfx.PlayDestroy(leaderCenter);
                            SpawnBurst(leaderCenter, 24, 165f, new Color("C084FC"), 0.65f);
                            RemoveDyingProxy(sid);
                        })
                        .Wait(0.12f));
                }
                else
                {
                    // 普通破坏演出：在原座位碎裂成蓝白光点并缩小消散
                    _seq.Enqueue(new Seq()
                        .Do(() =>
                        {
                            _vfx.PlayDestroy(unitCenter);
                            SpawnBurst(unitCenter, 22, 150f, new Color("93C5FD"), 0.65f);
                            if (sid >= 0) _anims.PlayLeave(sid);
                        })
                        .Wait(0.26f)
                        .Do(() =>
                        {
                            if (sid >= 0) RemoveDyingProxy(sid);
                        }));
                }
                break;
            }

            case CoreEvent.OnBanish:
            {
                if (sid >= 0 && ev.Subject?.Snapshot?.Zone == ZoneRef.Board)
                    EnsureDyingProxy(sid, ev.Subject);

                Vector2 unitCenter = center;
                if (sid >= 0 && _unitTops.TryGetValue(sid, out var t0))
                    unitCenter = t0 + new Vector2(BoardUnitWidget.UnitW * 0.5f, BoardUnitWidget.UnitH * 0.5f);

                _seq.Enqueue(new Seq()
                    .Do(() =>
                    {
                        _vfx.PlayBanish(unitCenter);
                        SpawnBurst(unitCenter, 18, 140f, new Color("A78BFA"), 0.60f);
                        if (sid >= 0) _anims.PlayLeave(sid);
                    })
                    .Wait(0.24f)
                    .Do(() =>
                    {
                        if (sid >= 0) RemoveDyingProxy(sid);
                    }));
                break;
            }

            case CoreEvent.OnPlay:
            {
                // 按值捕获这张卡的定义。用成员变量的话，一回合内连续出牌
                // 会让后一张覆盖前一张，前面排队的动画全部画成同一张卡。
                bool isMine = ev.PlayerId < 0 || ev.PlayerId == _ctrl.MyPlayerId;
                var playedDef = ev.Subject?.Snapshot?.Def
                             ?? _ctrl.State.CardDefOf(ev.Subject?.Snapshot?.CardId ?? 0);
                if (!isMine && playedDef != null)
                    _seq.Enqueue(BuildEnemyPlayCardSeq(playedDef));
                break;
            }
            case CoreEvent.OnAbilityActivated:
            {
                // 按值捕获：AbilityDef 与卡定义都可能在后续被改写。
                var abDef = ev.ActivatedAbility;
                var cardDef = ev.Subject?.Snapshot?.Def
                           ?? _ctrl.State.CardDefOf(ev.Subject?.Snapshot?.CardId ?? 0);
                int pid2 = ev.PlayerId >= 0 ? ev.PlayerId : ownerPid;
                if (cardDef != null && abDef != null)
                    _seq.Enqueue(new Seq().Do(() => _toastStack?.PushToast(cardDef, abDef, pid2)));
                break;
            }

            case CoreEvent.OnStatsModified:
            {
                // 身材变化：棋子弹跳 + 飘字。
                if (sid >= 0)
                {
                    int da = ev.Amount;
                    int dl = ev.SecondaryAmount;
                    bool buff = da + dl >= 0;
                    _seq.Enqueue(new Seq().Do(() =>
                    {
                        var c = CenterOf(ev.Subject, ownerPid);
                        _anims.PlayStatPulse(sid, buff);
                        // 攻击/生命两个分量都显示：「+2/+2」「-2/-0」。
                        string S(int v) => (v >= 0 ? "+" : "") + v;
                        string txt = S(da) + "/" + S(dl);
                        if (buff) _vfx.PlayBuff(c, txt); else _vfx.PlayDebuff(c, txt);
                    }));
                }
                break;
            }

            case CoreEvent.OnKeywordModified:
            {
                if (sid >= 0)
                {
                    _seq.Enqueue(new Seq().Do(() =>
                    {
                        var c = CenterOf(ev.Subject, ownerPid);
                        int kind = ev.Amount;
                        if (kind == -99) _vfx.PlaySilence(c);
                        else if (kind > 0)
                        {
                            // 按实际被赋予的关键词挑贴图，不能一律用护盾徽章。
                            string key = ev.CounterName switch
                            {
                                "bane" => "badge_bane",
                                "drain" => "badge_drain",
                                "free_evolve" or "free_super_evolve" => "badge_free_evolve",
                                "max_attacks_per_turn" => "badge_multi_attack",
                                _ => "badge_shield",
                            };
                            _vfx.PlayKeywordGrant(c, key);
                        }
                    }));
                }
                break;
            }

            case CoreEvent.OnCardRevealed:
            {
                // 公开手牌：只是可见性变化，播一圈提示光即可。
                if (sid >= 0) RebuildHand();
                break;
            }
            case CoreEvent.OnEnterField:
            {
                // 入队瞬间先将随从设为隐身预备态（Alpha = 0），
                // 等队列真正轮到这一段播放时才钻出地面！
                if (sid >= 0) _anims.PrepareEnter(sid, BoardUnitWidget.UnitH);

                _seq.Enqueue(new Seq()
                    .Do(() =>
                    {
                        Vector2 c = CenterOf(ev.Subject, ownerPid);
                        _vfx.PlayEnterField(c);
                        SpawnBurst(c + new Vector2(0f, BoardUnitWidget.UnitH * 0.4f),
                                   12, 95f, new Color("A5B4FC"), 0.45f, -40f, 2.6f);
                        if (sid >= 0) _anims.PlayEnter(sid, BoardUnitWidget.UnitH);
                    })
                    .Wait(0.22f));
                break;
            }

            case CoreEvent.OnDraw:
            {
                // 只有我方抽牌才播「飞进手牌区」的动画。
                // 敌方抽牌玩家看不到手牌，原版也只有牌库数量的变化 ——
                // 早先不判断归属，敌方每抽一张都在右下角飞一张牌出去，
                // 看起来像是自己在抽牌。
                bool isMine = ev.PlayerId < 0 || ev.PlayerId == _ctrl.MyPlayerId;

                if (isMine)
                {
                    _seq.Enqueue(new Seq()
                        .Then(CreateDrawFlyIn())
                        .Do(RebuildHand));
                }
                else
                {
                    // 敌方抽牌：只在屏幕右上（敌方牌库计数）给一个轻微提示
                    _seq.Enqueue(new Seq()
                        .Do(() => _rightCol?.QueueRedraw())
                        .Wait(0.12f));
                }
                break;
            }

            case CoreEvent.TurnStart:
                _seq.Enqueue(new Seq()
                    .Do(() => _rightCol?.QueueRedraw())
                    .Wait(0.18f));
                break;

            case CoreEvent.TurnEnd:
                _seq.Enqueue(new Seq()
                    .Do(() => _rightCol?.QueueRedraw())
                    .Wait(0.15f));
                break;

            case CoreEvent.OnTransform:
                _seq.Enqueue(new Seq()
                    .Do(() => _vfx.PlayTransform(CenterOf(ev.Subject, ownerPid)))
                    .Wait(0.16f));
                break;

            // 第三层（阻塞性动画）：进化与超进化标记 MarkBlocking(true)！
            // 一旦进入队列，IsInputBlocked 立即为 true，玩家必须等待进化动画播完才能继续操作！
            case CoreEvent.OnEvolve:
                // 后端已结算 +2/+2，但翻面动画还排在队列里。
                // 先挂进「未播进化」账本，棋子保持进化前的外框与身材。
                if (sid >= 0) _pendingEvolveVisual.Add(sid);

                _seq.Enqueue(new Seq()
                    .MarkBlocking(true)
                    .Do(() =>
                    {
                        Vector2 c = CenterOf(ev.Subject, ownerPid);
                        _vfx.PlayEvolve(c, false);
                        SpawnBurst(c, 26, 170f, new Color("F59E0B"), 0.65f, -30f, 3.2f);
                        if (sid >= 0)
                        {
                            // 翻面开始的这一刻才切换成进化态：金框与身材同帧到位。
                            _pendingEvolveVisual.Remove(sid);
                            ApplyMotionsToWidgets();
                            _anims.PlayEvolve(sid, false);
                        }
                    })
                    .Wait(0.58f));
                break;

            case CoreEvent.OnSuperEvolve:
                if (sid >= 0) _pendingEvolveVisual.Add(sid);

                _seq.Enqueue(new Seq()
                    .MarkBlocking(true)
                    .Do(() =>
                    {
                        Vector2 c = CenterOf(ev.Subject, ownerPid);
                        _vfx.PlayEvolve(c, true);
                        SpawnBurst(c, 32, 200f, new Color("C084FC"), 0.70f, -40f, 3.5f);
                        if (sid >= 0)
                        {
                            _pendingEvolveVisual.Remove(sid);
                            ApplyMotionsToWidgets();
                            _anims.PlayEvolve(sid, true);
                        }
                    })
                    .Wait(0.58f));
                break;
        }
    }

    /// <summary>敌方手牌区中心（顶部约 (265,4) 起的一排牌背）。</summary>
    private Vector2 EnemyHandAnchor()
    {
        var vp = GetEffectiveViewportSize();
        float sx = vp.X / 1280f;
        return new Vector2(265f * sx, 48f * (vp.Y / 720f));
    }

    /// <summary>
    /// 敌方出牌：一张牌背从敌方手牌区微微抬起并放大。
    ///
    /// 敌方手牌是盖着的，所以用牌背表现；停 1 秒由外层 Seq.Wait 负责。
    /// </summary>
    /// <summary>
    /// 在顶层 _overlayLayer 上创建攻击冲撞替身。
    ///
    /// 为什么不用原座位的棋子直接位移：
    /// 攻击者可能在这一击里被打死，RebuildBoards 会当场把它的控件删掉，
    /// 等动画播到时场上已经没有那个控件了 —— 表现就是「对着空气播」。
    /// 另外 _boardFoe 的层级在主战者与手牌之下，往下冲撞会钻到别的东西背面。
    /// 因此在 ZIndex 62 的浮层上另起一个替身，无论生死敌我都稳定可见。
    ///
    /// 三段：后仰蓄力 -> 高速俯冲 -> 弹回原位。
    /// </summary>
    private Anim CreateAttackLungeAnim(
        long atkId, EntityRef? atkRef, int atkPid,
        EntityRef? defRef, int defPid)
    {
        BoardUnitWidget actor = null;
        Vector2 startTopLeft = Vector2.Zero;
        Vector2 targetDelta = Vector2.Zero;
        bool hitFired = false;

        void SetStationaryVisible(bool vis)
        {
            foreach (var wd in BoardUnitsOf(_boardAlly).Concat(BoardUnitsOf(_boardFoe)))
                if (wd.Entity != null && wd.Entity.EntityId == atkId) wd.Visible = vis;
            if (_dyingProxies.TryGetValue(atkId, out var dp) && IsInstanceValid(dp))
                dp.Visible = vis;
        }

        return new Anim(0.34f, Ease.Linear, v =>
        {
            if (actor == null)
            {
                // 第一帧才建节点：入队时建会让多段动画同时抢同一个位置。
                RuntimeEntity ent = null;
                if (!_lastBoardEntities.TryGetValue(atkId, out ent) && atkRef?.Snapshot != null)
                {
                    var sn = atkRef.Value.Snapshot;
                    var df = sn.Def ?? _ctrl.State.CardDefOf(sn.CardId);
                    if (df != null)
                    {
                        ent = new RuntimeEntity
                        {
                            EntityId = sn.EntityId,
                            EntrySerial = sn.EntrySerial,
                            Def = df,
                            PlayerId = sn.PlayerId,
                            Attack = sn.Attack,
                            Life = Math.Max(1, sn.Life),
                            MaxLife = sn.MaxLife,
                            Cost = sn.Cost,
                            IsEvolved = sn.IsEvolved,
                            IsSuperEvolved = sn.IsSuperEvolved,
                        };
                    }
                }
                if (ent?.Def == null || _overlayLayer == null) return;

                Vector2 atkCenter = CenterOf(atkRef, atkPid);
                Vector2 defCenter = CenterOf(defRef, defPid);
                startTopLeft = _unitTops.TryGetValue(atkId, out var tl)
                    ? tl
                    : atkCenter - new Vector2(BoardUnitWidget.UnitW * 0.5f,
                                              BoardUnitWidget.UnitH * 0.5f);
                targetDelta = defCenter - atkCenter;

                actor = new BoardUnitWidget();
                actor.Setup(ent, false, false);
                actor.VisualLifeOffset = _pendingLifeDelta.GetValueOrDefault(atkId, 0);
                actor.MouseFilter = MouseFilterEnum.Ignore;
                actor.Position = startTopLeft;
                actor.ZIndex = 62;
                _overlayLayer.AddChild(actor);

                SetStationaryVisible(false);
            }

            if (!IsInstanceValid(actor)) return;

            Vector2 offset;
            float scale;
            var dir = targetDelta.Length() > 0.01f ? targetDelta.Normalized() : Vector2.Zero;
            if (v < 0.25f)
            {
                float k = Ease.OutQuad(v / 0.25f);
                offset = -dir * (18f * k);
                scale = 1f + 0.12f * k;
            }
            else if (v < 0.52f)
            {
                float k = Ease.InQuad((v - 0.25f) / 0.27f);
                Vector2 windup = -dir * 18f;
                offset = windup.Lerp(targetDelta * 0.84f, k);
                scale = 1.12f + 0.06f * k;
            }
            else
            {
                if (!hitFired)
                {
                    hitFired = true;
                    Vector2 atkC = CenterOf(atkRef, atkPid);
                    Vector2 defC = CenterOf(defRef, defPid);
                    _vfx?.PlayAttackLunge(null, atkC, defC);
                }
                float k = Ease.OutCubic((v - 0.52f) / 0.48f);
                offset = targetDelta * (0.84f * (1f - k));
                scale = 1.18f - 0.18f * k;
            }

            actor.Position = startTopLeft + offset;
            actor.Scale = new Vector2(scale, scale);
        }, () =>
        {
            if (actor != null && IsInstanceValid(actor)) actor.QueueFree();
            SetStationaryVisible(true);
        });
    }

    /// <summary>
    /// 敌方出牌：从我方手牌区方向飞出一张**完整正面卡**，停留 1 秒后淡出。
    ///
    /// 节点必须在轮到这一段播放时才创建 ——
    /// 入队时创建会让连续出牌的几张卡同时抢同一个起点，
    /// 而且 CreateEnemyPlayOut 在入队那一刻读坐标时第一段还没跑，拿到的是起点。
    /// </summary>
    private Seq BuildEnemyPlayCardSeq(ShadowverseFan.KernelV3.Ast.CardDef def)
    {
        CardVisualWidget widget = null;
        Vector2 from = Vector2.Zero;
        Vector2 showPos = Vector2.Zero;

        return new Seq()
            .Then(new Anim(0.28f, Ease.OutCubic, v =>
            {
                if (widget == null && _overlayLayer != null)
                {
                    from = EnemyHandAnchor();
                    showPos = from + new Vector2(0f, 68f);

                    widget = new CardVisualWidget();
                    widget.Setup(def, _ctrl.Store.CardPrinter, showCopyPips: false);
                    widget.SetCardSize(138f, 182f);
                    widget.MouseFilter = MouseFilterEnum.Ignore;
                    widget.Position = from;
                    widget.ZIndex = 64;
                    _overlayLayer.AddChild(widget);
                }
                if (widget == null || !IsInstanceValid(widget)) return;
                widget.Position = from.Lerp(showPos, v);
                widget.Scale = Vector2.One * (0.85f + 0.15f * v);
                widget.Modulate = new Color(1, 1, 1, v);
            }))
            .Wait(1.0f)
            .Then(new Anim(0.26f, Ease.InCubic, v =>
            {
                if (widget == null || !IsInstanceValid(widget)) return;
                widget.Position = showPos.Lerp(showPos + new Vector2(0f, -28f), v);
                widget.Modulate = new Color(1, 1, 1, 1f - v);
            }, () =>
            {
                if (widget != null && IsInstanceValid(widget)) widget.QueueFree();
            }));
    }
    private Anim CreateDrawFlyIn()
    {
        if (_overlayLayer == null)
            return new Anim(0f, Ease.Linear, _ => { });

        var vp = GetEffectiveViewportSize();
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            new Color("1A2438"), SvWbTheme.AccentCyan, 8, 2, 10));
        card.Size = new Vector2(64, 88);
        var from = new Vector2(vp.X + 20f, vp.Y - 124f);
        var to = new Vector2(vp.X - 160f, vp.Y - 110f);
        card.Position = from;
        card.MouseFilter = MouseFilterEnum.Ignore;
        card.ZIndex = 57;
        _overlayLayer.AddChild(card);

        return new Anim(0.22f, Ease.OutCubic, v =>
        {
            if (!IsInstanceValid(card)) return;
            card.Position = from.Lerp(to, v);
            card.Modulate = new Color(1, 1, 1, 1f - Mathf.Max(0f, (v - 0.7f) / 0.3f));
        }, () => { if (IsInstanceValid(card)) card.QueueFree(); });
    }

    private static IEnumerable<BoardUnitWidget> BoardUnitsOf(Node container)
    {
        if (container == null) yield break;
        foreach (var child in container.GetChildren())
        {
            if (child is BoardUnitWidget w) { yield return w; continue; }
            foreach (var sub in BoardUnitsOf(child)) yield return sub;
        }
    }

    private void ApplyMotionsToWidgets()
    {
        ApplyMotionsIn(_boardAlly);
        ApplyMotionsIn(_boardFoe);

        foreach (var kv in _dyingProxies)
        {
            if (IsInstanceValid(kv.Value))
            {
                kv.Value.ApplyMotion(_anims.MotionOf(kv.Key));
                kv.Value.VisualLifeOffset = _pendingLifeDelta.GetValueOrDefault(kv.Key, 0);
                kv.Value.HideEvolveVisual = _pendingEvolveVisual.Contains(kv.Key);
            }
        }

        // 主战者的盾牌同样要延后跳变
        if (_allyLeader?.Leader != null)
            _allyLeader.VisualLifeOffset =
                _pendingLifeDelta.GetValueOrDefault(_allyLeader.Leader.EntityId, 0);
        if (_foeLeader?.Leader != null)
            _foeLeader.VisualLifeOffset =
                _pendingLifeDelta.GetValueOrDefault(_foeLeader.Leader.EntityId, 0);
    }

    private void ApplyMotionsIn(Node container)
    {
        if (container == null || _anims == null) return;
        foreach (var w in BoardUnitsOf(container))
        {
            if (w.Entity == null) continue;
            w.ApplyMotion(_anims.MotionOf(w.Entity.EntityId));

            // 视觉血量 / 进化延迟：账本上是「已扣但未播」的部分
            w.VisualLifeOffset = _pendingLifeDelta.GetValueOrDefault(w.Entity.EntityId, 0);
            w.HideEvolveVisual = _pendingEvolveVisual.Contains(w.Entity.EntityId);

            // 不再用控件的实时坐标回写 _unitTops / _unitCenters。
            //
            // RebuildBoards 刚把棋子塞进 HBoxContainer 的同一帧里，
            // Godot 还没排版，所有 w.GlobalPosition 都是 (0,0)/(容器左上角)，
            // 这一段会把 ComputeSlotTopLeft 算出的确定坐标全覆盖成容器原点 ——
            // 攻击动画据此算出的位移就变成从屏幕左上角飞出去。
            // 坐标一律只信 ComputeSlotTopLeft。
        }
    }

    private void SpawnBurst(Vector2 globalPos, int count, float spread, Color tint,
                            float life = 0.7f, float gravity = 60f, float size = 3.2f)
    {
        if (_overlayLayer == null) return;
        var b = new ParticleBurst();
        _overlayLayer.AddChild(b);
        b.Setup(globalPos, count, spread, tint, life, gravity, size);
    }

    private void SpawnDamageFloat(Vector2 center, int amount, bool heal)
    {
        if (amount == 0 || _overlayLayer == null) return;
        var f = new DamageFloat();
        _overlayLayer.AddChild(f);
        f.Setup(amount, center, heal);
    }

    private void ReleaseAttack(Vector2 screenPos)
    {
        _field.IsDraggingAttack = false;
        _field.QueueRedraw();
        _anims.ClearAiming();

        if (_attacker == null) return;

        // 只有在阻塞状态（IsInputBlocked）下才拒绝攻击；
        // 普通非阻塞动画播放期间完全允许发起攻击，攻击动画会自动排进 _seq 尾部！
        if (IsInputBlocked)
        {
            _attacker = null;
            RebuildBoards();
            return;
        }

        var atk = _attacker;
        _attacker = null;

        var legal = _ctrl.LegalAttackTargets(atk);

        var foeLeader = _ctrl.Leader(1);
        if (foeLeader != null && legal.Contains(foeLeader.EntityId)
            && _foeLeader.GetGlobalRect().HasPoint(screenPos))
        {
            _lastHitBySuperEvolved = atk.IsSuperEvolved;
            if (IsRemote) { RemoteAttack(atk.EntityId, foeLeader.EntityId); return; }
            _ctrl.TryAttack(atk, foeLeader, out _);
            Rebuild();
            return;
        }

        foreach (var w in BoardUnitsOf(_boardFoe))
        {
            if (w.Entity == null) continue;
            if (!w.GetGlobalRect().HasPoint(screenPos)) continue;
            if (!legal.Contains(w.Entity.EntityId)) continue;

            _lastHitBySuperEvolved = atk.IsSuperEvolved;
            if (IsRemote) { RemoteAttack(atk.EntityId, w.Entity.EntityId); return; }
            _ctrl.TryAttack(atk, w.Entity, out _);
            Rebuild();
            return;
        }
    }

    /// <summary>手牌控件池。数量只跟手牌张数走，不随布局变化重建。</summary>
    private readonly List<CardVisualWidget> _handWidgets = new();
    private Control _handTray;

    private void RebuildHand()
    {
        if (_handLayer == null || _ctrl == null) return;

        var hand = _ctrl.Hand(0);
        int n = hand.Count;

        // 多退少补：只在手牌张数变化时增删控件。
        // 原来无条件 foreach QueueFree 再 new，展开/收起过渡的 0.15 秒里
        // 每帧都会把全部手牌销毁重建，一秒钟造出上百个控件。
        while (_handWidgets.Count > n)
        {
            var last = _handWidgets[^1];
            _handWidgets.RemoveAt(_handWidgets.Count - 1);
            if (IsInstanceValid(last)) last.QueueFree();
        }
        while (_handWidgets.Count < n)
        {
            var nw = new CardVisualWidget();
            _handLayer.AddChild(nw);
            _handWidgets.Add(nw);
        }

        // 逐张换绑：牌变了才重新 Setup，同一个实体只刷新运行时数值。
        for (int i = 0; i < n; i++)
        {
            var card = hand[i];
            var wd = _handWidgets[i];
            if (!IsInstanceValid(wd)) continue;
            wd.Rebind(card.Def, card, _store.CardPrinter);
            BindHandCardClicks(wd, card);
        }

        LayoutHandWidgetsOnly();
    }

    /// <summary>给手牌控件挂点击与右键回调（先清空再挂，避免复用后叠加）。</summary>
    private void BindHandCardClicks(CardVisualWidget wd, RuntimeEntity card)
    {
        wd.ClearClickHandlers();

        var bound = card;
        wd.OnLeftClick += _ =>
        {
            if (!_seq.Busy && _pending != null && IsInstanceValid(_pending)
                && _pending.TryPickBoardOrHandEntity(bound.EntityId))
                return;

            _actionPanel.ShowForEntity(bound);
            if (!IsInputBlocked && _ctrl.IsInFuseMode && !ReferenceEquals(_ctrl.FuseBaseCard, bound))
            {
                _ctrl.ToggleFuseMaterial(bound);
                _actionPanel.ShowForEntity(_ctrl.FuseBaseCard);
                RebuildHand();
                return;
            }
            if (IsInputBlocked) return;

            // 若这次按下发生在「缩小态 -> 放大态」那一瞬间，
            // 用按下时按原布局算出的那张牌，而不是重建后正好压在这个位置的牌。
            var pressed = _pressedCard;
            _pressedCard = null;
            _draggingCard = pressed ?? bound;
            _draggingPlay = true;
        };
        wd.OnRightClick += _ => _actionPanel.ShowForEntity(bound);
    }

    /// <summary>
    /// 纯几何重排：只改位置与尺寸，零节点分配。
    ///
    /// 展开/收起的每帧插值走这里，绝不再调 RebuildHand。
    /// </summary>
    private void LayoutHandWidgetsOnly()
    {
        if (_handLayer == null || _ctrl == null) return;

        var hand = _ctrl.Hand(0);
        int n = hand.Count;

        var vp = GetEffectiveViewportSize();
        float sx = vp.X / DesignW;

        // 手牌尺寸与距窗口底部距离（HandTrayBottomGap = 8px）保持恒定，不随窗口宽度纵向膨胀。
        float t = Ease.OutCubic(Mathf.Clamp(_handAnim, 0f, 1f));
        float cardW = Mathf.Lerp(88f, 138f, t);
        float cardH = Mathf.Lerp(116f, 182f, t);
        float rightEdge = vp.X - 14f;

        float minLeft = Mathf.Lerp(750f, 420f, t) * sx;
        float maxTrayW = Mathf.Max(220f, rightEdge - minLeft);

        float idealStep = cardW + Mathf.Lerp(4f, 6f, t);
        float neededW = n <= 0 ? 180f : cardW + (n - 1) * idealStep + 20f;
        float trayW = Mathf.Clamp(neededW, 180f, maxTrayW);
        float trayLeft = rightEdge - trayW;
        float bottomY = vp.Y - HandTrayBottomGap;
        float trayH = cardH + 14f;
        float trayTop = bottomY - trayH;

        if (_handTray == null || !IsInstanceValid(_handTray))
        {
            _handTray = new Control { MouseFilter = MouseFilterEnum.Ignore };
            _handLayer.AddChild(_handTray);
        }
        _handTray.Position = new Vector2(trayLeft, trayTop);
        _handTray.Size = new Vector2(trayW, trayH);

        // 记下当前手牌区矩形，供 _Input 判断「按下是否落在手牌区」。
        _handRect = new Rect2(trayLeft, trayTop, trayW, trayH);
        _handHitRects.Clear();

        if (n == 0) return;

        float stepX = n == 1 ? 0f : Mathf.Min(idealStep, (trayW - 20f - cardW) / (n - 1));
        float startX = trayLeft + 10f;

        for (int i = 0; i < n && i < _handWidgets.Count; i++)
        {
            var wd = _handWidgets[i];
            if (!IsInstanceValid(wd)) continue;
            wd.Position = new Vector2(startX + i * stepX, trayTop + 7f);
            wd.SetCardSize(cardW, cardH);
            _handHitRects.Add((new Rect2(wd.Position, new Vector2(cardW, cardH)), hand[i]));
        }
    }
    /// <summary>该点是否落在某个合法候选（棋子 / 主战者）上。</summary>
    private bool HitAnyCandidate(Vector2 at)
    {
        var pending = _ctrl?.Runner?.PendingChoice;
        if (pending == null) return false;

        foreach (var id in pending.Candidates)
        {
            foreach (var w in BoardUnitsOf(_boardAlly).Concat(BoardUnitsOf(_boardFoe)))
            {
                if (w.Entity == null || w.Entity.EntityId != id) continue;
                if (w.GetGlobalRect().HasPoint(at)) return true;
            }
            if (_allyLeader?.Leader?.EntityId == id
                && _allyLeader.GetGlobalRect().HasPoint(at)) return true;
            if (_foeLeader?.Leader?.EntityId == id
                && _foeLeader.GetGlobalRect().HasPoint(at)) return true;

            // 手牌候选：如「选择 1 张手牌舍弃」
            foreach (var (rect, card) in _handHitRects)
                if (card != null && card.EntityId == id && rect.HasPoint(at)) return true;
        }
        return false;
    }

    /// <summary>
    /// 按当前已布局的手牌矩形做命中判定。
    ///
    /// 用登记表而不是遍历控件：控件是 Godot 节点，RebuildHand 之后
    /// 到下一帧之前位置尚未刷新，直接读 GlobalPosition 会拿到旧值。
    /// </summary>
    private RuntimeEntity HitTestHandCard(Vector2 at)
    {
        foreach (var (rect, card) in _handHitRects)
        {
            if (rect.HasPoint(at)) return card;
        }
        return null;
    }

    // ---------------------------------------------------------------- 输入

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion mm && _draggingPlay && _draggingCard != null)
        {
            _dragTarget = mm.Position;

            const float PLAY_THRESHOLD = 20f;
            if (!_dragFollow && mm.Position.Y < PlayLineY - PLAY_THRESHOLD)
                BeginDragFollow();
            return;
        }

        // ---- 发动前预选：右键或点空白处取消 ----
        //
        // 只对 CanCancel 的挂起有效：那意味着卡还没上场、PP/EP 还没扣，
        // 取消是零成本的。结算中必选（已抽牌 / 已发现）不允许反悔。
        if (_ctrl != null && _ctrl.CanCancelPending)
        {
            if (@event is InputEventMouseButton rmb
                && rmb.Pressed && rmb.ButtonIndex == MouseButton.Right)
            {
                _ctrl.CancelPendingChoice();
                Rebuild();
                GetViewport().SetInputAsHandled();
                return;
            }

            if (@event is InputEventMouseButton lmb
                && lmb.Pressed && lmb.ButtonIndex == MouseButton.Left)
            {
                // 点在模式弹窗 / 查看战场 / 多选确认按钮上时交给控件自己处理。
                // _Input 先于 GuiInput 执行，不先让一步的话，
                // 预选阶段任何模式都点不了，一点就被当成「点空白取消」。
                if (_pending != null && IsInstanceValid(_pending)
                    && _pending.IsPointOnOverlayUi(lmb.GlobalPosition))
                    return;

                // 只有「选目标」才存在「点空白 = 取消」的语义。
                // 选模式、多选确认这些都必须点到具体控件上，
                // 点空白不取消，否则玩家永远没机会做选择。
                var pc = _ctrl.Runner.PendingChoice;
                bool onTarget = pc != null && pc.Kind == ChoiceKind.SelectTarget
                                && HitAnyCandidate(lmb.GlobalPosition);
                if (pc != null && pc.Kind == ChoiceKind.SelectTarget && !onTarget)
                {
                    _ctrl.CancelPendingChoice();
                    Rebuild();
                    GetViewport().SetInputAsHandled();
                    return;
                }
            }
        }

        if (@event is not InputEventMouseButton mb || mb.ButtonIndex != MouseButton.Left) return;

        // ---- 手牌展开态：点在**手牌区之外**任意位置即收起 ----
        //
        // 原来这条逻辑挂在 _field.GuiInput 上，只有点战场背景才生效；
        // 点顶部菜单栏、右侧栏、左上角面板按钮都不会收起，玩家得
        // 专门找一块空地戳一下才行。收到这里按矩形判定，
        // 「手牌区以外」就一律回到常态，符合直觉。
        //
        // 拖拽中不收起：手上的牌还举着，这时候收手牌会把它一起抹掉。
        if (mb.Pressed && _handExpanded && !_draggingPlay && !_handRect.HasPoint(mb.Position))
        {
            _handExpanded = false;
            RebuildHand();
            return;
        }

        // ---- 手牌区：按下即展开 ----
        //
        // 规则：
        //   · 常态（未展开）下，在手牌区任意位置按下 → 展开
        //   · 已经在拖拽手牌时**不打断**（拖拽优先）
        //   · 已展开状态下按下去不做任何事，交给卡片自己的拖拽逻辑
        //
        // 放在这里而不是给卡片加 GuiInput，是因为要覆盖
        // 「按在卡片间隙/留白上」的情况；同时卡片的 OnLeftClick
        // 在按下时也会触发拖拽，所以必须靠 _draggingPlay 做互斥。
        if (mb.Pressed && !_handExpanded && !_draggingPlay && _handRect.HasPoint(mb.Position))
        {
            // 按下这一刻必须**先按当前（缩小态）布局**算出命中的是哪张牌。
            //
            // 因为 _Input 早于卡片的 GuiInput 执行：等 RebuildHand() 把布局
            // 换成放大态之后，卡片控件已经是新的一批、位置也全变了，
            // 卡片自己的回调拿到的 bound 与玩家实际按下的那张对不上 ——
            // 表现就是「拖出来的不是我按的那张」。
            _pressedCard = HitTestHandCard(mb.Position);

            _handExpanded = true;
            RebuildHand();
            return;
        }

        if (!mb.Pressed && _draggingPlay && _draggingCard != null)
        {
            if (IsInputBlocked)
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

            // 松手判定必须与拖拽时的高亮用同一条动态线。
            //
            // ShouldPlayOnRelease 内部比的是写死的 HandZoneTopY(500f)，
            // 1600x900 下把牌拖到战场下半区（Y≈580）时蓝框已经亮了，
            // 松手却判为不出牌。
            bool shouldPlay = _dragFollow && mb.GlobalPosition.Y < PlayLineY;
            EndDragFollow();

            if (shouldPlay)
            {
                SpawnBurst(_dragPos, 26, 190f, new Color("FDE68A"), 0.62f, -20f, 3.0f);
                if (IsRemote) { RemotePlayCard(card.EntityId); return; }
                _ctrl.TryPlayCard(card, out _);
                Rebuild();
            }
            else
            {
                RebuildHand();
            }
        }
    }

    private void BeginDragFollow()
    {
        if (_draggingCard == null) return;
        _dragFollow = true;

        _dragGhost = new CardVisualWidget();
        _dragGhost.Setup(_draggingCard.Def, _store.CardPrinter, 0, 3, false);
        _dragGhost.SetCardSize(150f, 198f);
        _dragGhost.MouseFilter = MouseFilterEnum.Ignore;
        _dragGhost.ZIndex = 55;
        _dragGhost.Modulate = new Color(1, 1, 1, 0.94f);
        _overlayLayer.AddChild(_dragGhost);

        _dragPos = _dragTarget;
        _dragGhost.Position = _dragPos - new Vector2(75f, 99f);
    }

    private void EndDragFollow()
    {
        _dragFollow = false;
        if (_dragGhost != null && IsInstanceValid(_dragGhost))
            _dragGhost.QueueFree();
        _dragGhost = null;
    }

    private void StepDragFollow(float dt)
    {
        if (!_dragFollow || _dragGhost == null || !IsInstanceValid(_dragGhost)) return;

        float k = 1f - Mathf.Pow(0.001f, dt * 1.9f);
        _dragPos = _dragPos.Lerp(_dragTarget, Mathf.Clamp(k, 0f, 1f));
        _dragGhost.Position = _dragPos - new Vector2(75f, 99f);

        float inHand = Mathf.Clamp((PlayLineY - _dragPos.Y) / 120f, 0f, 1f);
        _dragGhost.Scale = Vector2.One * (0.96f + 0.10f * inHand);
    }

    private static void DrawPageIcon(Control host)
    {
        var rect = new Rect2(14f, 12f, 24f, 28f);
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color(0.06f, 0.09f, 0.16f, 0.9f));
        host.DrawRect(new Rect2(4f, 4f, 44f, 44f), new Color("D4AF37"), false, 1.5f);

        Vector2[] page =
        {
            new(rect.Position.X, rect.Position.Y),
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + 8f),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + rect.Size.Y),
            new(rect.Position.X, rect.Position.Y + rect.Size.Y),
        };
        host.DrawColoredPolygon(page, new Color("F8FAFC"));

        Vector2[] fold =
        {
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y),
            new(rect.Position.X + rect.Size.X - 8f, rect.Position.Y + 8f),
            new(rect.Position.X + rect.Size.X, rect.Position.Y + 8f),
        };
        host.DrawColoredPolygon(fold, new Color("CBD5E1"));

        for (int i = 0; i < 3; i++)
        {
            float y = rect.Position.Y + 12f + i * 6f;
            host.DrawLine(new Vector2(rect.Position.X + 5f, y),
                          new Vector2(rect.Position.X + rect.Size.X - 5f, y),
                          new Color(0.35f, 0.40f, 0.50f), 1.2f);
        }
    }

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
        _unitTops.Clear();
        _lastBoardEntities.Clear();
        _pendingLifeDelta.Clear();
        _pendingEvolveVisual.Clear();
        foreach (var p in _dyingProxies.Values)
            if (IsInstanceValid(p)) p.QueueFree();
        _dyingProxies.Clear();
        _anims.ClearAll();
        _seq.Clear();

        Boot(_store, _myDeck, _foeDeck);
    }

    private void OpenInfoModal()
    {
        var modal = new BattleInfoModal();
        AddChild(modal);
        modal.Setup(_ctrl);
    }

    // ---------------------------------------------------------------- 布局与截图辅助

    public void SpawnBadgeDemo()
    {
        if (_ctrl == null) return;
        var pool = _ctrl.Store.GetPoolView("all-clans");
        var cards = pool.EnumerateCollectibleCards().ToList();

        var none = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.None);
        var lw = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.LastWords);
        var trg = cards.FirstOrDefault(c => UnitAbilityBadgeResolver.Resolve(c) == UnitAbilityBadge.Triggered);

        foreach (var def in new[] { lw, trg, none })
        {
            if (def == null) continue;
            var e = _ctrl.State.SpawnCard(def.CardId, ZoneRef.Board, 0);
            if (e != null) e.EnteredTurn = _ctrl.State.TurnNumber - 1;
        }

        // 与 SpawnBoardDemo 同理：推游标必须在 Rebuild 之前。
        _lastEventIdx = _ctrl.State.EventLog.Count;
        _seq.Clear();
        _anims.ClearAll();
        Rebuild();
    }

    public void PlayDamageDemo()
    {
        if (_ctrl?.State == null) return;

        var foeUnits = _ctrl.State.EnumerateZone(ZoneRef.Board, 1).Take(2).ToList();
        foreach (var u in foeUnits)
            _ctrl.State.ApplyDamage(u, 3, _ctrl.MyPlayerId);

        _ctrl.State.ApplyDamage(_ctrl.Leader(_ctrl.MyPlayerId), 5, 1);

        Rebuild();
    }

    private const float DesignW = 1280f;
    private const float DesignH = 720f;
    private const float HandTrayBottomGap = 8f;
    private const float RightColumnRightMargin = 245f;

    private Vector2 GetEffectiveViewportSize()
    {
        var vp = Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = GetViewportRect().Size;
        if (vp.X <= 0f || vp.Y <= 0f) vp = new Vector2(DesignW, DesignH);
        return vp;
    }

    /// <summary>
    /// 按实际视口重排全部战场元素。
    /// 铁律：手牌区、纹章区、三个信息栏（左下/右上计数栏、右侧能量点栏、左侧书页按钮）、主战者
    /// 距窗口底部的距离严格保持恒定！
    /// </summary>
    private void LayoutBattlefield()
    {
        var vp = GetEffectiveViewportSize();

        // 浮窗栈锚在左下角纹章区正上方，跟随视口高度。
        _toastStack?.UpdateAnchor(vp);
        float sx = vp.X / DesignW;
        float sy = vp.Y / DesignH;

        Vector2 arena = new(
            BattlefieldCanvas.ArenaCenter.X * sx,
            BattlefieldCanvas.ArenaCenter.Y * sy);

        const float LeaderHalfW = 120f;

        if (_foeLeader != null)
        {
            _foeLeader.Position = new Vector2(
                arena.X - LeaderHalfW, -LeaderMedallionWidget.DomeTopY);
            var ldr1 = _ctrl?.Leader(1);
            if (ldr1 != null)
                _unitCenters[ldr1.EntityId] = _foeLeader.Position + new Vector2(LeaderHalfW, 58f);
        }

        if (_allyLeader != null)
        {
            // 我方主战者距窗口底部距离恒定（FlatEdgeY = 112px）
            _allyLeader.Position = new Vector2(
                arena.X - LeaderHalfW,
                vp.Y - LeaderMedallionWidget.FlatEdgeY);
            var ldr0 = _ctrl?.Leader(0);
            if (ldr0 != null)
                _unitCenters[ldr0.EntityId] = _allyLeader.Position + new Vector2(LeaderHalfW, 54f);
        }

        // 右侧能量点指令栏：距窗口底部距离恒定（720 - 115 = 605）
        if (_rightCol != null)
        {
            _rightCol.Position = new Vector2(
                vp.X - RightColumnRightMargin, vp.Y - 605f);
        }

        // 左侧对局信息书页按钮：距窗口底部距离恒定（720 - 312 = 408）
        if (_pageBtn != null)
        {
            _pageBtn.Position = new Vector2(52f, vp.Y - 408f);
        }

        float boardW = 700f * sx;
        float boardX = arena.X - boardW * 0.5f;
        float halfUnit = BoardUnitWidget.UnitH * 0.5f;

        // 两排棋子的竖直偏移**不对称**：
        //   敌方可以贴中线往上，我方必须给「出牌判定线」留出空间。
        // 720p 下原先是 ±96，我方底边落在 510，而出牌线在 500 ——
        // 随从压进了拖牌时的提示区域，蓝框一亮就盖住半张卡。
        // 现在把我方那排上收，底边与判定线保持 24px 间隔。
        float foeGap = 104f * sy;
        float allyGap = 64f * sy;

        if (_boardFoe != null)
        {
            _boardFoe.Position = new Vector2(boardX, arena.Y - foeGap - halfUnit);
            _boardFoe.Size = new Vector2(boardW, 0);
            _boardFoe.CustomMinimumSize = new Vector2(boardW, 0);
        }

        if (_boardAlly != null)
        {
            _boardAlly.Position = new Vector2(boardX, arena.Y + allyGap - halfUnit);
            _boardAlly.Size = new Vector2(boardW, 0);
            _boardAlly.CustomMinimumSize = new Vector2(boardW, 0);
        }

        if (_handLayer != null && _ctrl != null)
            RebuildHand();

        _field?.QueueRedraw();
    }

    public void SpawnBoardDemo()
    {
        if (_ctrl?.State == null) return;
        var ids = new[] { 10001120, 10001110, 10001130, 10002120 };
        for (int i = 0; i < ids.Length; i++)
        {
            // EnteredTurn 设为上一回合，解除召唤失调 ——
            // 否则演示场面里的随从全都不能攻击，
            // 实机摸不到「按住随从选目标」那套交互。
            var ally = _ctrl.State.SpawnCard(ids[i], ZoneRef.Board, _ctrl.MyPlayerId);
            if (ally != null) ally.EnteredTurn = _ctrl.State.TurnNumber - 1;

            var foe = _ctrl.State.SpawnCard(ids[i], ZoneRef.Board,
                                            _ctrl.MyPlayerId == 0 ? 1 : 0);
            if (foe != null) foe.EnteredTurn = _ctrl.State.TurnNumber - 1;
        }

        // 演示随从应当**直接就位**，不要排队播入场动画。
        //
        // 这是截图工具的场面布置，不是真实对局：8 个随从各播 0.34 秒入场，
        // 队列要跑近 3 秒才轮到后面塞进去的 --demo-damage 伤害飘字，
        // 而 DevShot 默认只等几十帧 —— 截图里当然什么都看不到。
        //
        // 顺序很要紧：**先推游标，再 Rebuild**。
        //
        // 反过来的话，Rebuild 里的 ConsumeNewEventsToVfx 会读到 8 条
        // OnEnterField，给每颗棋子挂 PrepareEnter（Alpha=0 隐身）。
        // 之后即使 _anims.Clear() 清了轨道，控件身上的 _motion 缓存
        // 仍停在 Alpha=0 —— 第 0 帧整排随从全是隐形的。
        _lastEventIdx = _ctrl.State.EventLog.Count;
        _seq.Clear();
        _anims.ClearAll();
        Rebuild();
    }

    public void SetHandExpanded(bool v) { _handExpanded = v; RebuildHand(); }
    public BattleSessionController Controller => _ctrl;
}

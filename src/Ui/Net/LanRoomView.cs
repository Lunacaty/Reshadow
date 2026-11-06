using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Net;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;
using ShadowverseFan.Ui.Widgets;

namespace ShadowverseFan.Ui.Net;

/// <summary>
/// 独立「房间场景」：建房 / 加入成功后，房主与客机都停在这里。
///
/// 分工：大厅只管选卡池、建房、加入；进房间后各自换牌组（即时上报）、
/// 各自准备，房主决定先手模式，双方都准备好才允许「开始对战」。
/// 真建立对局（BootHost / BootRemote）由上层在 START 事件到达后完成，
/// 本视图只负责在正确的时机发出 OnStartMatchRequested。
/// </summary>
public partial class LanRoomView : Control
{
    /// <summary>房主发出 START 或客机收到 START：参数 = resolvedFirstPlayer。</summary>
    public event Action<int> OnStartMatchRequested;
    /// <summary>点「离开房间」：由上层清理连接并回主菜单。</summary>
    public event Action OnLeaveRoom;

    private GameSessionStore _store;
    private AuthoritativeMatchHost _host;
    private RemoteBattleClient _client;
    private string _poolId = "";
    private ConstructedDeck _initialDeck;
    private bool _swappedToBattle;
    private int _startFirstPlayer;

    /// <summary>下拉框下标对应的合法牌组缓存；下标 0 是占位提示。</summary>
    private readonly List<ConstructedDeck> _decks = new();
    /// <summary>本地准备按钮的目标状态。</summary>
    private bool _localReady;
    /// <summary>网络线程只写这份快照，主线程通过 CallDeferred 消费。</summary>
    private NetRoomStateDto _latestRoom;

    private Label _poolLabel;
    private OptionButton _deckPicker;
    private Label _opponentDeckLabel;
    private Label _readyLabel;
    private Label _firstPlayerLabel;
    private Button _hostFirstBtn;
    private Button _hostSecondBtn;
    private Button _randomBtn;
    private Button _readyBtn;
    private Button _startBtn;
    private Label _status;

    /// <summary>房主模式：host 非空；客机模式：client 非空，两者互斥。</summary>
    public void BootHostRoom(GameSessionStore store, string poolId, ConstructedDeck initialDeck,
                             AuthoritativeMatchHost host)
    {
        _store = store;
        _poolId = poolId;
        _initialDeck = initialDeck;
        _host = host;
        BuildUi(isHost: true);

        // 房主本地也要刷新：SetHostDeck / SetHostReady 等都会重新 PublishRoom。
        _host.RoomStateChanged += OnRoomState;
        _host.StartMatchRequested += OnStartMatch;
        RefreshFromRoom(_host.RoomState);
    }

    /// <summary>客机模式：连接已发起，首包 ROOM 到达后房间信息才完整。</summary>
    public void BootGuestRoom(GameSessionStore store, string poolId, ConstructedDeck initialDeck,
                              RemoteBattleClient client)
    {
        _store = store;
        _poolId = poolId;
        _initialDeck = initialDeck;
        _client = client;
        BuildUi(isHost: false);

        _client.RoomStateReceived += OnRoomState;
        _client.StartMatchReceived += OnStartMatch;
        _client.Failed += OnClientFailed;
        RefreshFromRoom(_client.LatestRoomState);
    }

    // ---------------------------------------------------------------- 界面

    private void BuildUi(bool isHost)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        var bg = new AstralBackgroundCanvas();
        AddChild(bg);

        var panel = new PanelContainer();
        AddChild(panel);
        SvWbTheme.KeepCentered(this, panel, new Vector2(860, 600));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.AccentGold, 14, 2, 20));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        panel.AddChild(root);

        var title = new Label { Text = isHost ? "🏠 联机房间（房主）" : "🔗 联机房间（客机）" };
        title.AddThemeFontSizeOverride("font_size", 22);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        root.AddChild(title);

        _poolLabel = new Label { Text = "赛制卡池：" + _poolId };
        _poolLabel.AddThemeFontSizeOverride("font_size", 14);
        _poolLabel.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        root.AddChild(_poolLabel);

        root.AddChild(new HSeparator());

        // ① 我方牌组：只列本卡池下校验合法的牌组，切换即时上报。
        var deckRow = new HBoxContainer();
        deckRow.AddThemeConstantOverride("separation", 8);
        var deckHint = new Label { Text = "我方出战牌组：", CustomMinimumSize = new Vector2(120, 32) };
        deckHint.AddThemeFontSizeOverride("font_size", 14);
        deckRow.AddChild(deckHint);

        _deckPicker = new OptionButton { CustomMinimumSize = new Vector2(360, 32) };
        SvWbTheme.StyleButton(_deckPicker);
        SvWbTheme.StyleOptionPopup(_deckPicker);
        _deckPicker.ItemSelected += OnDeckSelected;
        deckRow.AddChild(_deckPicker);
        root.AddChild(deckRow);
        RefreshDeckPicker();

        _opponentDeckLabel = new Label { Text = "对手牌组：等待对手…" };
        _opponentDeckLabel.AddThemeFontSizeOverride("font_size", 14);
        _opponentDeckLabel.AddThemeColorOverride("font_color", SvWbTheme.TextSecondary);
        root.AddChild(_opponentDeckLabel);

        _readyLabel = new Label { Text = "房主：○ 未准备    客机：○ 未准备" };
        _readyLabel.AddThemeFontSizeOverride("font_size", 14);
        root.AddChild(_readyLabel);

        root.AddChild(new HSeparator());

        // ② 先后手模式：只有房主可点；客机三个按钮只读，另有一行换算后的结论。
        var modeRow = new HBoxContainer();
        modeRow.AddThemeConstantOverride("separation", 8);
        var modeHint = new Label { Text = "先手模式：", CustomMinimumSize = new Vector2(120, 32) };
        modeHint.AddThemeFontSizeOverride("font_size", 14);
        modeRow.AddChild(modeHint);

        _hostFirstBtn = MakeModeButton("房主先手", FirstPlayerMode.HostFirst, isHost);
        _hostSecondBtn = MakeModeButton("客机先手", FirstPlayerMode.HostSecond, isHost);
        _randomBtn = MakeModeButton("随机", FirstPlayerMode.Random, isHost);
        modeRow.AddChild(_hostFirstBtn);
        modeRow.AddChild(_hostSecondBtn);
        modeRow.AddChild(_randomBtn);
        root.AddChild(modeRow);

        _firstPlayerLabel = new Label { Text = "我方先手" };
        _firstPlayerLabel.AddThemeFontSizeOverride("font_size", 15);
        _firstPlayerLabel.AddThemeColorOverride("font_color", SvWbTheme.AccentCyan);
        root.AddChild(_firstPlayerLabel);

        root.AddChild(new HSeparator());

        // ③ 准备 / 开始 / 离开。
        var actionRow = new HBoxContainer();
        actionRow.AddThemeConstantOverride("separation", 10);

        _readyBtn = new Button { Text = "✔ 准备", CustomMinimumSize = new Vector2(160, 38) };
        SvWbTheme.StyleButton(_readyBtn, isPrimary: true);
        _readyBtn.Pressed += OnReadyPressed;
        actionRow.AddChild(_readyBtn);

        _startBtn = new Button { Text = "⚔ 开始对战", CustomMinimumSize = new Vector2(180, 38) };
        SvWbTheme.StyleButton(_startBtn, isPrimary: true, SvWbTheme.AccentGold);
        _startBtn.Pressed += OnStartPressed;
        _startBtn.Visible = isHost;
        actionRow.AddChild(_startBtn);

        var leave = new Button { Text = "← 离开房间", CustomMinimumSize = new Vector2(160, 38) };
        SvWbTheme.StyleButton(leave);
        leave.Pressed += () => OnLeaveRoom?.Invoke();
        actionRow.AddChild(leave);
        root.AddChild(actionRow);

        _status = new Label { Text = "房间已就绪" };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _status.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        root.AddChild(_status);
    }

    private Button MakeModeButton(string text, FirstPlayerMode mode, bool isHost)
    {
        var b = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(150, 32),
            ToggleMode = true,
            Disabled = !isHost,
        };
        SvWbTheme.StyleButton(b);
        // 客机没有 SetFirstPlayerMode 上行消息：三个按钮只读，仅反映房主设置。
        if (isHost) b.Pressed += () => _host?.SetFirstPlayerMode(mode);
        return b;
    }

    /// <summary>与大厅同一套严格校验：只列本卡池且 ResolveAndValidate 通过的牌组。</summary>
    private void RefreshDeckPicker()
    {
        if (_deckPicker == null || _store == null) return;
        _deckPicker.Clear();
        _decks.Clear();
        _deckPicker.AddItem("选择本卡池下的出战牌组…", 0);

        var poolView = _store.GetPoolView(_poolId);
        if (poolView == null) return;

        foreach (var d in _store.SavedDecks.Where(
                     x => string.Equals(x.PoolId, _poolId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var profile = DeckRuleResolver.ResolveAndValidate(d, poolView);
                if (profile == null || !profile.IsValid) continue;
            }
            catch { continue; }

            _decks.Add(d);
            _deckPicker.AddItem(d.Name, _decks.Count);
        }

        int idx = _decks.FindIndex(x => string.Equals(x.DeckId, _initialDeck?.DeckId, StringComparison.Ordinal));
        if (idx >= 0) _deckPicker.Select(idx + 1);
    }

    /// <summary>切换牌组立刻上报，不等点开始；房主/客机各自走各自的上行。</summary>
    private void OnDeckSelected(long idx)
    {
        int i = (int)idx - 1;
        if (i < 0 || i >= _decks.Count) return;
        var deck = _decks[i];

        _localReady = false;
        if (_host != null) _host.SetHostDeck(deck);
        else _client?.SubmitDeck(deck);
        // 换牌组后服务端会强制取消该方准备，本地按钮同步回「准备」。
        UpdateReadyButton();
    }

    private void OnReadyPressed()
    {
        _localReady = !_localReady;
        if (_host != null) _host.SetHostReady(_localReady);
        else _client?.SetReady(_localReady);
        UpdateReadyButton();
    }

    private void UpdateReadyButton()
    {
        if (_readyBtn == null) return;
        _readyBtn.Text = _localReady ? "↩ 取消准备" : "✔ 准备";
    }

    private void OnStartPressed()
    {
        if (_host == null || !_host.CanStartMatch) return;
        _host.StartMatch();
    }

    // ---------------------------------------------------------------- 网络事件

    /// <summary>网络线程回调：只暂存快照，Godot 场景树的改动必须切回主线程。</summary>
    private void OnRoomState(NetRoomStateDto room)
    {
        _latestRoom = room;
        CallDeferred(MethodName.ApplyRoomStateOnMainThread);
    }

    private void ApplyRoomStateOnMainThread()
    {
        var room = _latestRoom;
        if (room == null || _swappedToBattle) return;
        RefreshFromRoom(room);
    }

    /// <summary>START 到达：只记下 firstPlayer 并切主线程，绝不在此建对局。</summary>
    private void OnStartMatch(int firstPlayer)
    {
        _startFirstPlayer = firstPlayer;
        CallDeferred(MethodName.EmitStartOnMainThread);
    }

    private void EmitStartOnMainThread()
    {
        if (_swappedToBattle) return;
        _swappedToBattle = true;
        OnStartMatchRequested?.Invoke(_startFirstPlayer);
    }

    private void OnClientFailed(string message)
    {
        _failedMessage = message;
        CallDeferred(MethodName.ApplyFailureOnMainThread);
    }

    private string _failedMessage;

    private void ApplyFailureOnMainThread()
    {
        if (_status != null) _status.Text = "✘ " + (_failedMessage ?? "连接中断");
    }

    /// <summary>按房间快照刷新全部只读信息与按钮可用性。</summary>
    private void RefreshFromRoom(NetRoomStateDto room)
    {
        if (room == null || _swappedToBattle) return;
        _latestRoom = room;
        if (_poolLabel != null) _poolLabel.Text = "赛制卡池：" + room.PoolId;

        bool isHost = _host != null;
        string mine = isHost ? room.HostDeckName : room.GuestDeckName;
        string foe = isHost ? room.GuestDeckName : room.HostDeckName;
        if (_opponentDeckLabel != null)
            _opponentDeckLabel.Text = "对手牌组：" + (string.IsNullOrEmpty(foe) ? "等待对手…" : foe)
                                     + "    我方牌组：" + (string.IsNullOrEmpty(mine) ? "未选择" : mine);

        if (_readyLabel != null)
            _readyLabel.Text = "房主：" + ReadyText(room.HostReady)
                             + "    客机：" + ReadyText(room.GuestReady);

        _localReady = isHost ? room.HostReady : room.GuestReady;
        UpdateReadyButton();

        // 客机换算成自己的视角：房主先手=我方后手，客机先手=我方先手，随机按解析结果。
        if (_firstPlayerLabel != null)
            _firstPlayerLabel.Text = DescribeFirstPlayer(room, isHost);

        if (_hostFirstBtn != null)
        {
            _hostFirstBtn.ButtonPressed = room.FirstMode == FirstPlayerMode.HostFirst;
            _hostSecondBtn.ButtonPressed = room.FirstMode == FirstPlayerMode.HostSecond;
            _randomBtn.ButtonPressed = room.FirstMode == FirstPlayerMode.Random;
        }

        if (_startBtn != null)
            _startBtn.Disabled = !(_host?.CanStartMatch ?? false);

        if (_status != null) _status.Text = room.Status ?? "";
    }

    private static string ReadyText(bool ready) => ready ? "✔ 已准备" : "○ 未准备";

    private static string DescribeFirstPlayer(NetRoomStateDto room, bool isHost)
    {
        if (room.FirstMode == FirstPlayerMode.Random)
        {
            // Random 由权威端用确定性种子解析，双方拿到同一个 ResolvedFirstPlayer。
            bool iGoFirst = isHost
                ? room.ResolvedFirstPlayer == 0
                : room.ResolvedFirstPlayer == 1;
            return "随机（本局解析：我方" + (iGoFirst ? "先手" : "后手") + "）";
        }
        bool first = isHost
            ? room.FirstMode == FirstPlayerMode.HostFirst
            : room.FirstMode == FirstPlayerMode.HostSecond;
        return first ? "我方先手" : "我方后手";
    }

    public override void _ExitTree()
    {
        if (_host != null)
        {
            _host.RoomStateChanged -= OnRoomState;
            _host.StartMatchRequested -= OnStartMatch;
        }
        if (_client != null)
        {
            _client.RoomStateReceived -= OnRoomState;
            _client.StartMatchReceived -= OnStartMatch;
            _client.Failed -= OnClientFailed;
        }
    }
}

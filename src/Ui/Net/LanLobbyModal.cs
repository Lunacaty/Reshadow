using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using ShadowverseFan.KernelV3.Net;
using ShadowverseFan.KernelV3.Packs;
using ShadowverseFan.Ui.Bridge;
using ShadowverseFan.Ui.Theme;

namespace ShadowverseFan.Ui.Net;

/// <summary>
/// 局域网 / Radmin VPN 联机大厅。
///
/// 建房：选卡池与牌组 -> 监听 TCP 27650 + 广播 UDP 27651 ->
///       界面直接展示本机 IP（Radmin 排最前）并带「复制 IP」按钮。
/// 加入：自动刷出广播房间，也可粘贴好友 IP 直连。
/// </summary>
public partial class LanLobbyModal : Control
{
    public event Action OnCancelled;
    public event Action<LanRoomInfo> OnJoinRoom;
    public event Action<string, ConstructedDeck> OnHostRoom;

    private GameSessionStore _store;
    private LanDiscoveryService _discovery;

    private OptionButton _poolPicker;
    private OptionButton _deckPicker;
    private VBoxContainer _roomList;
    private VBoxContainer _ipList;
    private LineEdit _manualIp;
    private Label _status;

    public void Boot(GameSessionStore store)
    {
        _store = store;
        _discovery = new LanDiscoveryService();
        _discovery.RoomsChanged += RefreshRooms;

        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 92;

        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.78f) };
        dim.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dim);

        var panel = new PanelContainer();
        AddChild(panel);
        panel.SetAnchorsPreset(LayoutPreset.TopLeft);
        SvWbTheme.KeepCentered(this, panel, new Vector2(940, 600));
        panel.AddThemeStyleboxOverride("panel", SvWbTheme.MakePanelBox(
            SvWbTheme.BgPanel, SvWbTheme.AccentGold, 14, 2, 20));

        var root = new VBoxContainer();
        root.AddThemeConstantOverride("separation", 10);
        panel.AddChild(root);

        var title = new Label { Text = "🌐 局域网 / Radmin VPN 联机对战" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", new Color("FEF08A"));
        root.AddChild(title);

        root.AddChild(BuildHostSection());

        var sep = new HSeparator();
        root.AddChild(sep);

        root.AddChild(BuildJoinSection());

        _status = new Label { Text = "就绪" };
        _status.AddThemeFontSizeOverride("font_size", 12);
        _status.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        root.AddChild(_status);

        var close = new Button { Text = "关闭" };
        SvWbTheme.StyleButton(close);
        close.Pressed += () => { Shutdown(); OnCancelled?.Invoke(); };
        root.AddChild(close);

        RefreshIpList();
        RefreshDecks();
    }

    // ---------------------------------------------------------------- 建房

    private Control BuildHostSection()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);

        var head = new Label { Text = "创建房间（作为房主）" };
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AddThemeColorOverride("font_color", new Color("FDE68A"));
        box.AddChild(head);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        _poolPicker = new OptionButton { CustomMinimumSize = new Vector2(220, 32) };
        SvWbTheme.StyleButton(_poolPicker);
        SvWbTheme.StyleOptionPopup(_poolPicker);
        row.AddChild(_poolPicker);

        _deckPicker = new OptionButton { CustomMinimumSize = new Vector2(220, 32) };
        SvWbTheme.StyleButton(_deckPicker);
        SvWbTheme.StyleOptionPopup(_deckPicker);
        row.AddChild(_deckPicker);

        var hostBtn = new Button { Text = "🏠 创建并开始广播", CustomMinimumSize = new Vector2(180, 32) };
        SvWbTheme.StyleButton(hostBtn, isPrimary: true);
        hostBtn.Pressed += DoHost;
        row.AddChild(hostBtn);
        box.AddChild(row);

        _ipList = new VBoxContainer();
        _ipList.AddThemeConstantOverride("separation", 3);
        box.AddChild(_ipList);

        return box;
    }

    private void RefreshIpList()
    {
        if (_ipList == null) return;
        foreach (var c in _ipList.GetChildren()) { _ipList.RemoveChild(c); c.QueueFree(); }

        var hint = new Label { Text = "本机地址（把 Radmin 那条发给好友即可远程直连）：" };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
        _ipList.AddChild(hint);

        var addrs = LanNetworkHelper.GetLocalIpv4Addresses();
        if (addrs.Count == 0)
        {
            var none = new Label { Text = "（未检测到可用网卡）" };
            none.AddThemeFontSizeOverride("font_size", 11);
            _ipList.AddChild(none);
            return;
        }

        foreach (var (ip, tag) in addrs)
        {
            var r = new HBoxContainer();
            r.AddThemeConstantOverride("separation", 8);

            var lbl = new Label { Text = ip + "   [" + tag + "]", SizeFlagsHorizontal = SizeFlags.ExpandFill };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            r.AddChild(lbl);

            var cp = new Button { Text = "📋 复制 IP", CustomMinimumSize = new Vector2(100, 26) };
            SvWbTheme.StyleButton(cp);
            string captured = ip;
            cp.Pressed += () =>
            {
                DisplayServer.ClipboardSet(captured);
                if (_status != null) _status.Text = "已复制 " + captured;
            };
            r.AddChild(cp);

            _ipList.AddChild(r);
        }
    }

    private void RefreshDecks()
    {
        if (_poolPicker == null || _deckPicker == null) return;
        _poolPicker.Clear();
        _poolPicker.AddItem("先选卡池…", 0);

        var pools = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < pools.Count; i++) _poolPicker.AddItem(pools[i], i + 1);

        _deckPicker.Clear();
        _deckPicker.AddItem("选我方牌组…", 0);
        for (int i = 0; i < _store.SavedDecks.Count; i++)
        {
            var d = _store.SavedDecks[i];
            _deckPicker.AddItem(d.Name, i + 1);
        }
    }

    private void DoHost()
    {
        if (_deckPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "请先选择我方牌组";
            return;
        }
        var deck = _store.SavedDecks[_deckPicker.Selected - 1];
        string poolId = _poolPicker.Selected > 0 ? _poolPicker.GetItemText(_poolPicker.Selected) : "";

        var addrs = LanNetworkHelper.GetLocalIpv4Addresses();
        string hostIp = addrs.Count > 0 ? addrs[0].Ip : "0.0.0.0";
        _discovery.StartBroadcast(System.Environment.MachineName, LanDiscoveryService.MatchPort, poolId, "");

        if (_status != null)
            _status.Text = "房间已开播，监听端口 " + LanDiscoveryService.MatchPort + "，本机 " + hostIp;

        Shutdown();
        OnHostRoom?.Invoke(poolId, deck);
    }

    // ---------------------------------------------------------------- 加入

    private Control BuildJoinSection()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);

        var head = new Label { Text = "加入房间（作为客机）" };
        head.AddThemeFontSizeOverride("font_size", 14);
        head.AddThemeColorOverride("font_color", new Color("FDE68A"));
        box.AddChild(head);

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 8);

        _manualIp = new LineEdit
        {
            PlaceholderText = "粘贴好友 IP（如 26.12.34.56）",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddChild(_manualIp);

        var joinBtn = new Button { Text = "🔗 直连加入", CustomMinimumSize = new Vector2(130, 32) };
        SvWbTheme.StyleButton(joinBtn, isPrimary: true);
        joinBtn.Pressed += DoJoinManual;
        row.AddChild(joinBtn);

        var scanBtn = new Button { Text = "📡 扫描局域网", CustomMinimumSize = new Vector2(130, 32) };
        SvWbTheme.StyleButton(scanBtn);
        scanBtn.Pressed += () =>
        {
            _discovery.StartListening();
            if (_status != null) _status.Text = "正在扫描局域网房间…";
        };
        row.AddChild(scanBtn);
        box.AddChild(row);

        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(0, 160) };
        SvWbTheme.StyleScroll(scroll);
        _roomList = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _roomList.AddThemeConstantOverride("separation", 4);
        scroll.AddChild(_roomList);
        box.AddChild(scroll);

        return box;
    }

    private void RefreshRooms()
    {
        if (_roomList == null) return;
        foreach (var c in _roomList.GetChildren()) { _roomList.RemoveChild(c); c.QueueFree(); }

        if (_discovery.Rooms.Count == 0)
        {
            var none = new Label { Text = "（暂未发现房间，点「扫描局域网」或直接粘贴 IP）" };
            none.AddThemeFontSizeOverride("font_size", 11);
            none.AddThemeColorOverride("font_color", SvWbTheme.TextMuted);
            _roomList.AddChild(none);
            return;
        }

        foreach (var kv in _discovery.Rooms.OrderBy(x => x.Key))
        {
            var info = kv.Value;
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);

            var lbl = new Label
            {
                Text = info.HostName + "  " + info.Ip + ":" + info.Port + "   [" + info.PoolId + "]",
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            lbl.AddThemeFontSizeOverride("font_size", 12);
            row.AddChild(lbl);

            var join = new Button { Text = "加入", CustomMinimumSize = new Vector2(80, 26) };
            SvWbTheme.StyleButton(join);
            var captured = info;
            join.Pressed += () => { Shutdown(); OnJoinRoom?.Invoke(captured); };
            row.AddChild(join);

            _roomList.AddChild(row);
        }
    }

    private void DoJoinManual()
    {
        string ip = (_manualIp?.Text ?? "").Trim();
        if (ip.Length == 0)
        {
            if (_status != null) _status.Text = "请先填写对方 IP";
            return;
        }

        var info = new LanRoomInfo { Ip = ip, Port = LanDiscoveryService.MatchPort, HostName = ip };
        Shutdown();
        OnJoinRoom?.Invoke(info);
    }

    private void Shutdown()
    {
        _discovery?.Stop();
    }

    public override void _ExitTree() => Shutdown();
}

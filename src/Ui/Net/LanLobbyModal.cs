using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    /// <summary>本实例是否已经建房（「本机双开」据此决定是否先建房）。</summary>
    private bool _hostCreated;
    /// <summary>--join-local 自动加入时使用的卡池（延迟到主线程执行）。</summary>
    private string _autoJoinPoolId;

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
        _poolPicker.ItemSelected += idx => RefreshDecks((int)idx);
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

        var doubleBtn = new Button { Text = "🖥 本机双开", CustomMinimumSize = new Vector2(130, 32) };
        SvWbTheme.StyleButton(doubleBtn);
        // 一键双开：本实例先建房（若还没建），再用同一个 exe 起第二实例自动连 127.0.0.1。
        doubleBtn.Pressed += DoLocalDoubleLaunch;
        row.AddChild(doubleBtn);
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

    /// <summary>与下拉框下标对应的牌组缓存（RefreshDecks 填充）。</summary>
    private readonly List<ShadowverseFan.KernelV3.Packs.ConstructedDeck> _filteredDecks = new();

    private void RefreshDecks()
    {
        if (_poolPicker == null || _deckPicker == null) return;
        _poolPicker.Clear();
        _poolPicker.AddItem("① 先选卡池…", 0);

        var pools = _store.PoolConfigs.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        for (int i = 0; i < pools.Count; i++) _poolPicker.AddItem(pools[i], i + 1);

        RefreshDecks(-1);
    }

    /// <summary>
    /// 严格先选卡池：牌组下拉只列出**属于该卡池且校验合法**的牌组。
    /// 早先不分卡池全列，选了不匹配的牌组进对局才被规则拦截，白白绕一圈。
    /// </summary>
    private void RefreshDecks(long poolIdx)
    {
        if (_deckPicker == null) return;
        _deckPicker.Clear();
        _filteredDecks.Clear();
        _deckPicker.AddItem("② 选择该卡池下的出战牌组…", 0);

        if (poolIdx <= 0) return;
        string poolId = _poolPicker.GetItemText((int)poolIdx);
        var poolView = _store.GetPoolView(poolId);
        if (poolView == null) return;

        foreach (var d in _store.SavedDecks.Where(
                     x => string.Equals(x.PoolId, poolId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                // 不合法的牌组（张数不足/同名超限/职业不符）直接不列。
                ShadowverseFan.KernelV3.Packs.DeckRuleResolver.ResolveAndValidate(d, poolView);
            }
            catch { continue; }

            _filteredDecks.Add(d);
            _deckPicker.AddItem(d.Name, _filteredDecks.Count);
        }
    }

    private void DoHost()
    {
        if (_poolPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请先选择赛制卡池";
            return;
        }
        if (_deckPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请选择该卡池下的出战牌组";
            return;
        }
        var deck = _filteredDecks[_deckPicker.Selected - 1];
        string poolId = _poolPicker.GetItemText(_poolPicker.Selected);

        _hostCreated = true;
        OnHostRoom?.Invoke(poolId, deck);
    }

    // ---------------------------------------------------------------- 本机双开

    /// <summary>
    /// 一键本机双开：用当前 exe 再起一个进程，带上 --join-local=&lt;卡池&gt;。
    /// 第二实例在 MainMenuView 解析该参数，自动以 127.0.0.1 + 同卡池进入「加入」态，
    /// 绕开 VPN 网卡广播出来的 fake-ip 无法直连的问题。
    /// </summary>
    private void DoLocalDoubleLaunch()
    {
        if (_poolPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请先选择赛制卡池，再点「本机双开」";
            return;
        }
        if (_deckPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请选择该卡池下的出战牌组，再点「本机双开」";
            return;
        }
        string poolId = _poolPicker.GetItemText(_poolPicker.Selected);

        // exe 路径取不到就明确阻塞，绝不伪造一个「看起来能跑」的启动。
        string exe = ResolveCurrentExe();
        if (exe == null)
        {
            if (_status != null)
                _status.Text = "✘ 本机双开阻塞：无法可靠取得当前可执行文件路径（OS.GetExecutablePath / Environment.ProcessPath 均不可用）";
            return;
        }

        // 先建房：BeginHosting 是同步的，监听就绪后再起第二实例，避免连接竞态。
        // 大厅随即被隐藏并 Swap 到房间界面，但本方法仍在同一个调用栈里继续执行。
        if (!_hostCreated) DoHost();

        try
        {
            var psi = new ProcessStartInfo { FileName = exe, UseShellExecute = false };
            foreach (var a in BuildLocalLaunchArgs(poolId)) psi.ArgumentList.Add(a);
            Process.Start(psi);
            if (_status != null)
                _status.Text = "✔ 已启动本机第二实例（127.0.0.1 · " + poolId + "），将自动加入房间";
        }
        catch (Exception ex)
        {
            if (_status != null) _status.Text = "✘ 本机双开失败：" + ex.Message;
        }
    }

    /// <summary>取当前进程的可执行文件；两条可靠来源都拿不到就返回 null。</summary>
    private static string ResolveCurrentExe()
    {
        string exe = null;
        try { exe = OS.GetExecutablePath(); } catch { }
        if (!IsUsableExe(exe))
        {
            try { exe = System.Environment.ProcessPath; } catch { exe = null; }
        }
        return IsUsableExe(exe) ? exe : null;
    }

    private static bool IsUsableExe(string path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    /// <summary>
    /// 第二实例参数：编辑器里运行时须带 --path 指向本项目；
    /// 「--」之后是 Godot 透传的自定义参数，OS.GetCmdlineArgs() 与
    /// OS.GetCmdlineUserArgs() 都能读到。导出版只需自定义参数。
    /// </summary>
    private static List<string> BuildLocalLaunchArgs(string poolId)
    {
        var args = new List<string>();
        if (OS.HasFeature("editor"))
        {
            args.Add("--path");
            args.Add(ProjectSettings.GlobalizePath("res://"));
        }
        args.Add("--");
        args.Add("--join-local=" + Uri.EscapeDataString(poolId ?? ""));
        return args;
    }

    /// <summary>第二实例入口：自动选同卡池 + 该卡池第一副合法牌组，直连 127.0.0.1。</summary>
    public void AutoJoinLocal(string poolId)
    {
        _autoJoinPoolId = poolId ?? "";
        CallDeferred(MethodName.DoAutoJoinLocal);
    }

    private void DoAutoJoinLocal()
    {
        int poolIdx = -1;
        for (int i = 1; i < _poolPicker.ItemCount; i++)
        {
            if (string.Equals(_poolPicker.GetItemText(i), _autoJoinPoolId,
                              StringComparison.OrdinalIgnoreCase))
            {
                poolIdx = i;
                break;
            }
        }
        if (poolIdx < 0)
        {
            if (_status != null) _status.Text = "✘ 本机双开：本机找不到卡池 [" + _autoJoinPoolId + "]";
            return;
        }

        _poolPicker.Select(poolIdx);
        RefreshDecks(poolIdx);
        if (_filteredDecks.Count == 0)
        {
            if (_status != null) _status.Text = "✘ 本机双开：卡池 [" + _autoJoinPoolId + "] 下没有合法牌组";
            return;
        }

        _deckPicker.Select(1);
        if (_manualIp != null) _manualIp.Text = "127.0.0.1";
        DoJoinManual();
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
            // 走统一的 TryJoinRoom：它会先校验卡池一致性再发起连接。
            join.Pressed += () => TryJoinRoom(captured);
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

        // 加入前先把「卡池 + 牌组」两道校验做掉：
        // 卡池没选/牌组没选根本不该发起连接；选了之后本地指纹一致才准进。
        if (_poolPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请先选择与房间相同的赛制卡池";
            return;
        }
        if (_deckPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请选择该卡池下的出战牌组";
            return;
        }

        string poolId = _poolPicker.GetItemText(_poolPicker.Selected);
        var myDeck = _filteredDecks[_deckPicker.Selected - 1];
        var info = new LanRoomInfo
        {
            Ip = ip,
            Port = LanDiscoveryService.MatchPort,
            HostName = ip,
            PoolId = poolId,
        };
        PendingJoinDeck = myDeck;
        Shutdown();
        OnJoinRoom?.Invoke(info);
    }

    /// <summary>本次加入使用的牌组（DeckManagementRoot 取走后清空）。</summary>
    public ShadowverseFan.KernelV3.Packs.ConstructedDeck PendingJoinDeck { get; private set; }


    /// <summary>
    /// 统一的加入入口：先校验「卡池相同 + 指纹一致 + 已选牌组」，再发起连接。
    /// 扫描列表与直连按钮都走这里，避免两条路径的校验不一致。
    /// </summary>
    private void TryJoinRoom(LanRoomInfo info)
    {
        if (info == null) return;

        if (_poolPicker.Selected <= 0 || _deckPicker.Selected <= 0)
        {
            if (_status != null) _status.Text = "✘ 请先在上方选择与房间一致的赛制卡池与出战牌组";
            return;
        }

        string myPoolId = _poolPicker.GetItemText(_poolPicker.Selected);

        // 广播包里的卡池与我们选的不一致 -> 直接拒绝，别让玩家进去才发现打不了。
        if (!string.IsNullOrEmpty(info.PoolId)
            && !string.Equals(myPoolId, info.PoolId, StringComparison.OrdinalIgnoreCase))
        {
            if (_status != null)
                _status.Text = "✘ 卡池不匹配：房间为 [" + info.PoolId + "]，你选的是 [" + myPoolId + "]";
            return;
        }

        // 指纹比对：把「改过卡包」的玩家挡在门外。
        if (!string.IsNullOrEmpty(info.PoolHashPrefix))
        {
            string localHash = ShadowverseFan.KernelV3.Net.PoolIntegrityHasher
                .ComputePoolHash(_store.GetPoolView(myPoolId));
            if (!string.Equals(localHash, info.PoolHashPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (_status != null)
                    _status.Text = "✘ 防作弊拦截：本地卡池 [" + myPoolId + "] 与房主不一致，拒绝加入";
                return;
            }
        }

        PendingJoinDeck = _filteredDecks[_deckPicker.Selected - 1];
        info.PoolId = myPoolId;
        Shutdown();
        OnJoinRoom?.Invoke(info);
    }

    private void Shutdown()
    {
        _discovery?.Stop();
    }

    public override void _ExitTree() => Shutdown();
}

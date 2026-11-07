using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ShadowverseFan.KernelV3.Battle;
using ShadowverseFan.KernelV3.Vm;

namespace ShadowverseFan.KernelV3.Net;

/// <summary>
/// 权威房主服务端。
///
/// 联机铁律：只有房主持有真实 BattleState / MatchRunner。
/// 客机只能发「意图」（我要打这张牌），不能发「结果」（我的 PP 现在是 7）。
/// 权威端收到意图后先验身份与合法性，再在本地真实状态上执行，
/// 然后针对每个观察者各裁一份快照发回去。
///
/// 这样即便作弊者把客户端内存改到 99 PP / 999 攻击力，
/// 他发来的 PlayCard 会在权威端因真实 PP 不足被拒，伤害也按权威端数值算。
/// </summary>
public sealed class AuthoritativeMatchHost : IDisposable
{
    private TcpListener _listener;
    private readonly Dictionary<int, TcpClient> _clients = new();   // playerId -> client
    private readonly Dictionary<int, int> _eventCursor = new();
    private readonly object _gate = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(int PlayerId, NetActionPacket Action)> _inbox = new();
    private bool _running;

    public BattleState State { get; private set; }
    public MatchRunner Runner { get; private set; }
    public int Port { get; private set; } = LanDiscoveryService.MatchPort;
    private string _poolId = "";

    /// <summary>状态推进后广播快照（UI 层可挂在这里刷新画面）。</summary>
    public event Action SnapshotPushed;

    /// <summary>客机已连入房间（握手校验通过）。参数仍是客机真实牌组，但不再代表可以开局。</summary>
    public event Action<ShadowverseFan.KernelV3.Packs.ConstructedDeck> ClientMatched;

    /// <summary>房间状态变更后触发；房主本地也收到同一份快照。</summary>
    public event Action<NetRoomStateDto> RoomStateChanged;

    /// <summary>房主发出 START 后触发；参数 = ResolvedFirstPlayer（0=房主先手，1=客机先手）。</summary>
    public event Action<int> StartMatchRequested;

    /// <summary>对局进行中或房间内客机断开连接时触发（176 号文档 三.3）。</summary>
    public event Action<int> ClientDisconnected;

    /// <summary>客机当前登记的真实牌组（房间内可随时更换，开局时以最新一份为准）。</summary>
    public ShadowverseFan.KernelV3.Packs.ConstructedDeck ClientDeck { get; private set; }

    private Packs.CardPool _hostPoolView;
    public ShadowverseFan.KernelV3.Packs.ConstructedDeck HostDeck { get; private set; }

    private NetRoomStateDto _room = new();
    /// <summary>当前房间快照（只读语义；每次变更都是新对象）。</summary>
    public NetRoomStateDto RoomState => Volatile.Read(ref _room);

    private bool _guestJoined;
    private bool _hostDeckValid;
    private bool _guestDeckValid;
    private string _hostDeckError = "";
    private string _guestDeckError = "";
    private bool _hostReady;
    private bool _guestReady;
    private FirstPlayerMode _firstMode = FirstPlayerMode.HostFirst;
    private string _statusNote = "等待客机加入房间";
    private bool _matchStarted;
    private int _frozenFirst;

    /// <summary>双方牌组合法、客机已连入、双方都已准备，且尚未开局。</summary>
    public bool CanStartMatch { get { lock (_gate) return CanStartMatchLocked(); } }

    /// <summary>按先手模式解析；Random 用端口 + 卡池 + 双方 deckId 的稳定哈希，不用随机源。</summary>
    public int ResolvedFirstPlayer { get { lock (_gate) return ComputeFirstPlayerLocked(); } }

    public void Start(BattleState state, MatchRunner runner, int port = LanDiscoveryService.MatchPort)
    {
        State = state;
        Runner = runner;
        Port = port;
        _hostPoolView = state?.Pool;
        _poolId = state?.Pool?.PoolId ?? _poolId;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;

        _ = Task.Run(AcceptLoop);
    }

    /// <summary>
    /// 房间阶段入口：只起监听与握手校验，**绝不建对局**。
    /// 房主牌组在这里即时校验一次；State/Runner 要等 StartMatchRequested
    /// 之后由 UI 调 Attach 接入。
    /// </summary>
    public void BeginHosting(Packs.CardPool poolView,
        ShadowverseFan.KernelV3.Packs.ConstructedDeck hostDeck,
        int port = LanDiscoveryService.MatchPort)
    {
        _hostPoolView = poolView;
        HostDeck = hostDeck;
        Port = port;
        _poolId = poolView?.PoolId ?? hostDeck?.PoolId ?? "";

        lock (_gate)
        {
            string err = ValidateDeck(hostDeck) ?? "";
            _hostDeckError = err;
            _hostDeckValid = err.Length == 0;
            _hostReady = false;
            _guestReady = false;
            _guestJoined = false;
            _guestDeckValid = false;
            _guestDeckError = "客机尚未连入";
            _firstMode = FirstPlayerMode.HostFirst;
            _matchStarted = false;
            _frozenFirst = 0;
            _statusNote = "房间已创建，等待客机加入";
            RebuildRoomLocked();
        }
        RoomStateChanged?.Invoke(RoomState);

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;

        _ = Task.Run(AcceptLoop);
    }

    // ---------------------------------------------------------------- 房间阶段

    /// <summary>牌组即时校验：返回 null 表示合法，否则返回单行中文原因。</summary>
    private string ValidateDeck(ShadowverseFan.KernelV3.Packs.ConstructedDeck deck)
    {
        if (deck == null) return "构筑为空";
        try
        {
            object dp = (object)State?.Pool ?? _hostPoolView;
            var dpool = dp as Packs.CardPool
                        ?? (dp as Packs.CardPoolRuntime)?.ToCardPoolView();
            var profile = Packs.DeckRuleResolver.ResolveAndValidate(deck, dpool);
            if (profile == null) return "无法解析牌组";
            return profile.IsValid ? null : string.Join("；", profile.Errors);
        }
        catch (Exception ex)
        {
            return "牌组校验失败：" + ex.Message;
        }
    }

    private static string OneLine(string s)
        => (s ?? "").Replace("\r", " ").Replace("\n", " ").Replace("|", " ").Trim();

    /// <summary>
    /// 行协议用竖线与换行分帧：JSON 里出现物理换行或竖线都会把消息切断。
    /// System.Text.Json 已把换行转义成 \n，这里再把竖线换成等价的 \u007C 转义，
    /// 反序列化后仍是原字符，但线路上不含分隔符。
    /// </summary>
    private static string WireJson(object value)
        => JsonSerializer.Serialize(value).Replace("|", "\\u007C");

    /// <summary>随机模式：端口 + 卡池 + 双方 deckId 的 SHA256 稳定哈希，双方无需交换随机数。</summary>
    private int ComputeFirstPlayerLocked()
    {
        if (_matchStarted) return _frozenFirst;
        switch (_firstMode)
        {
            case FirstPlayerMode.HostSecond: return 1;
            case FirstPlayerMode.Random:
            {
                string seed = Port + "|" + _poolId + "|"
                              + (HostDeck?.DeckId ?? "") + "|" + (ClientDeck?.DeckId ?? "");
                byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
                // 混合全部字节，避免只取首字节导致某些种子组合偏置。
                uint acc = 0x9E3779B9u;
                foreach (byte b in h) acc = (acc ^ b) * 16777619u;
                return (int)((acc >> 16) & 1u);
            }
            default: return 0;
        }
    }

    private bool CanStartMatchLocked()
        => !_matchStarted && _guestJoined
           && _hostDeckValid && _guestDeckValid
           && _hostReady && _guestReady;

    /// <summary>重算 Status，保证非法牌组时强制该方 Ready=false 且 CanStartMatch=false。</summary>
    private void RebuildRoomLocked()
    {
        if (!_hostDeckValid) _hostReady = false;
        if (!_guestDeckValid) _guestReady = false;

        string status = OneLine(_statusNote);
        if (!_hostDeckValid) status = "房主牌组不合法：" + OneLine(_hostDeckError);
        else if (!_guestJoined) status = OneLine(_statusNote);
        if (_guestJoined && !_guestDeckValid)
            status = (status.Length > 0 ? status + "；" : "") + "客机牌组不合法：" + OneLine(_guestDeckError);

        _room = new NetRoomStateDto
        {
            PoolId = OneLine(_poolId),
            HostDeckName = OneLine(HostDeck?.Name),
            GuestDeckName = _guestJoined ? OneLine(ClientDeck?.Name) : "",
            GuestJoined = _guestJoined,
            HostReady = _hostReady,
            GuestReady = _guestReady,
            FirstMode = _firstMode,
            ResolvedFirstPlayer = ComputeFirstPlayerLocked(),
            Status = status,
        };
    }

    /// <summary>把当前房间状态广播给双方（房主本地事件 + 所有已连入的客机）。</summary>
    private void PublishRoom()
    {
        NetRoomStateDto snapshot;
        List<TcpClient> targets;
        lock (_gate)
        {
            RebuildRoomLocked();
            snapshot = _room;
            targets = _clients.Values.ToList();
        }
        string line = "ROOM|" + WireJson(snapshot);
        foreach (var c in targets) _ = SendLine(c.GetStream(), line);
        RoomStateChanged?.Invoke(snapshot);
    }

    /// <summary>房主换牌组：立即重新校验，非法时写 Status 并强制取消准备。</summary>
    public void SetHostDeck(ShadowverseFan.KernelV3.Packs.ConstructedDeck deck)
    {
        lock (_gate)
        {
            HostDeck = deck;
            _hostDeckError = ValidateDeck(deck) ?? "";
            _hostDeckValid = _hostDeckError.Length == 0;
            _hostReady = false;   // 换牌组一律重新准备。
            _statusNote = "房主已更换牌组，请重新准备";
        }
        PublishRoom();
    }

    /// <summary>房主是唯一权威：只有本地调用能改先手模式，客机无此上行消息。</summary>
    public void SetFirstPlayerMode(FirstPlayerMode mode)
    {
        lock (_gate)
        {
            if (mode != FirstPlayerMode.HostFirst && mode != FirstPlayerMode.HostSecond
                && mode != FirstPlayerMode.Random) return;
            _firstMode = mode;
            _statusNote = "房主已设置先手模式";
        }
        PublishRoom();
    }

    /// <summary>房主准备状态；牌组不合法时拒绝置为已准备。</summary>
    public void SetHostReady(bool ready)
    {
        lock (_gate)
        {
            if (ready && !_hostDeckValid) return;
            _hostReady = ready;
            _statusNote = ready ? "房主已准备" : "房主取消准备";
        }
        PublishRoom();
    }

    /// <summary>开始对战：广播 START 并通知房主 UI 建立真实对局（Attach 仍由 UI 调）。</summary>
    public void StartMatch()
    {
        int first;
        List<TcpClient> targets;
        lock (_gate)
        {
            RebuildRoomLocked();
            if (!CanStartMatchLocked()) return;
            first = ComputeFirstPlayerLocked();
            _frozenFirst = first;
            _matchStarted = true;
            _room.ResolvedFirstPlayer = first;
            _room.Status = "对战开始，先手玩家 " + first;
            targets = _clients.Values.ToList();
        }
        string roomLine = "ROOM|" + WireJson(RoomState);
        string startLine = "START|" + first;
        foreach (var c in targets)
        {
            _ = SendLine(c.GetStream(), roomLine);
            _ = SendLine(c.GetStream(), startLine);
        }
        RoomStateChanged?.Invoke(RoomState);
        StartMatchRequested?.Invoke(first);
    }

    /// <summary>客机上行的新牌组：校验后覆盖 ClientDeck，非法则写 Status 并强制取消准备。</summary>
    private void ApplyGuestDeck(ShadowverseFan.KernelV3.Packs.ConstructedDeck deck)
    {
        lock (_gate)
        {
            string err = ValidateDeck(deck) ?? "";
            // 无论合法与否都记录最新牌组：合法时房主用最新牌组开局，
            // 非法时保留展示但不允许准备/开局。
            ClientDeck = deck;
            _guestDeckError = err;
            _guestDeckValid = err.Length == 0;
            _guestReady = false;
            _statusNote = err.Length == 0 ? "客机已更换牌组，请重新准备" : "";
        }
        PublishRoom();
    }

    /// <summary>客机上行的准备状态；牌组不合法时拒绝置为已准备。</summary>
    private void ApplyGuestReady(bool ready)
    {
        lock (_gate)
        {
            if (ready && (!_guestJoined || !_guestDeckValid)) return;
            _guestReady = ready;
            _statusNote = ready ? "客机已准备" : "客机取消准备";
        }
        PublishRoom();
    }

    /// <summary>开局后把真实对局接入权威端，此后快照广播照常工作。</summary>
    public void Attach(BattleState state, MatchRunner runner)
    {
        State = state;
        Runner = runner;
        BroadcastSnapshot();
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleClient(client));
            }
            catch { break; }
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        var stream = client.GetStream();
        var buf = new byte[8192];
        var sb = new StringBuilder();
        int myPlayerId = -1;

        try
        {
            while (_running && client.Connected)
            {
                int n = await stream.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));

                // 换行分帧：一条消息一行，避免粘包。
                while (true)
                {
                    string all = sb.ToString();
                    int nl = all.IndexOf('\n');
                    if (nl < 0) break;
                    string line = all.Substring(0, nl);
                    sb.Clear().Append(all.Substring(nl + 1));
                    if (line.Length == 0) continue;

                    var msg = TryParse(line);
                    if (msg == null) continue;

                    if (msg.Value.Kind == NetMsgKind.Hello)
                    {
                        int want = msg.Value.PlayerId;
                        if (want < 0 || want > 1 || _clients.ContainsKey(want))
                        {
                            await SendLine(stream, "ERR|seat taken");
                            continue;
                        }

                        // 铁律一：开局卡池指纹比对。
                        // 126 轮起**空哈希也直接拒绝**——允许跳过校验等于给改卡包留后门。
                        // 任何一方用记事本改本地 pack.json（把 1 费妖精改成 99/99、
                        // 或改 MatchRules 的生命值）都会让哈希对不上，当场拒绝。
                        if (string.IsNullOrEmpty(msg.Value.PoolHash))
                        {
                            await SendLine(stream, "ERR|握手缺少卡池指纹，拒绝连接");
                            continue;
                        }
                        {
                            // 等待期 State 还没建：哈希从 BeginHosting 备好的卡池视图取。
                            object poolSrc = (object)State?.Pool ?? _hostPoolView;
                            string mine = poolSrc switch
                            {
                                Packs.CardPoolRuntime rt => PoolIntegrityHasher.ComputePoolHash(rt),
                                Packs.CardPool cp => PoolIntegrityHasher.ComputePoolHash(cp),
                                _ => "",
                            };
                            if (!string.Equals(mine, msg.Value.PoolHash, StringComparison.OrdinalIgnoreCase))
                            {
                                await SendLine(stream, "ERR|卡池校验失败：双方卡包/规则不一致（" +
                                               mine.Substring(0, Math.Min(8, mine.Length)) + " vs " +
                                               msg.Value.PoolHash.Substring(0, Math.Min(8, msg.Value.PoolHash.Length)) + "）");
                                continue;
                            }
                        }

                        // 铁律一（续）：牌组合法性。客机自报的牌组必须在权威端
                        // 按本卡池规则重新校验 —— 塞满 10 张虹卡会被这里拦下。
                        // 126 轮起牌组也必填：没有对手牌组就没法开局。
                        if (msg.Value.Deck == null)
                        {
                            await SendLine(stream, "ERR|握手缺少牌组数据，拒绝连接");
                            continue;
                        }
                        string deckErr = ValidateDeck(msg.Value.Deck);
                        if (deckErr != null)
                        {
                            // 握手阶段保持旧行为：非法牌组直接拒绝连入。
                            await SendLine(stream, "ERR|牌组不合法：" + OneLine(deckErr));
                            continue;
                        }

                        // 首包必达：先把客机登记进 _clients 并回 ACK，再通知 UI、再广播 ROOM。
                        // 反过来会让 ClientMatched / ROOM 广播时客机还没注册，首包石沉大海。
                        myPlayerId = want;
                        lock (_gate)
                        {
                            _clients[myPlayerId] = client;
                            _eventCursor[myPlayerId] = 0;
                            ClientDeck = msg.Value.Deck;
                            _guestJoined = true;
                            _guestDeckError = "";
                            _guestDeckValid = true;
                            _guestReady = false;
                            _statusNote = "客机已连入房间";
                        }
                        await SendLine(stream, "ACK|" + myPlayerId);
                        // HELLO 之后必须发一次 ROOM，让客机进房间界面；此处不建对局、不推快照。
                        PublishRoom();
                        ClientMatched?.Invoke(msg.Value.Deck);
                    }
                    else if (msg.Value.Kind == NetMsgKind.RoomDeck && myPlayerId >= 0)
                    {
                        ApplyGuestDeck(msg.Value.Deck);
                    }
                    else if (msg.Value.Kind == NetMsgKind.RoomReady && myPlayerId >= 0)
                    {
                        ApplyGuestReady(msg.Value.PlayerId != 0);
                    }
                    else if (msg.Value.Kind == NetMsgKind.Action && myPlayerId >= 0)
                    {
                        _inbox.Enqueue((myPlayerId, msg.Value.Action));
                    }
                }
            }
        }
        catch { }
        finally
        {
            try { client.Close(); } catch { }
            if (myPlayerId >= 0)
            {
                lock (_gate)
                {
                    _clients.Remove(myPlayerId);
                    _eventCursor.Remove(myPlayerId);
                    _guestJoined = false;
                    _guestReady = false;
                    _guestDeckValid = false;
                    _guestDeckError = "客机已断开连接";
                    ClientDeck = null;
                    _statusNote = "客机已离开房间";
                }
                PublishRoom();
                ClientDisconnected?.Invoke(myPlayerId);
            }
        }
    }

    /// <summary>由房主主线程在 _Process 中轮询调用：主线程单线程执行指令并广播快照，杜绝跨线程并发竞态（176/177 号文档 一.1）。返回本帧是否有指令被处理。</summary>
    public bool PumpActionsOnMainThread()
    {
        bool any = false;
        while (_inbox.TryDequeue(out var item))
        {
            ApplyAction(item.PlayerId, item.Action);
            any = true;
        }
        return any;
    }

    /// <summary>权威端执行一条意图。合法性由 MatchRunner 判定，不信任客机自报。</summary>
    private void ApplyAction(int playerId, NetActionPacket a)
    {
        if (a == null || State == null || Runner == null) return;
        // 校验选择归属：只有当前需要作答的玩家才允许提交/取消选择（177 号文档 二.1）
        if (a.Kind is NetActionKind.SubmitChoice or NetActionKind.CancelPending)
        {
            if (Runner.PendingChoice == null || Runner.PendingChoice.PlayerId != playerId) return;
        }
        else if (State.ActivePlayer != playerId) return;

        switch (a.Kind)
        {
            case NetActionKind.PlayCard:
            {
                var e = FindEntity(playerId, a.EntityId);
                if (e != null) Runner.PlayCard(e);
                break;
            }
            case NetActionKind.Attack:
            {
                var atk = FindBoardEntity(playerId, a.EntityId);
                var def = FindAnyEntity(a.TargetId);
                if (atk != null && def != null) Runner.Attack(atk, def);
                break;
            }
            case NetActionKind.Evolve:
            {
                var e = FindBoardEntity(playerId, a.EntityId);
                if (e != null) Runner.Evolve(e, a.Flag);
                break;
            }
            case NetActionKind.EngageAmulet:
            {
                var e = FindBoardEntity(playerId, a.EntityId);
                if (e != null) Runner.EngageAmulet(e);
                break;
            }
            case NetActionKind.UseExtraPp:
                Runner.UseExtraPp();
                break;
            case NetActionKind.CancelPending:
                // 预选阶段取消（还没扣费）也要走权威端，否则本地取消了、
                // 权威端还停在等选择，两边就此卡死。
                Runner.CancelPendingChoice();
                break;
            case NetActionKind.SubmitChoice:
                Runner.SubmitChoice(a.ChoiceId, a.Selected);
                break;
            case NetActionKind.EndTurn:
                Runner.EndTurn();
                break;
            case NetActionKind.FuseCards:
            {
                if (a.EntityId >= 0 && a.Selected != null && a.Selected.Length > 0)
                {
                    var baseCard = FindEntity(playerId, a.EntityId);
                    if (baseCard != null && baseCard.PlayerId == playerId)
                    {
                        var mats = a.Selected.Select(id => FindEntity(playerId, id)).Where(m => m != null).ToList();
                        Runner.FuseCards(baseCard, mats);
                    }
                }
                break;
            }
        }

        BroadcastSnapshot();
    }

    private RuntimeEntity FindEntity(int playerId, int entityId)
    {
        var p = State.Players[playerId];
        return p.Hand.FirstOrDefault(x => x.EntityId == entityId)
               ?? p.Board.FirstOrDefault(x => x.EntityId == entityId);
    }

    private RuntimeEntity FindBoardEntity(int playerId, int entityId)
        => State.Players[playerId].Board.FirstOrDefault(x => x.EntityId == entityId);

    /// <summary>按 EntityId 在双方战场与主战者里找目标（Attack 需要实体而非 Id）。</summary>
    private RuntimeEntity FindAnyEntity(int entityId)
    {
        for (int pid = 0; pid < 2; pid++)
        {
            var p = State.Players[pid];
            var hit = p.Board.FirstOrDefault(x => x.EntityId == entityId);
            if (hit != null) return hit;
            if (p.Leader != null && p.Leader.EntityId == entityId) return p.Leader;
        }
        return null;
    }

    /// <summary>给每个观察者各推一份裁剪快照。</summary>
    public void BroadcastSnapshot()
    {
        // 开局前 State / Runner 还是 null：BeginHosting 尚未 Attach，
        // 但后台网络线程已经收到 HELLO 并调到这里。此时推快照必然
        // 在 FogOfWarSanitizer 里空引用，异常冒到 HandleClient 的 finally
        // 把刚收到 ACK 的客机直接踢掉。等主线程 Attach 后会自动再推一次。
        if (State == null || Runner == null) return;
        lock (_gate)
        {
            foreach (var pid in _clients.Keys.ToList()) PushSnapshotTo(pid);
        }
        SnapshotPushed?.Invoke();
    }

    private void PushSnapshotTo(int playerId)
    {
        if (State == null || Runner == null) return;
        if (!_clients.TryGetValue(playerId, out var client) || client == null) return;
        int from = _eventCursor.TryGetValue(playerId, out var c) ? c : 0;

        var snap = FogOfWarSanitizer.BuildForViewer(State, Runner, playerId, from);
        _eventCursor[playerId] = State.EventLog.Count;

        try
        {
            string json = JsonSerializer.Serialize(snap);
            _ = SendLine(client.GetStream(), "SNAP|" + json);
        }
        catch { }
    }

    /// <summary>一条消息一次同步写入；SendGate 串行化，防止两条消息在字节流上交叉。</summary>
    private static Task SendLine(NetworkStream s, string line)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(line + "\n");
            lock (SendGate) s.Write(b, 0, b.Length);
        }
        catch { }
        return Task.CompletedTask;
    }

    private static readonly object SendGate = new();

    /// <summary>解析出的一条消息。握手需要额外携带卡池哈希与牌组，故用结构体而非元组。</summary>
    private readonly struct ParsedMsg
    {
        public ParsedMsg(NetMsgKind kind) { Kind = kind; PlayerId = -1; Action = null; PoolHash = null; Deck = null; }
        public NetMsgKind Kind { get; }
        public int PlayerId { get; init; }
        public NetActionPacket Action { get; init; }
        public string PoolHash { get; init; }
        public Packs.ConstructedDeck Deck { get; init; }
    }

    private static ParsedMsg? TryParse(string line)
    {
        try
        {
            if (line.StartsWith("HELLO|"))
            {
                // 格式：HELLO|<seat>|<poolHash>|<deckJson>
                var parts = line.Split('|');
                int pid = parts.Length > 1 && int.TryParse(parts[1], out var v) ? v : -1;
                string hash = parts.Length > 2 ? parts[2] : null;
                Packs.ConstructedDeck deck = null;
                if (parts.Length > 3 && parts[3].Length > 0)
                {
                    // 牌组名可能含竖线：把后续片段拼回完整 JSON，避免被分隔符切断。
                    string deckJson = string.Join("|", parts.Skip(3));
                    try { deck = JsonSerializer.Deserialize<Packs.ConstructedDeck>(deckJson); }
                    catch { deck = null; }
                }
                return new ParsedMsg(NetMsgKind.Hello) { PlayerId = pid, PoolHash = hash, Deck = deck };
            }
            if (line.StartsWith("ACTION|"))
            {
                var a = JsonSerializer.Deserialize<NetActionPacket>(line.Substring(7));
                return new ParsedMsg(NetMsgKind.Action) { Action = a };
            }
            if (line.StartsWith("DECK|"))
            {
                var deck = JsonSerializer.Deserialize<Packs.ConstructedDeck>(line.Substring(5));
                return new ParsedMsg(NetMsgKind.RoomDeck) { Deck = deck };
            }
            if (line.StartsWith("READY|"))
            {
                int ready = line.Length > 6 && line.Substring(6).Trim() == "1" ? 1 : 0;
                return new ParsedMsg(NetMsgKind.RoomReady) { PlayerId = ready };
            }
        }
        catch { }
        return null;
    }
    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
        lock (_gate)
        {
            foreach (var c in _clients.Values)
            {
                try { c.Close(); } catch { }
            }
            _clients.Clear();
        }
    }
}

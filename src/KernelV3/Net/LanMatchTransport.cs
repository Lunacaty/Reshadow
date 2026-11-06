using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    private bool _running;

    public BattleState State { get; private set; }
    public MatchRunner Runner { get; private set; }
    public int Port { get; private set; } = LanDiscoveryService.MatchPort;

    /// <summary>状态推进后广播快照（UI 层可挂在这里刷新画面）。</summary>
    public event Action SnapshotPushed;

    /// <summary>客机握手全部通过（卡池一致 + 牌组合法）。参数是客机的真实牌组。</summary>
    public event Action<ShadowverseFan.KernelV3.Packs.ConstructedDeck> ClientMatched;

    /// <summary>客机握手通过后带上的真实牌组（房主据此开局）。</summary>
    public ShadowverseFan.KernelV3.Packs.ConstructedDeck ClientDeck { get; private set; }

    private Packs.CardPool _hostPoolView;
    public ShadowverseFan.KernelV3.Packs.ConstructedDeck HostDeck { get; private set; }

    public void Start(BattleState state, MatchRunner runner, int port = LanDiscoveryService.MatchPort)
    {
        State = state;
        Runner = runner;
        Port = port;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;

        _ = Task.Run(AcceptLoop);
    }

    /// <summary>
    /// 126 轮：房主**先等客机**再开局。BeginHosting 只起监听与握手校验，
    /// State/Runner 等客机通过校验后由房主 UI 创建（Attach 时才接入）。
    /// 握手哈希用本方卡池视图计算。
    /// </summary>
    public void BeginHosting(Packs.CardPool poolView,
        ShadowverseFan.KernelV3.Packs.ConstructedDeck hostDeck,
        int port = LanDiscoveryService.MatchPort)
    {
        _hostPoolView = poolView;
        HostDeck = hostDeck;
        Port = port;

        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        _running = true;

        _ = Task.Run(AcceptLoop);
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
                        {
                            try
                            {
                                object dp = (object)State?.Pool ?? _hostPoolView;
                                var dpool = dp as Packs.CardPool
                                        ?? (dp as Packs.CardPoolRuntime)?.ToCardPoolView();

                                // ResolveAndValidate **不抛异常** —— 它把所有不合规项写进
                                // profile.Errors 然后正常返回。只 try/catch 等于完全没校验：
                                // 客机提交 10 张同名卡、跨职业混组、甚至 3 张牌的牌组都能进来。
                                var profile = Packs.DeckRuleResolver.ResolveAndValidate(msg.Value.Deck, dpool);
                                if (profile == null || !profile.IsValid)
                                {
                                    string why = profile == null
                                        ? "无法解析牌组"
                                        : string.Join("；", profile.Errors);
                                    await SendLine(stream, "ERR|牌组不合法：" + why);
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                await SendLine(stream, "ERR|牌组校验失败：" + ex.Message);
                                continue;
                            }
                        }

                        // 全部通过：记住客机牌组，供「真实双人开局」使用。
                        ClientDeck = msg.Value.Deck;
                        ClientMatched?.Invoke(msg.Value.Deck);

                        myPlayerId = want;
                        lock (_gate) _clients[myPlayerId] = client;
                        _eventCursor[myPlayerId] = 0;
                        await SendLine(stream, "ACK|" + myPlayerId);
                        PushSnapshotTo(myPlayerId);
                    }                    else if (msg.Value.Kind == NetMsgKind.Action && myPlayerId >= 0)
                    {
                        ApplyAction(myPlayerId, msg.Value.Action);
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
                lock (_gate) _clients.Remove(myPlayerId);
                _eventCursor.Remove(myPlayerId);
            }
        }
    }

    /// <summary>权威端执行一条意图。合法性由 MatchRunner 判定，不信任客机自报。</summary>
    private void ApplyAction(int playerId, NetActionPacket a)
    {
        if (a == null || State == null || Runner == null) return;
        if (State.ActivePlayer != playerId && a.Kind != NetActionKind.SubmitChoice) return;

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
        lock (_gate)
        {
            foreach (var pid in _clients.Keys.ToList()) PushSnapshotTo(pid);
        }
        SnapshotPushed?.Invoke();
    }

    private void PushSnapshotTo(int playerId)
    {
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

    private static async Task SendLine(NetworkStream s, string line)
    {
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(line + "\n");
            await s.WriteAsync(b, 0, b.Length);
        }
        catch { }
    }

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
                    try { deck = JsonSerializer.Deserialize<Packs.ConstructedDeck>(parts[3]); }
                    catch { deck = null; }
                }
                return new ParsedMsg(NetMsgKind.Hello) { PlayerId = pid, PoolHash = hash, Deck = deck };
            }
            if (line.StartsWith("ACTION|"))
            {
                var a = JsonSerializer.Deserialize<NetActionPacket>(line.Substring(7));
                return new ParsedMsg(NetMsgKind.Action) { Action = a };
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

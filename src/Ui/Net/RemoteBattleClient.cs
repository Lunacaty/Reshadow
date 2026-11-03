using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ShadowverseFan.KernelV3.Net;

namespace ShadowverseFan.Ui.Net;

/// <summary>
/// 客机端：把权威端下发的裁剪快照映射成本地只读镜像，驱动战斗界面演出。
///
/// 它**不持有真实 BattleState**：所有数值都来自快照，
/// 所以本地改内存毫无意义 —— 下一次快照推送就把改动覆盖回去了。
/// 玩家操作也不在本地结算，只发 NetActionPacket 给权威端。
/// </summary>
public sealed class RemoteBattleClient : IDisposable
{
    private TcpClient _client;
    private NetworkStream _stream;
    private readonly StringBuilder _buf = new();
    private bool _running;

    public int MyPlayerId { get; private set; } = -1;
    public bool Connected => _client?.Connected == true;

    /// <summary>最近一次收到的快照。</summary>
    public NetStateSnapshot Latest { get; private set; }

    /// <summary>收到新快照（UI 层据此刷新棋盘）。</summary>
    public event Action<NetStateSnapshot> SnapshotReceived;

    /// <summary>握手完成。</summary>
    public event Action<int> HandshakeCompleted;

    /// <summary>被服务端拒绝或断线。</summary>
    public event Action<string> Failed;

    public async Task<bool> ConnectAsync(string ip, int port, int seat)
    {
        try
        {
            _client = new TcpClient();
            await _client.ConnectAsync(ip, port);
            _stream = _client.GetStream();
            _running = true;

            await SendLine("HELLO|" + seat);
            _ = Task.Run(ReceiveLoop);
            return true;
        }
        catch (Exception ex)
        {
            Failed?.Invoke("连接失败：" + ex.Message);
            return false;
        }
    }

    private async Task ReceiveLoop()
    {
        var raw = new byte[16384];
        try
        {
            while (_running && _client != null && _client.Connected)
            {
                int n = await _stream.ReadAsync(raw, 0, raw.Length);
                if (n <= 0) break;
                _buf.Append(Encoding.UTF8.GetString(raw, 0, n));

                while (true)
                {
                    string all = _buf.ToString();
                    int nl = all.IndexOf('\n');
                    if (nl < 0) break;
                    string line = all.Substring(0, nl);
                    _buf.Clear().Append(all.Substring(nl + 1));
                    if (line.Length == 0) continue;
                    HandleLine(line);
                }
            }
        }
        catch { }
        finally
        {
            _running = false;
            Failed?.Invoke("与服务端断开");
        }
    }

    private void HandleLine(string line)
    {
        if (line.StartsWith("ACK|"))
        {
            if (int.TryParse(line.Substring(4), out int pid))
            {
                MyPlayerId = pid;
                HandshakeCompleted?.Invoke(pid);
            }
            return;
        }
        if (line.StartsWith("ERR|"))
        {
            Failed?.Invoke(line.Substring(4));
            return;
        }
        if (line.StartsWith("SNAP|"))
        {
            try
            {
                var snap = JsonSerializer.Deserialize<NetStateSnapshot>(line.Substring(5));
                if (snap == null) return;
                Latest = snap;
                SnapshotReceived?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Failed?.Invoke("快照解析失败：" + ex.Message);
            }
        }
    }

    // ---------------------------------------------------------------- 上行意图

    public Task SendAction(NetActionPacket a) => SendLine("ACTION|" + JsonSerializer.Serialize(a));

    public Task PlayCard(int entityId)
        => SendAction(new NetActionPacket { Kind = NetActionKind.PlayCard, EntityId = entityId });

    public Task Attack(int attackerId, int targetId)
        => SendAction(new NetActionPacket { Kind = NetActionKind.Attack, EntityId = attackerId, TargetId = targetId });

    public Task Evolve(int entityId, bool super)
        => SendAction(new NetActionPacket { Kind = NetActionKind.Evolve, EntityId = entityId, Flag = super });

    public Task EngageAmulet(int entityId)
        => SendAction(new NetActionPacket { Kind = NetActionKind.EngageAmulet, EntityId = entityId });

    public Task EndTurn()
        => SendAction(new NetActionPacket { Kind = NetActionKind.EndTurn });

    public Task UseExtraPp()
        => SendAction(new NetActionPacket { Kind = NetActionKind.UseExtraPp });

    public Task CancelPending()
        => SendAction(new NetActionPacket { Kind = NetActionKind.CancelPending });

    public Task SubmitChoice(int choiceId, params int[] selected)
        => SendAction(new NetActionPacket { Kind = NetActionKind.SubmitChoice, ChoiceId = choiceId, Selected = selected });

    private async Task SendLine(string line)
    {
        if (_stream == null) return;
        try
        {
            byte[] b = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(b, 0, b.Length);
        }
        catch { }
    }

    public void Dispose()
    {
        _running = false;
        try { _client?.Close(); } catch { }
        _client = null;
        _stream = null;
    }
}

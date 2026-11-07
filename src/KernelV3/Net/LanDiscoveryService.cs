using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ShadowverseFan.KernelV3.Net;

/// <summary>局域网 / Radmin VPN 网络工具。</summary>
public static class LanNetworkHelper
{
    /// <summary>
    /// 枚举本机可用 IPv4，Radmin VPN（26.x.x.x）排最前，
    /// 方便玩家一键复制给远程好友直连。
    /// </summary>
    public static List<(string Ip, string Tag)> GetLocalIpv4Addresses()
    {
        var list = new List<(string Ip, string Tag)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.")) continue;

                    bool isRadminNic =
                        (ni.Description ?? "").Contains("Radmin", StringComparison.OrdinalIgnoreCase)
                        || (ni.Name ?? "").Contains("Radmin", StringComparison.OrdinalIgnoreCase);

                    string tag = ip.StartsWith("26.") || isRadminNic ? "Radmin VPN（远程直连）"
                               : ip.StartsWith("192.168.") || ip.StartsWith("10.") || ip.StartsWith("172.")
                                 ? "局域网（" + ni.Name + "）"
                               : ni.Name;
                    list.Add((ip, tag));
                }
            }
        }
        catch { }

        return list.OrderByDescending(x => x.Tag.StartsWith("Radmin"))
                   .ThenBy(x => x.Ip, StringComparer.Ordinal)
                   .ToList();
    }
}

/// <summary>被发现的一个房间。</summary>
public sealed class LanRoomInfo
{
    public string HostName { get; set; } = "";
    public string Ip { get; set; } = "";
    public int Port { get; set; }
    public string PoolId { get; set; } = "";
    public string PoolHashPrefix { get; set; } = "";
    public long LastSeenMs { get; set; }
}

/// <summary>
/// UDP 27651 局域网房间广播。
///
/// 房主每秒广播一次自己的存在，客机监听并维护房间列表。
/// 广播包只有房名/IP/端口/卡池 ID/哈希前缀 —— 不含任何牌组或状态信息。
/// </summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const int DiscoveryPort = 27651;
    public const int MatchPort = 27650;

    private UdpClient _udp;
    private bool _broadcasting;
    public bool IsBroadcasting => _broadcasting;
    private System.Threading.Timer _timer;

    /// <summary>发现到的房间（按 IP:Port 去重）。</summary>
    public Dictionary<string, LanRoomInfo> Rooms { get; } = new();

    public event Action RoomsChanged;

    /// <summary>作为房主开始广播。</summary>
    public void StartBroadcast(string hostName, int port, string poolId, string poolHash)
    {
        Stop();
        try
        {
            _udp = new UdpClient { EnableBroadcast = true };
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            _broadcasting = true;

            string payload = BuildPacket(hostName, port, poolId, poolHash);
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            var eps = GetBroadcastEndpoints(DiscoveryPort);

            _timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    if (_udp != null)
                    {
                        foreach (var ep in eps)
                        {
                            try { _udp.Send(bytes, bytes.Length, ep); } catch { }
                        }
                    }
                }
                catch { }
            }, null, 0, 1000);
        }
        catch { Stop(); }
    }

    /// <summary>获取所有活动网卡（物理、Wi-Fi、Radmin VPN、虚拟交换机）的定向子网广播终结点，杜绝多网卡环境下广播丢失（176 号文档 二.5）。</summary>
    private static List<IPEndPoint> GetBroadcastEndpoints(int port)
    {
        var eps = new List<IPEndPoint> { new(IPAddress.Broadcast, port) };
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                    byte[] ip = ua.Address.GetAddressBytes();
                    byte[] mask = ua.IPv4Mask.GetAddressBytes();
                    byte[] bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                    var ep = new IPEndPoint(new IPAddress(bcast), port);
                    if (!eps.Contains(ep)) eps.Add(ep);
                }
            }
        }
        catch { }
        return eps;
    }

    /// <summary>作为客机开始监听房间。</summary>
    public void StartListening()
    {
        Stop();
        try
        {
            _udp = new UdpClient();
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
            _broadcasting = false;

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                while (_udp != null)
                {
                    try
                    {
                        var res = await _udp.ReceiveAsync();
                        var info = ParsePacket(Encoding.UTF8.GetString(res.Buffer), res.RemoteEndPoint.Address.ToString());
                        if (info == null) continue;
                        info.LastSeenMs = Environment.TickCount64;
                        Rooms[info.Ip + ":" + info.Port] = info;
                        PruneStale();
                        RoomsChanged?.Invoke();
                    }
                    catch { break; }
                }
            });
        }
        catch { Stop(); }
    }

    private static string BuildPacket(string host, int port, string poolId, string hash)
        => "RESHADOW1|" + host + "|" + port + "|" + poolId + "|" + hash;

    private static LanRoomInfo ParsePacket(string raw, string fromIp)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("RESHADOW1|")) return null;
        var parts = raw.Split('|');
        if (parts.Length < 5) return null;
        if (!int.TryParse(parts[2], out int port)) return null;
        return new LanRoomInfo
        {
            HostName = parts[1],
            Ip = fromIp,
            Port = port,
            PoolId = parts[3],
            PoolHashPrefix = parts[4],
        };
    }

    private void PruneStale()
    {
        long now = Environment.TickCount64;
        foreach (var key in Rooms.Where(kv => now - kv.Value.LastSeenMs > 5000).Select(kv => kv.Key).ToList())
            Rooms.Remove(key);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        try { _udp?.Close(); } catch { }
        _udp = null;
        _broadcasting = false;
    }

    public void Dispose() => Stop();
}

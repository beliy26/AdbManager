using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AdbManager.Services;

/// <summary>
/// Поиск устройств с отладкой по сети (идея ЧистогоТВ): перебор домашних подсетей /24 на открытый
/// порт adb 5555. Адаптеры VPN и виртуальных машин пропускаются — устройства там нет.
/// </summary>
public static partial class NetworkScanner
{
    [GeneratedRegex("vpn|amnezia|wireguard|wintun|tap-|tun|openvpn|outline|hiddify|happ|v2ray|clash|hyper-v|vethernet|wsl|virtualbox|vmware|docker|zerotier|tailscale|radmin|hamachi",
        RegexOptions.IgnoreCase)]
    private static partial Regex VirtualNic();

    /// <summary>Префиксы домашних сетей (a.b.c) на активных физических адаптерах и собственные адреса.</summary>
    private static (HashSet<string> Prefixes, HashSet<string> Own) GetLocalNetworks()
    {
        var prefixes = new HashSet<string>();
        var own = new HashSet<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp) continue;
            if (VirtualNic().IsMatch($"{nic.Name} {nic.Description}")) continue;

            foreach (var addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var b = addr.Address.GetAddressBytes();
                var isPrivate = b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
                if (!isPrivate) continue;
                prefixes.Add($"{b[0]}.{b[1]}.{b[2]}");
                own.Add(addr.Address.ToString());
            }
        }
        return (prefixes, own);
    }

    /// <summary>Адреса с открытым портом в локальных подсетях.</summary>
    public static async Task<List<string>> ScanAsync(int port = 5555, int timeoutMs = 1500, CancellationToken ct = default)
    {
        var (prefixes, own) = GetLocalNetworks();
        var targets = prefixes.SelectMany(p => Enumerable.Range(1, 254).Select(i => $"{p}.{i}"))
                              .Where(ip => !own.Contains(ip))
                              .ToList();

        var found = new List<string>();
        var probes = targets.Select(async ip =>
        {
            using var client = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            try
            {
                await client.ConnectAsync(IPAddress.Parse(ip), port, timeout.Token);
                lock (found) found.Add(ip);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException) { }
        });
        await Task.WhenAll(probes);
        ct.ThrowIfCancellationRequested();

        return found.OrderBy(ip => Version.TryParse(ip, out var v) ? v : new Version()).ToList();
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    /// <summary>
    /// Если Windows ведёт трафик к адресу через VPN/виртуальный адаптер, возвращает его имя
    /// (тогда устройство в домашней сети недоступно).
    /// </summary>
    public static string? GetVirtualRouteAdapter(string host)
    {
        try
        {
            var ipText = host.Contains(':') ? host[..host.LastIndexOf(':')] : host;
            if (!IPAddress.TryParse(ipText, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
            if (GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), out var index) != 0) return null;

            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
            {
                try { return n.GetIPProperties().GetIPv4Properties()?.Index == index; }
                catch (NetworkInformationException) { return false; }
            });
            if (nic is null) return null;
            return VirtualNic().IsMatch($"{nic.Name} {nic.Description}") || nic.NetworkInterfaceType is NetworkInterfaceType.Ppp
                ? nic.Name
                : null;
        }
        catch
        {
            return null;
        }
    }
}

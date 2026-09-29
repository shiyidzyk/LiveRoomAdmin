using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiveRoomAdmin.Services
{
    public static class ServerDiscovery
    {
        public static async Task<string> FindAsync(int port = 3000, int timeoutMs = 500)
        {
            var subnets = GetSubnets();
            if (subnets.Count == 0) return null;
            var ips = new List<string>();
            foreach (var sub in subnets)
                for (int i = 1; i <= 254; i++)
                    ips.Add($"{sub}.{i}");
            using var cts = new CancellationTokenSource();
            var sem = new SemaphoreSlim(80);
            var tasks = ips.Select(async ip =>
            {
                try { await sem.WaitAsync(cts.Token); }
                catch (OperationCanceledException) { return null; }
                try { return await TryConnectAsync(ip, port, timeoutMs); }
                finally { try { sem.Release(); } catch { } }
            });
            var all = tasks.ToList();
            string found = null;
            while (all.Count > 0)
            {
                var done = await Task.WhenAny(all);
                all.Remove(done);
                string ip = null;
                try { ip = await done; } catch { }
                if (ip != null && found == null)
                {
                    found = ip;
                    cts.Cancel();
                }
            }
            try { await Task.WhenAll(all); } catch { }
            sem.Dispose();
            return found;
        }

        public static List<string> GetSubnets()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var desc = ni.Description.ToLower();
                        if (desc.Contains("vmware") || desc.Contains("virtualbox") ||
                            desc.Contains("hyper-v") || desc.Contains("loopback") ||
                            desc.Contains("docker") || desc.Contains("vpn") ||
                            desc.Contains("tap") || desc.Contains("tun")) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            var ip = ua.Address.ToString();
                            if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                            var p = ip.Split('.');
                            if (p.Length != 4) continue;
                            var sub = $"{p[0]}.{p[1]}.{p[2]}";
                            if (!list.Contains(sub)) list.Add(sub);
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private static async Task<string> TryConnectAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                using var tcp = new TcpClient();
                var connect = tcp.ConnectAsync(ip, port);
                var done = await Task.WhenAny(connect, Task.Delay(timeoutMs, cts.Token));
                if (done != connect)
                {
                    try { await connect; } catch { }
                    return null;
                }
                try { await connect; } catch { return null; }
                if (!tcp.Connected) return null;
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs + 300) };
                var resp = await http.GetAsync($"http://{ip}:{port}/api/room-state", cts.Token);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                if (body.Contains("room") || body.Contains("state") || body.Contains("code"))
                    return ip;
            }
            catch { }
            return null;
        }
    }
}

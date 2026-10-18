using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiveRoomAdmin.Services
{
    /// <summary>
    /// 集成式直播服务器管理器：负责拉起/停止 mediamtx + node + ffmpeg 核心业务。
    /// 默认不运行，由首页"开始直播"按钮触发。
    /// </summary>
    public static class LiveServer
    {
        public const int PortNode = 3000;
        public const int PortRtmp = 1935;
        public const int PortHls = 8888;
        public const int PortWebRtc = 8889;
        public const int PortRtsp = 8554;

        private static Process _mtx;
        private static Process _node;
        // 多直播间：每个房间一个 ffmpeg 转码进程（rtmp -> rtsp lowlatency）
        private static readonly Dictionary<string, Process> _ffmpegs = new Dictionary<string, Process>();
        private static Thread _ffmpegLoop;
        private static volatile bool _running;
        private static readonly object _lock = new object();
        private static readonly ConcurrentQueue<string> _logQueue = new ConcurrentQueue<string>();

        public static string BaseDir => Path.GetDirectoryName(typeof(App).Assembly.Location) ?? ".";
        public static string BinDir => Path.Combine(BaseDir, "bins");
        public static string ServerDir => Path.Combine(BaseDir, "server");
        public static string LogFile => Path.Combine(ServerDir, "server.log");

        public static bool IsRunning
        {
            get
            {
                lock (_lock) return _running && (_mtx != null || _node != null);
            }
        }

        public static event Action<string> LogLine;

        private static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            try { File.AppendAllText(LogFile, line + "\r\n"); } catch { }
            _logQueue.Enqueue(line);
            while (_logQueue.Count > 300) _logQueue.TryDequeue(out _);
            try { LogLine?.Invoke(line); } catch { }
        }

        /// <summary>读取最近 N 行日志</summary>
        public static string TailLog(int n = 200)
        {
            var arr = _logQueue.ToArray();
            var start = Math.Max(0, arr.Length - n);
            return string.Join("\r\n", arr, start, arr.Length - start);
        }

        /// <summary>本机局域网 IPv4（过滤虚拟网卡）</summary>
        public static string GetLocalIPv4()
        {
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        var desc = ni.Description.ToLower();
                        if (desc.Contains("vmware") || desc.Contains("virtualbox") || desc.Contains("hyper-v") ||
                            desc.Contains("loopback") || desc.Contains("docker") || desc.Contains("vpn") ||
                            desc.Contains("tap") || desc.Contains("tun") || desc.Contains("bluetooth")) continue;
                        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        {
                            if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            var ip = ua.Address.ToString();
                            if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                            return ip;
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return "127.0.0.1";
        }

        /// <summary>启动核心业务（mediamtx + node + ffmpeg 循环）</summary>
        public static string Start()
        {
            lock (_lock)
            {
                if (_running) return "服务已在运行";
                if (!File.Exists(Path.Combine(BinDir, "mediamtx.exe")) ||
                    !File.Exists(Path.Combine(BinDir, "node.exe")) ||
                    !File.Exists(Path.Combine(BinDir, "ffmpeg.exe")))
                {
                    return "缺少核心程序文件（bins/ 目录不完整）";
                }
                if (!File.Exists(Path.Combine(ServerDir, "server.js")))
                {
                    return "缺少 server.js";
                }

                _running = true;
                try { File.AppendAllText(LogFile, "\r\n"); } catch { }
                Log("=== 集成服务启动 ===");
                Log("版本: " + VersionInfo.CurrentVersion);
                Log("bins: " + BinDir);
                Log("server: " + ServerDir);

                // 1) mediamtx
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Path.Combine(BinDir, "mediamtx.exe"),
                        WorkingDirectory = ServerDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(Path.Combine(ServerDir, "mediamtx.yml"));
                    _mtx = Process.Start(psi);
                    AttachReader(_mtx, "mtx");
                    Log($"mediamtx 已启动 (RTMP {PortRtmp} / HLS {PortHls} / WebRTC {PortWebRtc} / RTSP {PortRtsp})");
                }
                catch (Exception ex) { Log("mediamtx 启动失败: " + ex.Message); }

                // 2) node
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = Path.Combine(BinDir, "node.exe"),
                        WorkingDirectory = ServerDir,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    };
                    psi.ArgumentList.Add(Path.Combine(ServerDir, "server.js"));
                    _node = Process.Start(psi);
                    AttachReader(_node, "node");
                    Log($"node 已启动 (HTTP {PortNode})");
                }
                catch (Exception ex) { Log("node 启动失败: " + ex.Message); }

                // 3) ffmpeg 保活循环
                _ffmpegLoop = new Thread(FfmpegLoop) { IsBackground = true };
                _ffmpegLoop.Start();

                // 4) Windows 防火墙入站放行（局域网 OBS 推流/观众访问必需）
                EnsureFirewallRules();

                Task.Delay(1500).ContinueWith(_ => Log("=== 集成服务启动完成 ==="));
                return "";
            }
        }

        /// <summary>自动添加 Windows 防火墙入站规则（需管理员权限，失败不阻塞服务）</summary>
        private static void EnsureFirewallRules()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("advfirewall");
                psi.ArgumentList.Add("firewall");
                psi.ArgumentList.Add("add");
                psi.ArgumentList.Add("rule");
                psi.ArgumentList.Add("name=LiveRoomAdmin");
                psi.ArgumentList.Add("dir=in");
                psi.ArgumentList.Add("action=allow");
                psi.ArgumentList.Add("protocol=TCP");
                psi.ArgumentList.Add($"localport={PortRtmp},{PortNode},{PortHls},{PortWebRtc},{PortRtsp}");
                using var p = Process.Start(psi);
                if (p == null) { Log("防火墙规则添加失败: 无法启动 netsh"); return; }
                p.WaitForExit(8000);
                if (p.ExitCode == 0)
                    Log($"防火墙入站规则已放行: {PortRtmp}/{PortNode}/{PortHls}/{PortWebRtc}/{PortRtsp}");
                else
                    Log($"防火墙规则添加失败(退出码 {p.ExitCode})：请以管理员身份运行一次客户端，或手动放行 {PortRtmp}/{PortNode}/{PortHls}/{PortWebRtc}/{PortRtsp} 入站");
            }
            catch (Exception ex)
            {
                Log("防火墙规则添加失败: " + ex.Message);
            }
        }

        /// <summary>停止核心业务</summary>
        public static void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                Log("=== 集成服务停止 ===");
                KillAllFfmpeg();
                try { _node?.Kill(true); } catch { }
                try { _node?.WaitForExit(2000); } catch { }
                try { _mtx?.Kill(true); } catch { }
                try { _mtx?.WaitForExit(2000); } catch { }
                _node = null; _mtx = null;
                Log("服务已停止");
            }
        }

        /// <summary>重启全部服务（管理页"重启全部服务"按钮）</summary>
        public static void RestartAll()
        {
            Task.Run(() =>
            {
                Stop();
                Thread.Sleep(1500);
                Start();
            });
        }

        private static void TryKill(Process p)
        {
            try { if (p != null && !p.HasExited) p.Kill(true); } catch { }
        }

        private static void KillAllFfmpeg()
        {
            lock (_ffmpegs)
            {
                foreach (var p in _ffmpegs.Values) TryKill(p);
                _ffmpegs.Clear();
            }
        }

        /// <summary>从服务端拉取直播间 key 列表（失败回退默认主直播间）</summary>
        private static List<string> GetRoomsFromServer()
        {
            var list = new List<string> { "main" };
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = client.GetAsync($"http://127.0.0.1:{PortNode}/api/rooms").Result;
                if (!resp.IsSuccessStatusCode) return list;
                var body = resp.Content.ReadAsStringAsync().Result;
                var doc = System.Text.Json.JsonDocument.Parse(body);
                foreach (var x in doc.RootElement.GetProperty("data").EnumerateArray())
                    list.Add(x.GetProperty("key").GetString());
            }
            catch { }
            return list.Distinct().ToList();
        }

        /// <summary>ffmpeg 保活循环（多房间）：按房间列表为每个未下播房间拉起转码进程</summary>
        private static void FfmpegLoop()
        {
            int failCount = 0;
            DateTime lastErrLog = DateTime.MinValue;
            string lastErr = "";
            while (_running)
            {
                try
                {
                    var rooms = GetRoomsFromServer();
                    // 1) 清理已删除房间的残留进程
                    lock (_ffmpegs)
                    {
                        foreach (var k in _ffmpegs.Keys.ToList())
                            if (!rooms.Contains(k)) { TryKill(_ffmpegs[k]); _ffmpegs.Remove(k); }
                    }
                    // 2) 逐个房间：下播则不运行；未下播且进程不存在则拉起
                    foreach (var room in rooms)
                    {
                        if (IsRoomStopped(room))
                        {
                            Process p;
                            lock (_ffmpegs) { _ffmpegs.TryGetValue(room, out p); if (p != null) { TryKill(p); _ffmpegs.Remove(room); } }
                            continue;
                        }
                        Process proc;
                        lock (_ffmpegs)
                        {
                            if (_ffmpegs.TryGetValue(room, out proc) && proc != null && !proc.HasExited) continue;
                        }
                        var psi = new ProcessStartInfo
                        {
                            FileName = Path.Combine(BinDir, "ffmpeg.exe"),
                            WorkingDirectory = ServerDir,
                            UseShellExecute = false,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            CreateNoWindow = true
                        };
                        foreach (var arg in new[]
                        {
                            "-hide_banner", "-loglevel", "warning", "-fflags", "nobuffer",
                            "-i", "rtmp://127.0.0.1:1935/stream/" + room,
                            "-c:v", "copy", "-c:a", "opus", "-b:a", "64k",
                            "-strict", "-2", "-rtsp_transport", "tcp",
                            "-f", "rtsp", "rtsp://127.0.0.1:8554/lowlatency/" + room
                        }) psi.ArgumentList.Add(arg);

                        var fp = Process.Start(psi);
                        AttachReader(fp, "ff-" + room);
                        lock (_ffmpegs) _ffmpegs[room] = fp;
                        failCount = 0;
                        var now = DateTime.Now;
                        var msg = "ffmpeg 转码守护已就绪 (房间 " + room + "，等待推流，无流时自动重试)";
                        if ((msg != lastErr || (now - lastErrLog).TotalSeconds >= 30))
                        {
                            Log(msg);
                            lastErrLog = now;
                            lastErr = msg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failCount++;
                    Log($"ffmpeg 异常: {ex.Message} (第{failCount}次)");
                    if (failCount >= 5) { Log("ffmpeg 连续失败5次，停止重试（HLS 兜底可用）"); break; }
                }
                if (_running) Thread.Sleep(3000);
            }
        }

        /// <summary>轮询指定房间状态（是否被管理员下播）</summary>
        private static bool IsRoomStopped(string room)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = client.GetAsync($"http://127.0.0.1:{PortNode}/api/room-state?room={Uri.EscapeDataString(room)}").Result;
                if (!resp.IsSuccessStatusCode) return false;
                var body = resp.Content.ReadAsStringAsync().Result;
                return body.Contains("\"stopped\":true") || body.Contains("\"stopped\": true");
            }
            catch { return false; }
        }

        private static void AttachReader(Process p, string tag)
        {
            if (p == null) return;
            if (p.StandardOutput != null)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                            if (!string.IsNullOrWhiteSpace(line)) Log($"[{tag}] {line}");
                    }
                    catch { }
                }) { IsBackground = true };
                t.Start();
            }
            if (p.StandardError != null)
            {
                var t = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardError.ReadLine()) != null)
                            if (!string.IsNullOrWhiteSpace(line)) Log($"[{tag}] {line}");
                    }
                    catch { }
                }) { IsBackground = true };
                t.Start();
            }
        }
    }
}
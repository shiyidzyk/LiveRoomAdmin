using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LiveRoomAdmin.Services
{
    public static class LiveServer
    {
        public const int PortNode = 3000;
        public const int PortRtmp = 1935;
        public const int PortHls = 8888;
        public const int PortWebRtc = 8889;
        public const int PortRtsp = 8554;

        private static Process _mtx;
        private static Process _node;
        private static Process _ffmpeg;
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

        public static string TailLog(int n = 200)
        {
            var arr = _logQueue.ToArray();
            var start = Math.Max(0, arr.Length - n);
            return string.Join("\r\n", arr, start, arr.Length - start);
        }

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
                _ffmpegLoop = new Thread(FfmpegLoop) { IsBackground = true };
                _ffmpegLoop.Start();
                EnsureFirewallRules();
                Task.Delay(1500).ContinueWith(_ => Log("=== 集成服务启动完成 ==="));
                return "";
            }
        }

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

        public static void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                Log("=== 集成服务停止 ===");
                KillFfmpeg();
                try { _node?.Kill(true); } catch { }
                try { _node?.WaitForExit(2000); } catch { }
                try { _mtx?.Kill(true); } catch { }
                try { _mtx?.WaitForExit(2000); } catch { }
                _node = null; _mtx = null;
                Log("服务已停止");
            }
        }

        public static void RestartAll()
        {
            Task.Run(() =>
            {
                Stop();
                Thread.Sleep(1500);
                Start();
            });
        }

        private static void KillFfmpeg()
        {
            try { _ffmpeg?.Kill(true); } catch { }
            _ffmpeg = null;
        }

        private static void FfmpegLoop()
        {
            int failCount = 0;
            DateTime lastErrLog = DateTime.MinValue;
            string lastErr = "";
            while (_running)
            {
                try
                {
                    if (IsRoomStopped())
                    {
                        KillFfmpeg();
                        Thread.Sleep(3000);
                        continue;
                    }
                    if (_ffmpeg != null && !_ffmpeg.HasExited)
                    {
                        Thread.Sleep(3000);
                        continue;
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
                        "-i", "rtmp://127.0.0.1:1935/stream/test",
                        "-c:v", "copy", "-c:a", "opus", "-b:a", "64k",
                        "-strict", "-2", "-rtsp_transport", "tcp",
                        "-f", "rtsp", "rtsp://127.0.0.1:8554/lowlatency"
                    }) psi.ArgumentList.Add(arg);
                    _ffmpeg = Process.Start(psi);
                    AttachReader(_ffmpeg, "ff");
                    failCount = 0;
                    Log("ffmpeg 转码守护已就绪 (等待推流，无流时每5秒自动重试)");
                    _ffmpeg.WaitForExit(30 * 1000);
                    if (_running && _ffmpeg != null && !_ffmpeg.HasExited)
                    {
                        Log("ffmpeg 30s 无输出，重启");
                        KillFfmpeg();
                    }
                    else
                    {
                        var code = _ffmpeg?.ExitCode ?? -1;
                        var now = DateTime.Now;
                        var msg = $"等待推流中 (退出码 {code})...";
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
                    if (failCount >= 3) { Log("ffmpeg 连续失败3次，停止重试（HLS 兜底可用）"); break; }
                }
                if (_running) Thread.Sleep(5000);
            }
        }

        private static bool IsRoomStopped()
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var resp = client.GetAsync($"http://127.0.0.1:{PortNode}/api/room-state").Result;
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

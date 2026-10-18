using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using LiveRoomAdmin.Pages;

namespace LiveRoomAdmin.Services
{
    public class ApiClient
    {
        /// <summary>管理员会话失效（401/服务重启）时触发，MainWindow 据此弹重新登录</summary>
        public event Action SessionExpired;

        public string BaseUrl { get; set; } = "http://192.168.3.189:3000";
        public string AdminToken { get; set; } = "";
        private readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        public ApiClient()
        {
            _http.DefaultRequestHeaders.Add("User-Agent", "LiveRoomAdmin/1.0");
        }

        public void SetServer(string host, int port) => BaseUrl = $"http://{host}:{port}";

        private HttpRequestMessage Req(HttpMethod m, string path)
        {
            var req = new HttpRequestMessage(m, BaseUrl + path);
            if (!string.IsNullOrEmpty(AdminToken))
                req.Headers.Add("Authorization", "Bearer " + AdminToken);
            return req;
        }

        private async Task<JsonElement> Send(HttpRequestMessage req)
        {
            var usedToken = req.Headers.Authorization?.Parameter ?? "";
            var resp = await _http.SendAsync(req);
            var txt = await resp.Content.ReadAsStringAsync();
            // 401 = 会话失效（服务重启 / 被顶号）。
            // 仅当：该请求确实带着 token、失败的就是当前 token、且不在登录流程中，才判定失效并跳转——
            // 避免服务刚启动未登录时的轮询 401、或登录并发期间的旧请求 401 误把首页切走。
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                if (!string.IsNullOrEmpty(usedToken) && usedToken == AdminToken && !_loginInProgress)
                {
                    AdminToken = "";
                    SessionExpired?.Invoke();
                }
                throw new Exception("登录状态已失效，请重新登录");
            }
            if (!resp.IsSuccessStatusCode)
                throw new Exception($"HTTP {(int)resp.StatusCode}: {txt}");
            using var doc = JsonDocument.Parse(txt);
            return doc.RootElement.Clone();
        }

        /// <summary>当前登录管理员的角色（superadmin/admin）</summary>
        public string AdminRole { get; set; } = "";
        public string AdminUsername { get; set; } = "";
        public Dictionary<string, bool> AdminPerms { get; set; } = new Dictionary<string, bool>();
        /// <summary>本机设备标识（登录时上报，用于后台识别/设备管理）</summary>
        public static string DeviceName { get; } = Environment.MachineName + "（Windows）";
        public bool IsSuperAdmin => AdminRole == "superadmin";
        public string AdminUsernameSafe() => AdminUsername ?? "";
        private bool _loginInProgress;

        /// <summary>
        /// 统一登录入口：已登录则校验会话；未登录弹出（唯一一个）登录窗。
        /// 并发/重复调用复用同一登录流程，杜绝多个登录窗并发登录互相顶号。
        /// </summary>
        public async Task<bool> EnsureLoginAsync(Window owner)
        {
            if (!string.IsNullOrEmpty(AdminToken))
            {
                try { await FetchMe(); return true; }
                catch { AdminToken = ""; }
            }
            if (_loginInProgress)
            {
                while (_loginInProgress) await Task.Delay(300);
                return !string.IsNullOrEmpty(AdminToken);
            }
            _loginInProgress = true;
            try
            {
                return await LoginDialog.ShowAndLogin(owner);
            }
            finally { _loginInProgress = false; }
        }

        /// <summary>当前登录账户是否具备某项管理权限（superadmin 恒有）</summary>
        public bool HasPerm(string key)
        {
            if (AdminRole == "superadmin") return true;
            return AdminPerms != null && AdminPerms.TryGetValue(key, out var v) && v;
        }

        public async Task<string> AdminLogin(string username, string password)
        {
            var body = JsonSerializer.Serialize(new { username, password, device = DeviceName });
            var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/admin/login")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            var el = await Send(req);
            if (el.TryGetProperty("code", out var c) && c.GetInt32() == 0)
            {
                AdminToken = el.GetProperty("data").GetProperty("token").GetString();
                AdminRole = el.GetProperty("data").TryGetProperty("role", out var r) ? r.GetString() : "";
                AdminUsername = username;
                AdminPerms = el.GetProperty("data").TryGetProperty("perms", out var pm) ? ParsePerms(pm) : new Dictionary<string, bool>();
                return AdminToken;
            }
            throw new Exception(el.TryGetProperty("msg", out var m) ? m.GetString() : "登录失败");
        }

        /// <summary>恢复 token 后获取当前登录者信息（角色/用户名/权限）</summary>
        public async Task FetchMe()
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/me"));
            var d = el.GetProperty("data");
            AdminUsername = d.TryGetProperty("username", out var un) ? un.GetString() : "";
            AdminRole = d.TryGetProperty("role", out var r) ? r.GetString() : "";
            AdminPerms = d.TryGetProperty("perms", out var pm) ? ParsePerms(pm) : new Dictionary<string, bool>();
        }

        public async Task<(int online, List<Viewer> viewers, int uptime, int accountCount)> GetOverview()
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/overview"));
            var d = el.GetProperty("data");
            var viewers = new List<Viewer>();
            foreach (var v in d.GetProperty("viewers").EnumerateArray())
                viewers.Add(new Viewer { Name = v.GetProperty("name").GetString(), Role = v.GetProperty("role").GetString() });
            return (d.GetProperty("online").GetInt32(), viewers,
                    d.GetProperty("uptime").GetInt32(), d.GetProperty("accounts").GetInt32());
        }

        public async Task<List<UserInfo>> GetUsers()
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/users"));
            var list = new List<UserInfo>();
            foreach (var u in el.GetProperty("data").EnumerateArray())
                list.Add(new UserInfo
                {
                    Username = u.GetProperty("username").GetString(),
                    Role = u.GetProperty("role").GetString(),
                    RoleName = u.TryGetProperty("roleName", out var rn) ? rn.GetString() : u.GetProperty("role").GetString(),
                    Tokens = u.GetProperty("tokens").GetInt32(),
                    Banned = u.GetProperty("banned").GetBoolean(),
                    Online = u.GetProperty("online").GetBoolean(),
                    Perms = u.TryGetProperty("perms", out var pm) ? ParsePerms(pm) : null
                });
            return list;
        }

        private static Dictionary<string, bool> ParsePerms(System.Text.Json.JsonElement pm)
        {
            var d = new Dictionary<string, bool>();
            foreach (var k in new[] { "createUser", "editRole", "banUser", "kickUser", "warnUser", "roomControl", "resetPass", "deleteUser", "sessionMgr", "deviceKick" })
            {
                if (pm.TryGetProperty(k, out var v)) d[k] = v.GetBoolean();
            }
            return d;
        }

        public async Task DeleteUser(string username)
        {
            await Post("/admin/api/delete-user", new { username });
        }

        public async Task SetRole(string username, string role)
        {
            await Post("/admin/api/set-role", new { username, role });
        }

        public async Task SetPerms(string username, Dictionary<string, bool> perms)
        {
            await Post("/admin/api/set-perms", new { username, perms });
        }

        public async Task CreateUser(string username, string password, int tokens, string role)
        {
            var body = JsonSerializer.Serialize(new { username, password, tokens, role });
            var req = Req(HttpMethod.Post, "/admin/api/users");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        public async Task Post(string path, object payload)
        {
            var body = JsonSerializer.Serialize(payload);
            var req = Req(HttpMethod.Post, path);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        public async Task<string> GetLogs(string which)
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/logs?file=" + which));
            return el.GetProperty("data").GetString();
        }

        /// <summary>修改自己的密码</summary>
        public async Task ChangeMyPassword(string oldPassword, string newPassword)
        {
            var body = JsonSerializer.Serialize(new { oldPassword, newPassword });
            var req = Req(HttpMethod.Post, "/admin/api/change-my-password");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        /// <summary>管理员重置指定用户密码</summary>
        public async Task ResetUserPassword(string username, string password)
        {
            var body = JsonSerializer.Serialize(new { username, password });
            var req = Req(HttpMethod.Post, "/admin/api/update-user");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        public async Task<string> GetStreamUrl(string room = "main")
        {
            var el = await Send(Req(HttpMethod.Get, "/api/stream?room=" + Uri.EscapeDataString(room)));
            return el.GetProperty("data").GetProperty("hls").GetString();
        }

        /// <summary>获取直播间列表（公开接口）</summary>
        public async Task<List<RoomInfo>> GetRooms()
        {
            var el = await Send(Req(HttpMethod.Get, "/api/rooms"));
            var list = new List<RoomInfo>();
            foreach (var x in el.GetProperty("data").EnumerateArray())
                list.Add(new RoomInfo
                {
                    Key = x.GetProperty("key").GetString(),
                    Name = x.TryGetProperty("name", out var n) ? n.GetString() : x.GetProperty("key").GetString(),
                    Stopped = x.TryGetProperty("stopped", out var st) && st.GetBoolean(),
                    Message = x.TryGetProperty("message", out var ms) ? ms.GetString() : "",
                    Online = x.TryGetProperty("online", out var on) ? on.GetInt32() : 0
                });
            return list;
        }

        /// <summary>新建直播间（仅最高管理员）</summary>
        public async Task<string> CreateRoom(string name)
        {
            var el = await Send(ReqWithBody(HttpMethod.Post, "/admin/api/rooms", new { name }));
            return el.GetProperty("data").GetProperty("key").GetString();
        }

        /// <summary>删除直播间（仅最高管理员，主直播间不可删）</summary>
        public async Task DeleteRoom(string key)
        {
            await Send(ReqWithBody(HttpMethod.Delete, "/admin/api/rooms", new { key }));
        }

        private HttpRequestMessage ReqWithBody(HttpMethod m, string path, object payload)
        {
            var req = Req(m, path);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            return req;
        }

        // ---- 多端登录策略 / 设备管理 ----
        public async Task<(bool multiEnabled, int maxDevices)> GetLoginPolicy()
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/login-policy"));
            var d = el.GetProperty("data");
            return (d.TryGetProperty("multiEnabled", out var m) && m.GetBoolean(),
                    d.TryGetProperty("maxDevices", out var n) ? n.GetInt32() : 1);
        }

        public async Task SaveLoginPolicy(bool multiEnabled, int maxDevices)
        {
            await Post("/admin/api/login-policy", new { multiEnabled, maxDevices });
        }

        public async Task<List<DeviceInfo>> GetDevices(string username)
        {
            var el = await Send(Req(HttpMethod.Get, "/admin/api/devices?username=" + Uri.EscapeDataString(username)));
            var list = new List<DeviceInfo>();
            foreach (var x in el.GetProperty("data").EnumerateArray())
                list.Add(new DeviceInfo
                {
                    Sess = x.GetProperty("sess").GetString(),
                    Ip = x.TryGetProperty("ip", out var ip) ? ip.GetString() : "",
                    Ua = x.TryGetProperty("ua", out var ua) ? ua.GetString() : "",
                    Device = x.TryGetProperty("device", out var dv) ? dv.GetString() : "",
                    LoginAt = x.TryGetProperty("loginAt", out var la) ? la.GetInt64() : 0,
                    Online = x.TryGetProperty("online", out var on) && on.GetBoolean(),
                    Current = x.TryGetProperty("current", out var cu) && cu.GetBoolean()
                });
            return list;
        }

        public async Task KickDevice(string username, string sess)
        {
            await Post("/admin/api/kick-device", new { username, sess });
        }
    }

    public class Viewer { public string Name; public string Role; }
    /// <summary>直播间信息</summary>
    public class RoomInfo
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public bool Stopped { get; set; }
        public string Message { get; set; }
        public int Online { get; set; }
        public override string ToString() => Name;
    }
    public class UserInfo
    {
        public string Username { get; set; }
        public string Role { get; set; }
        public string RoleName { get; set; }
        public int Tokens { get; set; }
        public bool Banned { get; set; }
        public bool Online { get; set; }
        public Dictionary<string, bool> Perms { get; set; }
    }

    /// <summary>一条登录会话（设备）信息，用于后台设备识别与按设备下线</summary>
    public class DeviceInfo
    {
        public string Sess { get; set; }   // 会话标识（token 前8位）
        public string Ip { get; set; }
        public string Ua { get; set; }
        public string Device { get; set; } // 设备名
        public long LoginAt { get; set; }  // 登录时间（毫秒）
        public bool Online { get; set; }   // 当前是否连着 SSE
        public bool Current { get; set; }  // 是否为发起请求的本机
    }
}
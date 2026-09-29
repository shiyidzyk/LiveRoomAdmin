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

        public string AdminRole { get; set; } = "";
        public string AdminUsername { get; set; } = "";
        public Dictionary<string, bool> AdminPerms { get; set; } = new Dictionary<string, bool>();
        public static string DeviceName { get; } = Environment.MachineName + "（Windows）";
        public bool IsSuperAdmin => AdminRole == "superadmin";
        public string AdminUsernameSafe() => AdminUsername ?? "";
        private bool _loginInProgress;

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

        public async Task DeleteUser(string username) => await Post("/admin/api/delete-user", new { username });
        public async Task SetRole(string username, string role) => await Post("/admin/api/set-role", new { username, role });
        public async Task SetPerms(string username, Dictionary<string, bool> perms) => await Post("/admin/api/set-perms", new { username, perms });

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

        public async Task ChangeMyPassword(string oldPassword, string newPassword)
        {
            var body = JsonSerializer.Serialize(new { oldPassword, newPassword });
            var req = Req(HttpMethod.Post, "/admin/api/change-my-password");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        public async Task ResetUserPassword(string username, string password)
        {
            var body = JsonSerializer.Serialize(new { username, password });
            var req = Req(HttpMethod.Post, "/admin/api/update-user");
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            await Send(req);
        }

        public async Task<string> GetStreamUrl()
        {
            var el = await Send(Req(HttpMethod.Get, "/api/stream"));
            return el.GetProperty("data").GetProperty("hls").GetString();
        }

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

    public class DeviceInfo
    {
        public string Sess { get; set; }
        public string Ip { get; set; }
        public string Ua { get; set; }
        public string Device { get; set; }
        public long LoginAt { get; set; }
        public bool Online { get; set; }
        public bool Current { get; set; }
    }
}

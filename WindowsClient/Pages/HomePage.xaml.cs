using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LibVLCSharp.Shared;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin.Pages
{
    public partial class HomePage : UserControl
    {
        private LibVLC _libVLC;
        private LibVLCSharp.Shared.MediaPlayer _mp;
        private bool _adminAuthed;
        private CancellationTokenSource _sseCts;
        private CancellationTokenSource _playCts;

        public HomePage()
        {
            InitializeComponent();
            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _mp = new LibVLCSharp.Shared.MediaPlayer(_libVLC);
                VlcView.MediaPlayer = _mp;
            }
            catch (Exception ex)
            {
                LoadingMask.Visibility = Visibility.Collapsed;
                Overlay.Visibility = Visibility.Visible;
                TxtOverlayMsg.Text = "视频组件初始化失败: " + ex.Message;
            }
        }

        public void RefreshLangTexts()
        {
            if (LiveServer.IsRunning)
            {
                BtnLive.Content = LocService.T("■ 停止直播", "■ Stop Streaming");
                TxtSub.Text = LocService.T("直播服务运行中（本机）", "Live service running (local)");
            }
            else
            {
                BtnLive.Content = LocService.T("▶ 开始直播", "▶ Start Streaming");
                TxtSub.Text = LocService.T("直播间实时画面与状态", "Live preview and room status");
            }
            UpdateStreamInfo();
        }

        public void UpdateStreamInfo()
        {
            TxtAdminUser.Text = "admin";
            TxtAdminPass.Text = "admin123";
            if (!LiveServer.IsRunning)
            {
                WrapRtmp.Visibility = Visibility.Collapsed;
                WrapKey.Visibility = Visibility.Collapsed;
                CardWebLink.Visibility = Visibility.Collapsed;
                return;
            }
            try
            {
                var host = LiveServer.GetLocalIPv4();
                TxtRtmp.Text = $"rtmp://{host}:1935/stream";
                TxtKey.Text = "test";
                TxtWebLink.Text = $"http://{host}:3000";
                CardWebLink.Visibility = Visibility.Visible;
                WrapRtmp.Visibility = Visibility.Visible;
                WrapKey.Visibility = Visibility.Visible;
            }
            catch
            {
                WrapRtmp.Visibility = Visibility.Collapsed;
                WrapKey.Visibility = Visibility.Collapsed;
            }
        }

        private Window SafeOwner() => Window.GetWindow(this) ?? Application.Current.MainWindow;

        private async void BtnLive_Click(object sender, RoutedEventArgs e)
        {
            var owner = SafeOwner();
            if (LiveServer.IsRunning)
            {
                if (!await EnsureCanControlAsync(owner)) return;
                DoStop(owner);
                return;
            }
            BtnLive.IsEnabled = false;
            BtnLive.Content = LocService.T("启动中...", "Starting...");
            TxtSub.Text = LocService.T("正在启动直播服务 (mediamtx + node + ffmpeg)...", "Starting live services (mediamtx + node + ffmpeg)...");
            try
            {
                var err = LiveServer.Start();
                if (!string.IsNullOrEmpty(err))
                {
                    MessageBox.Show(owner, err, LocService.T("启动失败", "Startup failed"), MessageBoxButton.OK, MessageBoxImage.Error);
                    ResetLiveButton();
                    return;
                }
                var ready = false;
                for (int i = 0; i < 20; i++)
                {
                    await Task.Delay(1000);
                    try
                    {
                        using var c = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        var r = await c.GetAsync("http://127.0.0.1:3000/api/room-state");
                        if (r.IsSuccessStatusCode) { ready = true; break; }
                    }
                    catch { }
                }
                if (!ready)
                {
                    MessageBox.Show(owner, LocService.T("服务启动超时，已回滚。请查看日志页排查", "Service startup timed out and was rolled back. Check the Logs page."), LocService.T("警告", "Warning"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    LiveServer.Stop();
                    ResetLiveButton();
                    return;
                }
                if (!await EnsureCanControlAsync(owner))
                {
                    LiveServer.Stop();
                    ResetLiveButton();
                    LoadingMask.Visibility = Visibility.Visible;
                    return;
                }
                DoStart(owner);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, LocService.T("启动异常: ", "Startup error: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                try { LiveServer.Stop(); } catch { }
                ResetLiveButton();
            }
        }

        private void DoStop(Window owner)
        {
            LiveServer.Stop();
            _playCts?.Cancel();
            _mp?.Stop();
            ResetLiveButton();
            TxtSub.Text = LocService.T("直播服务已停止", "Live service stopped");
            LoadingMask.Visibility = Visibility.Visible;
            if (owner is MainWindow mw) mw.NotifyStatus();
            UpdateStreamInfo();
        }

        private async void DoStart(Window owner)
        {
            try
            {
                MainWindow.Api.SetServer("127.0.0.1", 3000);
                if (owner is MainWindow mw) await mw.RefreshStatus();
                UpdateStreamInfo();
                StartStream();
                BtnLive.Content = LocService.T("■ 停止直播", "■ Stop Streaming");
                BtnLive.Background = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
                TxtSub.Text = LocService.T("直播服务运行中（本机）", "Live service running (local)");
                MessageBox.Show(owner,
                    LocService.T("推流地址已生成。若 OBS 仍连接超时：\n① 关闭后以「管理员身份运行」本客户端再点开始直播，自动放行防火墙（1935/3000/8888/8554）；\n② 或在 Windows 防火墙高级设置中手动添加入站规则放行上述 TCP 端口。",
                                 "Stream URL is ready. If OBS still times out:\n① Run this client as Administrator and click Start Streaming to auto-add firewall rules (1935/3000/8888/8554);\n② Or manually add inbound TCP rules in Windows Firewall for those ports."),
                    LocService.T("防火墙提示", "Firewall notice"), MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, LocService.T("启动异常: ", "Startup error: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnLive.IsEnabled = true;
            }
        }

        private void ResetLiveButton()
        {
            BtnLive.Content = LocService.T("▶ 开始直播", "▶ Start Streaming");
            BtnLive.Background = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E));
            BtnLive.IsEnabled = true;
        }

        private async Task<bool> EnsureCanControlAsync(Window owner)
        {
            if (!await MainWindow.Api.EnsureLoginAsync(owner)) return false;
            if (!MainWindow.Api.HasPerm("roomControl"))
            {
                MessageBox.Show(owner,
                    LocService.T("当前账户没有「直播控制」权限，无法开停播。\n请登录具备相关权限的账户（最高管理员，或被授予直播控制权限的管理员）。",
                                 "This account has no Live Control permission. Please log in with an authorized account (Super Admin, or an admin granted Live Control)."),
                    LocService.T("权限不足", "Permission denied"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            return true;
        }

        private void CopyText(string text, Button btn)
        {
            try { Clipboard.SetText(text); }
            catch { }
            if (btn == null) return;
            var old = btn.Content;
            btn.Content = LocService.T("已复制 ✓", "Copied ✓");
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (s, e) => { btn.Content = old; timer.Stop(); };
            timer.Start();
        }

        private void BtnCopyRtmp_Click(object sender, RoutedEventArgs e) => CopyText(TxtRtmp.Text, (Button)sender);
        private void BtnCopyKey_Click(object sender, RoutedEventArgs e) => CopyText(TxtKey.Text, (Button)sender);
        private void BtnCopyWebLink_Click(object sender, RoutedEventArgs e) => CopyText(TxtWebLink.Text, (Button)sender);
        private void BtnCopyAdminUser_Click(object sender, RoutedEventArgs e) => CopyText(TxtAdminUser.Text, (Button)sender);
        private void BtnCopyAdminPass_Click(object sender, RoutedEventArgs e) => CopyText(TxtAdminPass.Text, (Button)sender);

        public void StartStream()
        {
            if (_mp == null) return;
            _playCts?.Cancel();
            _playCts = new CancellationTokenSource();
            var ct = _playCts.Token;
            Task.Run(async () =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        var url = await MainWindow.Api.GetStreamUrl();
                        using (var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(3) })
                        {
                            var r = await probe.GetAsync(url, ct);
                            if (!r.IsSuccessStatusCode)
                            {
                                Dispatcher.Invoke(() =>
                                {
                                    LoadingMask.Visibility = Visibility.Visible;
                                    TxtSub.Text = LocService.T("等待推流中…（请用 OBS 向推流地址推流）", "Waiting for stream... (Push from OBS)");
                                });
                                await Task.Delay(5000, ct);
                                continue;
                            }
                        }
                        Dispatcher.Invoke(() =>
                        {
                            var media = new Media(_libVLC, url, FromType.FromLocation);
                            _mp.Stop();
                            _mp.Play(media);
                            LoadingMask.Visibility = Visibility.Collapsed;
                            TxtSub.Text = LocService.T("直播流: ", "Stream: ") + url;
                        });
                        await Task.Delay(10000, ct);
                        if (_mp != null && !_mp.IsPlaying && LiveServer.IsRunning)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                LoadingMask.Visibility = Visibility.Visible;
                                TxtSub.Text = LocService.T("直播流中断，正在重连…", "Stream interrupted, reconnecting...");
                            });
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            LoadingMask.Visibility = Visibility.Visible;
                            TxtSub.Text = LocService.T("等待推流中… ", "Waiting for stream... ") + ex.Message;
                        });
                        try { await Task.Delay(5000, ct); } catch { break; }
                    }
                }
            });
        }

        public async Task StartSse()
        {
            _sseCts?.Cancel();
            _sseCts = new CancellationTokenSource();
            var ct = _sseCts.Token;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var token = MainWindow.Api.AdminToken;
                    if (string.IsNullOrEmpty(token))
                    {
                        await Task.Delay(3000, ct);
                        continue;
                    }
                    using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                    var req = new HttpRequestMessage(HttpMethod.Get, MainWindow.Api.BaseUrl + "/api/events?token=" + token);
                    var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (!resp.IsSuccessStatusCode) { await Task.Delay(5000, ct); continue; }
                    using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string line;
                    string evt = "";
                    string data = "";
                    while ((line = await reader.ReadLineAsync()) != null)
                    {
                        if (line.StartsWith("event:")) evt = line.Substring(6).Trim();
                        else if (line.StartsWith("data:")) data = line.Substring(5).Trim();
                        else if (string.IsNullOrEmpty(line))
                        {
                            if (!string.IsNullOrEmpty(data)) HandleSse(evt, data);
                            evt = ""; data = "";
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { await Task.Delay(5000, ct); }
            }
        }

        private void HandleSse(string evt, string data)
        {
            Dispatcher.Invoke(() =>
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    switch (evt)
                    {
                        case "danmaku":
                            AddChat($"{Get(root, "name")}: {Get(root, "text")}");
                            break;
                        case "gift":
                            AddChat($"{Get(root, "name")} 送出 {Get(root, "giftIcon")} {Get(root, "giftName")} ({Get(root, "price")}币)");
                            break;
                        case "roomwarn":
                            ShowOverlay("⚠ " + Get(root, "message"));
                            break;
                        case "roomstop":
                            ShowOverlay("⛔ " + Get(root, "message"));
                            break;
                        case "roomresume":
                            HideOverlay();
                            break;
                        case "viewers":
                            TxtOnline.Text = Get(root, "count");
                            break;
                        case "system":
                            AddChat($"[系统] {Get(root, "name")} 进入直播间");
                            break;
                        case "kicked":
                        case "banned":
                            var oldSess = Get(root, "sess");
                            var curSess = MainWindow.Api.AdminToken.Length >= 8 ? MainWindow.Api.AdminToken.Substring(0, 8) : "";
                            if (!string.IsNullOrEmpty(oldSess) && oldSess != curSess) break;
                            MainWindow.Api.AdminToken = "";
                            SettingsService.Set("admin_token", "");
                            var kmsg = Get(root, "message");
                            MessageBox.Show(SafeOwner(),
                                string.IsNullOrEmpty(kmsg) ? LocService.T("你已被管理员踢出直播间", "You have been kicked out by the admin") : kmsg,
                                evt == "banned" ? LocService.T("已封禁", "Banned") : LocService.T("已下线", "Kicked"),
                                MessageBoxButton.OK,
                                evt == "banned" ? MessageBoxImage.Warning : MessageBoxImage.Information);
                            break;
                    }
                }
                catch { }
            });
        }

        private static string Get(System.Text.Json.JsonElement el, string name) =>
            el.TryGetProperty(name, out var v) ? v.ToString() : "";

        private void AddChat(string line)
        {
            ListChat.Items.Add(line);
            while (ListChat.Items.Count > 300) ListChat.Items.RemoveAt(0);
            ListChat.ScrollIntoView(ListChat.Items[ListChat.Items.Count - 1]);
        }

        private void ShowOverlay(string msg)
        {
            TxtOverlayMsg.Text = msg;
            Overlay.Visibility = Visibility.Visible;
        }

        private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

        public async Task UpdateOverview(int online, List<Viewer> viewers, int uptime, int accounts)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                TxtOnline.Text = online.ToString();
                TxtUptime.Text = uptime >= 3600
                    ? $"{uptime / 3600}:{uptime % 3600 / 60:D2}:{uptime % 60:D2}"
                    : $"{uptime / 60:D2}:{uptime % 60:D2}";
                TxtAccounts.Text = accounts.ToString();
                ListViewers.ItemsSource = null;
                var items = new List<ViewerItem>();
                foreach (var v in viewers)
                    items.Add(new ViewerItem { Name = v.Name, RoleVisibility = v.Role == "admin" ? Visibility.Visible : Visibility.Collapsed });
                ListViewers.ItemsSource = items;
            });
        }

        private void BtnReconnect_Click(object sender, RoutedEventArgs e)
        {
            LoadingMask.Visibility = Visibility.Visible;
            StartStream();
        }

        private async void BtnAdminLogin_Click(object sender, RoutedEventArgs e)
        {
            var owner = SafeOwner();
            if (await LoginDialog.ShowAndLogin(owner))
            {
                TxtSub.Text = LocService.T("管理员已登录：", "Admin logged in: ") + MainWindow.Api.AdminUsernameSafe() + LocService.T("（", " (") + (MainWindow.Api.IsSuperAdmin ? LocService.T("最高管理员", "Super Admin") : LocService.T("管理员", "Admin")) + LocService.T("）", ")");
                if (owner is MainWindow mw) mw.NotifyStatus();
            }
        }
    }

    public class ViewerItem
    {
        public string Name { get; set; }
        public Visibility RoleVisibility { get; set; }
    }

    public class LoginDialog : Window
    {
        public string Username { get; private set; }
        public string Password { get; private set; }
        private readonly TextBox _u = new TextBox { Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(8, 6, 8, 6) };
        private readonly PasswordBox _p = new PasswordBox { Margin = new Thickness(0, 8, 0, 8), Padding = new Thickness(8, 6, 8, 6) };

        public static async Task<bool> ShowAndLogin(Window owner)
        {
            var dlg = new LoginDialog();
            if (owner != null)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            if (dlg.ShowDialog() != true) return false;
            return true;
        }

        public LoginDialog()
        {
            Title = LocService.T("管理员登录", "Admin Login");
            Width = 360; Height = 300;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            var grid = new Grid { Margin = new Thickness(20) };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = LocService.T("管理员账号", "Admin Account"), FontSize = 13 });
            sp.Children.Add(_u);
            sp.Children.Add(new TextBlock { Text = LocService.T("密码", "Password"), FontSize = 13 });
            sp.Children.Add(_p);
            var tip = new TextBlock { Text = LocService.T("仅管理员 / 最高管理员可登录，需具备相关管理权限", "Only Admins / Super Admin can log in, and the account needs the relevant permissions"), FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };
            sp.Children.Add(tip);
            var ok = new Button { Content = LocService.T("登录", "Login"), Width = 120, Height = 36, Margin = new Thickness(0, 10, 0, 0) };
            ok.Click += async (_, _) =>
            {
                ok.IsEnabled = false;
                ok.Content = LocService.T("登录中...", "Logging in...");
                try
                {
                    await MainWindow.Api.AdminLogin(_u.Text.Trim(), _p.Password);
                    SettingsService.Set("admin_token", MainWindow.Api.AdminToken);
                    Username = _u.Text.Trim();
                    Password = _p.Password;
                    MessageBox.Show(this, LocService.T("登录成功", "Login successful"), LocService.T("提示", "Notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                    DialogResult = true;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, LocService.T("登录失败：", "Login failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                    ok.IsEnabled = true;
                    ok.Content = LocService.T("登录", "Login");
                }
            };
            sp.Children.Add(ok);
            grid.Children.Add(sp);
            Content = grid;
        }
    }
}

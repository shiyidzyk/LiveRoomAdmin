using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiveRoomAdmin.Pages;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin
{
    public partial class MainWindow : Window
    {
        public static ApiClient Api { get; private set; } = new ApiClient();
        private readonly HomePage _home = new HomePage();
        private readonly LogPage _log = new LogPage();
        private readonly AdminPage _admin = new AdminPage();
        private readonly SettingsPage _settings = new SettingsPage();

        public MainWindow()
        {
            InitializeComponent();
            TxtVersion.Text = VersionInfo.CurrentVersion;
            NavHome.Checked += (_, _) => MainContent.Content = _home;
            NavLog.Checked += (_, _) => MainContent.Content = _log;
            NavAdmin.Checked += (_, _) => MainContent.Content = _admin;
            NavSettings.Checked += (_, _) => { _settings.Reset(); MainContent.Content = _settings; };
            MainContent.Content = _home;

            DateTime lastExpire = DateTime.MinValue;
            Api.SessionExpired += () => Dispatcher.Invoke(async () =>
            {
                if ((DateTime.Now - lastExpire).TotalSeconds < 5) return;
                lastExpire = DateTime.Now;
                SettingsService.Set("admin_token", "");
                MainContent.Content = _admin;
                await Api.EnsureLoginAsync(this);
            });

            Loaded += async (_, _) => await Startup();
        }

        private async Task Startup()
        {
            var host = SettingsService.Get("server_host", "");
            var port = int.Parse(SettingsService.Get("server_port", "3000"));

            if (!string.IsNullOrEmpty(host) && !LiveServer.IsRunning)
            {
                Api.SetServer(host, port);
                TxtServer.Text = $"服务器: {host}:{port}";
            }
            else
            {
                Api.SetServer("127.0.0.1", port);
                TxtServer.Text = "服务器: " + (LocService.IsEn ? "Local (start streaming from Home to run services)" : "本机（点击首页「开始直播」启动服务）");
                DotStatus.Fill = System.Windows.Media.Brushes.Gray;
                TxtConn.Text = LocService.T("未启动", "Not started");
            }

            Api.AdminToken = SettingsService.Get("admin_token", "");
            if (!string.IsNullOrEmpty(Api.AdminToken))
            {
                try { await Api.FetchMe(); }
                catch { Api.AdminToken = ""; }
            }

            await RefreshStatus();
            _home.UpdateStreamInfo();
            _home.StartStream();
            _log.StartPolling();
            _ = PollStatusLoop();
            _ = _home.StartSse();
        }

        private async Task PollStatusLoop()
        {
            while (true)
            {
                try
                {
                    await Task.Delay(3000);
                    await RefreshStatus();
                    if (!string.IsNullOrEmpty(Api.AdminToken))
                        await _admin.RefreshUsers();
                }
                catch { await Task.Delay(3000); }
            }
        }

        public async Task RefreshStatus()
        {
            try
            {
                var (online, viewers, uptime, accounts) = await Api.GetOverview();
                DotStatus.Fill = System.Windows.Media.Brushes.LimeGreen;
                TxtConn.Text = LocService.T("已连接", "Connected");
                TxtOnline.Text = $"{LocService.T("在线", "Online")}: {online}  {LocService.T("观众", "viewers")}: {viewers.Count}";
                if (_home != null) await _home.UpdateOverview(online, viewers, uptime, accounts);
                _home.UpdateStreamInfo();
            }
            catch
            {
                DotStatus.Fill = System.Windows.Media.Brushes.Red;
                TxtConn.Text = LocService.T("未连接", "Disconnected");
                TxtOnline.Text = $"{LocService.T("在线", "Online")}: -";
            }
        }

        public void ShowMessage(string msg, bool error = false)
        {
            MessageBox.Show(this, msg, error ? LocService.T("错误", "Error") : LocService.T("提示", "Notice"),
                MessageBoxButton.OK, error ? MessageBoxImage.Error : MessageBoxImage.Information);
        }

        public void NotifyStatus()
        {
            _ = RefreshStatus();
        }

        public void ApplyLanguage()
        {
            try
            {
                TxtVersion.Text = VersionInfo.CurrentVersion;
                _home.RefreshLangTexts();
                _admin.ApplyLang();
                _ = RefreshStatus();
            }
            catch { }
        }

        protected override void OnClosed(EventArgs e)
        {
            try { if (LiveServer.IsRunning) LiveServer.Stop(); } catch { }
            base.OnClosed(e);
        }
    }
}
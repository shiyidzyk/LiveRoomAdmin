using System;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin.Pages
{
    public partial class SettingsPage : UserControl
    {
        public SettingsPage()
        {
            InitializeComponent();
            MenuServer.IsChecked = true;
            ShowServer();
            Loaded += (_, _) =>
            {
                TxtHost.Text = SettingsService.Get("server_host", "");
                TxtPort.Text = SettingsService.Get("server_port", "3000");
                ChkAutoDetect.IsChecked = SettingsService.Get("auto_detect", "1") == "1";
                TxtAppName.Text = VersionInfo.AppName;
                TxtVersion.Text = VersionInfo.CurrentVersion;
                TxtBuildInfo.Text = VersionInfo.BuildInfo + " · 无浏览器组件 · 局域网直播服务管理";
                TxtChangelog.Text = VersionInfo.Changelog;
                ChkAdminWeb.IsChecked = AdminWebEnabled();
                UpdateAdminWebUrl();
                InitThemeUI();
            };
        }

        private static string AdminWebFlag => System.IO.Path.Combine(LiveServer.ServerDir, "admin_web.json");

        private static bool AdminWebEnabled()
        {
            try { return System.IO.File.Exists(AdminWebFlag); }
            catch { return false; }
        }

        private void ChkAdminWeb_Changed(object sender, RoutedEventArgs e)
        {
            try
            {
                var on = ChkAdminWeb.IsChecked == true;
                var flag = AdminWebFlag;
                if (on)
                {
                    System.IO.Directory.CreateDirectory(LiveServer.ServerDir);
                    System.IO.File.WriteAllText(flag, "{\"enabled\":true}");
                }
                else if (System.IO.File.Exists(flag))
                {
                    System.IO.File.Delete(flag);
                }
                UpdateAdminWebUrl();
                MessageBox.Show(Window.GetWindow(this),
                    on
                        ? "网页端管理页已开启，同局域网访问：http://" + LiveServer.GetLocalIPv4() + ":3000/admin.html"
                        : "网页端管理页已关闭",
                    "提示");
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), "操作失败: " + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateAdminWebUrl()
        {
            if (TxtAdminWebUrl == null) return;
            TxtAdminWebUrl.Text = AdminWebEnabled()
                ? $"已开启：http://{LiveServer.GetLocalIPv4()}:3000/admin.html（同局域网可访问）"
                : "已关闭（默认）。开启后同局域网可通过 http://服务器IP:3000/admin.html 进入管理页（仅本机集成服务生效）";
        }

        public static Task<string> AutoDetectAsync(int port)
        {
            return ServerDiscovery.FindAsync(port, 500);
        }

        public void Reset()
        {
            if (MenuAbout == null || AboutSubMenu == null) return;
            AboutSubMenu.Visibility = Visibility.Collapsed;
            if (LangZh != null && LangEn != null)
            {
                if (LocService.IsEn) LangEn.IsChecked = true;
                else LangZh.IsChecked = true;
            }
            if (MenuServer.IsChecked != true)
                MenuServer.IsChecked = true;
            else
                ShowServer();
        }

        private void Lang_Changed(object sender, RoutedEventArgs e)
        {
            if (LangZh == null || LangEn == null) return;
            string lang = LangEn.IsChecked == true ? "en" : "zh";
            if (lang == LocService.Current) return;
            LocService.Apply(lang);
            if (Window.GetWindow(this) is MainWindow mw) mw.ApplyLanguage();
        }

        private void ShowPanel(ScrollViewer which)
        {
            if (PanelServer == null || PanelAbout == null || PanelAppearance == null) return;
            PanelServer.Visibility = which == PanelServer ? Visibility.Visible : Visibility.Collapsed;
            PanelAppearance.Visibility = which == PanelAppearance ? Visibility.Visible : Visibility.Collapsed;
            PanelAbout.Visibility = which == PanelAbout ? Visibility.Visible : Visibility.Collapsed;
        }

        private void MenuServer_Checked(object sender, RoutedEventArgs e) => ShowPanel(PanelServer);

        private void MenuAppearance_Checked(object sender, RoutedEventArgs e)
        {
            if (AboutSubMenu != null) AboutSubMenu.Visibility = Visibility.Collapsed;
            ShowPanel(PanelAppearance);
        }

        private void MenuAbout_Checked(object sender, RoutedEventArgs e)
        {
            if (AboutSubMenu == null || MenuChangelog == null) return;
            AboutSubMenu.Visibility = Visibility.Visible;
            if (MenuChangelog.IsChecked != true)
                MenuChangelog.IsChecked = true;
            else
                ShowAbout();
        }

        private void MenuAbout_Unchecked(object sender, RoutedEventArgs e)
        {
            if (AboutSubMenu == null) return;
            AboutSubMenu.Visibility = Visibility.Collapsed;
        }

        private void MenuChangelog_Checked(object sender, RoutedEventArgs e) => ShowAbout();

        private void ShowServer() => ShowPanel(PanelServer);

        private void ShowAbout() => ShowPanel(PanelAbout);

        private void InitThemeUI()
        {
            var cur = ThemeService.Current;
            if (TxtTheme != null) TxtTheme.Text = ThemeService.DisplayName(cur);
            UpdateThemeTicks(cur);
        }

        private void UpdateThemeTicks(string key)
        {
            if (TickDefault == null) return;
            TickDefault.Visibility = key == "default" ? Visibility.Visible : Visibility.Collapsed;
            TickMiuix.Visibility = key == "miuix" ? Visibility.Visible : Visibility.Collapsed;
            TickMaterial.Visibility = key == "material" ? Visibility.Visible : Visibility.Collapsed;
            Brush sel = (Brush)Application.Current.Resources["SecondaryBgBrush"];
            ThemeDefault.Background = key == "default" ? sel : Brushes.Transparent;
            ThemeMiuix.Background = key == "miuix" ? sel : Brushes.Transparent;
            ThemeMaterial.Background = key == "material" ? sel : Brushes.Transparent;
        }

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            var cur = ThemeService.Current;
            TxtTheme.Text = ThemeService.DisplayName(cur);
            UpdateThemeTicks(cur);
            ThemePopup.IsOpen = true;
        }

        private void ThemeItem_Click(object sender, RoutedEventArgs e)
        {
            var key = (string)((Button)sender).Tag;
            ThemeService.Apply(key);
            TxtTheme.Text = ThemeService.DisplayName(key);
            UpdateThemeTicks(key);
            ThemePopup.IsOpen = false;
        }

        private async void BtnDetect_Click(object sender, RoutedEventArgs e)
        {
            var portText = TxtPort.Text.Trim();
            int port = 3000;
            if (!int.TryParse(portText, out port) || port <= 0 || port > 65535)
            { MessageBox.Show(Window.GetWindow(this), "端口无效", "提示"); return; }
            BtnDetect.IsEnabled = false;
            BtnDetect.Content = "检测中...";
            TxtTestResult.Text = "正在扫描局域网，请稍候（约 5-10 秒）...";
            try
            {
                var found = await ServerDiscovery.FindAsync(port, 500);
                if (found != null)
                {
                    TxtHost.Text = found;
                    TxtTestResult.Text = $"✅ 自动检测到服务器: {found}，点击「保存并重连」生效";
                }
                else
                {
                    TxtTestResult.Text = "❌ 未检测到直播服务器，请检查：\n① 服务器是否已启动 ② 是否在同一网段 ③ 服务端防火墙是否放行端口";
                }
            }
            finally
            {
                BtnDetect.IsEnabled = true;
                BtnDetect.Content = "立即自动检测";
            }
        }

        private void SaveSettings()
        {
            var host = TxtHost.Text.Trim();
            var portText = TxtPort.Text.Trim();
            int port = 3000;
            if (!int.TryParse(portText, out port) || port <= 0 || port > 65535)
            { MessageBox.Show(Window.GetWindow(this), "端口无效", "提示"); return; }
            SettingsService.Set("server_host", host);
            SettingsService.Set("server_port", port.ToString());
            SettingsService.Set("auto_detect", ChkAutoDetect.IsChecked == true ? "1" : "0");
            MainWindow.Api.SetServer(host, port);
        }

        private async void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            var w = (MainWindow)Window.GetWindow(this);
            await w.RefreshStatus();
            MessageBox.Show(Window.GetWindow(this), "已保存并重连", "提示");
        }

        private async void BtnTest_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings();
            TxtTestResult.Text = "测试中...";
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                var resp = await client.GetAsync(MainWindow.Api.BaseUrl + "/api/room-state");
                TxtTestResult.Text = resp.IsSuccessStatusCode ? "✅ 连接成功，服务器在线" : "⚠ 服务器响应异常: " + (int)resp.StatusCode;
            }
            catch (Exception ex)
            {
                TxtTestResult.Text = "❌ 连接失败: " + ex.Message;
            }
        }
    }
}

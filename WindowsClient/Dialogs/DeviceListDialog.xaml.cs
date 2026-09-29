using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin.Dialogs
{
    public partial class DeviceListDialog : Window
    {
        private readonly ApiClient _api;
        private readonly string _username;

        public DeviceListDialog(Window owner, ApiClient api, string username)
        {
            InitializeComponent();
            Owner = owner;
            _api = api;
            _username = username;
            TxtTitle.Text = LocService.T("登录设备 · ", "Devices · ") + username;
            Loaded += async (_, _) => await LoadDevices();
        }

        public static void Show(Window owner, ApiClient api, string username)
        {
            new DeviceListDialog(owner, api, username).ShowDialog();
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        private async Task LoadDevices()
        {
            try
            {
                var devices = await _api.GetDevices(_username);
                int online = 0;
                foreach (var d in devices) if (d.Online) online++;
                TxtSub.Text = LocService.T("共 ", "Total ") + devices.Count + LocService.T(" 台设备 · ", " devices · ") + online + LocService.T(" 台在线", " online");
                DevicePanel.Children.Clear();
                foreach (var d in devices)
                {
                    var card = new Border
                    {
                        Style = (Style)Application.Current.FindResource("CardStyle"),
                        Margin = new Thickness(0, 0, 8, 12)
                    };
                    var sp = new StackPanel();
                    var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
                    titleRow.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrEmpty(d.Device) ? LocService.T("未知设备", "Unknown device") : d.Device,
                        FontSize = 15, FontWeight = FontWeights.SemiBold,
                        Foreground = Res("TextPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center
                    });
                    if (d.Current)
                        titleRow.Children.Add(new TextBlock
                        {
                            Text = LocService.T("（本机）", " (this device)"), FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                            Foreground = Res("ThemeBrush"), VerticalAlignment = VerticalAlignment.Center
                        });
                    titleRow.Children.Add(new TextBlock
                    {
                        Text = d.Online ? LocService.T("  ● 在线", "  ● Online") : LocService.T("  ● 离线", "  ● Offline"),
                        FontSize = 12, Margin = new Thickness(8, 0, 0, 0),
                        Foreground = d.Online ? new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)) : Res("TextSecondaryBrush"),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    sp.Children.Add(titleRow);
                    sp.Children.Add(InfoLine(LocService.T("设备标识", "Device ID"), d.Sess));
                    sp.Children.Add(InfoLine(LocService.T("IP 地址", "IP Address"), string.IsNullOrEmpty(d.Ip) ? "-" : d.Ip));
                    sp.Children.Add(InfoLine(LocService.T("登录时间", "Login Time"),
                        d.LoginAt > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(d.LoginAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "-"));
                    if (!string.IsNullOrEmpty(d.Ua))
                        sp.Children.Add(InfoLine(LocService.T("客户端", "Client"), TrimUa(d.Ua)));
                    if (d.Current)
                    {
                        sp.Children.Add(new TextBlock
                        {
                            Text = LocService.T("当前正在使用的设备不可下线", "The device you are using cannot be kicked"),
                            FontSize = 12, Margin = new Thickness(0, 10, 0, 0),
                            Foreground = Res("TextSecondaryBrush")
                        });
                    }
                    else
                    {
                        var btn = new Button
                        {
                            Content = LocService.T("强制下线此设备", "Force Offline This Device"),
                            Style = (Style)Application.Current.FindResource("DangerBtnStyle"),
                            Margin = new Thickness(0, 10, 0, 0),
                            Tag = d.Sess
                        };
                        btn.Click += BtnKickDevice_Click;
                        sp.Children.Add(btn);
                    }
                    card.Child = sp;
                    DevicePanel.Children.Add(card);
                }
            }
            catch (Exception ex) { TxtSub.Text = LocService.T("加载失败：", "Load failed: ") + ex.Message; }
        }

        private static StackPanel InfoLine(string label, string value)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            sp.Children.Add(new TextBlock
            {
                Text = label + "：", FontSize = 12,
                Foreground = Res("TextSecondaryBrush")
            });
            sp.Children.Add(new TextBlock
            {
                Text = value, FontSize = 12,
                Foreground = Res("TextPrimaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 440
            });
            return sp;
        }

        private static string TrimUa(string ua)
        {
            ua = ua.Replace('\n', ' ').Trim();
            return ua.Length > 70 ? ua.Substring(0, 70) + "…" : ua;
        }

        private async void BtnKickDevice_Click(object sender, RoutedEventArgs e)
        {
            var sess = (string)((Button)sender).Tag;
            if (MessageBox.Show(this, LocService.T("确认强制下线设备（", "Force offline device (") + sess + LocService.T("）？\n该设备将立即被登出，不影响同账号其他设备。", ")?\nIt will be logged out immediately; other devices of the same account are unaffected."),
                LocService.T("确认", "Confirm"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try
            {
                await _api.KickDevice(_username, sess);
                await LoadDevices();
            }
            catch (Exception ex) { MessageBox.Show(this, LocService.T("操作失败：", "Operation failed: ") + ex.Message); }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await LoadDevices();

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}

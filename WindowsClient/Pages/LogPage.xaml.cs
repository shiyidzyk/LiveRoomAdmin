using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin.Pages
{
    public partial class LogPage : UserControl
    {
        private CancellationTokenSource _cts;
        private bool _polling;

        public LogPage()
        {
            InitializeComponent();
        }

        public void StartPolling()
        {
            if (_polling) return;
            _polling = true;
            _cts = new CancellationTokenSource();
            _ = PollLoop(_cts.Token);
        }

        private async Task PollLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000);
                try { await LoadLogs(); }
                catch { }
            }
        }

        private async Task LoadLogs()
        {
            if (LiveServer.IsRunning)
            {
                var localTxt = LiveServer.TailLog(200);
                await Dispatcher.InvokeAsync(() =>
                {
                    TxtHint.Visibility = Visibility.Collapsed;
                    TxtLog.Text = localTxt;
                    TxtLog.ScrollToEnd();
                });
                return;
            }
            if (string.IsNullOrEmpty(MainWindow.Api.AdminToken))
            {
                Dispatcher.Invoke(() =>
                {
                    TxtHint.Visibility = Visibility.Visible;
                    TxtLog.Text = "";
                });
                return;
            }
            var which = ((ComboBoxItem)CmbSource.SelectedItem)?.Content?.ToString() switch
            {
                "mediamtx.log" => "mediamtx",
                "ffmpeg.log" => "ffmpeg",
                _ => "server"
            };
            var txt = await MainWindow.Api.GetLogs(which);
            await Dispatcher.InvokeAsync(() =>
            {
                TxtHint.Visibility = Visibility.Collapsed;
                TxtLog.Text = txt;
                TxtLog.ScrollToEnd();
            });
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            try { await LoadLogs(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e) => TxtLog.Text = "";
    }
}

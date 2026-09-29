using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LiveRoomAdmin
{
    public partial class App : Application
    {
        public static string LogDir { get; private set; } = ".";

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            LogDir = Path.GetDirectoryName(typeof(App).Assembly.Location) ?? ".";
            WriteLog("=== 程序启动 ===");
            WriteLog("程序路径: " + typeof(App).Assembly.Location);
            WriteLog("OS: " + Environment.OSVersion);
            WriteLog(".NET: " + Environment.Version);
            WriteLog("工作目录: " + Environment.CurrentDirectory);

            // 应用保存的界面主题（须在创建主窗口前，确保初始加载即为所选主题）
            try { Services.ThemeService.Init(); WriteLog("界面主题: " + Services.ThemeService.Current); }
            catch (Exception ex) { WriteLog("主题加载失败: " + ex.Message); }

            // 应用保存的语言（须在创建主窗口前，确保初始加载即为所选语言）
            try { Services.LocService.Init(); WriteLog("界面语言: " + Services.LocService.Current); }
            catch (Exception ex) { WriteLog("语言加载失败: " + ex.Message); }

            // 全局异常捕获
            DispatcherUnhandledException += (s, args) =>
            {
                WriteLog("Dispatcher异常: " + args.Exception);
                try
                {
                    MessageBox.Show("程序发生未处理错误:\n" + args.Exception, "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                WriteLog("AppDomain异常: " + args.ExceptionObject);
            };
            TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                WriteLog("Task异常: " + args.Exception);
                args.SetObserved();
            };

            try
            {
                WriteLog("创建主窗口...");
                var win = new MainWindow();
                WriteLog("主窗口创建成功，开始显示...");
                win.Show();
                WriteLog("窗口已显示");
            }
            catch (Exception ex)
            {
                WriteLog("主窗口创建失败: " + ex);
                try
                {
                    MessageBox.Show("启动失败:\n" + ex, "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                Shutdown(1);
            }
        }

        internal static void WriteLog(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(LogDir, "app.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\r\n");
            }
            catch { }
        }
    }
}

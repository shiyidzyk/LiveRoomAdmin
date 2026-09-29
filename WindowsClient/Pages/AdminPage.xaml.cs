using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveRoomAdmin.Dialogs;
using LiveRoomAdmin.Services;

namespace LiveRoomAdmin.Pages
{
    public partial class AdminPage : UserControl
    {
        public AdminPage()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                if (!string.IsNullOrEmpty(MainWindow.Api.AdminToken)) await LoadPolicy();
            };
        }

        private async Task LoadPolicy()
        {
            try
            {
                var (multi, max) = await MainWindow.Api.GetLoginPolicy();
                ChkMultiEnabled.IsChecked = multi;
                TxtMaxDevices.Text = max.ToString();
            }
            catch { }
        }

        private async void BtnSavePolicy_Click(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (!MainWindow.Api.HasPerm("sessionMgr"))
            {
                MessageBox.Show(win, LocService.T("你没有「多端登录与设备管理」权限，请联系最高管理员授予", "You have no Multi-device & Sessions permission. Contact the Super Admin."), LocService.T("无权限", "No permission"));
                return;
            }
            if (!int.TryParse(TxtMaxDevices.Text.Trim(), out int max) || max < 1) max = 1;
            if (max > 100) max = 100;
            bool multi = ChkMultiEnabled.IsChecked == true;
            try
            {
                await MainWindow.Api.SaveLoginPolicy(multi, max);
                TxtPolicyInfo.Text = LocService.T("已保存：", "Saved: ") + (multi ? LocService.T($"允许多端登录，最多 {max} 台设备同时在线", $"Multi-device login allowed, up to {max} devices") : LocService.T("关闭多端登录（单端，新登录顶号）", "Multi-device login off (single session, new login kicks old)"));
            }
            catch (Exception ex) { MessageBox.Show(win, LocService.T("保存失败：", "Save failed: ") + ex.Message); }
        }

        public void ApplyLang()
        {
            try { _ = RefreshUsers(); } catch { }
            try
            {
                var multi = ChkMultiEnabled.IsChecked == true;
                TxtPolicyInfo.Text = multi
                    ? LocService.T($"允许多端登录，最多 {TxtMaxDevices.Text} 台设备同时在线", $"Multi-device login allowed, up to {TxtMaxDevices.Text} devices")
                    : LocService.T("关闭多端登录（单端，新登录顶号）", "Multi-device login off (single session, new login kicks old)");
            }
            catch { }
        }

        private void BtnDevices_Click(object sender, RoutedEventArgs e)
        {
            var username = (string)((Button)sender).Tag;
            DeviceListDialog.Show(Window.GetWindow(this), MainWindow.Api, username);
        }

        public Task<bool> EnsureLoginAsync()
        {
            return MainWindow.Api.EnsureLoginAsync(Window.GetWindow(this));
        }

        public async Task RefreshUsers()
        {
            if (string.IsNullOrEmpty(MainWindow.Api.AdminToken)) return;
            ChkNewAdmin.IsEnabled = MainWindow.Api.IsSuperAdmin;
            try
            {
                var users = await MainWindow.Api.GetUsers();
                var me = MainWindow.Api;
                var items = new List<UserRow>();
                foreach (var u in users)
                {
                    string status;
                    Brush color;
                    if (u.Banned) { status = LocService.T("已封禁", "Banned"); color = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)); }
                    else if (u.Online) { status = LocService.T("在线", "Online"); color = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)); }
                    else { status = LocService.T("离线", "Offline"); color = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)); }
                    Brush roleColor = u.Role switch
                    {
                        "superadmin" => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
                        "admin" => new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
                        _ => new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80))
                    };
                    string roleName = u.RoleName ?? (u.Role == "superadmin" ? LocService.T("最高管理员", "Super Admin") : u.Role == "admin" ? LocService.T("管理员", "Admin") : LocService.T("观众", "Viewer"));
                    string permSummary = u.Role switch
                    {
                        "superadmin" => LocService.T("全部权限", "All permissions"),
                        "admin" => PermSummaryText(u.Perms),
                        _ => "—"
                    };
                    var canBan = CanOperate(me.AdminRole, u.Role) && me.HasPerm("banUser");
                    var canKick = CanOperate(me.AdminRole, u.Role) && me.HasPerm("kickUser");
                    var canWarn = CanOperate(me.AdminRole, u.Role) && me.HasPerm("warnUser");
                    var canDelete = CanOperate(me.AdminRole, u.Role) && me.HasPerm("deleteUser") && u.Username != me.AdminUsernameSafe();
                    var canPromote = me.IsSuperAdmin && u.Role == "user" && u.Username != me.AdminUsernameSafe();
                    var canDemote = me.IsSuperAdmin && u.Role == "admin" && u.Username != me.AdminUsernameSafe();
                    var canDevices = u.Online && me.HasPerm("deviceKick");
                    items.Add(new UserRow
                    {
                        Username = u.Username,
                        RoleName = roleName,
                        RoleColor = roleColor,
                        Tokens = u.Tokens.ToString(),
                        StatusText = status,
                        StatusColor = color,
                        PermSummary = permSummary,
                        BanVisibility = u.Banned ? Visibility.Collapsed : (canBan ? Visibility.Visible : Visibility.Collapsed),
                        UnbanVisibility = u.Banned ? (canBan ? Visibility.Visible : Visibility.Collapsed) : Visibility.Collapsed,
                        KickVisibility = canKick ? Visibility.Visible : Visibility.Collapsed,
                        WarnVisibility = canWarn ? Visibility.Visible : Visibility.Collapsed,
                        DevicesVisibility = canDevices ? Visibility.Visible : Visibility.Collapsed,
                        DeleteVisibility = canDelete ? Visibility.Visible : Visibility.Collapsed,
                        PromoteVisibility = canPromote ? Visibility.Visible : Visibility.Collapsed,
                        DemoteVisibility = canDemote ? Visibility.Visible : Visibility.Collapsed
                    });
                }
                await Dispatcher.InvokeAsync(() => ListUsers.ItemsSource = items);
                await Dispatcher.InvokeAsync(LoadPermUsers);
            }
            catch { }
        }

        private static bool CanOperate(string operatorRole, string targetRole)
        {
            var lv = new Dictionary<string, int> { { "superadmin", 3 }, { "admin", 2 }, { "user", 1 } };
            return (lv.TryGetValue(operatorRole, out var lo) ? lo : 0) > (lv.TryGetValue(targetRole, out var lt) ? lt : 0);
        }

        private static string PermSummaryText(Dictionary<string, bool> p)
        {
            if (p == null) return LocService.T("默认", "Default");
            var sb = new System.Text.StringBuilder();
            if (p.GetValueOrDefault("createUser")) sb.Append("建 ");
            if (p.GetValueOrDefault("editRole")) sb.Append("角色 ");
            if (p.GetValueOrDefault("banUser")) sb.Append("封 ");
            if (p.GetValueOrDefault("kickUser")) sb.Append("踢 ");
            if (p.GetValueOrDefault("warnUser")) sb.Append("警 ");
            if (p.GetValueOrDefault("roomControl")) sb.Append("直播 ");
            if (p.GetValueOrDefault("resetPass")) sb.Append("改密 ");
            if (p.GetValueOrDefault("deleteUser")) sb.Append("删 ");
            return sb.Length == 0 ? LocService.T("无权限", "No permission") : sb.ToString().Trim();
        }

        private void LoadPermUsers()
        {
            if (CmbPermUser == null) return;
            var selected = CmbPermUser.SelectedItem as string;
            var admins = new List<string>();
            try
            {
                foreach (var item in ListUsers.ItemsSource as IEnumerable<UserRow> ?? new List<UserRow>())
                {
                    if (item.RoleName == "管理员") admins.Add(item.Username);
                }
            }
            catch { }
            CmbPermUser.ItemsSource = admins;
            if (selected != null && admins.Contains(selected)) CmbPermUser.SelectedItem = selected;
            else if (admins.Count > 0) CmbPermUser.SelectedIndex = 0;
            TxtPermInfo.Text = MainWindow.Api.IsSuperAdmin
                ? LocService.T("当前登录：最高管理员，可设置所有管理员权限", "Logged in as Super Admin: can configure all admin permissions")
                : LocService.T("仅最高管理员可设置权限", "Only the Super Admin can manage permissions");
        }

        private async void CmbPermUser_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (CmbPermUser == null || !(CmbPermUser.SelectedItem is string uname)) return;
            try
            {
                var users = await MainWindow.Api.GetUsers();
                foreach (var u in users)
                {
                    if (u.Username == uname && u.Role == "admin" && u.Perms != null)
                    {
                        PmCreateUser.IsChecked = u.Perms.GetValueOrDefault("createUser");
                        PmEditRole.IsChecked = u.Perms.GetValueOrDefault("editRole");
                        PmBanUser.IsChecked = u.Perms.GetValueOrDefault("banUser");
                        PmKickUser.IsChecked = u.Perms.GetValueOrDefault("kickUser");
                        PmWarnUser.IsChecked = u.Perms.GetValueOrDefault("warnUser");
                        PmRoomControl.IsChecked = u.Perms.GetValueOrDefault("roomControl");
                        PmResetPass.IsChecked = u.Perms.GetValueOrDefault("resetPass");
                        PmDeleteUser.IsChecked = u.Perms.GetValueOrDefault("deleteUser");
                        PmSessionMgr.IsChecked = u.Perms.GetValueOrDefault("sessionMgr");
                        PmDeviceKick.IsChecked = u.Perms.GetValueOrDefault("deviceKick");
                        break;
                    }
                }
            }
            catch { }
        }

        private async void BtnSavePerms_Click(object sender, RoutedEventArgs e)
        {
            if (!MainWindow.Api.IsSuperAdmin) { MessageBox.Show(Window.GetWindow(this), LocService.T("仅最高管理员可设置权限", "Only the Super Admin can manage permissions"), LocService.T("提示", "Notice")); return; }
            if (!(CmbPermUser.SelectedItem is string uname)) { MessageBox.Show(Window.GetWindow(this), LocService.T("请先选择管理员账户", "Select an admin account first"), LocService.T("提示", "Notice")); return; }
            var perms = new Dictionary<string, bool>
            {
                ["createUser"] = PmCreateUser.IsChecked == true,
                ["editRole"] = PmEditRole.IsChecked == true,
                ["banUser"] = PmBanUser.IsChecked == true,
                ["kickUser"] = PmKickUser.IsChecked == true,
                ["warnUser"] = PmWarnUser.IsChecked == true,
                ["roomControl"] = PmRoomControl.IsChecked == true,
                ["resetPass"] = PmResetPass.IsChecked == true,
                ["deleteUser"] = PmDeleteUser.IsChecked == true,
                ["sessionMgr"] = PmSessionMgr.IsChecked == true,
                ["deviceKick"] = PmDeviceKick.IsChecked == true
            };
            try
            {
                await MainWindow.Api.SetPerms(uname, perms);
                MessageBox.Show(Window.GetWindow(this), LocService.T("已保存 ", "Saved ") + uname + LocService.T(" 的权限", " permissions"), LocService.T("提示", "Notice"));
                await RefreshUsers();
            }
            catch (Exception ex)
            { MessageBox.Show(Window.GetWindow(this), LocService.T("保存失败: ", "Save failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnCreateUser_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var username = TxtNewUser.Text.Trim();
                var password = TxtNewPass.Text.Trim();
                if (username.Length < 2 || password.Length < 4)
                { MessageBox.Show(Window.GetWindow(this), LocService.T("用户名至少2位，密码至少4位", "Username at least 2 chars, password at least 4"), LocService.T("提示", "Notice")); return; }
                int tokens = 0; int.TryParse(TxtNewTokens.Text.Trim(), out tokens);
                var role = ChkNewAdmin.IsChecked == true && MainWindow.Api.IsSuperAdmin ? "admin" : "user";
                await MainWindow.Api.CreateUser(username, password, tokens, role);
                MessageBox.Show(Window.GetWindow(this), LocService.T("创建成功", "Created"), LocService.T("提示", "Notice"));
                TxtNewUser.Clear(); TxtNewPass.Clear();
                await RefreshUsers();
                ((MainWindow)Window.GetWindow(this)).NotifyStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(Window.GetWindow(this), LocService.T("创建失败: ", "Create failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void BtnRefreshUsers_Click(object sender, RoutedEventArgs e) => await RefreshUsers();

        private async void BtnBan_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            try { await MainWindow.Api.Post("/admin/api/ban", new { username = u }); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnUnban_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            try { await MainWindow.Api.Post("/admin/api/unban", new { username = u }); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnKick_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            try { await MainWindow.Api.Post("/admin/api/kick", new { username = u }); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnWarnUser_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            try { await MainWindow.Api.Post("/admin/api/warn", new { username = u, message = LocService.T("管理员警告你请注意发言", "Admin warning: please mind your language") }); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            if (MessageBox.Show(Window.GetWindow(this), LocService.T("确定删除账号 ", "Delete account ") + u + LocService.T("?此操作不可恢复！", "? This cannot be undone!"), LocService.T("删除确认", "Confirm Delete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            try { await MainWindow.Api.DeleteUser(u); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), LocService.T("删除失败: ", "Delete failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnPromote_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            if (MessageBox.Show(Window.GetWindow(this), LocService.T("确定将 ", "Promote ") + u + LocService.T(" 提升为管理员？", " to admin?"), LocService.T("角色调整", "Role Change"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { await MainWindow.Api.SetRole(u, "admin"); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), LocService.T("操作失败: ", "Operation failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnDemote_Click(object sender, RoutedEventArgs e)
        {
            var u = ((Button)sender).Tag as string;
            if (MessageBox.Show(Window.GetWindow(this), LocService.T("确定将 ", "Demote ") + u + LocService.T(" 降为观众？", " to viewer?"), LocService.T("角色调整", "Role Change"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { await MainWindow.Api.SetRole(u, "user"); await RefreshUsers(); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), LocService.T("操作失败: ", "Operation failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try { await MainWindow.Api.Post("/admin/api/stop", new { }); TxtRoomState.Text = LocService.T("房间状态: 已下播", "Room: Offline"); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnResume_Click(object sender, RoutedEventArgs e)
        {
            try { await MainWindow.Api.Post("/admin/api/resume", new { }); TxtRoomState.Text = LocService.T("房间状态: 直播中", "Room: Live"); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnRestart_Click(object sender, RoutedEventArgs e)
        {
            if (LiveServer.IsRunning)
            {
                LiveServer.RestartAll();
                TxtRoomState.Text = LocService.T("房间状态: 重启中...", "Room: Restarting...");
                return;
            }
            try { await MainWindow.Api.Post("/admin/api/restart", new { }); TxtRoomState.Text = LocService.T("房间状态: 重启中...", "Room: Restarting..."); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnWarnRoom_Click(object sender, RoutedEventArgs e)
        {
            try { await MainWindow.Api.Post("/admin/api/warnroom", new { message = TxtWarnMsg.Text.Trim() }); }
            catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnChangeMyPass_Click(object sender, RoutedEventArgs e)
        {
            var oldP = TxtOldPass.Text.Trim();
            var newP = TxtMyNewPass.Text.Trim();
            if (string.IsNullOrEmpty(oldP) || newP.Length < 4)
            { MessageBox.Show(Window.GetWindow(this), LocService.T("原密码不能为空，新密码至少4位", "Current password required, new password at least 4 chars"), LocService.T("提示", "Notice")); return; }
            try
            {
                await MainWindow.Api.ChangeMyPassword(oldP, newP);
                MessageBox.Show(Window.GetWindow(this), LocService.T("密码修改成功", "Password changed"), LocService.T("提示", "Notice"));
                TxtOldPass.Clear(); TxtMyNewPass.Clear();
            }
            catch (Exception ex)
            { MessageBox.Show(Window.GetWindow(this), LocService.T("修改失败: ", "Change failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async void BtnResetUserPass_Click(object sender, RoutedEventArgs e)
        {
            var user = TxtResetUser.Text.Trim();
            var pass = TxtResetPass.Text.Trim();
            if (user.Length < 2 || pass.Length < 4)
            { MessageBox.Show(Window.GetWindow(this), LocService.T("用户名至少2位，新密码至少4位", "Username at least 2 chars, new password at least 4"), LocService.T("提示", "Notice")); return; }
            try
            {
                await MainWindow.Api.ResetUserPassword(user, pass);
                MessageBox.Show(Window.GetWindow(this), LocService.T("已重置 ", "Password reset for ") + user, LocService.T("提示", "Notice"));
                TxtResetUser.Clear(); TxtResetPass.Clear();
            }
            catch (Exception ex)
            { MessageBox.Show(Window.GetWindow(this), LocService.T("重置失败: ", "Reset failed: ") + ex.Message, LocService.T("错误", "Error"), MessageBoxButton.OK, MessageBoxImage.Error); }
        }
    }

    public class UserRow
    {
        public string Username { get; set; }
        public string RoleName { get; set; }
        public Brush RoleColor { get; set; }
        public string Tokens { get; set; }
        public string StatusText { get; set; }
        public Brush StatusColor { get; set; }
        public string PermSummary { get; set; }
        public Visibility BanVisibility { get; set; }
        public Visibility UnbanVisibility { get; set; }
        public Visibility KickVisibility { get; set; }
        public Visibility WarnVisibility { get; set; }
        public Visibility DevicesVisibility { get; set; }
        public Visibility DeleteVisibility { get; set; }
        public Visibility PromoteVisibility { get; set; }
        public Visibility DemoteVisibility { get; set; }
    }
}

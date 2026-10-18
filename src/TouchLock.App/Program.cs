using System.Diagnostics;
using System.Runtime.InteropServices;
using TouchLock.Shared;
using TouchLock.UI;
namespace TouchLock.App;
internal static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        if (args.SequenceEqual(["--inventory"]))
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Windows = Environment.OSVersion.VersionString, Version = Diagnostics.Version, Inventory = WindowsDevices.Inventory(), Targets = new WindowsDevices().Enumerate() })); return 0;
        }
        if (args.SequenceEqual(["--status"]))
        {
            try { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(ServiceClient.Request(new("status")).GetAwaiter().GetResult())); return 0; } catch { return 2; }
        }
#if DEBUG
        if (args.Length == 2 && args[0] == "--probe-input") { ApplicationConfiguration.Initialize(); Application.Run(new InputProbe(args[1])); return 0; }
        if (args.SequenceEqual(["--test-authentication"])) return Diagnostic.Run().GetAwaiter().GetResult();
        if (args.SequenceEqual(["--test-authentication-with-settings-ui"])) return Diagnostic.Run(verifySettingsUi: true).GetAwaiter().GetResult();
        if (args.SequenceEqual(["--test-bluetooth-settings"])) return Diagnostic.RunBluetoothSettings().GetAwaiter().GetResult();
        if (args.SequenceEqual(["--test-pause"])) { try { ServiceClient.Request(new("test-pause")).GetAwaiter().GetResult(); } catch { } return 0; }
        if (args.Length == 2 && args[0] == "--preview-ui") { ApplicationConfiguration.Initialize(); return Diagnostic.Preview(args[1]); }
#endif
        if (args.Length != 0) return 64;
        using var mutex = new Mutex(true, @"Local\TouchLock.Tray.v2." + Process.GetCurrentProcess().SessionId, out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
        try { Services.Start(); } catch { /* Tray exposes the unavailable service and maintenance path. */ }
        Application.Run(new Tray()); return 0;
    }
}
internal sealed class Tray : ApplicationContext
{
    private readonly NotifyIcon icon;
    private readonly ToolStripMenuItem status = new("TouchLock"), locking = new("锁定触摸"), unlocking = new("解除触摸锁定..."), changing = new("修改管理口令..."), exit = new("退出...");
    private readonly ToolStripMenuItem bluetooth = new("禁止进入 Windows 蓝牙设置") { CheckOnClick = false };
    private readonly ToolStripMenuItem startup = new("开机自启动") { CheckOnClick = false };
    private readonly BluetoothSettingsMonitor bluetoothMonitor = new();
    private readonly System.Windows.Forms.Timer poll = new() { Interval = 1000 };
    private readonly Hotkey hotkey;
    private bool busy, polling;
    private Status? latest;
    public Tray()
    {
        var menu = new ContextMenuStrip(); status.Enabled = false;
        menu.Items.AddRange([status, new ToolStripSeparator(), locking, unlocking, bluetooth, startup, changing, new ToolStripSeparator(), new ToolStripMenuItem("关于 TouchLock", null, (_, _) => About()), new ToolStripSeparator(), exit]);
        icon = new NotifyIcon { Icon = SystemIcons.Shield, Text = "TouchLock", ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) menu.Show(Cursor.Position); };
        locking.Click += async (_, _) => await Lock(); unlocking.Click += async (_, _) => await Authenticate(AuthenticationIntent.Unlock);
        changing.Click += async (_, _) => await Authenticate(AuthenticationIntent.ChangePassword); exit.Click += async (_, _) => await Authenticate(AuthenticationIntent.Exit);
        bluetooth.Click += async (_, _) => await ToggleBluetoothSettings();
        startup.Click += async (_, _) => await Authenticate(latest?.StartupEnabled == true ? AuthenticationIntent.DisableStartup : AuthenticationIntent.EnableStartup);
        hotkey = new Hotkey(async () => await ToggleTouch()); poll.Tick += async (_, _) => await Poll(); poll.Start();
        _ = Poll();
    }
    private void RefreshMenu()
    {
        status.Text = latest == null ? "TouchLock · 服务连接中" : "TouchLock · " + (latest.State switch
        {
            LockState.Locked => "触摸已锁定", LockState.Locking => "正在锁定触摸", LockState.Authenticating => "正在认证", LockState.Restoring => "正在恢复触摸", LockState.Stopping => "正在退出", LockState.Unavailable => latest.Reason.StartsWith("未找到", StringComparison.Ordinal) ? "无可操作触摸设备" : "不可用", _ => "触摸可用"
        });
        icon.Text = status.Text.Length <= 63 ? status.Text : status.Text[..63];
        locking.Enabled = !busy && latest is { State: LockState.Unlocked, Available: true };
        unlocking.Enabled = !busy && latest?.State == LockState.Locked;
        changing.Enabled = exit.Enabled = !busy && latest?.State is LockState.Unlocked or LockState.Unavailable or LockState.Locked;
        bluetooth.Enabled = !busy && latest is { Stopped: false, State: LockState.Unlocked or LockState.Unavailable or LockState.Locked };
        bool policyChanged = bluetooth.Checked != (latest?.BluetoothSettingsBlocked == true);
        bluetooth.Checked = latest?.BluetoothSettingsBlocked == true;
        if (policyChanged) _ = Task.Run(WindowsSettingsPolicy.NotifyChange); // Notify windows in the interactive session, not service session 0.
        bluetoothMonitor.Enabled = bluetooth.Checked;
        startup.Checked = latest?.StartupEnabled == true;
        startup.Enabled = !busy && latest is { Stopped: false, StartupError.Length: 0, State: LockState.Unlocked or LockState.Unavailable or LockState.Locked };
        bluetooth.Text = "禁止进入 Windows 蓝牙设置" + (bluetoothMonitor.Error.Length > 0 || latest?.BluetoothSettingsError.Length > 0 ? "（异常，查看关于）" : "");
    }
    private async Task Poll()
    {
        if (polling) return; polling = true;
        try { latest = (await ServiceClient.Request(new("status"))).Status; if (latest.Stopped && !busy) ExitThread(); }
        catch { latest = new(LockState.Unavailable, 0, false, "服务未运行。请重新打开 TouchLock，或运行安装程序维护。", BluetoothSettingsBlocked: latest?.BluetoothSettingsBlocked == true, BluetoothSettingsError: latest?.BluetoothSettingsError ?? "", StartupEnabled: latest?.StartupEnabled == true, StartupError: "服务未连接，无法确认自启动状态。"); }
        finally { polling = false; RefreshMenu(); }
    }
    private async Task Lock()
    {
        if (busy) return;
        busy = true; RefreshMenu();
        try
        {
            var result = await ServiceClient.Complete(await ServiceClient.RequestWhenIdle(new("lock"))); latest = result.Status;
            if (!result.Success) MessageBox.Show(result.Error, "TouchLock");
        }
        catch { MessageBox.Show("无法确认锁定结果，请检查托盘状态及诊断日志。", "TouchLock"); }
        finally { busy = false; RefreshMenu(); }
    }
    private async Task ToggleTouch()
    {
        if (busy) return;
        busy = true; RefreshMenu();
        try
        {
            var result = await ServiceClient.Complete(await ServiceClient.RequestWhenIdle(new("refresh"))); latest = result.Status;
            if (!result.Success) return;
        }
        catch { return; }
        finally { busy = false; RefreshMenu(); }
        if (latest?.State == LockState.Locked) await Authenticate(AuthenticationIntent.Unlock);
        else if (latest is { State: LockState.Unlocked, Available: true }) await Lock();
    }
    private async Task ToggleBluetoothSettings()
    {
        if (busy || latest == null) return;
        if (latest.BluetoothSettingsBlocked) { await Authenticate(AuthenticationIntent.AllowBluetoothSettings); return; }
        busy = true; RefreshMenu();
        try
        {
            var result = await ServiceClient.Request(new("block-bluetooth-settings")); latest = result.Status;
            if (!result.Success) MessageBox.Show(result.Error, "TouchLock");
        }
        catch { MessageBox.Show("无法保存蓝牙设置限制。请稍后重试，或运行安装程序维护。", "TouchLock"); }
        finally { busy = false; RefreshMenu(); }
    }
    private async Task Authenticate(AuthenticationIntent intent)
    {
        if (busy) return; busy = true; RefreshMenu(); Guid request = Guid.Empty;
        try
        {
            var started = await ServiceClient.RequestWhenIdle(new("begin", Intent: intent));
            if (!started.Success) { MessageBox.Show(started.Error, "TouchLock"); return; }
            request = started.RequestId;
            using var dialog = new PasswordDialog(change: intent == AuthenticationIntent.ChangePassword, exit: intent == AuthenticationIntent.Exit, allowBluetoothSettings: intent == AuthenticationIntent.AllowBluetoothSettings,
                actionText: intent == AuthenticationIntent.EnableStartup ? "开启开机自启动" : intent == AuthenticationIntent.DisableStartup ? "关闭开机自启动" : null);
            if (dialog.ShowDialog() != DialogResult.OK) return;
            var result = await ServiceClient.Submit(request, dialog.Password, dialog.NewPassword); request = Guid.Empty;
            result = await ServiceClient.Complete(result); latest = result.Status;
            if (!result.Success) MessageBox.Show(result.Error, "TouchLock");
            else if (intent == AuthenticationIntent.Exit && result.Status.Stopped) ExitThread();
        }
        catch { MessageBox.Show("无法完成认证。请稍后重试，或运行安装程序维护。", "TouchLock"); }
        finally
        {
            if (request != Guid.Empty) { try { latest = (await ServiceClient.Request(new("cancel", RequestId: request))).Status; } catch { } }
            busy = false; RefreshMenu();
        }
    }
    private void About()
    {
        using var form = new AboutDialog(latest, hotkey.Registered, bluetoothMonitor.Error);
        form.ShowDialog();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { poll.Stop(); poll.Dispose(); bluetoothMonitor.Dispose(); hotkey.Dispose(); icon.Visible = false; icon.Dispose(); }
        base.Dispose(disposing);
    }
}
internal sealed class Hotkey : NativeWindow, IDisposable
{
    [DllImport("user32", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32")] private static extern bool UnregisterHotKey(nint window, int id);
    private readonly Action action;
    public bool Registered { get; }
    public Hotkey(Action action) { this.action = action; CreateHandle(new CreateParams { Parent = -3 }); Registered = RegisterHotKey(Handle, 1, 1 | 2 | 8 | 0x4000, 13); }
    protected override void WndProc(ref Message message) { if (message.Msg == 0x312) action(); base.WndProc(ref message); }
    public void Dispose() { if (Registered) UnregisterHotKey(Handle, 1); DestroyHandle(); }
}

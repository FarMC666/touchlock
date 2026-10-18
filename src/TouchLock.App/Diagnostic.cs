#if DEBUG
using System.Security.Cryptography;
using TouchLock.Shared;
namespace TouchLock.App;
internal static class Diagnostic
{
    public static int Preview(string directory)
    {
        Directory.CreateDirectory(directory);
        TouchLock.UI.AdaptiveForm.VerifyLayout(() => new AboutDialog(new Status(LockState.Unavailable, 0, false, string.Concat(Enumerable.Repeat("设备状态异常，请检查日志。 ", 30))), false, "蓝牙设置策略读取失败。"), directory, "about");
        TouchLock.UI.AdaptiveForm.VerifyLayout(() => new TouchLock.UI.PasswordDialog(change: true), directory, "password");
        foreach (string mode in new[] { "initial", "change", "unlock", "uninstall", "bluetooth" })
        {
            using var form = new TouchLock.UI.PasswordDialog(initialize: mode == "initial", change: mode == "change", uninstall: mode == "uninstall", allowBluetoothSettings: mode == "bluetooth");
            string sample = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            form.ShowInTaskbar = false; form.Opacity = 0;
            form.Shown += (_, _) =>
            {
                form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, mode + ".png"));
                foreach (var box in Descendants(form).OfType<TextBox>()) box.Text = sample;
                ((Button)form.AcceptButton!).PerformClick();
            };
            form.ShowDialog();
            byte[] expected = System.Text.Encoding.UTF8.GetBytes(sample);
            try
            {
                if (form.DialogResult != DialogResult.OK || !CryptographicOperations.FixedTimeEquals(mode is "unlock" or "uninstall" or "bluetooth" ? form.Password : form.NewPassword, expected)) throw new InvalidOperationException($"UI password capture failed: result={form.DialogResult}, bytes={form.NewPassword.Length}/{form.Password.Length}, expected={expected.Length}.");
                Console.WriteLine("PASS UI password input " + mode);
            }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }
        return 0;
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            yield return control;
            foreach (var nested in Descendants(control)) yield return nested;
        }
    }
    public static async Task<int> RunBluetoothSettings()
    {
        try
        {
            void Check(bool result, string label) { if (!result) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
            async Task Wait(Func<bool> ready, string label)
            {
                long until = Environment.TickCount64 + 10000;
                while (!await Task.Run(ready)) { if (Environment.TickCount64 >= until) throw new TimeoutException(label); await Task.Delay(100); }
            }
            void Open(string uri) { using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
            bool BluetoothOpen()
            {
                try { return BluetoothSettingsMonitor.SettingsWindows().Any(BluetoothSettingsMonitor.IsBluetoothPage); }
                catch (System.Windows.Automation.ElementNotAvailableException) { return false; } // Window closed while the test was inspecting it.
            }
            Check(BluetoothSettingsMonitor.IsBluetoothControl("SystemSettings_Device_BluetoothRadioToggle_ToggleSwitch") && BluetoothSettingsMonitor.IsBluetoothControl("SystemSettings_Device_BluetoothLegacyLink_Hyperlink"), "Windows 10/11 Bluetooth page identifiers");
            Check(!BluetoothSettingsMonitor.IsBluetoothControl("Bluetooth") && !BluetoothSettingsMonitor.IsBluetoothControl("SystemSettings_Devices_MouseSpeed") && !BluetoothSettingsMonitor.IsBluetoothControl("SystemSettings_Device_BluetoothRadioToggleOther"), "navigation labels and unrelated device controls ignored");
            using var monitor = new BluetoothSettingsMonitor();
            Open("ms-settings:bluetooth"); await Wait(BluetoothOpen, "open Bluetooth page");
            await Task.Delay(750); Check(await Task.Run(BluetoothOpen), "unchecked option allows Bluetooth settings");
            monitor.Enabled = true;
            await Wait(() => BluetoothSettingsMonitor.SettingsWindows().Count == 0, "close existing Bluetooth page");
            Check(true, "checking option closes an already open Bluetooth settings window");
            foreach (string uri in new[] { "ms-settings:display", "ms-settings:printers", "ms-settings:mousetouchpad" })
            {
                Open(uri); await Wait(() => BluetoothSettingsMonitor.SettingsWindows().Count > 0, "open " + uri);
                await Task.Delay(1000); Check(BluetoothSettingsMonitor.SettingsWindows().Count > 0 && !await Task.Run(BluetoothOpen), "unrelated settings remain open: " + uri);
            }
            Open("ms-settings:bluetooth"); await Wait(() => BluetoothSettingsMonitor.SettingsWindows().Count == 0, "close navigated Bluetooth page");
            Check(true, "navigating from another settings page to Bluetooth closes the window");
            Open("ms-settings:bluetooth"); await Task.Delay(1500); await Wait(() => BluetoothSettingsMonitor.SettingsWindows().Count == 0, "close reopened Bluetooth page");
            Check(true, "reopening Bluetooth settings is still blocked");
            monitor.Enabled = false;
            Open("ms-settings:bluetooth"); await Wait(BluetoothOpen, "allow Bluetooth again");
            await Task.Delay(750); Check(await Task.Run(BluetoothOpen) && monitor.Error.Length == 0, "unchecking option allows Bluetooth settings again");
            // Leave the test-opened window closed without changing Bluetooth hardware or pairing.
            monitor.Enabled = true; await Wait(() => BluetoothSettingsMonitor.SettingsWindows().Count == 0, "close test window");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.GetType().Name + " " + e.Message); return 1; }
    }
    public static async Task<int> Run(bool verifySettingsUi = false)
    {
        byte[] password = await Wire.ReadSecret(Console.OpenStandardInput(), CancellationToken.None);
        byte[] next = RandomNumberGenerator.GetBytes(24); next = System.Text.Encoding.UTF8.GetBytes(Convert.ToHexString(next));
        try
        {
            async Task<ControlReply> Begin(AuthenticationIntent intent) => await ServiceClient.RequestWhenIdle(new("begin", Intent: intent));
            void Check(bool result, string label) { if (!result) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
            var start = await ServiceClient.Request(new("status"));
            Check(start.Status.State is LockState.Unlocked or LockState.Unavailable, "service starts unlocked");
            var challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "exit authentication starts");
            var wrong = await ServiceClient.Submit(challenge.RequestId, [], []); Check(!wrong.Success && wrong.Status.State != LockState.Authenticating && !wrong.Status.Stopped, "wrong password keeps service running");
            var replay = await ServiceClient.Submit(challenge.RequestId, password, []); Check(!replay.Success, "expired request rejected");
            challenge = await Begin(AuthenticationIntent.Exit);
            var cancel = await ServiceClient.Request(new("cancel", RequestId: challenge.RequestId)); Check(cancel.Success && !cancel.Status.Stopped, "cancel restores previous state");
            challenge = await Begin(AuthenticationIntent.ChangePassword);
            var change = await ServiceClient.Submit(challenge.RequestId, password, next); Check(change.Success, "password change succeeds");
            challenge = await Begin(AuthenticationIntent.ChangePassword);
            change = await ServiceClient.Submit(challenge.RequestId, next, password); Check(change.Success, "new password verified and original restored");
            var policy = new WindowsSettingsPolicy();
            string? beforePolicy = policy.Read();
            async Task SettingsPage(string uri, bool bluetoothPage)
            {
                await Task.Run(WindowsSettingsPolicy.NotifyChange);
                long until = Environment.TickCount64 + 15000;
                while (true)
                {
                    try { using var opened = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); }
                    catch (System.ComponentModel.Win32Exception e) when (!bluetoothPage && uri != "ms-settings:display")
                    {
                        // Some builds reject a hidden URI instead of redirecting. Still require a visible non-Bluetooth page.
                        Console.WriteLine($"INFO restricted URI activation result 0x{e.NativeErrorCode:X}: {uri}");
                    }
                    await Task.Delay(750);
                    bool expected = await Task.Run(() =>
                    {
                        try
                        {
                            var windows = BluetoothSettingsMonitor.SettingsWindows();
                            return windows.Count > 0 && windows.Any(BluetoothSettingsMonitor.IsBluetoothPage) == bluetoothPage;
                        }
                        catch (System.Windows.Automation.ElementNotAvailableException) { return false; }
                        catch (System.Runtime.InteropServices.COMException) { return false; }
                    });
                    if (expected) { Check(true, "real Settings navigation " + uri + (bluetoothPage ? " accessible" : " stays outside Bluetooth page")); return; }
                    if (Environment.TickCount64 >= until) throw new TimeoutException("Settings policy navigation: " + uri);
                }
            }
            if (verifySettingsUi)
            {
                await SettingsPage("ms-settings:bluetooth", true);
                await SettingsPage("ms-settings:connecteddevices", true);
            }
            var blocked = await ServiceClient.Request(new("block-bluetooth-settings"));
            Check(blocked.Success && blocked.Status.BluetoothSettingsBlocked && policy.Read() == RestrictionsStore.Block(beforePolicy), "real SYSTEM IPC applies machine Bluetooth page policy");
            if (verifySettingsUi)
            {
                await SettingsPage("ms-settings:display", false);
                await SettingsPage("ms-settings:bluetooth", false);
                await SettingsPage("ms-settings:connecteddevices", false);
            }
            else Console.WriteLine("SKIP interactive Settings URI verification; opt in with -VerifySettingsUi on an interactive test computer.");
            challenge = await Begin(AuthenticationIntent.AllowBluetoothSettings);
            var denied = await ServiceClient.Submit(challenge.RequestId, [], []);
            Check(!denied.Success && policy.Read() == RestrictionsStore.Block(beforePolicy), "wrong Bluetooth password retains actual registry policy");
            challenge = await Begin(AuthenticationIntent.AllowBluetoothSettings);
            var allowed = await ServiceClient.Submit(challenge.RequestId, password, []);
            Check(allowed.Success && !allowed.Status.BluetoothSettingsBlocked && policy.Read() == beforePolicy, "authorized Bluetooth cleanup restores actual registry policy");
            if (verifySettingsUi && beforePolicy == null)
            {
                await SettingsPage("ms-settings:bluetooth", true);
                using var closeTestPage = new BluetoothSettingsMonitor(); closeTestPage.Enabled = true;
                await Task.Delay(750);
            }
            var startup = new WindowsStartupConfiguration();
            bool beforeStartup = startup.Read();
            challenge = await Begin(AuthenticationIntent.EnableStartup);
            var automatic = await ServiceClient.Submit(challenge.RequestId, password, []);
            Check(automatic.Success && automatic.Status.StartupEnabled && startup.Read(), "authenticated IPC enables actual SCM automatic start");
            challenge = await Begin(beforeStartup ? AuthenticationIntent.EnableStartup : AuthenticationIntent.DisableStartup);
            var originalStartup = await ServiceClient.Submit(challenge.RequestId, password, []);
            Check(originalStartup.Success && startup.Read() == beforeStartup, "authenticated IPC restores original SCM startup setting");
            for (int i = 0; i < 5; i++)
            {
                challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "wrong-password trial " + (i + 1));
                Check(!(await ServiceClient.Submit(challenge.RequestId, [], [])).Success, "wrong password rejected " + (i + 1));
            }
            Check(!(await Begin(AuthenticationIntent.Exit)).Success, "five failures trigger cooldown");
            await Task.Delay(31000);
            challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "cooldown expires");
            var exit = await ServiceClient.Complete(await ServiceClient.Submit(challenge.RequestId, password, [])); Check(exit.Success && exit.Status.Stopped, "correct password authorizes exit after completed recovery"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.GetType().Name + " " + e.Message); return 1; }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(next); }
    }
}
#endif

using System.Diagnostics;
using TouchLock.Shared;
using TouchLock.UI;
namespace TouchLock.Setup;
internal static class Program
{
    [STAThread] public static int Main(string[] args)
    {
        if (!WindowsSecurity.Elevated) return 5;
        ApplicationConfiguration.Initialize();
        // Run maintenance outside the installed directory so uninstall can remove every installed file.
        if (Environment.ProcessPath!.StartsWith(Paths.Install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            string temporary = Path.Combine(Path.GetTempPath(), "TouchLock-Maintenance-" + Guid.NewGuid().ToString("N")); Paths.NoLinks(temporary);
            Directory.CreateDirectory(temporary); WindowsSecurity.ProtectDirectory(temporary, false);
            string executable = Path.Combine(temporary, "TouchLock.Setup.exe"); File.Copy(Environment.ProcessPath, executable);
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, ArgumentList = { "--maintenance", "--wait-parent", Environment.ProcessId.ToString() } }); return 0;
        }
        if (args.Length == 3 && args[0] == "--maintenance" && args[1] == "--wait-parent" && int.TryParse(args[2], out int parent))
        {
            try { using var p = Process.GetProcessById(parent); if (!p.WaitForExit(15000)) return 6; } catch (ArgumentException) { }
        }
#if DEBUG
        if (args.SequenceEqual(["--test-install"])) return TestInstall();
        if (args.Length == 2 && args[0] == "--test-install") return TestInstall(directory: args[1]);
        if (args.SequenceEqual(["--test-reset"])) return TestInstall(reset: true);
        if (args.SequenceEqual(["--test-uninstall"])) return TestUninstall(true);
        if (args.SequenceEqual(["--test-uninstall-keep"])) return TestUninstall(false);
        if (args.SequenceEqual(["--test-uninstall-cancel"])) return TestCancelUninstall();
        if (args.Length == 2 && args[0] == "--preview-setup")
        {
            using var form = new SetupForm { ShowInTaskbar = false, Opacity = 0 };
            form.Shown += (_, _) => { using var bitmap = new Bitmap(form.Width, form.Height); form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[1]); form.Close(); };
            Application.Run(form); return 0;
        }
#endif
        using var mutex = new Mutex(true, @"Global\TouchLock.Setup.v2", out bool first);
        if (!first) { MessageBox.Show("TouchLock 安装程序已经运行。", "TouchLock"); return 0; }
        try { Application.Run(new SetupForm()); return 0; }
        catch (Exception e) { MessageBox.Show(e.Message, "TouchLock 安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
        finally
        {
            string directory = Path.GetDirectoryName(Environment.ProcessPath)!;
            if (directory.StartsWith(Path.Combine(Path.GetTempPath(), "TouchLock-Maintenance-"), StringComparison.OrdinalIgnoreCase))
            { WindowsSecurity.MoveFileExW(Environment.ProcessPath!, null, 4); WindowsSecurity.MoveFileExW(directory, null, 4); }
        }
    }
#if DEBUG
    private static int TestInstall(bool reset = false, string? directory = null)
    {
        byte[] password = Wire.ReadSecret(Console.OpenStandardInput(), CancellationToken.None).GetAwaiter().GetResult();
        try { if (reset) Installer.Reset(password); else Installer.Install(password, directory); return 0; } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(password); }
    }
    private static int TestUninstall(bool deleteConfiguration)
    {
        byte[] password = Wire.ReadSecret(Console.OpenStandardInput(), CancellationToken.None).GetAwaiter().GetResult();
        try { Installer.Uninstall(deleteConfiguration, password); return 0; }
        catch (UnauthorizedAccessException e) { Console.Error.WriteLine(e.Message); return 10; }
        catch (IOException e) { Console.Error.WriteLine(e.Message); return 11; }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(password); }
    }
    private static int TestCancelUninstall()
    {
        using var form = new SetupForm { ShowInTaskbar = false, Opacity = 0 };
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        bool cancelled = false;
        timer.Tick += (_, _) =>
        {
            var dialog = Application.OpenForms.OfType<PasswordDialog>().FirstOrDefault();
            if (dialog == null) return;
            timer.Stop(); cancelled = true; ((Button)dialog.CancelButton!).PerformClick();
        };
        form.Shown += (_, _) => { timer.Start(); form.BeginInvoke(() => { form.TestUninstallClick(); form.Close(); }); };
        Application.Run(form); return cancelled ? 0 : 12;
    }
#endif
}
internal sealed class SetupForm : Form
{
    private readonly Button install = new() { Text = "安装并设置管理口令", Width = 380, Height = 40 };
    private readonly Button reset = new() { Text = "重置忘记的管理口令", Width = 380, Height = 40 };
    private readonly Button uninstall = new() { Text = "卸载 TouchLock", Width = 380, Height = 40 };
    private readonly CheckBox delete = new() { Text = "卸载时同时删除口令配置", AutoSize = true };
    private readonly TextBox location = new() { Width = 390 };
    private readonly Button browse = new() { Text = "浏览...", Width = 85, Height = 33 };
    public SetupForm()
    {
        Text = "TouchLock 1.0.0 安装与维护"; ClientSize = new Size(550, 490); StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 10); FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(25), FlowDirection = FlowDirection.TopDown, WrapContents = false }; Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "TouchLock\n临时锁定触摸，保留鼠标和键盘操作。\n无需驱动证书或测试模式。", AutoSize = true, Margin = new Padding(0, 0, 0, 15) });
        layout.Controls.Add(new Label { Text = "安装位置", AutoSize = true });
        var locationRow = new Panel { Width = 490, Height = 38 }; location.Top = 4; browse.Left = 400;
        locationRow.Controls.Add(location); locationRow.Controls.Add(browse); layout.Controls.Add(locationRow);
        layout.Controls.Add(new Label { Text = "可输入路径，或浏览选择上级文件夹。\n请选择本地 NTFS 磁盘上的新文件夹。", AutoSize = true, MaximumSize = new Size(490, 0), Font = new Font(Font.FontFamily, 9), ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 12) });
        layout.Controls.Add(install); layout.Controls.Add(reset); layout.Controls.Add(delete); layout.Controls.Add(uninstall);
        bool installed = Services.Exists() || InstallationDirectory.Registered || Directory.Exists(Paths.Install) || Services.Exists("TouchLockFilter");
        location.Text = installed ? Paths.Install : InstallationDirectory.Default;
        location.ReadOnly = installed; browse.Enabled = !installed;
        browse.Click += (_, _) =>
        {
            using var chooser = new FolderBrowserDialog { Description = "选择存放 TouchLock 的上级文件夹", UseDescriptionForTitle = true, SelectedPath = Path.GetPathRoot(location.Text) ?? InstallationDirectory.Default };
            if (chooser.ShowDialog(this) == DialogResult.OK) location.Text = Path.Combine(chooser.SelectedPath, "TouchLock");
        };
        install.Enabled = !installed; reset.Enabled = uninstall.Enabled = Installer.CurrentInstallation;
        if (installed && !Installer.CurrentInstallation) layout.Controls.Add(new Label { AutoSize = true, Text = "检测到旧版或不完整安装，请先使用原安装程序卸载。" });
        install.Click += (_, _) => Execute(() =>
        {
            string selected = InstallationDirectory.ValidateNew(location.Text);
            using var dialog = new PasswordDialog(initialize: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            Installer.Install(dialog.NewPassword, selected); MessageBox.Show(this, "安装完成。TouchLock 已进入系统托盘。", "TouchLock"); Close();
        });
        reset.Click += (_, _) => Execute(() =>
        {
            // Recover first, even if the password dialog is later cancelled.
            Installer.StopAndRestore();
            using var dialog = new PasswordDialog(initialize: true);
            if (dialog.ShowDialog(this) == DialogResult.OK) { Installer.Reset(dialog.NewPassword); MessageBox.Show(this, "管理口令已重置，触摸已恢复。", "TouchLock"); }
            else { try { Services.Start(); } catch { } }
        });
        uninstall.Click += (_, _) => Execute(() =>
        {
            using var dialog = new PasswordDialog(uninstall: true);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            Installer.Uninstall(delete.Checked, dialog.Password);
            MessageBox.Show(this, "TouchLock 已卸载，触摸已恢复。", "TouchLock"); Close();
        });
    }
#if DEBUG
    internal void TestUninstallClick() => uninstall.PerformClick();
#endif
    private void Execute(Action action)
    {
        Enabled = false;
        try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, "TouchLock", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally { if (!IsDisposed) Enabled = true; }
    }
}

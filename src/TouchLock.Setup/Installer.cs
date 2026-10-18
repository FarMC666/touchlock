using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;
using TouchLock.Shared;
namespace TouchLock.Setup;
internal static class Installer
{
    private const string RegistryPath = InstallationDirectory.RegistryPath;
    public static bool CurrentInstallation => File.Exists(Path.Combine(Paths.Install, "TouchLock.Service.exe")) && File.Exists(Path.Combine(Paths.Install, "TouchLock.Recovery.exe")) && FileVersionInfo.GetVersionInfo(Path.Combine(Paths.Install, "TouchLock.Service.exe")).ProductVersion?.Split('+')[0] == "1.0.0" && !Services.Exists("TouchLockFilter");
    private static void RequireAdmin() { if (!WindowsSecurity.Elevated) throw new UnauthorizedAccessException("请以管理员身份运行安装程序。"); Paths.NoLinks(Paths.Install); Paths.NoLinks(Paths.Data); }
    private static void Sc(params string[] args)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var p = Process.Start(start) ?? throw new IOException();
        string output = p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30000)) { p.Kill(); throw new IOException("Windows 服务操作超时。"); }
        if (p.ExitCode != 0) throw new IOException("Windows 服务操作失败：" + output.Trim());
    }
    public static void Install(byte[] password, string? directory = null)
    {
        RequireAdmin();
        if (Services.Exists() || InstallationDirectory.Registered || Directory.Exists(Paths.Install) || Services.Exists("TouchLockFilter")) throw new IOException("检测到已安装的 TouchLock，请先卸载旧版本。不会覆盖现有安装。");
        string install = InstallationDirectory.ValidateNew(directory ?? InstallationDirectory.Default);
        using var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("TouchLock.Payload.zip") ?? throw new IOException("安装程序未包含应用文件，请使用完整发布安装包。");
        using var zip = new ZipArchive(payload, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
            if (entry.FullName.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entry.FullName) || entry.FullName.Contains(':')) throw new InvalidDataException();
        Paths.PrepareData();
        WindowsSecurity.WithDeviceMutex(() => new DeviceTransaction(new WindowsDevices(), Paths.Journal).Restore());
        new RestrictionsStore(Paths.Restrictions).Restore();
        // Compute and validate before creating a partially installed service.
        var record = PasswordStore.Derive(password);
        InstallationDirectory.Prepare(install);
        bool created = false;
        try
        {
            foreach (var entry in zip.Entries)
            {
                string target = Path.GetFullPath(Path.Combine(install, entry.FullName));
                if (!target.StartsWith(install + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); entry.ExtractToFile(target);
            }
            File.Copy(Environment.ProcessPath!, Path.Combine(install, "TouchLock.Setup.exe"));
            using var key = Registry.LocalMachine.CreateSubKey(RegistryPath);
            key.SetValue("InstallLocation", install); // Maintenance must locate this installation even after a failed start.
            Sc("create", "TouchLock", "binPath=", '"' + Path.Combine(install, "TouchLock.Service.exe") + '"', "start=", "demand", "DisplayName=", "TouchLock"); created = true;
            Sc("description", "TouchLock", "TouchLock 临时触摸锁定及故障恢复服务");
            Sc("sdset", "TouchLock", "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCRP;;;IU)");
            Sc("failure", "TouchLock", "reset=", "86400", "actions=", "restart/10000/restart/10000/restart/60000");
            AtomicFile.Write(Paths.Password, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(record)); WindowsSecurity.ProtectFile(Paths.Password);
            key.SetValue("DisplayName", "TouchLock"); key.SetValue("DisplayVersion", "1.0.0"); key.SetValue("Publisher", "TouchLock");
            key.SetValue("DisplayIcon", Path.Combine(install, "TouchLock.exe"));
            key.SetValue("UninstallString", '"' + Path.Combine(install, "TouchLock.Setup.exe") + "\" --maintenance");
            key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
            Services.Start(); AddShortcut();
        }
        catch
        {
            if (created) { try { StopAndRestore(); Sc("delete", "TouchLock"); } catch { throw new IOException("安装未完成，请重新运行安装程序维护；已保留恢复记录。"); } }
            Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
            if (Directory.Exists(install)) Directory.Delete(install, true);
            throw;
        }
    }
    private static void AddShortcut()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TouchLock");
        Paths.NoLinks(directory); Directory.CreateDirectory(directory);
        Type shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new IOException("无法创建开始菜单快捷方式。");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try { dynamic link = shell.CreateShortcut(Path.Combine(directory, "TouchLock.lnk")); try { link.TargetPath = Path.Combine(Paths.Install, "TouchLock.exe"); link.WorkingDirectory = Paths.Install; link.Save(); } finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link); } }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    public static void StopAndRestore()
    {
        RequireAdmin();
        if (!CurrentInstallation) throw new IOException("请使用原安装程序卸载旧版本 TouchLock；当前安装程序不维护旧驱动版。");
        Paths.PrepareData();
        if (Services.Exists() && Services.ProcessId() != 0)
        {
            try { Sc("stop", "TouchLock"); } catch { if (Services.ProcessId() != 0) throw; }
            var timeout = Stopwatch.StartNew();
            while (Services.ProcessId() != 0) { if (timeout.Elapsed.TotalSeconds > 40) throw new IOException("服务仍在恢复触摸，请稍后重试。"); Thread.Sleep(200); }
        }
        WindowsSecurity.WithDeviceMutex(() => new DeviceTransaction(new WindowsDevices(), Paths.Journal).Restore());
        foreach (var p in Process.GetProcessesByName("TouchLock.Recovery"))
        {
            using (p) { using var h = WindowsSecurity.OpenProcess(0x1000 | 0x100000, false, (uint)p.Id); if (!h.IsInvalid && WindowsSecurity.Image(h).Equals(Path.Combine(Paths.Install, "TouchLock.Recovery.exe"), StringComparison.OrdinalIgnoreCase) && !p.WaitForExit(35000)) throw new IOException("恢复进程仍在运行，请稍后重试。"); }
        }
        new RestrictionsStore(Paths.Restrictions).Restore();
    }
    public static void Reset(byte[] password)
    {
        RequireAdmin(); StopAndRestore(); new PasswordStore(Paths.Password).Set(password); WindowsSecurity.ProtectFile(Paths.Password); Services.Start();
    }
    public static void Uninstall(bool deleteConfiguration, ReadOnlySpan<byte> password)
    {
        RequireAdmin();
        bool verified;
        try { verified = new PasswordStore(Paths.Password).Verify(password); }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { throw new IOException("无法验证管理口令。请通过维护界面重置口令后再卸载。", e); }
        if (!verified) throw new UnauthorizedAccessException("管理口令不正确，未卸载 TouchLock。");
        // Authentication must precede every operation that stops the service or restores touch.
        StopAndRestore();
        string install = Paths.Install;
        foreach (var p in Process.GetProcessesByName("TouchLock"))
        {
            using (p)
            {
                using var h = WindowsSecurity.OpenProcess(0x1000 | 0x100000 | 1, false, (uint)p.Id);
                if (!h.IsInvalid && WindowsSecurity.Image(h).Equals(Path.Combine(Paths.Install, "TouchLock.exe"), StringComparison.OrdinalIgnoreCase)) { WindowsSecurity.TerminateProcess(h, 0); WindowsSecurity.WaitForSingleObject(h, 5000); }
            }
        }
        if (Services.Exists()) Sc("delete", "TouchLock");
        Registry.LocalMachine.DeleteSubKeyTree(RegistryPath, false);
        string shortcuts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "TouchLock"); Paths.NoLinks(shortcuts);
        string shortcut = Path.Combine(shortcuts, "TouchLock.lnk"); if (File.Exists(shortcut)) File.Delete(shortcut);
        if (Directory.Exists(shortcuts) && !Directory.EnumerateFileSystemEntries(shortcuts).Any()) Directory.Delete(shortcuts);
        if (Directory.Exists(install)) Directory.Delete(install, true);
        if (deleteConfiguration)
        {
            foreach (string name in new[] { "password.json", "recovery.json", "restrictions.json" }) { string path = Path.Combine(Paths.Data, name); if (File.Exists(path)) File.Delete(path); }
            if (Directory.Exists(Paths.Data) && !Directory.EnumerateFileSystemEntries(Paths.Data).Any()) Directory.Delete(Paths.Data);
        }
    }
}

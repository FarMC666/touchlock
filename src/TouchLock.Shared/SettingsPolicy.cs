using Microsoft.Win32;
using System.Runtime.InteropServices;
namespace TouchLock.Shared;

public interface ISettingsPolicy
{
    string? Read();
    void Write(string? value);
    void EnsureSupported();
}
public sealed class WindowsSettingsPolicy : ISettingsPolicy
{
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint parameter, string area, uint flags, uint timeout, out nuint result);
    public static void NotifyChange() => SendMessageTimeoutW(0xffff, 0x001a, 0, "Policy", 2, 1000, out _);
    private const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public static string Edition
    {
        get { using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"); return key?.GetValue("EditionID") as string ?? "unknown"; }
    }
    public void EnsureSupported()
    {
        if (Environment.OSVersion.Version.Build < 15063 || !new[] { "Professional", "Enterprise", "Education", "IoTEnterprise" }.Any(p => Edition.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            throw new PlatformNotSupportedException($"此 Windows 版本（{Edition}）的整机设置页策略未经支持，未开启蓝牙设置保护。");
    }
    public string? Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Key);
        object? value = key?.GetValue("SettingsPageVisibility", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (value != null && value is not string) throw new IOException("现有设置页策略类型无效，未修改系统策略。");
        return value as string;
    }
    public void Write(string? value)
    {
        using var key = Registry.LocalMachine.CreateSubKey(Key);
        if (value == null) key.DeleteValue("SettingsPageVisibility", false);
        else key.SetValue("SettingsPageVisibility", value, RegistryValueKind.String);
        key.Flush();
    }
}

using System.ComponentModel;
using System.Runtime.InteropServices;
namespace TouchLock.Shared;

public interface IStartupConfiguration
{
    bool Read();
    void Set(bool enabled);
}
public sealed class WindowsStartupConfiguration : IStartupConfiguration
{
    [StructLayout(LayoutKind.Sequential)] private struct Configuration
    {
        public uint Type, Start, Error;
        public nint BinaryPath, LoadOrder;
        public uint Tag;
        public nint Dependencies, Account, Display;
    }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32", SetLastError = true)] private static extern bool QueryServiceConfigW(nint service, nint config, uint size, out uint needed);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfigW(nint service, uint type, uint start, uint error, string? binary, string? loadOrder, nint tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32")] private static extern bool CloseServiceHandle(nint handle);
    private static T Service<T>(bool write, Func<nint, T> action)
    {
        nint manager = OpenSCManagerW(null, null, 1);
        if (manager == 0) throw new Win32Exception();
        try
        {
            nint service = OpenServiceW(manager, "TouchLock", write ? 1u | 2u : 1u);
            if (service == 0) throw new Win32Exception();
            try { return action(service); } finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }
    private static uint Read(nint service)
    {
        QueryServiceConfigW(service, 0, 0, out uint needed);
        if (Marshal.GetLastWin32Error() != 122 || needed > 65536 || needed < Marshal.SizeOf<Configuration>()) throw new Win32Exception();
        nint buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw new Win32Exception();
            var config = Marshal.PtrToStructure<Configuration>(buffer);
            string expected = '"' + Path.Combine(Paths.Install, "TouchLock.Service.exe") + '"';
            if (!expected.Equals(Marshal.PtrToStringUni(config.BinaryPath)?.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Windows 服务路径与安装位置不一致，未修改自启动配置。请使用安装程序维护。");
            if (config.Start is not (2 or 3)) throw new IOException("Windows 服务启动类型异常，请使用安装程序维护。");
            return config.Start;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public bool Read() => Service(false, service => Read(service) == 2);
    public void Set(bool enabled)
    {
        Service(true, service =>
        {
            Read(service); // Never redirect or alter a service with an unexpected executable.
            if (!ChangeServiceConfigW(service, uint.MaxValue, enabled ? 2u : 3u, uint.MaxValue, null, null, 0, null, null, null, null)) throw new Win32Exception();
            if ((Read(service) == 2) != enabled) throw new IOException("自启动配置写入后校验失败。");
            return true;
        });
    }
}

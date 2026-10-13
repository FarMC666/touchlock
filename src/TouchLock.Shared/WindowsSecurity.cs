using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace TouchLock.Shared;

public static class Paths
{
    public static string Data => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TouchLock");
    public static string Install => InstallationDirectory.Installed;
    public static string Password => Path.Combine(Data, "password.json");
    public static string Journal => Path.Combine(Data, "recovery.json");
    public static string Restrictions => Path.Combine(Data, "restrictions.json");
    public static void NoLinks(string path)
    {
        for (var d = new DirectoryInfo(path); d != null; d = d.Parent)
            if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("安装或配置路径不能包含链接。");
    }
    public static void PrepareData()
    {
        NoLinks(Data); Directory.CreateDirectory(Data);
        WindowsSecurity.ProtectDirectory(Data, false);
        foreach (string file in Directory.GetFiles(Data))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("配置路径包含链接。");
            WindowsSecurity.ProtectFile(file);
        }
    }
}
public static class WindowsSecurity
{
    [StructLayout(LayoutKind.Sequential)] private struct Attributes { public int Size; public nint Descriptor; public int Inherit; }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint version, out nint sd, out uint size);
    [DllImport("advapi32", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(nint sd, out bool present, out nint dacl, out bool defaulted);
    [DllImport("advapi32", SetLastError = true)] private static extern bool GetSecurityDescriptorOwner(nint sd, out nint owner, out bool defaulted);
    [DllImport("advapi32", CharSet = CharSet.Unicode)] private static extern uint SetNamedSecurityInfoW(string name, int kind, uint flags, nint owner, nint group, nint dacl, nint sacl);
    [DllImport("kernel32")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipeW(string name, uint open, uint mode, uint instances, uint output, uint input, uint timeout, ref Attributes security);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeWaitHandle CreateMutexW(ref Attributes security, bool owned, string name);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateDirectoryW(string path, ref Attributes security);
    public static void CreateInstallationDirectory(string path)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)", 1, out nint sd, out _)) throw new Win32Exception();
        try
        {
            var attributes = new Attributes { Size = Marshal.SizeOf<Attributes>(), Descriptor = sd };
            if (!CreateDirectoryW(path, ref attributes)) throw new Win32Exception();
        }
        finally { LocalFree(sd); }
    }
    public static NamedPipeServerStream Pipe(string name, bool systemOnly = false)
    {
        string text = systemOnly ? "D:P(A;;GA;;;SY)" : "D:P(A;;GA;;;SY)(A;;GRGW;;;IU)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(text, 1, out nint sd, out _)) throw new Win32Exception();
        try
        {
            var attrs = new Attributes { Size = Marshal.SizeOf<Attributes>(), Descriptor = sd };
            var h = CreateNamedPipeW(@"\\.\pipe\" + name, 3 | 0x40000000, 8, 8, 8192, 8192, 0, ref attrs);
            if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(); }
            return new(PipeDirection.InOut, true, false, h);
        }
        finally { LocalFree(sd); }
    }
    public static Mutex DeviceMutex()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW("D:P(A;;GA;;;SY)(A;;GA;;;BA)", 1, out nint sd, out _)) throw new Win32Exception();
        try
        {
            var attrs = new Attributes { Size = Marshal.SizeOf<Attributes>(), Descriptor = sd };
            var handle = CreateMutexW(ref attrs, false, @"Global\TouchLock.DeviceOperations.v2");
            if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
            var mutex = new Mutex(); mutex.SafeWaitHandle = handle; return mutex;
        }
        finally { LocalFree(sd); }
    }
    public static void WithDeviceMutex(Action action)
    {
        using var mutex = DeviceMutex(); bool acquired;
        try { acquired = mutex.WaitOne(15000); } catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("设备恢复正在进行，请稍后重试。");
        try { action(); } finally { mutex.ReleaseMutex(); }
    }
    public static void ProtectDirectory(string path, bool usersRead) => Protect(path, "D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)" + (usersRead ? "(A;OICI;0x1200a9;;;BU)" : ""), usersRead);
    public static void ProtectFile(string path) => Protect(path, "D:P(A;;FA;;;SY)(A;;FA;;;BA)");
    private static void Protect(string path, string text, bool administratorOwner = false)
    {
        if (administratorOwner) text = "O:BA" + text;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(text, 1, out nint sd, out _)) throw new Win32Exception();
        try
        {
            if (!GetSecurityDescriptorDacl(sd, out _, out nint dacl, out _)) throw new Win32Exception();
            nint owner = 0;
            if (administratorOwner && !GetSecurityDescriptorOwner(sd, out owner, out _)) throw new Win32Exception();
            uint result = SetNamedSecurityInfoW(path, 1, 0x80000004u | (administratorOwner ? 1u : 0u), owner, 0, dacl, 0);
            if (result != 0) throw new Win32Exception((int)result);
        }
        finally { LocalFree(sd); }
    }
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetNamedPipeClientSessionId(SafePipeHandle pipe, out uint session);
    [DllImport("kernel32", SetLastError = true)] public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32")] public static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32", SetLastError = true)] public static extern SafeFileHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(SafeFileHandle process, uint flags, StringBuilder name, ref uint size);
    [DllImport("kernel32", SetLastError = true)] private static extern bool GetProcessTimes(SafeFileHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32")] public static extern uint WaitForSingleObject(SafeFileHandle process, uint timeout);
    [DllImport("kernel32", SetLastError = true)] public static extern bool TerminateProcess(SafeFileHandle process, uint code);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool MoveFileExW(string source, string? target, uint flags);
    public static bool SameLiveProcess(ClientIdentity client)
    {
        using var handle = OpenProcess(0x1000 | 0x100000, false, client.Process);
        return !handle.IsInvalid && GetProcessTimes(handle, out long created, out _, out _, out _) && created == client.Created && WaitForSingleObject(handle, 0) == 258;
    }
    public static string Image(SafeFileHandle process)
    {
        uint size = 32768; var value = new StringBuilder((int)size);
        if (!QueryFullProcessImageNameW(process, 0, value, ref size)) throw new Win32Exception();
        return value.ToString();
    }
    public static ClientIdentity Client(NamedPipeServerStream pipe, string expectedImage)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) || !GetNamedPipeClientSessionId(pipe.SafePipeHandle, out uint session) || session != WTSGetActiveConsoleSessionId()) throw new UnauthorizedAccessException();
        using var process = OpenProcess(0x1000 | 0x100000, false, pid);
        if (process.IsInvalid || !Image(process).Equals(expectedImage, StringComparison.OrdinalIgnoreCase) || !GetProcessTimes(process, out long created, out _, out _, out _) || WaitForSingleObject(process, 0) != 258) throw new UnauthorizedAccessException();
        return new(pid, session, created);
    }
}
public sealed record ClientIdentity(uint Process, uint Session, long Created);
public static class Services
{
    [StructLayout(LayoutKind.Sequential)] private struct State { public uint Type, Status, Accepted, Error, Specific, Checkpoint, Hint, Pid, Flags; }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenSCManagerW(string? machine, string? db, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32")] private static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32", SetLastError = true)] private static extern bool StartServiceW(nint service, uint count, nint args);
    [DllImport("advapi32", SetLastError = true)] private static extern bool QueryServiceStatusEx(nint service, int level, out State state, uint size, out uint needed);
    public static uint ProcessId(string name = "TouchLock")
    {
        nint manager = OpenSCManagerW(null, null, 1); if (manager == 0) return 0;
        try { nint service = OpenServiceW(manager, name, 4); if (service == 0) return 0;
            try { return QueryServiceStatusEx(service, 0, out var state, (uint)Marshal.SizeOf<State>(), out _) && state.Status != 1 ? state.Pid : 0; }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
    public static bool Exists(string name = "TouchLock")
    {
        nint manager = OpenSCManagerW(null, null, 1); if (manager == 0) throw new Win32Exception();
        try { nint service = OpenServiceW(manager, name, 4); if (service != 0) { CloseServiceHandle(service); return true; } if (Marshal.GetLastWin32Error() != 1060) throw new Win32Exception(); return false; }
        finally { CloseServiceHandle(manager); }
    }
    public static void Start()
    {
        nint manager = OpenSCManagerW(null, null, 1); if (manager == 0) throw new Win32Exception();
        try { nint service = OpenServiceW(manager, "TouchLock", 0x10); if (service == 0) throw new Win32Exception();
            try { if (!StartServiceW(service, 0, 0) && Marshal.GetLastWin32Error() != 1056) throw new Win32Exception(); }
            finally { CloseServiceHandle(service); }
        } finally { CloseServiceHandle(manager); }
    }
    public static bool Genuine(NamedPipeClientStream pipe) => WindowsSecurity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid) && pid != 0 && ProcessId() == pid;
}

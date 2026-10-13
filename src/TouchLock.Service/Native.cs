using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TouchLock.Shared;
namespace TouchLock.Service;
internal static class Native
{
    [DllImport("wtsapi32", SetLastError = true)] private static extern bool WTSQueryUserToken(uint session, out SafeAccessTokenHandle token);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Startup { public uint Size; public string? Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public nint ReservedBytes, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public nint Process, Thread; public uint Id, ThreadId; }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcessAsUserW(SafeAccessTokenHandle token, string application, System.Text.StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref Startup startup, out ProcessInfo process);
    [DllImport("userenv", SetLastError = true)] private static extern bool CreateEnvironmentBlock(out nint environment, SafeAccessTokenHandle token, bool inherit);
    [DllImport("userenv")] private static extern bool DestroyEnvironmentBlock(nint environment);
    [DllImport("kernel32")] private static extern bool CloseHandle(nint handle);
    public static void StartTray()
    {
        uint session = WindowsSecurity.WTSGetActiveConsoleSessionId();
        if (session == uint.MaxValue || !WTSQueryUserToken(session, out var token)) throw new Win32Exception();
        using (token)
        {
            var startup = new Startup { Size = (uint)Marshal.SizeOf<Startup>(), Desktop = @"winsta0\default" };
            string executable = Path.Combine(AppContext.BaseDirectory, "TouchLock.exe");
            if (!CreateEnvironmentBlock(out nint env, token, false)) throw new Win32Exception();
            try
            {
                if (!CreateProcessAsUserW(token, executable, new System.Text.StringBuilder('"' + executable + '"'), 0, 0, false, 0x08000400, env, AppContext.BaseDirectory, ref startup, out var process)) throw new Win32Exception();
                CloseHandle(process.Thread); CloseHandle(process.Process);
            }
            finally { DestroyEnvironmentBlock(env); }
        }
    }
}

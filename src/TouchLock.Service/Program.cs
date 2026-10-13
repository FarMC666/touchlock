using System.Runtime.InteropServices;

namespace TouchLock.Service;
internal static class Program
{
    private static readonly CancellationTokenSource shutdown = new();
    private static Controller? controller;
    private static readonly ServiceMain main = MainService;
    private static readonly Handler handler = Control;
    private static nint registration;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMain(uint count, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate uint Handler(uint control, uint kind, nint data, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Entry { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMain? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Error, SpecificError, Checkpoint, WaitHint; }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceCtrlDispatcherW([In] Entry[] entries);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint RegisterServiceCtrlHandlerExW(string name, Handler callback, nint context);
    [DllImport("advapi32", SetLastError = true)] private static extern bool SetServiceStatus(nint registration, ref ServiceStatus status);
    [STAThread] public static int Main(string[] arguments)
    {
        if (arguments.SequenceEqual(["--console"])) { Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); }; Run(); return 0; }
        if (arguments.Length != 0) return 64;
        return StartServiceCtrlDispatcherW([new() { Name = "TouchLock", Main = main }, new()]) ? 0 : Marshal.GetLastWin32Error();
    }
    private static void MainService(uint _, nint __)
    {
        registration = RegisterServiceCtrlHandlerExW("TouchLock", handler, 0); if (registration == 0) return;
        Status(2, 0); Status(4, 1 | 4 | 64 | 128);
        uint failure = 0;
        try { Run(); } catch (Exception) { failure = 31; }
        finally { Status(1, 0, failure); } // Nonzero failures activate SCM recovery.
    }
    private static void Run() { using var core = new Controller(); controller = core; try { core.Run(shutdown.Token).GetAwaiter().GetResult(); } finally { controller = null; } }
    private static uint Control(uint command, uint kind, nint _, nint __)
    {
        if (command is 1 or 5 or 15) { controller?.SessionChanged(); shutdown.Cancel(); Status(3, 0); }
        else if (command == 14 && kind is 2 or 4 or 6 or 7) controller?.SessionChanged();
        else if (command == 13 && kind is 4 or 7 or 18) controller?.SessionChanged();
        return 0;
    }
    private static void Status(uint state, uint accepted, uint failure = 0) { var status = new ServiceStatus { Type = 16, State = state, Accepted = accepted, Win32Error = failure }; SetServiceStatus(registration, ref status); }
}

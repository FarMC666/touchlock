using System.IO.Pipes;
using TouchLock.Shared;
namespace TouchLock.Recovery;
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !args[0].StartsWith("TouchLock.Guard.v2.", StringComparison.Ordinal) || !System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem) return 64;
        Paths.PrepareData();
        Diagnostics.Initialize(Paths.Data);
        using var pipe = new NamedPipeClientStream(".", args[0], PipeDirection.InOut, PipeOptions.Asynchronous);
        using var connect = new CancellationTokenSource(10000);
        try { await pipe.ConnectAsync(connect.Token); } catch { return 2; }
        if (!Services.Genuine(pipe) || !WindowsSecurity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint pid)) return 3;
        using var parent = WindowsSecurity.OpenProcess(0x1000 | 0x100000 | 1, false, pid);
        if (parent.IsInvalid || !WindowsSecurity.Image(parent).Equals(Path.Combine(AppContext.BaseDirectory, "TouchLock.Service.exe"), StringComparison.OrdinalIgnoreCase)) return 4;
        await pipe.WriteAsync(new byte[] { 1 }); await pipe.FlushAsync(); // exact process handle acquired; ready.
        byte[] command = new byte[1];
        bool orderly = false;
        try
        {
            while (WindowsSecurity.WaitForSingleObject(parent, 0) == 258)
            {
                using var lease = new CancellationTokenSource(30000);
                int count = await pipe.ReadAsync(command, lease.Token);
                if (count == 0) break;
                if (command[0] == 2) { orderly = true; break; }
                if (command[0] != 1) break;
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException) { }
        // Never restore while the old service can perform a late disable.
        if (!orderly && WindowsSecurity.WaitForSingleObject(parent, 0) == 258)
        {
            if (!WindowsSecurity.TerminateProcess(parent, 90) || WindowsSecurity.WaitForSingleObject(parent, 15000) != 0) return 5;
        }
        var transaction = new DeviceTransaction(new WindowsDevices(), Paths.Journal);
        while (true)
        {
            try { WindowsSecurity.WithDeviceMutex(transaction.Restore); new RestrictionsStore(Paths.Restrictions).Restore(); return 0; }
            catch { await Task.Delay(1000); }
        }
    }
}

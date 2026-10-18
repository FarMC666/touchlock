using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using TouchLock.Shared;
namespace TouchLock.Service;
internal sealed class Controller : IDisposable
{
    private readonly object gate = new();
    private readonly DeviceTransaction devices;
    private readonly PasswordStore passwords;
    private readonly IDeviceBackend backend;
    private readonly RestrictionsStore restrictions;
    private bool bluetoothSettingsBlocked;
    private string bluetoothSettingsError = "";
    private LockState state = LockState.Unlocked, origin;
    private long generation, deadline, lastTray, cooldown;
    private int failures, sessionChange;
    private bool stop, guardFailed;
    private string reason = "", fingerprint = "";
    private Guid requestId;
    private AuthenticationIntent intent;
    private ClientIdentity? owner;
    private Process? guard;
    private NamedPipeServerStream? lease;
    public Controller() : this(new WindowsDevices(), Paths.Password, Paths.Journal, Paths.Restrictions) { }
    internal Controller(IDeviceBackend backend, string passwordPath, string journalPath, string restrictionsPath)
    {
        this.backend = backend; devices = new(backend, journalPath); passwords = new(passwordPath); restrictions = new(restrictionsPath);
        try { bluetoothSettingsBlocked = restrictions.Read(); }
        catch { bluetoothSettingsBlocked = true; bluetoothSettingsError = "蓝牙设置限制配置读取失败，已保持限制；可输入管理口令取消限制后重新设置。"; }
    }
    public void SessionChanged() => Interlocked.Exchange(ref sessionChange, 1);
    private bool GuardHealthy => !guardFailed && guard is { HasExited: false } && lease is { IsConnected: true };
    private static long Now => Environment.TickCount64;
    private void Transition(LockState next) { state = next; generation++; }
    private void Cancel() { requestId = Guid.Empty; owner = null; if (state == LockState.Authenticating) Transition(origin); }
    private string Availability()
    {
        if (!GuardHealthy) return "触摸恢复进程不可用，请重新启动 TouchLock。";
        try
        {
            passwords.Read();
            if (devices.Pending && state is not (LockState.Locked or LockState.Authenticating)) return "正在恢复触摸设备。";
            var found = backend.Enumerate();
            if (found.Count == 0) return "未找到可操作的 Kuma 或标准 HID 触摸屏。";
            if (found.Any(d => !d.Safe)) return found.First(d => !d.Safe).Reason;
            if (state is LockState.Unlocked or LockState.Unavailable && !found.Any(d => !d.Disabled && d.Disableable)) return "没有可以临时锁定的触摸屏。";
            return "";
        }
        catch { return "配置或设备不可用，请通过安装程序检查或重置管理口令。"; }
    }
    private Status Status()
    {
        string unavailable = reason.Length > 0 ? reason : Availability();
        var shown = state == LockState.Unlocked && unavailable.Length > 0 ? LockState.Unavailable : state;
        return new(shown, generation, unavailable.Length == 0, unavailable, stop, bluetoothSettingsBlocked, bluetoothSettingsError);
    }
    public async Task Run(CancellationToken shutdown)
    {
        Paths.PrepareData();
        Diagnostics.Initialize(Paths.Data);
        Diagnostics.Write("device-inventory", WindowsDevices.Inventory());
        try { WindowsSecurity.WithDeviceMutex(devices.Restore); } catch (Exception e) { reason = e.Message; Transition(LockState.Restoring); }
        try
        {
            string pipeName = "TouchLock.Guard.v2." + Guid.NewGuid().ToString("N");
            lease = WindowsSecurity.Pipe(pipeName, true);
            guard = Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TouchLock.Recovery.exe")) { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { pipeName } }) ?? throw new IOException();
            using var ready = new CancellationTokenSource(10000);
            await lease.WaitForConnectionAsync(ready.Token);
            if (!WindowsSecurity.GetNamedPipeClientProcessId(lease.SafePipeHandle, out uint peer) || peer != guard.Id) throw new IOException();
            byte[] byteReady = new byte[1]; await lease.ReadExactlyAsync(byteReady, ready.Token);
            if (byteReady[0] != 1) throw new IOException();
        }
        catch { guardFailed = true; reason = "触摸恢复进程启动失败，请重新启动 TouchLock。"; }
        using var io = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        Task control = Listen(false, io.Token), authentication = Listen(true, io.Token);
        try
        {
            while (!shutdown.IsCancellationRequested && !stop)
            {
                lock (gate)
                {
                    if (!GuardHealthy) { guardFailed = true; Cancel(); Restore(); reason = "触摸恢复进程异常，已停止锁定功能。"; }
                    if (Interlocked.Exchange(ref sessionChange, 0) != 0) { Cancel(); Restore(); }
                    if (state == LockState.Restoring) Restore();
                    if (state == LockState.Authenticating && (Now >= deadline || !OwnerAlive())) Cancel();
                    if (devices.Pending && state is LockState.Locked or LockState.Authenticating)
                    {
                        try { if (DeviceFingerprint() != fingerprint || backend.Enumerate().Any(d => !d.Safe) || devices.ReadJournal().Any(id => !backend.IsDisabled(id))) { Cancel(); Restore(); } }
                        catch { Cancel(); Restore(); }
                    }
                    if (Now - lastTray > 10000) { lastTray = Now; try { Native.StartTray(); } catch { } }
                }
                if (GuardHealthy)
                {
                    using var write = new CancellationTokenSource(2000);
                    try { await lease!.WriteAsync(new byte[] { 1 }, write.Token); await lease.FlushAsync(write.Token); }
                    catch { guardFailed = true; }
                }
                await Task.Delay(1000, shutdown);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (gate) { Cancel(); Restore(); }
            io.Cancel();
            try { await Task.WhenAll(control, authentication); } catch { }
            // If restore failed, leave the guardian alive to retry; never erase its journal.
            if (!devices.Pending && GuardHealthy)
            {
                try { await lease!.WriteAsync(new byte[] { 2 }); await lease.FlushAsync(); } catch { }
                try { if (guard != null) await guard.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
        }
    }
    private bool OwnerAlive()
    {
        if (owner == null || owner.Session != WindowsSecurity.WTSGetActiveConsoleSessionId()) return false;
        return WindowsSecurity.SameLiveProcess(owner);
    }
    private string DeviceFingerprint() => string.Join('\n', backend.Enumerate().Select(d => d.Id).Order(StringComparer.OrdinalIgnoreCase));
    private void Restore()
    {
        if (!devices.Pending) { if (state == LockState.Restoring) { reason = ""; Transition(LockState.Unlocked); } return; }
        Transition(LockState.Restoring);
        try { WindowsSecurity.WithDeviceMutex(devices.Restore); reason = ""; Transition(LockState.Unlocked); }
        catch (Exception e) { reason = e.Message; }
    }
    private async Task Listen(bool binary, CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var pipe = WindowsSecurity.Pipe(binary ? Wire.PasswordPipe : Wire.PipeName);
                await pipe.WaitForConnectionAsync(shutdown);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown); timeout.CancelAfter(10000);
                var client = WindowsSecurity.Client(pipe, Path.Combine(AppContext.BaseDirectory, "TouchLock.exe"));
                var request = await Wire.ReadAsync<ControlRequest>(pipe, timeout.Token);
                if (request.Version != Wire.Version) throw new InvalidDataException();
                ControlReply result;
                if (binary)
                {
                    byte[] password = [], replacement = [];
                    try
                    {
                        password = await Wire.ReadSecret(pipe, timeout.Token); replacement = await Wire.ReadSecret(pipe, timeout.Token);
                        lock (gate) result = Authenticate(request, client, password, replacement);
                    }
                    finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(replacement); }
                }
                else { lock (gate) result = Command(request, client); }
                await Wire.WriteAsync(pipe, result, timeout.Token);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch { /* Invalid/disconnected clients receive no authority or success response. */ }
        }
    }
    private ControlReply Reply(bool success, string error = "", Guid id = default) => new(success, Status(), error, id);
    internal ControlReply Command(ControlRequest command, ClientIdentity client)
    {
#if DEBUG
        if (command.Operation == "test-pause") { Thread.Sleep(35000); return Reply(false); }
#endif
        if (command.Operation == "status") { lastTray = Now; return Reply(true); }
        if (command.Operation == "block-bluetooth-settings")
        {
            if (stop) return Reply(false, "TouchLock 正在退出。");
            return SetBluetoothSettingsBlocked(true);
        }
        if (command.Operation == "lock")
        {
            if (state is LockState.Locked or LockState.Authenticating) return Reply(true);
            string unavailable = Availability(); if (unavailable.Length > 0) return Reply(false, unavailable);
            if (state != LockState.Unlocked) return Reply(false, "设备正在恢复。");
            Transition(LockState.Locking);
            try { WindowsSecurity.WithDeviceMutex(devices.Lock); fingerprint = DeviceFingerprint(); reason = ""; Transition(LockState.Locked); return Reply(true); }
            catch (Exception e) { reason = e.Message; Transition(devices.Pending ? LockState.Restoring : LockState.Unlocked); return Reply(false, reason); }
        }
        if (command.Operation == "begin")
        {
            if (state is not (LockState.Unlocked or LockState.Locked)) return Reply(false, "已有认证或设备操作正在进行。");
            if (!Enum.IsDefined(command.Intent) || command.Intent == AuthenticationIntent.Unlock && state != LockState.Locked) return Reply(false, "触摸尚未锁定。");
            if (command.Intent == AuthenticationIntent.AllowBluetoothSettings && !bluetoothSettingsBlocked) return Reply(false, "当前未禁止进入 Windows 蓝牙设置。");
            if (Now < cooldown) return Reply(false, "错误尝试过多，请等待 30 秒再试。");
            origin = state; intent = command.Intent; owner = client; requestId = Guid.NewGuid(); deadline = Now + 180000;
            Transition(LockState.Authenticating); return Reply(true, id: requestId);
        }
        if (command.Operation == "cancel" && command.RequestId == requestId && owner == client) { Cancel(); return Reply(true); }
        return Reply(false, "请求无效。");
    }
    private ControlReply SetBluetoothSettingsBlocked(bool blocked)
    {
        try
        {
            restrictions.Set(blocked); bluetoothSettingsBlocked = blocked; bluetoothSettingsError = ""; generation++; return Reply(true);
        }
        catch { bluetoothSettingsError = "蓝牙设置限制保存失败，原设置保持不变。请稍后重试，或运行安装程序维护。"; return Reply(false, bluetoothSettingsError); }
    }
    internal ControlReply Authenticate(ControlRequest command, ClientIdentity client, byte[] password, byte[] replacement)
    {
        if (command.Operation != "submit" || state != LockState.Authenticating || command.RequestId == Guid.Empty || command.RequestId != requestId || owner != client || Now >= deadline) return Reply(false, "认证已过期，请重新打开认证窗口。");
        try
        {
            if (Now < cooldown || !passwords.Verify(password))
            {
                failures++; if (failures >= 5) { cooldown = Now + 30000; failures = 0; }
                Cancel(); return Reply(false, Now < cooldown ? "错误尝试过多，请等待 30 秒再试。" : "管理口令不正确。");
            }
            failures = 0;
            if (intent == AuthenticationIntent.ChangePassword)
            {
                passwords.Set(replacement); Cancel(); return Reply(true);
            }
            if (intent == AuthenticationIntent.AllowBluetoothSettings)
            {
                Cancel(); return SetBluetoothSettingsBlocked(false);
            }
            var action = intent; Cancel(); Restore();
            if (devices.Pending) return Reply(false, reason);
            if (action == AuthenticationIntent.Exit) { Transition(LockState.Stopping); stop = true; }
            return Reply(true);
        }
        catch { Cancel(); return Reply(false, "口令配置读写失败，请通过安装程序维护。"); }
    }
    public void Dispose() { lease?.Dispose(); guard?.Dispose(); }
}

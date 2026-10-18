using TouchLock.Service;
using TouchLock.Shared;

internal static class ControllerDeviceTests
{
    public static void Run(string folder, byte[] password, Action<bool, string> check)
    {
        var backend = new Backend();
        using var controller = new Controller(backend, Path.Combine(folder, "password.json"), Path.Combine(folder, "async-recovery.json"), Path.Combine(folder, "async-restrictions.json"), () => true, action => action(), new TestSettingsPolicy());
        ClientIdentity client = new(201, 1, 555);
        ControlReply Wait(ControlReply accepted)
        {
            long until = Environment.TickCount64 + 10000;
            while (accepted.Status.OperationRunning)
            {
                if (Environment.TickCount64 >= until) throw new TimeoutException("device controller test");
                Thread.Sleep(10); accepted = controller.Command(new("status"), client);
            }
            return accepted;
        }
        Wait(controller.Command(new("refresh"), client));
        var accepted = controller.Command(new("lock"), client);
        check(accepted.Success && accepted.Status is { OperationRunning: true, State: LockState.Locking }, "device command returns accepted operation before completion");
        check(!controller.Command(new("lock"), client).Success, "duplicate device operation rejected");
        var locked = Wait(accepted);
        check(locked.Status is { State: LockState.Locked, TargetCount: 1, DisabledCount: 1 }, "locked status comes from verified device snapshot");
        var challenge = controller.Command(new("begin", Intent: AuthenticationIntent.Unlock), client);
        var wrong = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, [], []);
        check(!wrong.Success && backend.Disabled, "wrong password cannot restore device");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.Unlock), client);
        var restored = Wait(controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []));
        check(restored.Status.State == LockState.Unlocked && !backend.Disabled, "authorized restore completes asynchronously and updates snapshot");
        backend.Disabled = true;
        var external = Wait(controller.Command(new("refresh"), client));
        check(!external.Status.Available && external.Status.DisabledCount == 1 && !controller.Command(new("lock"), client).Success, "externally disabled device is not claimed as TouchLock lock");
    }
    private sealed class Backend : IDeviceBackend
    {
        public bool Disabled;
        public IReadOnlyList<TouchDevice> Enumerate() => [new("HID\\ControllerTest", Disabled, true, true)];
        public bool IsDisabled(string id) => Disabled;
        public void Disable(string id) { Thread.Sleep(150); Disabled = true; }
        public void Enable(string id) => Disabled = false;
    }
}

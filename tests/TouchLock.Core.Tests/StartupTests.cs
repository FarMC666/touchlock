using TouchLock.Service;
using TouchLock.Shared;

internal static class StartupTests
{
    public static void Run(string folder, Action<bool, string> check, byte[] password)
    {
        var startup = new Startup();
        using var controller = new Controller(new NoDevices(), Path.Combine(folder, "password.json"), Path.Combine(folder, "startup-recovery.json"), Path.Combine(folder, "startup-restrictions.json"), settingsPolicy: new TestSettingsPolicy(), startupConfiguration: startup);
        ClientIdentity client = new(401, 1, 44);
        check(!controller.Command(new("status"), client).Status.StartupEnabled, "startup status reads actual disabled configuration");
        startup.Enabled = true;
        check(controller.Command(new("status"), client).Status.StartupEnabled, "external startup change is reflected without JSON state");
        check(!controller.Command(new("disable-startup"), client).Success && startup.Enabled, "unauthenticated startup modification rejected");
        var challenge = controller.Command(new("begin", Intent: AuthenticationIntent.DisableStartup), client);
        var wrong = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, [], []);
        check(!wrong.Success && startup.Enabled && startup.Writes == 0, "wrong startup password performs no system write");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.DisableStartup), client);
        var result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
        check(result.Success && !result.Status.StartupEnabled, "authorized startup disable verified from system backend");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.EnableStartup), client);
        result = controller.Authenticate(new("submit", Intent: AuthenticationIntent.DisableStartup, RequestId: challenge.RequestId), client, password, []);
        check(result.Success && result.Status.StartupEnabled, "startup target is bound to original authentication intent");
        check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []).Success, "startup authorization cannot be replayed");
        startup.Fail = true;
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.DisableStartup), client);
        result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
        check(!result.Success && result.Status.StartupEnabled, "failed startup write never reports disabled");
    }
    private sealed class Startup : IStartupConfiguration
    {
        public bool Enabled, Fail;
        public int Writes;
        public bool Read() => Enabled;
        public void Set(bool enabled) { if (Fail) throw new IOException("simulated SCM failure"); Writes++; Enabled = enabled; }
    }
    private sealed class NoDevices : IDeviceBackend
    {
        public IReadOnlyList<TouchDevice> Enumerate() => [];
        public bool IsDisabled(string id) => throw new InvalidOperationException();
        public void Disable(string id) => throw new InvalidOperationException();
        public void Enable(string id) => throw new InvalidOperationException();
    }
}

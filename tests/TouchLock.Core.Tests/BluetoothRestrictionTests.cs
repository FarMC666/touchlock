using System.Reflection;
using System.Text.Json;
using TouchLock.Service;
using TouchLock.Shared;

internal static class BluetoothRestrictionTests
{
    public static void Run(string directory, byte[] password, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "restrictions.json"), passwordPath = Path.Combine(directory, "password.json"), journal = Path.Combine(directory, "bluetooth-test-recovery.json");
        var store = new RestrictionsStore(path);
        check(!store.Read(), "existing installation without restriction file defaults unchecked");
        store.Set(true); check(new RestrictionsStore(path).Read(), "Bluetooth restriction persists enabled");
        store.Set(false); check(!store.Read(), "Bluetooth restriction persists disabled");
        foreach (string invalid in new[] { "{}", "null", "{\"Version\":2,\"BluetoothSettingsBlocked\":false}", "{\"Version\":1}" })
        {
            File.WriteAllText(path, invalid);
            bool rejected = false; try { store.Read(); } catch (InvalidDataException) { rejected = true; }
            check(rejected, "invalid Bluetooth restriction configuration rejected: " + invalid);
        }
        var backend = new Devices();
        ClientIdentity client = new(101, 1, 123), other = new(102, 1, 124);
        using (var controller = new Controller(backend, passwordPath, journal, path))
        {
            var status = controller.Command(new("status"), client);
            check(status.Status.BluetoothSettingsBlocked && status.Status.BluetoothSettingsError.Length > 0, "corrupt configuration keeps Bluetooth restriction enabled");
            var challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            var result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
            check(result.Success && !result.Status.BluetoothSettingsBlocked && !store.Read(), "correct password repairs corrupt configuration and disables restriction");
            check(!controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client).Success, "unchecked restriction has no disable challenge");
            result = controller.Command(new("block-bluetooth-settings"), client);
            check(result.Success && result.Status.BluetoothSettingsBlocked && store.Read(), "checking requires no password or touchscreen availability");
            check(!controller.Command(new("allow-bluetooth-settings"), client).Success && store.Read(), "control command cannot bypass password to uncheck");
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, [], []);
            check(!result.Success && result.Status.BluetoothSettingsBlocked && store.Read(), "wrong password retains check and persisted restriction");
            check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []).Success, "Bluetooth challenge cannot be replayed after wrong password");
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), other, password, []).Success, "another client cannot submit Bluetooth challenge");
            check(!controller.Command(new("cancel", RequestId: challenge.RequestId), other).Success, "another client cannot cancel Bluetooth challenge");
            result = controller.Command(new("cancel", RequestId: challenge.RequestId), client);
            check(result.Success && result.Status.BluetoothSettingsBlocked && result.Status.State != LockState.Authenticating, "cancel retains restriction and restores authentication state");
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            typeof(Controller).GetField("deadline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, Environment.TickCount64 - 1);
            check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []).Success && store.Read(), "expired Bluetooth challenge cannot disable restriction");
            controller.Command(new("cancel", RequestId: challenge.RequestId), client);
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
                check(!result.Success && result.Status.BluetoothSettingsBlocked && store.Read() && result.Status.BluetoothSettingsError.Length > 0, "failed save never reports unchecked or loses restriction");
            }
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
            check(result.Success && !result.Status.BluetoothSettingsBlocked && result.Status.BluetoothSettingsError.Length == 0, "retry after save failure succeeds and clears error");
            controller.Command(new("block-bluetooth-settings"), client);
        }
        using (var restarted = new Controller(backend, passwordPath, journal, path))
        {
            check(restarted.Command(new("status"), client).Status.BluetoothSettingsBlocked, "service restart reloads checked restriction");
            // Seed a locked device and its journal; removing this restriction must leave both intact.
            new DeviceTransaction(backend, journal).Lock();
            typeof(Controller).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(restarted, LockState.Locked);
            var challenge = restarted.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            var result = restarted.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
            check(result.Success && result.Status.State == LockState.Locked && backend.Disabled && File.Exists(journal) && backend.EnableCalls == 0, "unchecking Bluetooth preserves locked touch device and recovery journal");
            restarted.Command(new("block-bluetooth-settings"), client);
            for (int i = 0; i < 5; i++)
            {
                challenge = restarted.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
                restarted.Authenticate(new("submit", RequestId: challenge.RequestId), client, [], []);
            }
            check(!restarted.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client).Success && store.Read(), "Bluetooth password failures use existing cooldown");
            check(restarted.Command(new("block-bluetooth-settings"), client).Success, "checking stays unrestricted during password cooldown");
        }
        var oldStatus = JsonSerializer.Deserialize<Status>("{\"State\":0,\"Generation\":0,\"Available\":true,\"Reason\":\"\"}")!;
        check(!oldStatus.BluetoothSettingsBlocked && oldStatus.BluetoothSettingsError == "", "older status messages remain compatible");
    }
    private sealed class Devices : IDeviceBackend
    {
        public bool Disabled;
        public int EnableCalls;
        public IReadOnlyList<TouchDevice> Enumerate() => [new("HID\\BluetoothRestrictionTest", Disabled, true, true)];
        public bool IsDisabled(string id) => Disabled;
        public void Disable(string id) => Disabled = true;
        public void Enable(string id) { Disabled = false; EnableCalls++; }
    }
}

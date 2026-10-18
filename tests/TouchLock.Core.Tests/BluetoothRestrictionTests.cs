using System.Reflection;
using TouchLock.Service;
using TouchLock.Shared;

internal sealed class TestSettingsPolicy : ISettingsPolicy
{
    public string? Value;
    public bool FailWrite, Unsupported;
    public string? Read() => Value;
    public void Write(string? value) { if (FailWrite) throw new IOException("simulated registry write failure"); Value = value; }
    public void EnsureSupported() { if (Unsupported) throw new PlatformNotSupportedException("unsupported edition"); }
}
internal static class BluetoothRestrictionTests
{
    public static void Run(string folder, byte[] password, Action<bool, string> check)
    {
        string path = Path.Combine(folder, "restrictions.json"), journal = Path.Combine(folder, "bluetooth-test-recovery.json");
        var policy = new TestSettingsPolicy();
        var store = new RestrictionsStore(path, policy);
        check(!store.Read(), "Bluetooth runtime restriction defaults off");
        check(RestrictionsStore.Block("hide:about") == "hide:about;bluetooth;connecteddevices", "hide policy retains unrelated pages");
        check(RestrictionsStore.Block("showonly:about;bluetooth;connecteddevices") == "showonly:about", "showonly policy removes Bluetooth routes only");
        store.Set(true);
        check(store.Read() && policy.Value == "hide:bluetooth;connecteddevices", "machine policy applied and ownership journaled");
        store.Set(false);
        check(!store.Pending && policy.Value == null, "original absent policy restored exactly");
        policy.Value = "hide:about"; store.Set(true); store.Restore();
        check(policy.Value == "hide:about", "existing policy preserved after cleanup");
        policy.Value = "showonly:about;bluetooth"; store.Set(true); store.Restore();
        check(policy.Value == "showonly:about;bluetooth", "existing showonly value restored exactly");
        policy.Value = null; store.Set(true); policy.Value = "hide:display";
        try { store.Restore(); throw new Exception("expected conflict"); } catch (IOException) { }
        check(store.Pending && policy.Value == "hide:display" && store.ValidationError().Length > 0, "external policy edits never overwritten or reported protected");
        policy.Value = "hide:bluetooth;connecteddevices"; store.Restore();
        policy.Unsupported = true;
        try { store.Set(true); throw new Exception("expected unsupported"); } catch (PlatformNotSupportedException) { }
        check(!store.Pending && policy.Value == null, "unsupported edition never reports policy enabled");
        policy.Unsupported = false; policy.FailWrite = true;
        try { store.Set(true); throw new Exception("expected write failure"); } catch (IOException) { }
        check(store.Pending, "write failure retains policy recovery journal");
        policy.FailWrite = false; store.Restore();
        check(!store.Pending && policy.Value == null, "failed write can recover without changing original policy");
        File.WriteAllText(path, "{\"Version\":1,\"BluetoothSettingsBlocked\":true}");
        store.Restore(); check(!store.Read(), "legacy persisted checkbox deliberately reset");
        File.WriteAllText(path, "{}");
        try { store.Restore(); throw new Exception("expected malformed journal"); } catch (InvalidDataException) { }
        check(File.Exists(path) && policy.Value == null, "corrupt ownership journal retained without registry writes");
        File.Delete(path);
        var backend = new Devices();
        ClientIdentity client = new(101, 1, 123), other = new(102, 1, 124);
        using (var controller = new Controller(backend, Path.Combine(folder, "password.json"), journal, path, settingsPolicy: policy))
        {
            var result = controller.Command(new("block-bluetooth-settings"), client);
            check(result.Success && result.Status.BluetoothSettingsBlocked && store.Read(), "checking applies policy without password or touchscreen");
            check(!controller.Command(new("allow-bluetooth-settings"), client).Success, "unprotected command cannot remove Bluetooth policy");
            var challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), other, password, []).Success, "Bluetooth challenge bound to client");
            result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, [], []);
            check(!result.Success && store.Read(), "wrong Bluetooth password preserves policy");
            check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []).Success, "Bluetooth challenge replay rejected");
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            controller.Command(new("cancel", RequestId: challenge.RequestId), client);
            check(store.Read(), "cancel preserves Bluetooth policy");
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            result = controller.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
            check(result.Success && !store.Pending && policy.Value == null, "authorized Bluetooth cleanup restores policy");
            controller.Command(new("block-bluetooth-settings"), client);
        }
        using (var restarted = new Controller(backend, Path.Combine(folder, "password.json"), journal, path, settingsPolicy: policy))
        {
            check(!restarted.Command(new("status"), client).Status.BluetoothSettingsBlocked && policy.Value == null, "full service restart resets Bluetooth and restores original policy");
            new DeviceTransaction(backend, journal).Lock();
            typeof(Controller).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(restarted, LockState.Locked);
            restarted.Command(new("block-bluetooth-settings"), client);
            var challenge = restarted.Command(new("begin", Intent: AuthenticationIntent.AllowBluetoothSettings), client);
            var result = restarted.Authenticate(new("submit", RequestId: challenge.RequestId), client, password, []);
            check(result.Success && result.Status.State == LockState.Locked && backend.Disabled && backend.EnableCalls == 0, "Bluetooth cleanup does not restore locked touch devices");
        }
        new DeviceTransaction(backend, journal).Restore();
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

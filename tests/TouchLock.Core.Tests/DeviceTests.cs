using TouchLock.Shared;

internal static class DeviceTests
{
    public static void Run(string folder, Action<bool, string> check)
    {
        const uint healthy = 8 | 0x2000;
        var kuma = Enumerable.Range(0, 5).Select(i => new DeviceNode("ROOT\\KUMA\\" + i, DeviceSelection.KumaName, "HIDClass", [], healthy, 0, 1)).ToArray();
        var mouse = new DeviceNode("HID\\MOUSE", "Mouse", "Mouse", ["HID_DEVICE_UP:0001_U:0002"], healthy, 0, 1);
        var touch = new DeviceNode("HID\\TOUCH", "Screen", "HIDClass", [DeviceSelection.TouchId], healthy, 0, 2);
        var result = DeviceSelection.Select([.. kuma, mouse, touch]);
        check(result.Count == 5 && result.All(d => d.Match == TouchMatch.Kuma && d.Safe), "all five non-HID Kuma nodes preferred despite companion mouse");
        check(DeviceSelection.Select([mouse, touch]).Single().Id == touch.Id, "standard touchscreen fallback excludes mouse");
        check(!DeviceSelection.Select([touch with { Parent = 1 }, mouse]).Single().Safe, "generic shared mouse channel refused");
        foreach (var unsafeNode in new[] { kuma[0] with { Bluetooth = true }, kuma[0] with { DeviceClass = "Keyboard" }, kuma[0] with { CompatibleIds = ["HID_DEVICE_UP:000D_U:0005"] }, kuma[0] with { ConfigRet = 5 } })
            check(!DeviceSelection.Select([unsafeNode]).Single().Safe, "Kuma explicit unsafe identity or unreadable state rejected");
        check(DeviceSelection.Select([touch with { Name = "Precision Touchpad" }]).All(d => !d.Safe), "touchpad never selected as safe");
        var backend = new Batch(result);
        var transaction = new DeviceTransaction(backend, Path.Combine(folder, "kuma-recovery.json"));
        transaction.Lock();
        check(backend.Disabled.Count == 5 && transaction.ReadJournal().Length == 5, "all five Kuma instances disabled and journaled");
        transaction.Restore(); check(backend.Disabled.Count == 0 && !transaction.Pending, "all five non-HID Kuma instances restored");
        backend.Fail = [kuma[1].Id, kuma[4].Id];
        try { transaction.Lock(); throw new Exception("lock should fail"); } catch (IOException) { }
        check(transaction.Summary is { Found: 5, Attempted: 5, Succeeded: 3, Failed: 2 }, "three successful and two failed disables accurately counted");
        check(backend.Disabled.Count == 0 && !transaction.Pending, "partial Kuma lock rolled back after attempting all five");
        backend.Fail.Clear(); backend.IgnoreDisable = true;
        try { transaction.Lock(); throw new Exception("lock should fail"); } catch (IOException) { }
        check(transaction.Summary is { Succeeded: 0, Failed: 5 } && !transaction.Pending, "API success without state change never accepted");
    }
    private sealed class Batch(IReadOnlyList<TouchDevice> devices) : IDeviceBackend
    {
        public HashSet<string> Disabled = [], Fail = [];
        public bool IgnoreDisable;
        public IReadOnlyList<TouchDevice> Enumerate() => devices.Select(d => d with { Disabled = Disabled.Contains(d.Id) }).ToArray();
        public bool IsDisabled(string id) => Disabled.Contains(id);
        public void Disable(string id) { if (Fail.Contains(id)) throw new IOException("CONFIGRET 0x33; Win32 5"); if (!IgnoreDisable) Disabled.Add(id); }
        public void Enable(string id) => Disabled.Remove(id);
    }
}

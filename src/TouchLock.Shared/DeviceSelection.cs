namespace TouchLock.Shared;

public enum TouchMatch { GenericHid, Kuma }
public enum DeviceState { Enabled, Disabled, Missing, Error, Unknown }
public sealed record DeviceNode(string Id, string Name, string DeviceClass, string[] CompatibleIds,
    uint Status, uint Problem, uint Parent = 0, Guid Container = default,
    uint ConfigRet = 0, bool Bluetooth = false, string[]? PropertyErrors = null)
{
    public bool NeedsRestart => Problem == 14 || (Status & 0x100) != 0;
    public string State => (ConfigRet != 0 ? DeviceState.Unknown : (Status & 0x400) != 0 && Problem == 22 ? DeviceState.Disabled :
        (Status & 0x400) != 0 || Problem != 0 ? DeviceState.Error : (Status & 8) != 0 ? DeviceState.Enabled : DeviceState.Unknown).ToString();
}

public static class DeviceSelection
{
    public const string KumaName = "Kuma - Touch Screen Extend Service";
    public const string TouchId = "HID_DEVICE_UP:000D_U:0004";
    public static bool IsKuma(DeviceNode node) => node.Name.Equals(KumaName, StringComparison.OrdinalIgnoreCase);
    public static IReadOnlyList<TouchDevice> Select(IReadOnlyList<DeviceNode> nodes)
    {
        bool kuma = nodes.Any(IsKuma);
        return nodes.Where(n => kuma ? IsKuma(n) : n.CompatibleIds.Contains(TouchId, StringComparer.OrdinalIgnoreCase)).Select(n =>
        {
            bool disabled = n.ConfigRet == 0 && (n.Status & 0x400) != 0 && n.Problem == 22;
            bool healthy = n.ConfigRet == 0 && !n.NeedsRestart && (disabled || ((n.Status & 0x400) == 0 && (n.Status & 8) != 0 && n.Problem == 0));
            bool mixed = n.CompatibleIds.Any(u => u.StartsWith("HID_DEVICE_UP:", StringComparison.OrdinalIgnoreCase) && !u.Equals(TouchId, StringComparison.OrdinalIgnoreCase));
            bool input = n.Bluetooth || n.Id.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) ||
                n.DeviceClass.Equals("Keyboard", StringComparison.OrdinalIgnoreCase) || n.DeviceClass.Equals("Mouse", StringComparison.OrdinalIgnoreCase) ||
                n.DeviceClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("touchpad", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("触控板", StringComparison.Ordinal);
            bool companion = !kuma && nodes.Any(o => (n.Parent != 0 && o.Parent == n.Parent || n.Container != Guid.Empty && o.Container == n.Container) &&
                o.CompatibleIds.Contains("HID_DEVICE_UP:0001_U:0002", StringComparer.OrdinalIgnoreCase));
            string reason = mixed || input || companion ? "触摸候选包含其他输入通道，拒绝操作。" : !healthy ? "触摸设备状态异常或无法查询。" :
                string.IsNullOrWhiteSpace(n.Id) ? "无法读取设备实例 ID。" : "";
            return new TouchDevice(n.Id, disabled, (n.Status & 0x2000) != 0, reason.Length == 0, reason,
                kuma ? TouchMatch.Kuma : TouchMatch.GenericHid, n.Name, n.DeviceClass, n.Status, n.Problem);
        }).ToArray();
    }
}

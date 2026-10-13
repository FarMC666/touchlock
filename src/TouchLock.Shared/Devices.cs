using System.Text.Json;
namespace TouchLock.Shared;
public sealed record TouchDevice(string Id, bool Disabled, bool Disableable, bool Safe, string Reason = "");
public interface IDeviceBackend
{
    IReadOnlyList<TouchDevice> Enumerate();
    bool IsDisabled(string id);
    void Disable(string id);
    void Enable(string id);
}
public sealed record RecoveryRecord(int Version, string[] DeviceIds);
public sealed class DeviceTransaction(IDeviceBackend backend, string journal)
{
    public bool Pending => File.Exists(journal);
    public string[] ReadJournal()
    {
        if (!Pending) return [];
        if (new FileInfo(journal).Length > 65536) throw new InvalidDataException("恢复记录损坏。");
        var r = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(journal)) ?? throw new InvalidDataException();
        if (r.Version != 1 || r.DeviceIds.Length > 128 || r.DeviceIds.Any(id => !id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || id.Length > 1024)) throw new InvalidDataException("恢复记录损坏。");
        return r.DeviceIds;
    }
    public void Lock()
    {
        if (Pending) throw new InvalidOperationException("上次触摸恢复尚未完成。");
        var all = backend.Enumerate();
        if (all.Count == 0) throw new InvalidOperationException("未找到标准 HID 触摸屏。");
        if (all.Any(d => !d.Safe)) throw new InvalidOperationException(all.First(d => !d.Safe).Reason);
        var targets = all.Where(d => !d.Disabled).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("触摸屏已由其他程序禁用。");
        if (targets.Any(d => !d.Disableable)) throw new InvalidOperationException("触摸屏不支持动态禁用。");
        List<string> owned = [];
        try
        {
            foreach (var d in targets)
            {
                if (backend.IsDisabled(d.Id)) throw new IOException("设备状态已变化，请重试。");
                owned.Add(d.Id);
                AtomicFile.Write(journal, JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(1, owned.ToArray())));
                backend.Disable(d.Id);
                if (!backend.IsDisabled(d.Id)) throw new IOException("触摸屏禁用未生效。");
            }
        }
        catch { Restore(); throw; }
    }
    public void Restore()
    {
        var ids = ReadJournal(); List<string> pending = [];
        foreach (string id in ids)
        {
            try { backend.Enable(id); if (backend.IsDisabled(id)) pending.Add(id); }
            catch { pending.Add(id); }
        }
        if (pending.Count > 0)
        {
            AtomicFile.Write(journal, JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(1, pending.ToArray())));
            throw new IOException("部分触摸屏尚未恢复，将继续重试；必要时重新连接设备或重启 Windows。");
        }
        if (Pending) File.Delete(journal);
    }
}

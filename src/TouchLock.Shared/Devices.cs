using System.Text.Json;
namespace TouchLock.Shared;
public sealed record TouchDevice(string Id, bool Disabled, bool Disableable, bool Safe, string Reason = "",
    TouchMatch Match = TouchMatch.GenericHid, string Name = "", string DeviceClass = "", uint Status = 0, uint Problem = 0);
public interface IDeviceBackend
{
    IReadOnlyList<TouchDevice> Enumerate();
    bool IsDisabled(string id);
    void Disable(string id);
    void Enable(string id);
    TouchDevice? Find(string id) => Enumerate().SingleOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}
public sealed record RecoveryTarget(string Id, TouchMatch Match, string Name, string DeviceClass);
public sealed record RecoveryRecord(int Version, string[] DeviceIds, RecoveryTarget[]? Targets = null);
public sealed record DeviceOperationSummary(int Found, int AlreadyDisabled, int Attempted, int Succeeded, int Failed, string Phase);
public sealed class DeviceTransaction(IDeviceBackend backend, string journal)
{
    public bool Pending => File.Exists(journal);
    public Action? Progress { get; set; }
    public DeviceOperationSummary? Summary { get; private set; }
    private RecoveryTarget[] ReadTargets()
    {
        Paths.NoLinks(journal);
        if (!Pending) return [];
        if (new FileInfo(journal).Length > 65536) throw new InvalidDataException("恢复记录损坏。");
        var r = JsonSerializer.Deserialize<RecoveryRecord>(File.ReadAllBytes(journal)) ?? throw new InvalidDataException();
        if (r.DeviceIds == null || r.DeviceIds.Length > 128 || r.DeviceIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() != r.DeviceIds.Length) throw new InvalidDataException("恢复记录损坏。");
        RecoveryTarget[] targets;
        if (r.Version == 1) targets = r.DeviceIds.Select(id => new RecoveryTarget(id, TouchMatch.GenericHid, "", "")).ToArray();
        else if (r.Version == 2 && r.Targets != null && r.Targets.Select(t => t.Id).SequenceEqual(r.DeviceIds)) targets = r.Targets;
        else throw new InvalidDataException("恢复记录版本无效。");
        foreach (var target in targets)
            if (string.IsNullOrWhiteSpace(target.Id) || target.Id.Length > 1024 || target.Id.Any(char.IsControl) || !target.Id.Contains('\\') ||
                !Enum.IsDefined(target.Match) || (target.Match == TouchMatch.GenericHid && !target.Id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) ||
                (target.Match == TouchMatch.Kuma && !target.Name.Equals(DeviceSelection.KumaName, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("恢复设备身份无效。");
        return targets;
    }
    public string[] ReadJournal() => ReadTargets().Select(t => t.Id).ToArray();
    private void Save(IEnumerable<RecoveryTarget> targets)
    {
        var values = targets.ToArray();
        AtomicFile.Write(journal, JsonSerializer.SerializeToUtf8Bytes(new RecoveryRecord(2, values.Select(t => t.Id).ToArray(), values)));
    }
    private void Report(DeviceOperationSummary summary)
    {
        Summary = summary; Diagnostics.Write("device-summary", summary); Progress?.Invoke();
    }
    public void Lock()
    {
        if (Pending) throw new InvalidOperationException("上次触摸恢复尚未完成。");
        var all = backend.Enumerate();
        if (all.Count == 0) throw new InvalidOperationException("未找到可操作的 Kuma 或标准 HID 触摸屏。");
        if (all.Count > 128) throw new InvalidOperationException("触摸设备数量超出安全上限。");
        if (all.Any(d => !d.Safe)) throw new InvalidOperationException(all.First(d => !d.Safe).Reason);
        var targets = all.Where(d => !d.Disabled).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("触摸屏已由其他程序禁用。");
        if (targets.Any(d => !d.Disableable)) throw new InvalidOperationException("触摸屏不支持动态禁用。");
        List<RecoveryTarget> owned = [];
        List<string> errors = [];
        int succeeded = 0, attempted = 0;
        Report(new(all.Count, all.Count - targets.Length, 0, 0, 0, "disable"));
        try
        {
            foreach (var d in targets)
            {
                Progress?.Invoke();
                if (backend.IsDisabled(d.Id)) throw new IOException("设备状态已变化，请重试。");
                owned.Add(new(d.Id, d.Match, d.Name, d.DeviceClass));
                Save(owned); // Must reach stable storage before a possible disable.
                attempted++;
                try
                {
                    Diagnostics.Write("device-before-disable", d);
                    backend.Disable(d.Id);
                    if (!backend.IsDisabled(d.Id)) throw new IOException("设备禁用调用未实际生效。");
                    succeeded++;
                }
                catch (Exception e) { errors.Add(d.Id + ": " + e.Message); Diagnostics.Write("device-disable-failed", new { d.Id, d.Name, Error = e.Message }); }
                Report(new(all.Count, all.Count - targets.Length, attempted, succeeded, errors.Count, "disable"));
            }
            var after = backend.Enumerate();
            Diagnostics.Write("device-after-disable", after);
            if (errors.Count > 0 || all.Any(d => !after.Any(a => a.Id.Equals(d.Id, StringComparison.OrdinalIgnoreCase) && a.Disabled && a.Safe)))
                throw new IOException($"发现 {all.Count} 个，尝试 {attempted} 个，成功禁用 {succeeded} 个，失败 {errors.Count} 个；最终状态校验未通过，将恢复本次操作。" + string.Join("；", errors));
        }
        catch
        {
            var locking = Summary;
            try { Restore(); } finally { Summary = locking; }
            throw;
        }
    }
    public void Restore()
    {
        var targets = ReadTargets();
        if (targets.Length == 0) return;
        List<RecoveryTarget> pending = [];
        int attempted = 0;
        foreach (var target in targets)
        {
            Progress?.Invoke(); attempted++;
            try
            {
                var current = backend.Find(target.Id);
                if (current == null || current.Match != target.Match ||
                    (target.Name.Length > 0 && !current.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) ||
                    (target.DeviceClass.Length > 0 && !current.DeviceClass.Equals(target.DeviceClass, StringComparison.OrdinalIgnoreCase))) throw new IOException("设备断开或恢复身份不匹配。");
                backend.Enable(target.Id);
                if (backend.IsDisabled(target.Id)) throw new IOException("设备仍处于禁用状态。");
                Diagnostics.Write("device-restored", new { target.Id, target.Name });
            }
            catch (Exception e) { pending.Add(target); Diagnostics.Write("device-restore-failed", new { target.Id, target.Name, Error = e.Message }); }
            Report(new(targets.Length, 0, attempted, attempted - pending.Count, pending.Count, "restore"));
        }
        if (pending.Count > 0)
        {
            Save(pending);
            throw new IOException($"{pending.Count} 个触摸设备尚未恢复，将继续重试；必要时重新连接设备或重启 Windows。");
        }
        if (Pending) File.Delete(journal);
    }
}

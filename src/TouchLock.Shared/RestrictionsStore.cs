using System.Text.Json;
namespace TouchLock.Shared;

public sealed class RestrictionsStore(string path, ISettingsPolicy? systemPolicy = null)
{
    private readonly ISettingsPolicy policy = systemPolicy ?? new WindowsSettingsPolicy();
    private sealed record Settings(int Version, bool? BluetoothSettingsBlocked = null, string? Before = null, string? Applied = null);
    public bool Pending => File.Exists(path);
    private Settings? Record()
    {
        Paths.NoLinks(path);
        if (!Pending) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("蓝牙策略恢复记录损坏。");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path));
        if (settings == null || !(settings.Version == 1 && settings.BluetoothSettingsBlocked != null || settings.Version == 2 && settings.Applied != null))
            throw new InvalidDataException("蓝牙策略恢复记录损坏，请使用管理员维护；未覆盖系统策略。");
        return settings;
    }
    public bool Read()
    {
        return Record()?.Version == 2;
    }
    public void Set(bool blocked)
    {
        Paths.NoLinks(path);
        if (!blocked) { Restore(); return; }
        policy.EnsureSupported();
        if (Record() is { Version: 2 })
        {
            if (ValidationError().Length > 0) throw new IOException(ValidationError());
            return;
        }
        string? before = policy.Read(); string applied = Block(before);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new Settings(2, Before: before, Applied: applied)));
        // A crash after this point leaves the original value available to the guardian.
        policy.Write(applied);
        if (policy.Read() != applied) throw new IOException("蓝牙设置策略写入后校验失败，已保留恢复记录。");
        Diagnostics.Write("bluetooth-policy-applied", new { Scope = "machine" });
    }
    public void Restore()
    {
        var record = Record();
        if (record == null) return;
        if (record.Version == 1) { File.Delete(path); return; } // Legacy UI-only checkbox is deliberately not restored.
        string? current = policy.Read();
        if (current == record.Applied)
        {
            policy.Write(record.Before);
            if (policy.Read() != record.Before) throw new IOException("原设置页策略恢复未生效，恢复记录已保留。");
        }
        else if (current != record.Before)
            throw new IOException("设置页策略已被其他管理工具修改，未覆盖外部配置。请管理员核对后恢复；记录已保留。");
        File.Delete(path);
        Diagnostics.Write("bluetooth-policy-restored", new { Scope = "machine" });
    }
    public string ValidationError()
    {
        var record = Record();
        return record is { Version: 2 } && policy.Read() != record.Applied ? "蓝牙保护策略已被外部配置改变，当前无法保证保护生效。请检查管理策略和诊断日志。" : "";
    }
    public static string Block(string? existing)
    {
        if (string.IsNullOrWhiteSpace(existing)) return "hide:bluetooth;connecteddevices";
        int colon = existing.IndexOf(':');
        if (colon < 0) throw new IOException("现有设置页策略格式不受支持，未覆盖原策略。");
        string mode = existing[..colon].Trim();
        var pages = existing[(colon + 1)..].Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mode.Equals("hide", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string page in new[] { "bluetooth", "connecteddevices" }) if (!pages.Contains(page, StringComparer.OrdinalIgnoreCase)) pages.Add(page);
            return "hide:" + string.Join(';', pages);
        }
        if (mode.Equals("showonly", StringComparison.OrdinalIgnoreCase))
        {
            pages.RemoveAll(page => page.Equals("bluetooth", StringComparison.OrdinalIgnoreCase) || page.Equals("connecteddevices", StringComparison.OrdinalIgnoreCase));
            if (pages.Count == 0) throw new IOException("现有仅显示策略只包含蓝牙页面，无法安全合并空列表。请管理员先调整该策略。");
            return "showonly:" + string.Join(';', pages);
        }
        throw new IOException("现有设置页策略模式不受支持，未覆盖原策略。");
    }
}

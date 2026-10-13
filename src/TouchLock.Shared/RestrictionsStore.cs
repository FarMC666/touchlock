using System.Text.Json;
namespace TouchLock.Shared;

public sealed class RestrictionsStore(string path)
{
    private sealed record Settings(int Version, bool? BluetoothSettingsBlocked);
    public bool Read()
    {
        Paths.NoLinks(path);
        if (!File.Exists(path)) return false; // Existing installations have no restrictions file.
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("蓝牙设置限制配置损坏。");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllBytes(path));
        if (settings is not { Version: 1, BluetoothSettingsBlocked: not null }) throw new InvalidDataException("蓝牙设置限制配置损坏。");
        return settings.BluetoothSettingsBlocked.Value;
    }
    public void Set(bool blocked)
    {
        Paths.NoLinks(path);
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(new Settings(1, blocked)));
    }
}

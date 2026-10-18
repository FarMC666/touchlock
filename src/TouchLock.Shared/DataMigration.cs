using System.Security.Cryptography;
using System.Text.Json;
namespace TouchLock.Shared;

public static class DataMigration
{
    private sealed record Marker(int Version, string Source);
    public static void CopyPassword(string sourceDirectory, string destinationDirectory)
    {
        if (Path.GetFullPath(sourceDirectory).Equals(Path.GetFullPath(destinationDirectory), StringComparison.OrdinalIgnoreCase)) return;
        Paths.NoLinks(sourceDirectory); Paths.NoLinks(destinationDirectory);
        string source = Path.Combine(sourceDirectory, "password.json"), destination = Path.Combine(destinationDirectory, "password.json");
        string markerPath = Path.Combine(destinationDirectory, "migration-v2.json");
        if (File.Exists(markerPath))
        {
            if (new FileInfo(markerPath).Length > 4096) throw new InvalidDataException("数据迁移记录损坏。");
            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllBytes(markerPath));
            if (marker is not { Version: 2 } || !sourceDirectory.Equals(marker.Source, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("数据迁移来源冲突。");
            if (File.Exists(destination)) { new PasswordStore(destination).Read(); return; }
            throw new IOException("已迁移的口令文件缺失，未使用旧口令替代。请管理员检查。");
        }
        if (!File.Exists(source)) return;
        new PasswordStore(source).Read();
        byte[] bytes = File.ReadAllBytes(source);
        try
        {
            if (File.Exists(destination) && !CryptographicOperations.FixedTimeEquals(bytes, File.ReadAllBytes(destination)))
                throw new IOException("安装盘与旧目录存在不同口令数据，未覆盖任何文件。请先通过原安装程序维护。");
            if (!File.Exists(destination)) AtomicFile.Write(destination, bytes);
            new PasswordStore(destination).Read();
            if (!CryptographicOperations.FixedTimeEquals(bytes, File.ReadAllBytes(destination))) throw new IOException("口令迁移校验失败，原数据已保留。");
            AtomicFile.Write(markerPath, JsonSerializer.SerializeToUtf8Bytes(new Marker(2, sourceDirectory)));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace TouchLock.Shared;
public sealed record PasswordRecord(int Version, string Algorithm, int Iterations, byte[] Salt, byte[] Hash);
public sealed class PasswordStore(string path)
{
    public const int Iterations = 600_000;
    public static bool ValidLength(ReadOnlySpan<byte> password)
    {
        try { int length = new UTF8Encoding(false, true).GetCharCount(password); return length is >= 8 and <= 128; }
        catch (DecoderFallbackException) { return false; }
    }
    public static PasswordRecord Derive(ReadOnlySpan<byte> password)
    {
        if (!ValidLength(password)) throw new InvalidDataException("管理口令须为 8～128 个字符。");
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new(1, "PBKDF2-HMAC-SHA256", Iterations, salt, Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32));
    }
    public PasswordRecord Read()
    {
        if (new FileInfo(path).Length > 4096) throw new InvalidDataException("口令配置损坏，请运行安装程序重置。");
        var r = JsonSerializer.Deserialize<PasswordRecord>(File.ReadAllBytes(path)) ?? throw new InvalidDataException();
        if (r.Version != 1 || r.Algorithm != "PBKDF2-HMAC-SHA256" || r.Iterations != Iterations || r.Salt is not { Length: 16 } || r.Hash is not { Length: 32 }) throw new InvalidDataException("口令配置损坏，请运行安装程序重置。");
        return r;
    }
    public bool Verify(ReadOnlySpan<byte> password)
    {
        var r = Read(); byte[] actual = Rfc2898DeriveBytes.Pbkdf2(password, r.Salt, r.Iterations, HashAlgorithmName.SHA256, 32);
        try { return CryptographicOperations.FixedTimeEquals(actual, r.Hash); }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }
    public void Set(ReadOnlySpan<byte> password) => AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(Derive(password)));
}
public static class AtomicFile
{
    public static void Write(string path, byte[] data)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var f = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { f.Write(data); f.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

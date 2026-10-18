using System.Buffers.Binary;
using System.Text.Json;
namespace TouchLock.Shared;
public enum LockState { Unlocked, Locking, Locked, Authenticating, Restoring, Stopping, Unavailable }
public enum AuthenticationIntent { Unlock, Exit, ChangePassword, AllowBluetoothSettings }
public sealed record Status(LockState State, long Generation, bool Available, string Reason, bool Stopped = false, bool BluetoothSettingsBlocked = false, string BluetoothSettingsError = "",
    int TargetCount = 0, int DisabledCount = 0, Guid OperationId = default, bool OperationRunning = false,
    string OperationError = "", DeviceOperationSummary? DeviceOperation = null, string DiagnosticsError = "");
public sealed record ControlRequest(string Operation, int Version = 2, AuthenticationIntent Intent = AuthenticationIntent.Unlock, Guid RequestId = default);
public sealed record ControlReply(bool Success, Status Status, string Error = "", Guid RequestId = default);
public static class Wire
{
    public const string PipeName = "TouchLock.Control.v2", PasswordPipe = "TouchLock.Password.v2";
    public const int Version = 2;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 8192) throw new InvalidDataException();
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, bytes.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > 8192) throw new InvalidDataException();
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException();
    }
    // Secrets never enter JSON. Caller erases the arrays after use.
    public static async Task WriteSecret(Stream stream, byte[] value, CancellationToken token)
    {
        if (value.Length > 512) throw new InvalidDataException();
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, value.Length);
        await stream.WriteAsync(size, token); await stream.WriteAsync(value, token);
    }
    public static async Task<byte[]> ReadSecret(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is < 0 or > 512) throw new InvalidDataException();
        byte[] value = new byte[length];
        try { await stream.ReadExactlyAsync(value, token); return value; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(value); throw; }
    }
}

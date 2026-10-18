using System.IO.Pipes;
using System.Security.Principal;
namespace TouchLock.Shared;
public static class ServiceClient
{
    public static async Task<ControlReply> Complete(ControlReply reply)
    {
        Guid operation = reply.Status.OperationId;
        long deadline = Environment.TickCount64 + 30 * 60 * 1000;
        while (reply.Success && reply.Status.OperationRunning && reply.Status.OperationId == operation)
        {
            if (Environment.TickCount64 >= deadline) return reply with { Success = false, Error = "设备操作仍未完成，请查看托盘状态与诊断日志；不要重复提交。" };
            await Task.Delay(100);
            reply = await Request(new("status"));
        }
        return reply.Status.OperationError.Length == 0 ? reply : reply with { Success = false, Error = reply.Status.OperationError };
    }
    private static NamedPipeClientStream Pipe(string name) => new(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
    public static async Task<ControlReply> Request(ControlRequest request)
    {
        using var timeout = new CancellationTokenSource(10000);
        using var pipe = Pipe(Wire.PipeName); await pipe.ConnectAsync(timeout.Token);
        if (!Services.Genuine(pipe)) throw new UnauthorizedAccessException("服务身份验证失败。");
        await Wire.WriteAsync(pipe, request, timeout.Token);
        return await Wire.ReadAsync<ControlReply>(pipe, timeout.Token);
    }
    public static async Task<ControlReply> Submit(Guid requestId, byte[] password, byte[] replacement)
    {
        using var timeout = new CancellationTokenSource(15000);
        using var pipe = Pipe(Wire.PasswordPipe); await pipe.ConnectAsync(timeout.Token);
        if (!Services.Genuine(pipe)) throw new UnauthorizedAccessException("服务身份验证失败。");
        await Wire.WriteAsync(pipe, new ControlRequest("submit", RequestId: requestId), timeout.Token);
        await Wire.WriteSecret(pipe, password, timeout.Token); await Wire.WriteSecret(pipe, replacement, timeout.Token); await pipe.FlushAsync(timeout.Token);
        return await Wire.ReadAsync<ControlReply>(pipe, timeout.Token);
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace TouchLock.Shared;

// Only explicitly selected diagnostic fields belong here. Never serialize IPC or password records.
public static class Diagnostics
{
    private static readonly object Gate = new();
    private static string? directory;
    public static string Version => typeof(Diagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    public static void Initialize(string dataDirectory)
    {
        directory = Path.Combine(dataDirectory, "Logs");
        Paths.NoLinks(directory);
        Directory.CreateDirectory(directory);
        WindowsSecurity.ProtectDirectory(directory, false);
        Write("startup", new { Windows = Environment.OSVersion.VersionString, Version, Administrator = WindowsSecurity.Elevated, System = WindowsIdentity.GetCurrent().IsSystem, Process = Process.GetCurrentProcess().ProcessName });
    }
    public static void Write(string eventName, object details)
    {
        if (directory == null) return;
        try
        {
            lock (Gate)
            {
                string file = Path.Combine(directory, Process.GetCurrentProcess().ProcessName + ".jsonl");
                Paths.NoLinks(file);
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024)
                {
                    for (int index = 4; index >= 1; index--)
                    {
                        string source = index == 1 ? file : file + "." + (index - 1);
                        if (File.Exists(source)) File.Move(source, file + "." + index, true);
                    }
                }
                File.AppendAllText(file, JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Event = eventName, Details = details }) + Environment.NewLine);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A full disk must not interrupt restoration. Surface the failure through the service status.
            Error = "诊断日志写入失败：" + e.Message;
        }
    }
    public static string Error { get; private set; } = "";
}

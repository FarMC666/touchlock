using TouchLock.Shared;
using TouchLock.UI;
namespace TouchLock.App;

internal sealed class AboutDialog : AdaptiveForm
{
    public AboutDialog(Status? status, bool hotkeyRegistered, string monitorError)
    {
        Text = "关于 TouchLock"; ClientSize = new Size(510, 460);
        var content = Content();
        content.Controls.Add(TextBlock("TouchLock\n\nVersion 1.0.0\n\n一个用于暂时锁定 Windows\n触摸屏输入的小工具。\n\n永远爱Ayanami"));
        content.Controls.Add(TextBlock((status?.Reason ?? "正在连接服务。") +
            $"\n目标设备：{status?.TargetCount ?? 0}，已禁用：{status?.DisabledCount ?? 0}" +
            (hotkeyRegistered ? "" : "\n快捷键被占用，请使用托盘菜单。") +
            (status?.BluetoothSettingsError.Length > 0 ? "\n" + status.BluetoothSettingsError : "") +
            (monitorError.Length > 0 ? "\n" + monitorError : "") +
            (status?.DiagnosticsError.Length > 0 ? "\n" + status.DiagnosticsError : "") +
            "\n\n诊断日志（管理员可读取）：\n" + Path.Combine(Paths.Data, "Logs") + "\n构建：" + Diagnostics.Version));
    }
}

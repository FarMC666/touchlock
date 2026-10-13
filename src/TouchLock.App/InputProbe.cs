#if DEBUG
using System.Runtime.InteropServices;
using System.Text.Json;
namespace TouchLock.App;
// Diagnostic window only: never hooks, redirects, or blocks input outside this window.
internal sealed class InputProbe : Form
{
    [StructLayout(LayoutKind.Sequential)] private struct Source { public uint Device, Origin; }
    [DllImport("user32")] private static extern bool GetPointerType(uint id, out uint type);
    [DllImport("user32")] private static extern bool GetCurrentInputMessageSource(out Source source);
    private readonly string log;
    private readonly Dictionary<uint, int> counters = [];
    private string last = "等待手机操作…";
    public InputProbe(string path)
    {
        log = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "");
        Text = "TouchLock 输入测试 — 请用手机操作"; ClientSize = new Size(700, 390);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Microsoft YaHei UI", 13);
        BackColor = Color.White; DoubleBuffered = true;
        FormClosed += (_, _) => Record("closed", 0, default);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        string text = "请用 UU 手机端的平板触控模式操作此白色区域：\n单指点击、拖动，再尝试双指操作。\n\n" +
            $"触摸按下：{counters.GetValueOrDefault(2u)}     鼠标点击：{counters.GetValueOrDefault(4u)}\n" +
            $"笔按下：{counters.GetValueOrDefault(3u)}\n\n最近输入：{last}\n\n测试完成后可以关闭此窗口。";
        e.Graphics.DrawString(text, Font, Brushes.Black, new RectangleF(25, 25, ClientSize.Width - 50, ClientSize.Height - 50));
    }
    private void Record(string message, uint pointerType, Source source)
    {
        File.AppendAllText(log, JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Message = message, PointerType = pointerType, DeviceType = source.Device, Origin = source.Origin }) + "\n");
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x246 or 0x247 or 0x201) // POINTERDOWN, POINTERUP, LBUTTONDOWN
        {
            GetCurrentInputMessageSource(out Source source);
            uint type = 4;
            if (m.Msg != 0x201) GetPointerType((uint)((long)m.WParam & 0xffff), out type);
            string name = m.Msg == 0x247 ? "pointer-up" : m.Msg == 0x201 ? "mouse-down" : "pointer-down";
            if (m.Msg != 0x247) counters[type] = counters.GetValueOrDefault(type) + 1;
            last = $"{name} / 类型 {type} / 来源 {source.Origin}";
            Record(name, type, source); Invalidate();
        }
        base.WndProc(ref m);
    }
}
#endif

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TouchLock.Shared;
namespace TouchLock.UI;
internal sealed class PasswordDialog : AdaptiveForm
{
    private readonly TextBox current = Box(), replacement = Box(), confirm = Box();
    private readonly bool initialize, change;
    public byte[] Password { get; private set; } = [];
    public byte[] NewPassword { get; private set; } = [];
    private static TextBox Box() => new() { UseSystemPasswordChar = true, MaxLength = 128, Dock = DockStyle.Top };
    public PasswordDialog(bool initialize = false, bool change = false, bool exit = false, bool uninstall = false, bool allowBluetoothSettings = false)
    {
        this.initialize = initialize; this.change = change;
        Text = initialize ? "设置 TouchLock 管理口令" : change ? "修改管理口令" : uninstall ? "卸载 TouchLock" : exit ? "退出 TouchLock" : allowBluetoothSettings ? "允许进入 Windows 蓝牙设置" : "解除触摸锁定";
        ClientSize = new Size(460, initialize ? 340 : change ? 410 : 300);
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft };
        var layout = Content(buttons);
        layout.Controls.Add(new Label { Text = initialize ? "设置独立管理口令（8～128 个字符）。\n解除限制、退出和卸载时需要使用。" : uninstall ? "卸载前需要验证管理口令。\n恢复触摸前请移开屏幕上的手指。" : allowBluetoothSettings ? "取消蓝牙设置限制前需要验证管理口令。\n使用鼠标或物理键盘输入管理口令。" : "恢复触摸前请移开屏幕上的手指。\n使用鼠标或物理键盘输入管理口令。", AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        if (!initialize) { layout.Controls.Add(new Label { Text = change ? "当前管理口令" : "管理口令", AutoSize = true }); layout.Controls.Add(current); }
        if (initialize || change)
        {
            layout.Controls.Add(new Label { Text = "新管理口令", AutoSize = true }); layout.Controls.Add(replacement);
            layout.Controls.Add(new Label { Text = "确认新管理口令", AutoSize = true }); layout.Controls.Add(confirm);
        }
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, AutoSize = true, MinimumSize = new Size(95, 34) };
        var ok = new Button { Text = uninstall ? "卸载" : "确定", AutoSize = true, MinimumSize = new Size(95, 34) };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); CancelButton = cancel; AcceptButton = ok;
        ok.Click += (_, _) => Accept(); FormClosed += (_, _) => { current.Clear(); replacement.Clear(); confirm.Clear(); };
    }
    [DllImport("user32", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint window, [Out] char[] value, int maximum);
    private static byte[] Read(TextBox box)
    {
        char[] value = new char[129];
        try { int length = GetWindowTextW(box.Handle, value, value.Length); return Encoding.UTF8.GetBytes(value, 0, length); }
        finally { Array.Clear(value); }
    }
    private void Accept()
    {
        byte[] old = [], next = [], repeated = [];
        try
        {
            if (!initialize) old = Read(current);
            if (initialize || change)
            {
                next = Read(replacement); repeated = Read(confirm);
                if (!PasswordStore.ValidLength(next)) { MessageBox.Show(this, "管理口令须为 8～128 个字符。", "TouchLock"); return; }
                if (!CryptographicOperations.FixedTimeEquals(next, repeated)) { MessageBox.Show(this, "两次输入的新管理口令不一致。", "TouchLock"); return; }
            }
            Password = old; NewPassword = next; old = []; next = [];
            DialogResult = DialogResult.OK; Close();
        }
        finally { CryptographicOperations.ZeroMemory(old); CryptographicOperations.ZeroMemory(next); CryptographicOperations.ZeroMemory(repeated); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { CryptographicOperations.ZeroMemory(Password); CryptographicOperations.ZeroMemory(NewPassword); }
        base.Dispose(disposing);
    }
}

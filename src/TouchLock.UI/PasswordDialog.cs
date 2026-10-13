using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TouchLock.Shared;
namespace TouchLock.UI;
internal sealed class PasswordDialog : Form
{
    private readonly TextBox current = Box(), replacement = Box(), confirm = Box();
    private readonly bool initialize, change;
    public byte[] Password { get; private set; } = [];
    public byte[] NewPassword { get; private set; } = [];
    private static TextBox Box() => new() { UseSystemPasswordChar = true, MaxLength = 128, Width = 370 };
    public PasswordDialog(bool initialize = false, bool change = false, bool exit = false)
    {
        this.initialize = initialize; this.change = change;
        Text = initialize ? "设置 TouchLock 管理口令" : change ? "修改管理口令" : exit ? "退出 TouchLock" : "解除触摸锁定";
        Font = new Font("Microsoft YaHei UI", 10); ClientSize = new Size(430, initialize ? 280 : change ? 365 : 255);
        StartPosition = FormStartPosition.CenterScreen; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24, 18, 24, 18), FlowDirection = FlowDirection.TopDown, WrapContents = false };
        Controls.Add(layout);
        layout.Controls.Add(new Label { Text = initialize ? "设置独立管理口令（8～128 个字符）。\n请记住口令，解锁和退出时需要使用。" : "恢复触摸前请移开屏幕上的手指。\n使用鼠标或物理键盘输入管理口令。", AutoSize = true, Margin = new Padding(0, 0, 0, 14) });
        if (!initialize) { layout.Controls.Add(new Label { Text = change ? "当前管理口令" : "管理口令", AutoSize = true }); layout.Controls.Add(current); }
        if (initialize || change)
        {
            layout.Controls.Add(new Label { Text = "新管理口令", AutoSize = true }); layout.Controls.Add(replacement);
            layout.Controls.Add(new Label { Text = "确认新管理口令", AutoSize = true }); layout.Controls.Add(confirm);
        }
        var buttons = new FlowLayoutPanel { Width = 370, Height = 45, FlowDirection = FlowDirection.RightToLeft, Margin = new Padding(0, 15, 0, 0) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Width = 95, Height = 32 };
        var ok = new Button { Text = "确定", Width = 95, Height = 32 };
        buttons.Controls.Add(cancel); buttons.Controls.Add(ok); layout.Controls.Add(buttons); CancelButton = cancel; AcceptButton = ok;
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

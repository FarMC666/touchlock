#if DEBUG
using System.Security.Cryptography;
using TouchLock.Shared;
namespace TouchLock.App;
internal static class Diagnostic
{
    public static int Preview(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string mode in new[] { "initial", "change", "unlock", "uninstall" })
        {
            using var form = new TouchLock.UI.PasswordDialog(initialize: mode == "initial", change: mode == "change", uninstall: mode == "uninstall");
            string sample = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            form.ShowInTaskbar = false; form.Opacity = 0;
            form.Shown += (_, _) =>
            {
                form.PerformLayout();
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(directory, mode + ".png"));
                foreach (var box in form.Controls.OfType<FlowLayoutPanel>().Single().Controls.OfType<TextBox>()) box.Text = sample;
                ((Button)form.AcceptButton!).PerformClick();
            };
            form.ShowDialog();
            byte[] expected = System.Text.Encoding.UTF8.GetBytes(sample);
            try
            {
                if (form.DialogResult != DialogResult.OK || !CryptographicOperations.FixedTimeEquals(mode is "unlock" or "uninstall" ? form.Password : form.NewPassword, expected)) throw new InvalidOperationException($"UI password capture failed: result={form.DialogResult}, bytes={form.NewPassword.Length}/{form.Password.Length}, expected={expected.Length}.");
                Console.WriteLine("PASS UI password input " + mode);
            }
            finally { CryptographicOperations.ZeroMemory(expected); }
        }
        return 0;
    }
    public static async Task<int> Run()
    {
        byte[] password = await Wire.ReadSecret(Console.OpenStandardInput(), CancellationToken.None);
        byte[] next = RandomNumberGenerator.GetBytes(24); next = System.Text.Encoding.UTF8.GetBytes(Convert.ToHexString(next));
        try
        {
            async Task<ControlReply> Begin(AuthenticationIntent intent) => await ServiceClient.Request(new("begin", Intent: intent));
            void Check(bool result, string label) { if (!result) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
            var start = await ServiceClient.Request(new("status"));
            Check(start.Status.State is LockState.Unlocked or LockState.Unavailable, "service starts unlocked");
            var challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "exit authentication starts");
            var wrong = await ServiceClient.Submit(challenge.RequestId, [], []); Check(!wrong.Success && wrong.Status.State != LockState.Authenticating && !wrong.Status.Stopped, "wrong password keeps service running");
            var replay = await ServiceClient.Submit(challenge.RequestId, password, []); Check(!replay.Success, "expired request rejected");
            challenge = await Begin(AuthenticationIntent.Exit);
            var cancel = await ServiceClient.Request(new("cancel", RequestId: challenge.RequestId)); Check(cancel.Success && !cancel.Status.Stopped, "cancel restores previous state");
            challenge = await Begin(AuthenticationIntent.ChangePassword);
            var change = await ServiceClient.Submit(challenge.RequestId, password, next); Check(change.Success, "password change succeeds");
            challenge = await Begin(AuthenticationIntent.ChangePassword);
            change = await ServiceClient.Submit(challenge.RequestId, next, password); Check(change.Success, "new password verified and original restored");
            for (int i = 0; i < 5; i++)
            {
                challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "wrong-password trial " + (i + 1));
                Check(!(await ServiceClient.Submit(challenge.RequestId, [], [])).Success, "wrong password rejected " + (i + 1));
            }
            Check(!(await Begin(AuthenticationIntent.Exit)).Success, "five failures trigger cooldown");
            await Task.Delay(31000);
            challenge = await Begin(AuthenticationIntent.Exit); Check(challenge.Success, "cooldown expires");
            var exit = await ServiceClient.Submit(challenge.RequestId, password, []); Check(exit.Success && exit.Status.Stopped, "correct password authorizes exit"); return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e.GetType().Name + " " + e.Message); return 1; }
        finally { CryptographicOperations.ZeroMemory(password); CryptographicOperations.ZeroMemory(next); }
    }
}
#endif

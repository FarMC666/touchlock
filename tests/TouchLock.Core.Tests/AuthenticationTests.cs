using System.Reflection;
using System.Text;
using TouchLock.Service;
using TouchLock.Shared;

internal static class AuthenticationTests
{
    public static void Run(string folder, byte[] password, Action<bool, string> check)
    {
        string passwordPath = Path.Combine(folder, "auth-password.json");
        new PasswordStore(passwordPath).Set(password);
        using var controller = new Controller(new NoDevices(), passwordPath, Path.Combine(folder, "auth-recovery.json"), Path.Combine(folder, "auth-restrictions.json"));
        ClientIdentity owner = new(301, 1, 22), intruder = new(302, 1, 23);
        var challenge = controller.Command(new("begin", Intent: AuthenticationIntent.Exit), owner);
        check(challenge.Success, "password exit available without touch hardware");
        check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), intruder, password, []).Success, "exit challenge bound to originating process");
        var wrong = controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, [], []);
        check(!wrong.Success && !wrong.Status.Stopped && wrong.Status.State != LockState.Authenticating, "wrong exit password retains running software");
        check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, password, []).Success, "consumed exit challenge cannot be replayed");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.ChangePassword), owner);
        var badReplacement = controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, password, Encoding.UTF8.GetBytes("short"));
        check(!badReplacement.Success && new PasswordStore(passwordPath).Verify(password), "invalid replacement preserves existing password and ends challenge");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.Exit), owner);
        typeof(Controller).GetField("deadline", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(controller, Environment.TickCount64 - 1);
        var expired = controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, password, []);
        check(!expired.Success && expired.Status.State != LockState.Authenticating && !expired.Status.Stopped, "expired owned challenge immediately cancels without exit");
        challenge = controller.Command(new("begin", Intent: AuthenticationIntent.ChangePassword), owner);
        controller.Command(new("cancel", RequestId: challenge.RequestId), owner);
        check(!controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, password, password).Success, "cancelled password change cannot execute later");
        for (int attempt = 0; attempt < 5; attempt++)
        {
            challenge = controller.Command(new("begin", Intent: AuthenticationIntent.Exit), owner);
            controller.Authenticate(new("submit", RequestId: challenge.RequestId), owner, [], []);
        }
        check(!controller.Command(new("begin", Intent: AuthenticationIntent.Exit), owner).Success, "exit password attempts share authentication cooldown");
    }
    private sealed class NoDevices : IDeviceBackend
    {
        public IReadOnlyList<TouchDevice> Enumerate() => [];
        public bool IsDisabled(string id) => throw new InvalidOperationException();
        public void Disable(string id) => throw new InvalidOperationException();
        public void Enable(string id) => throw new InvalidOperationException();
    }
}

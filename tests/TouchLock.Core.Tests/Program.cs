using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using TouchLock.Shared;
if (args.SequenceEqual(["--inventory"])) { Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new WindowsDevices().Enumerate())); return; }
if (args.Length == 2 && args[0] == "--validate-install") { Console.WriteLine(InstallationDirectory.ValidateNew(args[1])); return; }
int passed = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Throws(Action action, string name) { try { action(); } catch { Check(true, name); return; } throw new Exception(name); }
string folder = Path.Combine(Path.GetTempPath(), "TouchLock-Core-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
try
{
    string custom = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "TouchLock 自定义 安装");
    Check(InstallationDirectory.Normalize(custom.Replace('\\', '/')) == custom, "custom path keeps Unicode and spaces");
    Throws(() => InstallationDirectory.Normalize("relative/TouchLock"), "relative installation path rejected");
    Throws(() => InstallationDirectory.Normalize(Path.GetPathRoot(Environment.SystemDirectory)!), "drive root installation rejected");
    Throws(() => InstallationDirectory.Normalize(@"\\server\share\TouchLock"), "network installation rejected");
    Throws(() => InstallationDirectory.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "TouchLock")), "system directory installation rejected");
    Throws(() => InstallationDirectory.Normalize(Path.Combine(Paths.Data, "application")), "configuration directory installation rejected");
    Throws(() => InstallationDirectory.Normalize(Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "CON")), "reserved Windows folder name rejected");
    Throws(() => InstallationDirectory.Normalize(custom + "."), "ambiguous trailing dot rejected");
    File.WriteAllText(Path.Combine(folder, "keep.txt"), "preserved");
    Throws(() => InstallationDirectory.ValidateNew(folder), "existing installation folder rejected");
    Check(File.ReadAllText(Path.Combine(folder, "keep.txt")) == "preserved", "existing files preserved by path validation");
    Throws(() => InstallationDirectory.ValidateNew(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "TouchLock-" + Guid.NewGuid().ToString("N"))), "user-owned installation ancestor rejected");
    Check(InstallationDirectory.ValidateNew(Path.Combine(InstallationDirectory.Default + "-" + Guid.NewGuid().ToString("N"), "App")).Length > 0, "new folders under protected parent accepted");
    string journal = Path.Combine(folder, "recovery.json"); var fake = new Fake();
    var transaction = new DeviceTransaction(fake, journal);
    fake.Add("A"); fake.Add("PRE", true); fake.Add("B");
    fake.BeforeDisable = _ => Check(transaction.Pending, "journal exists before each disable");
    transaction.Lock(); Check(fake.IsDisabled("HID\\A") && fake.IsDisabled("HID\\B"), "all touchscreen targets disabled");
    Check(!transaction.ReadJournal().Contains("HID\\PRE"), "pre-disabled device not owned");
    transaction.Restore(); Check(!fake.IsDisabled("HID\\A") && fake.IsDisabled("HID\\PRE") && !transaction.Pending, "restore only owned devices");
    fake.FailDisable = "HID\\B";
    Throws(transaction.Lock, "partial failure reported"); Check(!fake.IsDisabled("HID\\A") && !transaction.Pending, "partial lock rolls back");
    fake.FailDisable = ""; transaction.Lock(); fake.FailEnable = "HID\\B";
    Throws(transaction.Restore, "restore failure reported"); Check(transaction.ReadJournal().SequenceEqual(["HID\\B"]), "failed restore retains only pending nodes");
    fake.FailEnable = ""; transaction.Restore(); Check(!transaction.Pending, "retry completes restore");
    fake.Safe = false; Throws(transaction.Lock, "mixed identity rejected before disable"); Check(!transaction.Pending, "unsafe selection leaves no journal"); fake.Safe = true;
    File.WriteAllText(journal, "{bad}"); Throws(transaction.Restore, "corrupt journal never silently discarded"); File.Delete(journal);
    File.WriteAllText(journal, "{\"Version\":1,\"DeviceIds\":[\"USB\\\\parent\"]}"); Throws(transaction.Restore, "non HID recovery target rejected"); File.Delete(journal);
    fake.Disableable = false; Throws(transaction.Lock, "non-dynamic device rejected"); fake.Disableable = true;
    var store = new PasswordStore(Path.Combine(folder, "password.json"));
    byte[] password = Encoding.UTF8.GetBytes("测试口令-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(12)));
    try
    {
        store.Set(password); var first = store.Read();
        Check(store.Verify(password) && !store.Verify([]), "password correct and wrong verification");
        Check(first.Iterations == 600000 && first.Salt.Length == 16 && first.Hash.Length == 32, "PBKDF2 parameters");
        store.Set(password); Check(!first.Salt.SequenceEqual(store.Read().Salt), "random salt each write");
        Check(!File.ReadAllText(Path.Combine(folder, "password.json")).Contains(Encoding.UTF8.GetString(password), StringComparison.Ordinal), "no plaintext persisted");
        Check(!PasswordStore.ValidLength(Encoding.UTF8.GetBytes("1234567")) && PasswordStore.ValidLength(Encoding.UTF8.GetBytes("12345678")) && !PasswordStore.ValidLength(Encoding.UTF8.GetBytes(new string('a', 129))), "password length bounds");
        File.WriteAllText(Path.Combine(folder, "password.json"), "{}"); Throws(() => store.Verify(password), "corrupt configuration rejects authentication");
    }
    finally { CryptographicOperations.ZeroMemory(password); }
    using var stream = new MemoryStream(); await Wire.WriteAsync(stream, new ControlRequest("lock"), CancellationToken.None); stream.Position = 0;
    Check((await Wire.ReadAsync<ControlRequest>(stream, CancellationToken.None)).Version == 2, "V2 framing round trip");
    using var bad = new MemoryStream(); byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, 513); bad.Write(size); bad.Position = 0;
    try { await Wire.ReadSecret(bad, CancellationToken.None); throw new Exception("secret bounds"); } catch (InvalidDataException) { Check(true, "binary secret length bounded"); }
    Console.WriteLine($"{passed} checks passed.");
}
catch (Exception e) { Console.Error.WriteLine("FAIL " + e.Message); Environment.ExitCode = 1; }
finally { Directory.Delete(folder, true); }
sealed class Fake : IDeviceBackend
{
    private readonly Dictionary<string, bool> nodes = [];
    public string FailDisable = "", FailEnable = "";
    public bool Safe = true, Disableable = true;
    public Action<string>? BeforeDisable;
    public void Add(string name, bool disabled = false) => nodes.Add("HID\\" + name, disabled);
    public IReadOnlyList<TouchDevice> Enumerate() => nodes.Select(n => new TouchDevice(n.Key, n.Value, Disableable, Safe, "mixed identity")).ToArray();
    public bool IsDisabled(string id) => nodes[id];
    public void Disable(string id) { BeforeDisable?.Invoke(id); if (id == FailDisable) throw new IOException(); nodes[id] = true; }
    public void Enable(string id) { if (id == FailEnable) throw new IOException(); nodes[id] = false; }
}

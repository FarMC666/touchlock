using TouchLock.Shared;

internal static class DataMigrationTests
{
    public static void Run(string folder, byte[] password, Action<bool, string> check)
    {
        check(Paths.DataForInstallation(@"D:\Apps\TouchLock") == @"D:\ProgramData\TouchLock", "application data follows installation volume");
        check(InstallationDirectory.Normalize(@"D:\TouchLock") == @"D:\TouchLock", "installation and data resolution have no recursive dependency");
        bool rejected = false;
        try { InstallationDirectory.Normalize(@"D:\ProgramData\TouchLock\App"); } catch (IOException) { rejected = true; }
        check(rejected, "application cannot overlap its data directory");
        string source = Path.Combine(folder, "legacy-data"), destination = Path.Combine(folder, "volume-data");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        string oldPassword = Path.Combine(source, "password.json"), newPassword = Path.Combine(destination, "password.json");
        new PasswordStore(oldPassword).Set(password);
        DataMigration.CopyPassword(source, destination);
        check(new PasswordStore(newPassword).Verify(password) && File.Exists(oldPassword), "migration preserves password and retains original source");
        byte[] preserved = File.ReadAllBytes(newPassword);
        new PasswordStore(oldPassword).Set(password); // Different salt models a diverging old source.
        DataMigration.CopyPassword(source, destination);
        check(preserved.SequenceEqual(File.ReadAllBytes(newPassword)), "completed migration never overwrites destination with stale source");
        File.Delete(newPassword); rejected = false;
        try { DataMigration.CopyPassword(source, destination); } catch (IOException) { rejected = true; }
        check(rejected && !File.Exists(newPassword), "missing migrated password never silently falls back to old copy");
        File.Delete(Path.Combine(destination, "migration-v2.json"));
        new PasswordStore(newPassword).Set(password);
        preserved = File.ReadAllBytes(newPassword); rejected = false;
        try { DataMigration.CopyPassword(source, destination); } catch (IOException) { rejected = true; }
        check(rejected && preserved.SequenceEqual(File.ReadAllBytes(newPassword)), "conflicting password stores left intact");
        File.WriteAllText(oldPassword, "{}"); rejected = false;
        try { DataMigration.CopyPassword(source, destination); } catch (InvalidDataException) { rejected = true; }
        check(rejected && preserved.SequenceEqual(File.ReadAllBytes(newPassword)), "invalid legacy password does not corrupt destination");
    }
}

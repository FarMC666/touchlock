using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;
namespace TouchLock.Shared;

public static class InstallationDirectory
{
    public const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TouchLock";
    public static string Default => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TouchLock");
    public static bool Registered { get { using var key = Registry.LocalMachine.OpenSubKey(RegistryPath); return key != null; } }
    public static string Installed
    {
        get
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            if (key == null) return Default; // Also recognises installations made before custom locations.
            return Normalize(key.GetValue("InstallLocation", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? throw new IOException("安装位置记录损坏，请使用原安装程序维护。"));
        }
    }
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value) || value.StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("请选择本地磁盘上的完整安装路径。");
        string input = value.Replace('/', '\\').TrimEnd('\\');
        if (input.Split('\\').Skip(1).Any(part => part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.'))) throw new IOException("安装路径包含空白或歧义文件夹名称。");
        string path = Path.GetFullPath(input).TrimEnd('\\');
        string root = Path.GetPathRoot(path) ?? "";
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' || path.Length <= root.Length || path.Length > 180) throw new IOException("请选择磁盘中的独立文件夹，安装路径最多 180 个字符。");
        foreach (string part in path[root.Length..].Split('\\'))
        {
            string name = part.Split('.')[0];
            if (part.Length == 0 || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("PRN", StringComparison.OrdinalIgnoreCase) || name.Equals("AUX", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) || (name.Length == 4 && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && name[3] is >= '1' and <= '9')) throw new IOException("安装路径包含 Windows 不支持的文件夹名称。");
        }
        foreach (string forbidden in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows), Paths.Data })
            if (path.Equals(forbidden, StringComparison.OrdinalIgnoreCase) || path.StartsWith(forbidden + '\\', StringComparison.OrdinalIgnoreCase)) throw new IOException("不能安装到 Windows 系统目录或 TouchLock 配置目录。");
        return path;
    }
    public static string ValidateNew(string value)
    {
        string path = Normalize(value); Paths.NoLinks(path);
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (!drive.IsReady || drive.DriveType != DriveType.Fixed || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new IOException("安装位置必须在本地 NTFS 固定磁盘上。");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("安装文件夹已存在。请选择一个新文件夹，避免覆盖或卸载其他文件。");
        // A protected child alone is insufficient if a user can rename an ancestor or rewrite its ACL.
        for (var parent = Directory.GetParent(path); parent != null; parent = parent.Parent)
        {
            if (!parent.Exists) continue;
            DirectorySecurity acl = parent.GetAccessControl();
            if (!Privileged(acl.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier ?? throw UnsafeParent())) throw UnsafeParent();
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0 || Privileged((SecurityIdentifier)rule.IdentityReference)) continue;
                FileSystemRights unsafeRights = FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.DeleteSubdirectoriesAndFiles;
                if (parent.Parent != null) unsafeRights |= FileSystemRights.Delete;
                // Generic ALL/WRITE rights can appear in inherited directory ACLs.
                const uint genericAll = 0x10000000, genericWrite = 0x40000000;
                if ((rule.FileSystemRights & unsafeRights) != 0 || ((uint)rule.FileSystemRights & (genericAll | genericWrite)) != 0) throw UnsafeParent();
            }
        }
        return path;
    }
    private static bool Privileged(SecurityIdentifier sid) => sid.Value is "S-1-5-18" or "S-1-5-32-544" or "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    private static IOException UnsafeParent() => new("上级文件夹可被普通用户替换，不能用于安装系统服务。请选择本地磁盘上的新文件夹，例如 D:\\TouchLock。");
    public static void Prepare(string path)
    {
        // Protect every new ancestor before creating children; never change an existing parent's ACL.
        var missing = new Stack<DirectoryInfo>();
        for (var directory = new DirectoryInfo(path); !directory.Exists; directory = directory.Parent ?? throw new IOException()) missing.Push(directory);
        foreach (var directory in missing) WindowsSecurity.CreateInstallationDirectory(directory.FullName);
    }
}

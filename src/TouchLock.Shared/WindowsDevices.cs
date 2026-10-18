using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace TouchLock.Shared;
public sealed class WindowsDevices : IDeviceBackend
{
    public const string TouchId = DeviceSelection.TouchId;
    [StructLayout(LayoutKind.Sequential)] private struct Info { public uint Size; public Guid Class; public uint Node; public nint Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey { public Guid Format; public uint Id; }
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi", SetLastError = true)] private static extern bool SetupDiEnumDeviceInfo(nint set, uint index, ref Info info);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceRegistryPropertyW(nint set, ref Info info, uint property, out uint type, byte[] buffer, uint size, out uint needed);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDeviceInstanceIdW(nint set, ref Info info, StringBuilder id, uint size, out uint needed);
    [DllImport("setupapi")] private static extern bool SetupDiDestroyDeviceInfoList(nint set);
    [DllImport("setupapi", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetupDiGetDevicePropertyW(nint set, ref Info info, ref PropertyKey key, out uint type, byte[] value, uint size, out uint needed, uint flags);
    [DllImport("cfgmgr32")] private static extern uint CM_Get_Parent(out uint parent, uint node, uint flags);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] private static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32")] private static extern uint CM_Get_DevNode_Status(out uint status, out uint problem, uint node, uint flags);
    [DllImport("cfgmgr32")] private static extern uint CM_Disable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32")] private static extern uint CM_Enable_DevNode(uint node, uint flags);
    [DllImport("cfgmgr32")] private static extern uint CM_MapCrToWin32Err(uint code, uint fallback);
    [DllImport("cfgmgr32", CharSet = CharSet.Unicode)] private static extern uint CM_Get_Device_IDW(uint node, StringBuilder id, uint length, uint flags);
    public static IReadOnlyList<DeviceNode> Inventory()
    {
        Guid any = Guid.Empty;
        nint set = SetupDiGetClassDevsW(ref any, null, 0, 2 | 4); // PRESENT | ALLCLASSES; Kuma need not be a standard HID collection.
        if (set == -1) throw new Win32Exception();
        try
        {
            List<DeviceNode> result = [];
            for (uint index = 0; ; index++)
            {
                var info = new Info { Size = (uint)Marshal.SizeOf<Info>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info)) { if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(); break; }
                List<string> errors = [];
                string[] Property(uint property)
                {
                    byte[] buffer = new byte[8192];
                    if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, (uint)buffer.Length, out uint bytes))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 122 && bytes <= 65536)
                        {
                            buffer = new byte[bytes];
                            if (SetupDiGetDeviceRegistryPropertyW(set, ref info, property, out _, buffer, bytes, out bytes))
                                return Encoding.Unicode.GetString(buffer, 0, checked((int)bytes)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                            error = Marshal.GetLastWin32Error();
                        }
                        errors.Add($"SPDRP_{property}: Win32 {error}"); return [];
                    }
                    return Encoding.Unicode.GetString(buffer, 0, checked((int)bytes)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                }
                string name = Property(12).FirstOrDefault() ?? Property(0).FirstOrDefault() ?? "";
                string deviceClass = Property(7).FirstOrDefault() ?? info.Class.ToString();
                var usages = Property(2).Concat(Property(1)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out _)) errors.Add($"InstanceId: Win32 {Marshal.GetLastWin32Error()}");
                uint code = CM_Get_DevNode_Status(out uint status, out uint problem, info.Node, 0);
                CM_Get_Parent(out uint parent, info.Node, 0);
                var key = new PropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                byte[] containerBytes = new byte[16];
                Guid container = SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, containerBytes, 16, out _, 0) && type == 0x0d ? new Guid(containerBytes) : Guid.Empty;
                bool bluetooth = false;
                uint ancestor = info.Node;
                for (int depth = 0; depth < 16; depth++)
                {
                    var ancestorId = new StringBuilder(1024);
                    if (CM_Get_Device_IDW(ancestor, ancestorId, (uint)ancestorId.Capacity, 0) == 0 && ancestorId.ToString().StartsWith("BTH", StringComparison.OrdinalIgnoreCase)) bluetooth = true;
                    if (CM_Get_Parent(out ancestor, ancestor, 0) != 0) break;
                }
                result.Add(new(id.ToString(), name, deviceClass, usages, status, problem, parent, container, code, bluetooth, errors.ToArray()));
            }
            return result;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    public IReadOnlyList<TouchDevice> Enumerate()
    {
        var nodes = Inventory();
        return DeviceSelection.Select(nodes);
    }
    public TouchDevice? Find(string id)
    {
        var nodes = Inventory();
        var node = nodes.SingleOrDefault(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (node == null) return null;
        // Restore generic owned devices even when a newly connected Kuma group takes precedence.
        return DeviceSelection.Select(nodes.Where(n => DeviceSelection.IsKuma(n) == DeviceSelection.IsKuma(node)).ToArray()).SingleOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
    private static uint Locate(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 1024 || !id.Contains('\\') || id.Any(char.IsControl)) throw new InvalidDataException();
        Check(CM_Locate_DevNodeW(out uint node, id, 0)); return node;
    }
    private static void Check(uint code)
    {
        if (code == 0) return;
        uint win32 = CM_MapCrToWin32Err(code, 31);
        throw new IOException($"Windows 设备操作失败：CONFIGRET 0x{code:X}，Win32 {win32} ({new Win32Exception((int)win32).Message})；需要重启={code == 0x22}，权限不足={win32 == 5}。");
    }
    public bool IsDisabled(string id)
    {
        Check(CM_Get_DevNode_Status(out uint status, out uint problem, Locate(id), 0));
        if ((status & 0x400) != 0 && problem == 22) return true;
        if ((status & 0x400) == 0 && problem == 0 && (status & 8) != 0) return false;
        throw new IOException($"设备状态无法确认为启用或禁用：status=0x{status:X}, problem={problem}。");
    }
    public void Disable(string id)
    {
        var device = Enumerate().SingleOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (device is not { Safe: true, Disableable: true, Disabled: false }) throw new IOException("设备身份或状态变化，拒绝禁用。");
        Check(CM_Disable_DevNode(Locate(id), 0x4)); // UI_NOT_OK only; never ABSOLUTE/HARDWARE/PERSIST.
        long deadline = Environment.TickCount64 + 5000;
        while (!IsDisabled(id)) { if (Environment.TickCount64 >= deadline) throw new IOException("触摸屏未及时完成禁用。"); Thread.Sleep(50); }
    }
    public void Enable(string id)
    {
        var d = Find(id);
        if (d == null) throw new IOException("等待触摸设备重新连接。");
        if (!d.Safe) throw new IOException("恢复设备身份或状态无法安全确认：" + d.Reason);
        if (d.Disabled) Check(CM_Enable_DevNode(Locate(id), 0));
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            Check(CM_Get_DevNode_Status(out uint status, out uint problem, Locate(id), 0));
            if ((status & 0x400) == 0 && (status & 8) != 0) return;
            if (Environment.TickCount64 >= deadline) throw new IOException($"触摸屏尚未恢复 (problem {problem})。");
            Thread.Sleep(50);
        }
    }
}

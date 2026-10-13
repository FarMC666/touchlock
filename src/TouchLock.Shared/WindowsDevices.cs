using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace TouchLock.Shared;
public sealed class WindowsDevices : IDeviceBackend
{
    public const string TouchId = "HID_DEVICE_UP:000D_U:0004";
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
    private sealed record Node(string Id, uint Parent, Guid Container, string[] Usages, uint Status, uint Problem);
    private static List<Node> Nodes()
    {
        Guid hid = new("745a17a0-74d3-11d0-b6fe-00a0c90f57da");
        nint set = SetupDiGetClassDevsW(ref hid, null, 0, 2);
        if (set == -1) throw new Win32Exception();
        try
        {
            List<Node> result = [];
            for (uint index = 0; ; index++)
            {
                var info = new Info { Size = (uint)Marshal.SizeOf<Info>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref info)) { if (Marshal.GetLastWin32Error() != 259) throw new Win32Exception(); break; }
                byte[] buffer = new byte[8192];
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref info, 2, out _, buffer, (uint)buffer.Length, out uint bytes)) continue;
                var usages = Encoding.Unicode.GetString(buffer, 0, checked((int)bytes)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                var id = new StringBuilder(1024);
                if (!SetupDiGetDeviceInstanceIdW(set, ref info, id, (uint)id.Capacity, out _) || CM_Get_DevNode_Status(out uint status, out uint problem, info.Node, 0) != 0) throw new Win32Exception();
                CM_Get_Parent(out uint parent, info.Node, 0);
                var key = new PropertyKey { Format = new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), Id = 2 };
                byte[] containerBytes = new byte[16];
                Guid container = SetupDiGetDevicePropertyW(set, ref info, ref key, out uint type, containerBytes, 16, out _, 0) && type == 0x0d ? new Guid(containerBytes) : Guid.Empty;
                result.Add(new(id.ToString(), parent, container, usages, status, problem));
            }
            return result;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
    public IReadOnlyList<TouchDevice> Enumerate()
    {
        var nodes = Nodes();
        return nodes.Where(n => n.Usages.Contains(TouchId, StringComparer.OrdinalIgnoreCase)).Select(n =>
        {
            bool mixed = n.Usages.Any(u => u.StartsWith("HID_DEVICE_UP:", StringComparison.OrdinalIgnoreCase) && !u.Equals(TouchId, StringComparison.OrdinalIgnoreCase));
            bool companion = nodes.Any(o => (n.Parent != 0 && o.Parent == n.Parent || n.Container != Guid.Empty && o.Container == n.Container) && o.Usages.Contains("HID_DEVICE_UP:0001_U:0002", StringComparer.OrdinalIgnoreCase));
            bool disabled = (n.Status & 0x400) != 0 && n.Problem == 22;
            bool healthy = disabled || ((n.Status & 0x400) == 0 && (n.Status & 8) != 0);
            return new TouchDevice(n.Id, disabled, (n.Status & 0x2000) != 0, !mixed && !companion && healthy,
                mixed || companion ? "触摸设备包含其他输入通道，无法保证只禁用触摸。" : healthy ? "" : "触摸设备状态异常。");
        }).ToArray();
    }
    private static uint Locate(string id)
    {
        if (!id.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        Check(CM_Locate_DevNodeW(out uint node, id, 0)); return node;
    }
    private static void Check(uint code) { if (code != 0) throw new IOException($"Windows 设备操作失败 (CONFIGRET 0x{code:X})。" + (code == 0x22 ? "该设备需要重启，不支持临时锁定。" : "")); }
    public bool IsDisabled(string id) { uint node = Locate(id); Check(CM_Get_DevNode_Status(out uint status, out uint problem, node, 0)); return (status & 0x400) != 0 && problem == 22; }
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
        var d = Enumerate().SingleOrDefault(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (d == null) throw new IOException("等待触摸设备重新连接。");
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

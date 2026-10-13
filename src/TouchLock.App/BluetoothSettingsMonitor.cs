using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TouchLock.Shared;
namespace TouchLock.App;

internal sealed class BluetoothSettingsMonitor : IDisposable
{
    private readonly object gate = new();
    private readonly Thread worker;
    private bool enabled, stopping;
    private string error = "";
    public string Error { get { lock (gate) return error; } }
    public bool Enabled { set { lock (gate) enabled = value; } }
    public BluetoothSettingsMonitor()
    {
        worker = new Thread(Run) { IsBackground = true, Name = "TouchLock Bluetooth settings" };
        worker.SetApartmentState(ApartmentState.MTA); worker.Start();
    }
    private void Run()
    {
        while (true)
        {
            lock (gate) { if (stopping) return; }
            bool check; lock (gate) check = enabled;
            if (check)
            {
                try
                {
                    foreach (nint window in SettingsWindows())
                    {
                        if (!IsBluetoothPage(window)) continue;
                        // Disabling the option and posting a close are serialized. Never kill Settings.
                        lock (gate)
                        {
                            if (stopping) return;
                            if (enabled && !PostMessageW(window, 0x0010, 0, 0)) throw new System.ComponentModel.Win32Exception();
                        }
                    }
                    lock (gate) error = "";
                }
                catch (ElementNotAvailableException) { /* Settings closed or navigated during inspection; retry. */ }
                catch { lock (gate) error = "无法检测或关闭 Windows 蓝牙设置。请重新打开 TouchLock 后重试。"; }
            }
            Thread.Sleep(250);
        }
    }
    internal static bool IsBluetoothPage(nint window)
    {
        var root = AutomationElement.FromHandle(window);
        var cache = new CacheRequest { AutomationElementMode = AutomationElementMode.None };
        cache.Add(AutomationElement.AutomationIdProperty);
        using (cache.Activate())
        {
            var controls = root.FindAll(TreeScope.Descendants, new NotCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "")));
            bool discovery = false, deviceList = false;
            foreach (AutomationElement control in controls)
            {
                string id = control.Cached.AutomationId;
                if (IsBluetoothControl(id)) return true;
                discovery |= id == "SystemSettings_Device_Discovery_Button";
                deviceList |= id is "SystemSettings_Devices_InputDeviceList_SettingsListItemsRepeater" or "SystemSettings_Devices_AudioDeviceList_SettingsListItemsRepeater" or "SystemSettings_Devices_OtherDeviceList_SettingsListItemsRepeater";
            }
            // With no Bluetooth adapter, the device page may omit the radio and legacy link.
            return discovery && deviceList;
        }
    }
    internal static bool IsBluetoothControl(string id)
    {
        // Page content IDs, shared by Windows 10/11; sidebar labels and search results are ignored.
        foreach (string prefix in new[] { "SystemSettings_Device_BluetoothRadioToggle", "SystemSettings_Device_BluetoothLegacyLink", "SystemSettings_Device_BluetoothQuickPairToggle" })
            if (id == prefix || id.StartsWith(prefix + "_", StringComparison.Ordinal)) return true;
        return false;
    }
    internal static IReadOnlyList<nint> SettingsWindows()
    {
        var processes = new HashSet<uint>();
        int session = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("SystemSettings"))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    using var handle = WindowsSecurity.OpenProcess(0x1000, false, (uint)process.Id);
                    if (!handle.IsInvalid && WindowsSecurity.Image(handle).Equals(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "ImmersiveControlPanel", "SystemSettings.exe"), StringComparison.OrdinalIgnoreCase)) processes.Add((uint)process.Id);
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        var windows = new List<nint>();
        if (processes.Count == 0) return windows;
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window)) return true;
            GetWindowThreadProcessId(window, out uint pid); bool settings = processes.Contains(pid);
            if (!settings)
                EnumChildWindows(window, (child, _) => { GetWindowThreadProcessId(child, out uint childPid); if (processes.Contains(childPid)) settings = true; return !settings; }, 0);
            // Windows 10 and some Windows 11 builds host Settings inside ApplicationFrameHost.
            if (settings) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }
    public void Dispose()
    {
        lock (gate) { stopping = true; enabled = false; }
        worker.Join(500); // An unresponsive accessibility provider must not hang tray exit.
    }
    private delegate bool EnumerateWindow(nint window, nint data);
    [DllImport("user32")] private static extern bool EnumWindows(EnumerateWindow callback, nint data);
    [DllImport("user32")] private static extern bool EnumChildWindows(nint window, EnumerateWindow callback, nint data);
    [DllImport("user32")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32", SetLastError = true)] private static extern bool PostMessageW(nint window, uint message, nint wParam, nint lParam);
}

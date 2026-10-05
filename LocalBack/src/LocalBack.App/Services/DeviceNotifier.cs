using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace LocalBack.App.Services;

/// <summary>
/// Hidden top-level window that receives WM_DEVICECHANGE broadcasts, so a backup starts the moment
/// the drive is plugged in. No polling.
/// </summary>
public sealed class DeviceNotifier : IDisposable
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
    private const int DBT_DEVTYP_VOLUME = 0x0002;

    private readonly HwndSource _source;

    /// <summary>Raised with drive roots such as "E:\".</summary>
    public event Action<IReadOnlyList<string>>? Arrived;
    public event Action<IReadOnlyList<string>>? Removed;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastVolume
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public int UnitMask;
        public short Flags;
    }

    public DeviceNotifier()
    {
        // A real (hidden) top-level window: message-only windows do not get device broadcasts.
        var p = new HwndSourceParameters("LocalBackDeviceWatcher") { Width = 0, Height = 0, WindowStyle = 0, PositionX = -32000, PositionY = -32000 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_DEVICECHANGE || lParam == IntPtr.Zero) return IntPtr.Zero;
        int evt = wParam.ToInt32();
        if (evt != DBT_DEVICEARRIVAL && evt != DBT_DEVICEREMOVECOMPLETE) return IntPtr.Zero;
        var hdr = Marshal.PtrToStructure<DevBroadcastVolume>(lParam);
        if (hdr.DeviceType != DBT_DEVTYP_VOLUME) return IntPtr.Zero;

        var roots = new List<string>();
        for (int i = 0; i < 26; i++)
            if ((hdr.UnitMask & (1 << i)) != 0) roots.Add($"{(char)('A' + i)}:\\");
        if (evt == DBT_DEVICEARRIVAL) Arrived?.Invoke(roots);
        else Removed?.Invoke(roots);
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

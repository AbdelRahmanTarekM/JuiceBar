using System.Runtime.InteropServices;
using System.Text;

namespace Bfm.Interop;

/// <summary>
/// Samples which mouse is moving, at most once per interval: registers for raw mouse input, takes the first
/// WM_INPUT, then unregisters until the next sample. Costs one message per interval instead of one per movement.
/// </summary>
internal sealed class RawMouse : IDisposable
{
    public const int WmInput = 0x00FF;
    const uint RidevInputSink = 0x100, RidevRemove = 0x1, RidHeader = 0x10000005, RidiDeviceName = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTHEADER { public uint Type, Size; public IntPtr Device, WParam; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr rawInput, uint command, out RAWINPUTHEADER data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfoW(IntPtr device, uint command, StringBuilder? data, ref uint size);

    readonly IntPtr _hwnd;
    readonly System.Windows.Forms.Timer _timer;
    readonly Dictionary<IntPtr, string> _names = new();
    bool _registered;

    /// <summary>Raised on the UI thread with the device path of the mouse that moved.</summary>
    public event Action<string>? Moved;

    public RawMouse(IntPtr hwnd, int intervalMs)
    {
        _hwnd = hwnd;
        _timer = new System.Windows.Forms.Timer { Interval = intervalMs };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            Register(true);
        };
        Register(true);
    }

    void Register(bool on)
    {
        if (_registered == on) return;
        var dev = new[] { new RAWINPUTDEVICE { UsagePage = 0x01, Usage = 0x02, Flags = on ? RidevInputSink : RidevRemove, Target = on ? _hwnd : IntPtr.Zero } };
        _registered = RegisterRawInputDevices(dev, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()) ? on : _registered;
    }

    /// <summary>Call from WndProc for WM_INPUT.</summary>
    public void Handle(IntPtr lParam)
    {
        if (!_registered) return;
        uint size = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(lParam, RidHeader, out var header, ref size, size) == uint.MaxValue) return;
        Register(false);
        _timer.Start();
        if (header.Device == IntPtr.Zero) return;
        if (!_names.TryGetValue(header.Device, out var name))
        {
            uint len = 0;
            GetRawInputDeviceInfoW(header.Device, RidiDeviceName, null, ref len);
            var sb = new StringBuilder((int)len + 1);
            GetRawInputDeviceInfoW(header.Device, RidiDeviceName, sb, ref len);
            _names[header.Device] = name = sb.ToString();
        }
        Moved?.Invoke(name);
    }

    public void Dispose()
    {
        _timer.Dispose();
        Register(false);
    }
}

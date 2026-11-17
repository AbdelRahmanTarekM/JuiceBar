using System.Runtime.InteropServices;

namespace Bfm.Interop;

internal sealed record ClassicDevice(string Address, string Name, uint ClassOfDevice, bool Connected);

/// <summary>Win32 Bluetooth API (classic BR/EDR devices only).</summary>
internal static class BluetoothApi
{
    [StructLayout(LayoutKind.Sequential)]
    struct SEARCH_PARAMS
    {
        public int dwSize;
        public bool fReturnAuthenticated, fReturnRemembered, fReturnUnknown, fReturnConnected, fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME { public short a, b, c, d, e, f, g, h; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVICE_INFO
    {
        public int dwSize;
        public ulong Address;
        public uint ulClassofDevice;
        public bool fConnected, fRemembered, fAuthenticated;
        public SYSTEMTIME stLastSeen, stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)] public string szName;
    }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstDevice(ref SEARCH_PARAMS p, ref DEVICE_INFO info);

    [DllImport("bthprops.cpl")]
    static extern bool BluetoothFindNextDevice(IntPtr h, ref DEVICE_INFO info);

    [DllImport("bthprops.cpl")]
    static extern bool BluetoothFindDeviceClose(IntPtr h);

    public static Dictionary<string, ClassicDevice> GetPairedDevices()
    {
        var map = new Dictionary<string, ClassicDevice>(StringComparer.OrdinalIgnoreCase);
        var p = new SEARCH_PARAMS
        {
            dwSize = Marshal.SizeOf<SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered = true,
            fReturnConnected = true,
        };
        var info = new DEVICE_INFO { dwSize = Marshal.SizeOf<DEVICE_INFO>() };
        var h = BluetoothFindFirstDevice(ref p, ref info);
        if (h == IntPtr.Zero) return map;
        try
        {
            do
            {
                var addr = info.Address.ToString("X12");
                map[addr] = new ClassicDevice(addr, info.szName, info.ulClassofDevice, info.fConnected);
                info.dwSize = Marshal.SizeOf<DEVICE_INFO>();
            } while (BluetoothFindNextDevice(h, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(h);
        }
        return map;
    }
}

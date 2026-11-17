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

    [StructLayout(LayoutKind.Sequential)]
    struct FIND_RADIO_PARAMS { public int dwSize; }

    [DllImport("bthprops.cpl", SetLastError = true)]
    static extern IntPtr BluetoothFindFirstRadio(ref FIND_RADIO_PARAMS p, out IntPtr radio);

    [DllImport("bthprops.cpl")]
    static extern bool BluetoothFindNextRadio(IntPtr find, out IntPtr radio);

    [DllImport("bthprops.cpl")]
    static extern bool BluetoothFindRadioClose(IntPtr find);

    // IOCTL_BTH_DISCONNECT_DEVICE: drops the baseband link to a classic device; pairing is kept.
    const uint IoctlBthDisconnectDevice = 0x0041000C;

    public static unsafe bool Disconnect(string address)
    {
        ulong addr = Convert.ToUInt64(address, 16);
        var p = new FIND_RADIO_PARAMS { dwSize = Marshal.SizeOf<FIND_RADIO_PARAMS>() };
        var find = BluetoothFindFirstRadio(ref p, out var radio);
        if (find == IntPtr.Zero) return false;
        try
        {
            do
            {
                using var h = new Microsoft.Win32.SafeHandles.SafeFileHandle(radio, ownsHandle: true);
                if (Kernel32.DeviceIoControl(h, IoctlBthDisconnectDevice, &addr, sizeof(ulong), null, 0, out _, null))
                    return true;
                Core.Log.Write($"bt disconnect {address}: error {Marshal.GetLastWin32Error()}");
            } while (BluetoothFindNextRadio(find, out radio));
        }
        finally
        {
            BluetoothFindRadioClose(find);
        }
        return false;
    }

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

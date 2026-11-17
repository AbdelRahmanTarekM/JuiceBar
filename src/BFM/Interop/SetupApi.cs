using System.Runtime.InteropServices;

namespace Bfm.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct DEVPROPKEY
{
    public Guid fmtid;
    public uint pid;

    public DEVPROPKEY(string guid, uint id)
    {
        fmtid = new Guid(guid);
        pid = id;
    }
}

internal sealed record PnpNode(string InstanceId, string? FriendlyName, byte? Battery, bool? IsConnected);

internal static class SetupApi
{
    const int DIGCF_PRESENT = 0x2, DIGCF_ALLCLASSES = 0x4, DIGCF_DEVICEINTERFACE = 0x10;

    public static readonly DEVPROPKEY FriendlyName = new("a45c254e-df1c-4efd-8020-67d146a850e0", 14);
    public static readonly DEVPROPKEY BluetoothBattery = new("104EA319-6EE2-4701-BD47-8DDBF425BBE5", 2);
    public static readonly DEVPROPKEY IsConnected = new("83DA6326-97A6-4088-9453-A1923F573B29", 15);

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, EntryPoint = "SetupDiGetClassDevsW")]
    static extern IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, string? enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, int index, ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, int index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data, char[] buf, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDevicePropertyW(IntPtr set, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
        out uint type, byte[]? buf, int size, out int required, int flags);

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    static readonly IntPtr Invalid = new(-1);

    public static List<string> GetInterfacePaths(Guid interfaceClass)
    {
        var paths = new List<string>();
        var set = SetupDiGetClassDevsW(ref interfaceClass, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == Invalid) return paths;
        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceClass, i, ref data); i++)
            {
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out int required, IntPtr.Zero);
                if (required <= 0) continue;
                var buf = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, buf, required, out _, IntPtr.Zero))
                        paths.Add(Marshal.PtrToStringUni(buf + 4)!);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    /// <summary>Lists present device nodes under a bus enumerator (e.g. BTHENUM, BTHLE).</summary>
    public static List<PnpNode> GetNodes(string enumerator, Func<string, bool> filter)
    {
        var list = new List<PnpNode>();
        var set = SetupDiGetClassDevsAll(IntPtr.Zero, enumerator, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        if (set == Invalid) return list;
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var idBuf = new char[512];
            for (int i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                if (!SetupDiGetDeviceInstanceIdW(set, ref data, idBuf, idBuf.Length, out int len)) continue;
                var id = new string(idBuf, 0, Math.Max(0, len - 1));
                if (!filter(id)) continue;

                string? name = null;
                byte? battery = null;
                bool? connected = null;
                if (GetProperty(set, ref data, FriendlyName) is { } nameBytes)
                    name = System.Text.Encoding.Unicode.GetString(nameBytes).TrimEnd('\0');
                if (GetProperty(set, ref data, BluetoothBattery) is { Length: >= 1 } b)
                    battery = b[0];
                if (GetProperty(set, ref data, IsConnected) is { Length: >= 1 } c)
                    connected = c[0] != 0;
                list.Add(new PnpNode(id, name, battery, connected));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    static byte[]? GetProperty(IntPtr set, ref SP_DEVINFO_DATA data, DEVPROPKEY key)
    {
        SetupDiGetDevicePropertyW(set, ref data, ref key, out _, null, 0, out int required, 0);
        if (required <= 0) return null;
        var buf = new byte[required];
        return SetupDiGetDevicePropertyW(set, ref data, ref key, out _, buf, buf.Length, out _, 0) ? buf : null;
    }
}

using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Bfm.Interop;

internal sealed record HidInterface(
    string Path, ushort Vid, ushort Pid, int Interface,
    ushort UsagePage, ushort Usage, int InputLength, int OutputLength, int FeatureLength,
    bool IsBluetooth = false);

internal static partial class Hid
{
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr data, out HIDP_CAPS caps);
    [DllImport("hid.dll", SetLastError = true)] internal static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(SafeFileHandle h, char[] buf, int len);

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
            NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
            NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [GeneratedRegex(@"vid_([0-9a-f]{4})&pid_([0-9a-f]{4})(?:&mi_([0-9a-f]{2}))?", RegexOptions.IgnoreCase)]
    private static partial Regex UsbIdRegex();

    // Classic Bluetooth HID: hid#{00001124-...}_vid&0002054c_pid&0ce6#...
    [GeneratedRegex(@"\{00001124-0000-1000-8000-00805f9b34fb\}_vid&[0-9a-f]{4}([0-9a-f]{4})_pid&([0-9a-f]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothIdRegex();

    /// <summary>Lists HID collections with the given vendor id (and product id, if given), over USB and,
    /// when <paramref name="includeBluetooth"/> is set, classic Bluetooth.</summary>
    public static List<HidInterface> Enumerate(ushort vid, ushort? pid = null, bool includeBluetooth = false)
    {
        var result = new List<HidInterface>();
        HidD_GetHidGuid(out var guid);
        foreach (var path in SetupApi.GetInterfacePaths(guid))
        {
            var m = UsbIdRegex().Match(path);
            bool bt = false;
            if (!m.Success && includeBluetooth)
            {
                m = BluetoothIdRegex().Match(path);
                bt = m.Success;
            }
            if (!m.Success) continue;
            var v = Convert.ToUInt16(m.Groups[1].Value, 16);
            var p = Convert.ToUInt16(m.Groups[2].Value, 16);
            if (v != vid || (pid is ushort want && p != want)) continue;
            int mi = !bt && m.Groups[3].Success ? Convert.ToInt32(m.Groups[3].Value, 16) : -1;

            using var h = Kernel32.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;
            if (!HidD_GetPreparsedData(h, out var pre)) continue;
            try
            {
                HidP_GetCaps(pre, out var caps);
                result.Add(new HidInterface(path, v, p, mi, caps.UsagePage, caps.Usage,
                    caps.InputReportByteLength, caps.OutputReportByteLength, caps.FeatureReportByteLength, bt));
            }
            finally
            {
                HidD_FreePreparsedData(pre);
            }
        }
        return result;
    }

    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort length, IntPtr data);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort length, IntPtr data);

    /// <summary>Report ids declared for a report type (0 input, 1 output, 2 feature). Diagnostic use.</summary>
    public static SortedSet<byte> ReportIds(string path, int type)
    {
        const int CapsSize = 72;
        var ids = new SortedSet<byte>();
        using var h = Kernel32.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid || !HidD_GetPreparsedData(h, out var pre)) return ids;
        try
        {
            foreach (var getter in new Func<int, byte[], ushort, (int, ushort)>[]
                     {
                         (t, b, n) => { var l = n; var s = HidP_GetValueCaps(t, b, ref l, pre); return (s, l); },
                         (t, b, n) => { var l = n; var s = HidP_GetButtonCaps(t, b, ref l, pre); return (s, l); },
                     })
            {
                var buf = new byte[CapsSize * 64];
                var (status, count) = getter(type, buf, 64);
                if (status != 0x00110000) continue;
                for (int i = 0; i < count; i++) ids.Add(buf[i * CapsSize + 2]);
            }
        }
        finally
        {
            HidD_FreePreparsedData(pre);
        }
        return ids;
    }

    [DllImport("hid.dll")] internal static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_Interface_PropertyW(string iface, ref DEVPROPKEY key, out uint type, byte[]? buf, ref int size, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out int devInst, string id, int flags);
    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out int parent, int devInst, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(int devInst, char[] buf, int len, int flags);

    [GeneratedRegex(@"&([0-9A-F]{12})_C[0-9A-F]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex BluetoothAddressRegex();

    /// <summary>Bluetooth address of the device a Bluetooth HID collection belongs to, from its ancestor device nodes.</summary>
    public static string? GetBluetoothAddress(string interfacePath)
    {
        var key = new DEVPROPKEY("78c34fc8-104a-4aca-9ea4-524d52996e57", 256); // DEVPKEY_Device_InstanceId
        int size = 0;
        CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, null, ref size, 0);
        if (size <= 0) return null;
        var buf = new byte[size];
        if (CM_Get_Device_Interface_PropertyW(interfacePath, ref key, out _, buf, ref size, 0) != 0) return null;
        var id = System.Text.Encoding.Unicode.GetString(buf).TrimEnd('\0');

        if (CM_Locate_DevNodeW(out int node, id, 0) != 0) return null;
        var idBuf = new char[512];
        for (int depth = 0; depth < 4; depth++)
        {
            if (CM_Get_Parent(out node, node, 0) != 0) return null;
            if (CM_Get_Device_IDW(node, idBuf, idBuf.Length, 0) != 0) return null;
            var m = BluetoothAddressRegex().Match(new string(idBuf).TrimEnd('\0'));
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        return null;
    }

    public static string? GetProductString(string path)
    {
        using var h = Kernel32.CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var buf = new char[128];
        if (!HidD_GetProductString(h, buf, buf.Length * 2)) return null;
        var s = new string(buf).TrimEnd('\0').Trim();
        return s.Length == 0 ? null : s;
    }
}

/// <summary>An open read/write handle to one HID collection.</summary>
internal sealed class HidChannel : IDisposable
{
    readonly SafeFileHandle _handle;
    readonly FileStream _stream;
    public HidInterface Info { get; }

    HidChannel(HidInterface info, SafeFileHandle handle)
    {
        Info = info;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, 1, isAsync: true);
    }

    public static HidChannel? Open(HidInterface info)
    {
        const uint GenericReadWrite = 0xC0000000, FileFlagOverlapped = 0x40000000;
        var h = Kernel32.CreateFileW(info.Path, GenericReadWrite, 3, IntPtr.Zero, 3, FileFlagOverlapped, IntPtr.Zero);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }
        return new HidChannel(info, h);
    }

    /// <summary>Sends an output report, padded to the collection's report length.</summary>
    public bool Write(ReadOnlySpan<byte> report)
    {
        var buf = new byte[Info.OutputLength];
        report[..Math.Min(report.Length, buf.Length)].CopyTo(buf);
        try
        {
            _stream.Write(buf, 0, buf.Length);
            return true;
        }
        catch (IOException)
        {
            return Hid.HidD_SetOutputReport(_handle, buf, buf.Length);
        }
    }

    public Task<byte[]?> ReadAsync(CancellationToken token) => ReadCore(token);

    public bool GetFeature(byte[] buffer) => Hid.HidD_GetFeature(_handle, buffer, buffer.Length);

    async Task<byte[]?> ReadCore(CancellationToken token)
    {
        var buf = new byte[Math.Max(1, Info.InputLength)];
        try
        {
            int n = await _stream.ReadAsync(buf, token).ConfigureAwait(false);
            return n > 0 ? buf : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Reads input reports until <paramref name="match"/> accepts one or the timeout expires.</summary>
    public byte[]? ReadUntil(Func<byte[], bool> match, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        while (!cts.IsCancellationRequested)
        {
            var r = ReadCore(cts.Token).GetAwaiter().GetResult();
            if (r is null) return null;
            if (match(r)) return r;
        }
        return null;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}

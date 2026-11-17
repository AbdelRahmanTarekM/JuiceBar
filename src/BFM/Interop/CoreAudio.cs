using System.Runtime.InteropServices;

namespace Bfm.Interop;

internal sealed record AudioEndpoint(string Id, string Name, bool IsRender);

/// <summary>Minimal Core Audio (MMDevice) interop: list endpoints, volume, mute, default device.</summary>
internal static class CoreAudio
{
    const uint DeviceStateActive = 1;
    const uint ClsctxAll = 0x17;

    static readonly Guid IidAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    static PROPERTYKEY _friendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    public static List<AudioEndpoint> List(bool render)
    {
        var list = new List<AudioEndpoint>();
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            Marshal.ThrowExceptionForHR(en.EnumAudioEndpoints(render ? 0 : 1, DeviceStateActive, out var col));
            col.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (col.Item(i, out var dev) != 0) continue;
                dev.GetId(out var id);
                list.Add(new AudioEndpoint(id, GetName(dev) ?? id, render));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(col);
            Marshal.ReleaseComObject(en);
        }
        catch (Exception ex)
        {
            Core.Log.Write("audio list failed: " + ex.Message);
        }
        return list;
    }

    /// <summary>Finds the active endpoint whose name contains the device name, preferring stereo over hands-free.</summary>
    public static AudioEndpoint? FindForDevice(string deviceName, bool render) =>
        List(render)
            .Where(e => e.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .FirstOrDefault();

    public static string? GetDefaultId(bool render)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDefaultAudioEndpoint(render ? 0 : 1, 0, out var dev) != 0) return null;
            dev.GetId(out var id);
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
            return id;
        }
        catch
        {
            return null;
        }
    }

    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++)
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
            Marshal.ReleaseComObject(policy);
            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Write("set default failed: " + ex.Message);
            return false;
        }
    }

    public static float? GetVolume(string id) => WithVolume(id, v => { v.GetMasterVolumeLevelScalar(out float f); return (float?)f; });

    public static void SetVolume(string id, float level) =>
        WithVolume(id, v => { var ctx = Guid.Empty; v.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx); return (float?)null; });

    public static bool? GetMute(string id) => WithVolume(id, v => { v.GetMute(out bool m); return (bool?)m; });

    public static void SetMute(string id, bool mute) =>
        WithVolume(id, v => { var ctx = Guid.Empty; v.SetMute(mute, ref ctx); return (bool?)null; });

    static T? WithVolume<T>(string id, Func<IAudioEndpointVolume, T?> action)
    {
        try
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (en.GetDevice(id, out var dev) != 0) return default;
            var iid = IidAudioEndpointVolume;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var obj));
            var vol = (IAudioEndpointVolume)obj;
            var result = action(vol);
            Marshal.ReleaseComObject(vol);
            Marshal.ReleaseComObject(dev);
            Marshal.ReleaseComObject(en);
            return result;
        }
        catch (Exception ex)
        {
            Core.Log.Write("endpoint volume failed: " + ex.Message);
            return default;
        }
    }

    static string? GetName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var store) != 0) return null;
        try
        {
            if (store.GetValue(ref _friendlyName, out var pv) != 0) return null;
            try
            {
                return pv.vt == 31 ? Marshal.PtrToStringUni(pv.pointer) : null;
            }
            finally
            {
                PropVariantClear(ref pv);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)]
    struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointer;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    class MMDeviceEnumeratorCom { }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class PolicyConfigCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid ctx);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid ctx);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    // Undocumented but stable since Windows 7; used by every "switch audio device" tool.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }
}

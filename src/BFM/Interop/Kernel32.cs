using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bfm.Interop;

internal static class Kernel32
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, int inSize,
        void* outBuf, int outSize, out int returned, NativeOverlapped* overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern unsafe bool GetOverlappedResult(SafeFileHandle h, NativeOverlapped* overlapped,
        out int transferred, bool wait);

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [DllImport("psapi.dll")]
    static extern bool EmptyWorkingSet(IntPtr process);

    /// <summary>Releases pages touched during startup or UI work so the idle footprint stays small.</summary>
    public static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
    }
}

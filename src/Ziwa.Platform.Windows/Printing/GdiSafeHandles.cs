using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Ziwa.Platform.Windows.Printing;

[SupportedOSPlatform("windows")]
public sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePrinterHandle()
        : base(ownsHandle: true)
    {
    }

    public SafePrinterHandle(IntPtr preexistingHandle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => ClosePrinter(handle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr hPrinter);
}

[SupportedOSPlatform("windows")]
public sealed class SafeGdiDcHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeGdiDcHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeGdiDcHandle(IntPtr preexistingHandle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    protected override bool ReleaseHandle() => DeleteDC(handle);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);
}

[SupportedOSPlatform("windows")]
public sealed class SafeGlobalMemoryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private const uint GmemFixed = 0x0000;
    private const uint GmemZeroInit = 0x0040;

    public SafeGlobalMemoryHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeGlobalMemoryHandle(IntPtr preexistingHandle, bool ownsHandle = true)
        : base(ownsHandle)
    {
        SetHandle(preexistingHandle);
    }

    public static SafeGlobalMemoryHandle Allocate(int byteCount)
    {
        if (byteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount), byteCount, "Allocation size must be positive.");
        }

        IntPtr ptr = GlobalAlloc(GmemFixed | GmemZeroInit, (UIntPtr)byteCount);
        if (ptr == IntPtr.Zero)
        {
            throw new OutOfMemoryException($"GlobalAlloc failed to allocate {byteCount} bytes (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return new SafeGlobalMemoryHandle(ptr, ownsHandle: true);
    }

    protected override bool ReleaseHandle() => GlobalFree(handle) == IntPtr.Zero;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);
}

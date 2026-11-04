using System.Runtime.InteropServices;

namespace TerraBase.Features.Clock;

internal static partial class KernelClock
{
    internal static double ErrorMilliseconds()
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
        {
            return double.PositiveInfinity;
        }

        var state = new Timex();
        var result = Read(ref state);
        if (result is < 0 or 5 || (state.Status & (0x40 | 0x1000)) != 0 || state.MaxError < 0)
        {
            return double.PositiveInfinity;
        }

        var offset = state.Offset / ((state.Status & 0x2000) != 0 ? 1_000_000.0 : 1_000.0);
        return Math.Abs(offset);
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    private struct Timex
    {
        [FieldOffset(8)] public long Offset;
        [FieldOffset(24)] public long MaxError;
        [FieldOffset(40)] public int Status;
    }

    [LibraryImport("libc", EntryPoint = "adjtimex")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Read(ref Timex state);
}

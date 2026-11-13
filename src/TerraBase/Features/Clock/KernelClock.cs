using System.Runtime.InteropServices;

namespace TerraBase.Features.Clock;

internal static partial class KernelClock
{
    internal static double ErrorMilliseconds
    {
        get
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            {
                return double.PositiveInfinity;
            }

            var reading = new Timex();
            var result = Read(ref reading);
            return result < 0 || result == 5 || (reading.Status & 0x1040) != 0 || reading.EstimatedError < 0
                ? double.PositiveInfinity
                : reading.EstimatedError / 1000d;
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 208)]
    private struct Timex
    {
        [FieldOffset(32)]
        internal long EstimatedError;
        [FieldOffset(40)]
        internal int Status;
    }

    [LibraryImport("libc", EntryPoint = "adjtimex")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int Read(ref Timex reading);
}

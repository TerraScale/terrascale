using System.Runtime.InteropServices;

namespace TerraBase.Primitives;

internal static class DirectorySync
{
    public static void Sync(string directory)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("directory sync is implemented for Linux, where TerraBase runs");
        }

        var descriptor = Native.Open(directory, 0);
        if (descriptor < 0)
        {
            throw new IOException($"opening a database directory to sync it failed (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            if (Native.Fsync(descriptor) != 0)
            {
                throw new IOException($"syncing a database directory failed (errno {Marshal.GetLastPInvokeError()})");
            }
        }
        finally
        {
            _ = Native.Close(descriptor);
        }
    }

    private static class Native
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Fsync(int descriptor);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        internal static extern int Close(int descriptor);
    }
}

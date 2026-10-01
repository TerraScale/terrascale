using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerraScale.DotNet.Native;

internal static partial class NativeFilePersistence
{
    internal static void MoveDurably(string temporary, string destination, string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_WRITE_THROUGH, without replacement of an existing file.
            if (!MoveFileEx(temporary, destination, 0x8)) throw IoError("Cannot persist native operation filename.");
            return;
        }

        using var parent = OpenUnix(directory, directory: true);
        File.Move(temporary, destination, overwrite: false);
        // Flushing the file alone does not persist its new directory entry.
        if (Fsync(parent) != 0) throw IoError("Cannot synchronize native operation directory.");
    }

    internal static FileStream OpenPrivateRead(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows callers own the containing directory's private ACL.
            if (new FileInfo(path).LinkTarget is not null)
                throw new InvalidDataException("Native operation file must not be a symbolic link.");
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        }

        var handle = OpenUnix(path, directory: false);
        try
        {
            // Inspect the opened inode, never a pathname that can be replaced
            // between permission validation and opening the file.
            var mode = File.GetUnixFileMode(handle);
            if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                throw new InvalidDataException("Native operation file must be private to its owner.");
            return new FileStream(handle, FileAccess.Read, 81920, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenUnix(string path, bool directory)
    {
        // O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK, plus O_DIRECTORY
        // for fsync. Nonblocking open also prevents a substituted FIFO hanging.

        var flags = OperatingSystem.IsLinux() ? 0x20000 | 0x80000 | 0x800 | (directory ? 0x10000 : 0)
            : OperatingSystem.IsMacOS() ? 0x100 | 0x1000000 | 0x4 | (directory ? 0x100000 : 0)
            : throw new PlatformNotSupportedException("Native operation persistence supports Linux, macOS and Windows.");
        var descriptor = Open(path, flags);
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == (OperatingSystem.IsLinux() ? 40 : 62))
                throw new InvalidDataException("Native operation path must not be a symbolic link.");
            throw IoError("Cannot open native operation path without following symbolic links.");
        }
        return new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    private static IOException IoError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle descriptor);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string destination, uint flags);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TerraScale.DotNet.Native;

internal static partial class NativeFilePersistence
{
    internal static async Task SaveAsync(byte[] bytes, string destination, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(destination)!;
        if (!OperatingSystem.IsWindows())
        {
            using var parent = NativeOperationDirectory.Open(directory);
            await parent.SaveAsync(bytes, Path.GetFileName(destination), cancellationToken).ConfigureAwait(false);
            return;
        }

        // Windows callers must provision a trusted private directory hierarchy.
        var temporary = Path.Combine(directory, ".native-operation-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!MoveFileEx(temporary, destination, 0x8)) throw IoError("Cannot persist native operation filename.");
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static void SynchronizeDirectory(SafeFileHandle parent)
    {
        if (Fsync(parent) != 0) throw IoError("Cannot synchronize native operation directory.");
    }

    internal static FileStream OpenPrivateRead(string path)
    {
        // Managed validation rejects embedded NUL before native string marshalling.
        path = Path.GetFullPath(path);
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
            if (FStat(handle, out var status) != 0) throw IoError("Cannot inspect native operation file type.");
            if ((status.Mode & 0xF000) != 0x8000)
                throw new InvalidDataException("Native operation file must be a regular file.");
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

    internal static SafeFileHandle OpenUnix(string path, bool directory)
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

    internal static IOException IoError(string message) => new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(SafeFileHandle descriptor);

    // .NET's System.Native normalizes stat layouts across Linux/macOS and
    // architectures. FileStatus matches the runtime's portable pal_io.h ABI.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileStatus
    {
        internal int Flags;
        internal int Mode;
        internal uint Uid;
        internal uint Gid;
        internal long Size;
        internal long ATime;
        internal long ATimeNsec;
        internal long MTime;
        internal long MTimeNsec;
        internal long CTime;
        internal long CTimeNsec;
        internal long BirthTime;
        internal long BirthTimeNsec;
        internal long Dev;
        internal long RDev;
        internal long Ino;
        internal uint UserFlags;
    }

    [LibraryImport("System.Native", EntryPoint = "SystemNative_FStat", SetLastError = true)]
    private static partial int FStat(SafeFileHandle descriptor, out NativeFileStatus status);

    [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveFileEx(string existing, string destination, uint flags);
}

// All Unix persistence operations use the same open directory descriptor.
// Its owner must be trusted; other users may not change its entries.
internal sealed partial class NativeOperationDirectory : IDisposable
{
    private readonly SafeFileHandle parent;
    private NativeOperationDirectory(SafeFileHandle parent) => this.parent = parent;

    internal static NativeOperationDirectory Open(string path)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Directory-descriptor persistence requires Unix.");
        var parent = NativeFilePersistence.OpenUnix(path, directory: true);
        try
        {
            if ((File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidDataException("Native operation directory must prevent changes by other users.");
            return new NativeOperationDirectory(parent);
        }
        catch { parent.Dispose(); throw; }
    }

    internal async Task SaveAsync(byte[] bytes, string fileName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(fileName) || fileName is "." or ".." || fileName != Path.GetFileName(fileName))
            throw new ArgumentException("Native operation destination must be a filename.", nameof(fileName));
        var temporary = ".native-operation-" + Guid.NewGuid().ToString("N") + ".tmp";
        // O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | O_CLOEXEC, mode 0600.
        var flags = OperatingSystem.IsLinux() ? 1 | 0x40 | 0x80 | 0x20000 | 0x80000
            : 1 | 0x200 | 0x800 | 0x100 | 0x1000000;
        var descriptor = OpenAt(parent, temporary, flags, 0x180);
        if (descriptor < 0) throw NativeFilePersistence.IoError("Cannot create private native operation file.");
        try
        {
            using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
            await using (var stream = new FileStream(handle, FileAccess.Write, 81920, isAsync: false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            // linkat publishes without replacing an existing destination.
            if (LinkAt(parent, temporary, parent, fileName, 0) != 0)
                throw NativeFilePersistence.IoError("Cannot publish native operation filename without replacement.");
            if (UnlinkAt(parent, temporary, 0) != 0)
                throw NativeFilePersistence.IoError("Cannot remove native operation temporary entry.");
            NativeFilePersistence.SynchronizeDirectory(parent);
        }
        finally
        {
            // Cleanup is relative to the original directory even if its path
            // was renamed or replaced; it never masks the persistence result.
            _ = UnlinkAt(parent, temporary, 0);
        }
    }

    public void Dispose() => parent.Dispose();

    [LibraryImport("libc", EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAt(SafeFileHandle parent, string path, int flags, uint mode);

    [LibraryImport("libc", EntryPoint = "linkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LinkAt(SafeFileHandle sourceParent, string source, SafeFileHandle destinationParent, string destination, int flags);

    [LibraryImport("libc", EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAt(SafeFileHandle parent, string path, int flags);
}

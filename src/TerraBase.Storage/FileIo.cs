using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Storage;

internal static partial class FileIo
{
    private const int Eintr = 4;
    private const int ShredBufferSize = 64 * 1024;

    public static void SyncDirectory(string? directory) => _ = SyncDirectoryError(directory);

    private static string? SyncDirectoryError(string? directory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(directory))
        {
            return null;
        }

        var fd = OpenDirectory(directory, 0);
        if (fd < 0)
        {
            return Marshal.GetLastPInvokeErrorMessage();
        }

        try
        {
            while (true)
            {
                if (Fsync(fd) == 0)
                {
                    return null;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != Eintr)
                {
                    return Marshal.GetPInvokeErrorMessage(error);
                }
            }
        }
        finally
        {
            _ = CloseFd(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "fdatasync", SetLastError = true)]
    private static partial int Fdatasync(int fd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseFd(int fd);

    public static string? FlushData(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsLinux())
        {
            return Flush(handle);
        }

        var addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var fd = (int)handle.DangerousGetHandle();
            while (true)
            {
                if (Fdatasync(fd) == 0)
                {
                    return null;
                }

                var error = Marshal.GetLastPInvokeError();
                if (error != Eintr)
                {
                    return Marshal.GetPInvokeErrorMessage(error);
                }
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    public static string? SyncFile(string path)
    {
        try
        {
            var access = OperatingSystem.IsLinux() ? FileAccess.Read : FileAccess.ReadWrite;
            using var handle = File.OpenHandle(path, FileMode.Open, access, FileShare.ReadWrite | FileShare.Delete);
            return FlushData(handle);
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static string? Write(SafeFileHandle handle, ReadOnlySpan<byte> bytes, long offset, bool fsync)
    {
        try
        {
            RandomAccess.Write(handle, bytes, offset);
            return fsync ? FlushData(handle) : null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    public static string? Read(SafeFileHandle handle, Span<byte> destination, long offset)
    {
        try
        {
            while (!destination.IsEmpty)
            {
                var read = RandomAccess.Read(handle, destination, offset);
                if (read == 0)
                {
                    return "unexpected end of file";
                }

                destination = destination[read..];
                offset += read;
            }

            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    public static SafeFileHandle? OpenHandle(string path, FileMode mode)
    {
        try
        {
            var existed = mode == FileMode.OpenOrCreate && File.Exists(path);
            var handle = File.OpenHandle(path, mode, FileAccess.ReadWrite, FileShare.Read);
            if (mode is FileMode.CreateNew or FileMode.Create || (mode == FileMode.OpenOrCreate && !existed))
            {
                SyncDirectory(Path.GetDirectoryName(path));
            }

            return handle;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static byte[]? ReadAll(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? SetLength(SafeFileHandle handle, long length)
    {
        try
        {
            RandomAccess.SetLength(handle, length);
            return FlushData(handle);
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    public static string? Flush(SafeFileHandle handle)
    {
        try
        {
            RandomAccess.FlushToDisk(handle);
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    public static string? Move(string source, string destination)
    {
        try
        {
            File.Move(source, destination);
            var destinationDirectory = Path.GetDirectoryName(destination);
            var sourceDirectory = Path.GetDirectoryName(source);
            SyncDirectory(destinationDirectory);
            if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.Ordinal))
            {
                SyncDirectory(sourceDirectory);
            }

            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    public static string? Replace(string source, string destination)
    {
        try
        {
            File.Move(source, destination, overwrite: true);
            SyncDirectory(Path.GetDirectoryName(destination));
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static string? MoveDirectory(string source, string destination)
    {
        try
        {
            if (SyncDirectoryError(source) is { } error)
            {
                return error;
            }

            Directory.Move(source, destination);
            var destinationSync = SyncDirectoryError(Path.GetDirectoryName(destination));
            var sourceSync = SyncDirectoryError(Path.GetDirectoryName(source));
            return destinationSync ?? sourceSync;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static Stream? OpenRead(string path)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static string? Shred(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ShredBufferSize);
                try
                {
                    for (long offset = 0; offset < length; offset += ShredBufferSize)
                    {
                        var slice = buffer.AsSpan(0, (int)Math.Min(ShredBufferSize, length - offset));
                        RandomNumberGenerator.Fill(slice);
                        RandomAccess.Write(handle, slice, offset);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                if (FlushData(handle) is { } flushError)
                {
                    return flushError;
                }
            }

            File.Delete(path);
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static string? DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static string? DeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    public static string? ListFiles(string directory, string searchPattern, out string[] files)
    {
        try
        {
            files = Directory.GetFiles(directory, searchPattern, SearchOption.TopDirectoryOnly);
            return null;
        }
        catch (IOException exception)
        {
            files = [];
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            files = [];
            return exception.Message;
        }
    }

    public static string? ListDirectories(string directory, out string[] directories)
    {
        try
        {
            directories = Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly);
            return null;
        }
        catch (IOException exception)
        {
            directories = [];
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            directories = [];
            return exception.Message;
        }
    }

    public static string? CreateDirectory(string path)
    {
        try
        {
            _ = Directory.CreateDirectory(path);
            SyncDirectory(Path.GetDirectoryName(path));
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }
}

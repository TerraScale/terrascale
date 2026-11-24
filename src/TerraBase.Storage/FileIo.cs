using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Storage;

internal static partial class FileIo
{
    public static void SyncDirectory(string? directory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(directory))
        {
            return;
        }

        var fd = OpenDirectory(directory, 0);
        if (fd < 0)
        {
            return;
        }

        try
        {
            _ = Fsync(fd);
        }
        finally
        {
            _ = CloseFd(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync")]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int CloseFd(int fd);
    public static string? Write(SafeFileHandle handle, ReadOnlySpan<byte> bytes, long offset, bool fsync)
    {
        try
        {
            RandomAccess.Write(handle, bytes, offset);
            if (fsync)
            {
                RandomAccess.FlushToDisk(handle);
            }

            return null;
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
            RandomAccess.FlushToDisk(handle);
            return null;
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
                var buffer = new byte[64 * 1024];
                for (long offset = 0; offset < length; offset += buffer.Length)
                {
                    RandomNumberGenerator.Fill(buffer);
                    RandomAccess.Write(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                }

                RandomAccess.FlushToDisk(handle);
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

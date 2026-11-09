using System.Runtime.InteropServices;

namespace TerraBase.Engine.Turso;

internal static class PageCache
{
    private const int DontNeed = 4;
    private const int ProtRead = 1;
    private const int MapShared = 1;

    private const int Window = 64 * 1024;

    internal static bool Evicted(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            RandomAccess.FlushToDisk(file);
            var fd = (int)file.DangerousGetHandle();
            var length = RandomAccess.GetLength(file);
            if (posix_fadvise(fd, 0, 0, DontNeed) != 0)
            {
                return false;
            }

            if (length == 0)
            {
                return true;
            }

            var map = mmap(0, (nuint)length, ProtRead, MapShared, fd, 0);
            if (map == -1)
            {
                return false;
            }

            try
            {
                var page = Environment.SystemPageSize;
                var pages = (length + page - 1) / page;
                var resident = new byte[(int)Math.Min(pages, Window)];
                for (long first = 0; first < pages; first += resident.Length)
                {
                    var count = (int)Math.Min(pages - first, resident.Length);
                    if (mincore(map + (nint)(first * page), (nuint)count * (nuint)page, resident) != 0
                        || resident.AsSpan(0, count).ContainsAnyExcept((byte)0))
                    {
                        return false;
                    }
                }

                return true;
            }
            finally
            {
                _ = munmap(map, (nuint)length);
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("libc")]
    private static extern int posix_fadvise(int fd, long offset, long length, int advice);

    [DllImport("libc")]
    private static extern nint mmap(nint address, nuint length, int protection, int flags, int fd, long offset);

    [DllImport("libc")]
    private static extern int munmap(nint address, nuint length);

    [DllImport("libc")]
    private static extern int mincore(nint address, nuint length, byte[] vector);
}

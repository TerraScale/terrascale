using System.Runtime.InteropServices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Proves that the kernel caches no page of a file (docs/terrabase/decisions/0013a, section 1.3): the file syncs without
/// error, its cached pages are dropped, and none is left. After an unclean exit on the same boot, a failed sync can leave
/// pages cached that never reached the disk, and reading them back would certify them. Once none is cached, every read
/// comes from the disk, as after a power loss, which every FULL commit survives. Anything unproven (another platform, an
/// error, a page still cached) is false.
/// </summary>
internal static class PageCache
{
    private const int DontNeed = 4; // POSIX_FADV_DONTNEED
    private const int ProtRead = 1;
    private const int MapShared = 1;

    // Pages checked per mincore call: the residency vector stays this many bytes.
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
            // A sync error reported now means pages that may differ from the disk.
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

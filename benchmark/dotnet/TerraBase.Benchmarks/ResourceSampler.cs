using System.Buffers.Text;
using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;

namespace TerraBase.Benchmarks;

public readonly record struct ResourceSnapshot(
    long TotalAllocatedBytes,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    TimeSpan GcPauseDuration,
    long ManagedHeapBytes,
    long GcCommittedBytes,
    TimeSpan TotalCpu,
    TimeSpan UserCpu,
    TimeSpan KernelCpu,
    long RssBytes,
    long PrivateDirtyBytes,
    int OpenFdCount,
    ProcIoSnapshot Io);

public sealed class ResourceSampler : IDisposable
{
    private const string ProcIoPath = "/proc/self/io";
    private const string ProcStatusPath = "/proc/self/status";
    private const string ProcFdPath = "/proc/self/fd";
    private const string ProcCpuInfoPath = "/proc/cpuinfo";

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly byte[] _ioBuffer = new byte[1024];
    private readonly byte[] _statusBuffer = new byte[16384];

    public ResourceSnapshot CaptureBeforeRound(out long timestamp)
    {
        var io = ReadProcIo();
        var openFdCount = ReadOpenFdCount();
        var (rssBytes, privateDirtyBytes) = ReadMemoryBytes();
        _process.Refresh();
        var totalCpu = _process.TotalProcessorTime;
        var userCpu = _process.UserProcessorTime;
        var kernelCpu = _process.PrivilegedProcessorTime;
        var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes;
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var gcPause = GC.GetTotalPauseDuration();
        var totalAllocated = GC.GetTotalAllocatedBytes(precise: true);
        timestamp = Stopwatch.GetTimestamp();

        return new ResourceSnapshot(
            totalAllocated,
            gen0,
            gen1,
            gen2,
            gcPause,
            managedHeapBytes,
            gcCommittedBytes,
            totalCpu,
            userCpu,
            kernelCpu,
            rssBytes,
            privateDirtyBytes,
            openFdCount,
            io);
    }

    public ResourceSnapshot CaptureAfterRound(out long timestamp)
    {
        timestamp = Stopwatch.GetTimestamp();
        var totalAllocated = GC.GetTotalAllocatedBytes(precise: true);
        var gcPause = GC.GetTotalPauseDuration();
        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        var gcCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes;
        _process.Refresh();
        var totalCpu = _process.TotalProcessorTime;
        var userCpu = _process.UserProcessorTime;
        var kernelCpu = _process.PrivilegedProcessorTime;
        var (rssBytes, privateDirtyBytes) = ReadMemoryBytes();
        var io = ReadProcIo();
        var openFdCount = ReadOpenFdCount();

        return new ResourceSnapshot(
            totalAllocated,
            gen0,
            gen1,
            gen2,
            gcPause,
            managedHeapBytes,
            gcCommittedBytes,
            totalCpu,
            userCpu,
            kernelCpu,
            rssBytes,
            privateDirtyBytes,
            openFdCount,
            io);
    }

    public ResourceSnapshot CaptureSnapshot() => CaptureAfterRound(out _);

    public long ReadWorkingSetBytes()
    {
        _process.Refresh();
        return _process.WorkingSet64;
    }

    public (long RssBytes, long PrivateDirtyBytes) ReadMemoryBytes()
    {
        if (TryReadFileUtf8(ProcStatusPath, _statusBuffer, out var bytesRead))
        {
            var span = _statusBuffer.AsSpan(0, bytesRead);
            var rssKiB = -1L;
            var anonKiB = -1L;
            var offset = 0;

            while (offset < span.Length && (rssKiB < 0 || anonKiB < 0))
            {
                var lineEnd = span[offset..].IndexOf((byte)'\n');
                var line = lineEnd >= 0 ? span.Slice(offset, lineEnd) : span[offset..];
                offset = lineEnd >= 0 ? offset + lineEnd + 1 : span.Length;

                if (rssKiB < 0 && line.StartsWith("VmRSS:"u8))
                {
                    if (TryParseLeadingInt64(line[6..], out var parsed))
                    {
                        rssKiB = parsed;
                    }
                }
                else if (anonKiB < 0 && line.StartsWith("RssAnon:"u8))
                {
                    if (TryParseLeadingInt64(line[8..], out var parsed))
                    {
                        anonKiB = parsed;
                    }
                }
            }

            if (rssKiB >= 0)
            {
                return (rssKiB * 1024L, anonKiB >= 0 ? anonKiB * 1024L : 0L);
            }
        }

        _process.Refresh();
        return (_process.WorkingSet64, 0L);
    }

    public ProcIoSnapshot ReadProcIo()
    {
        if (!TryReadFileUtf8(ProcIoPath, _ioBuffer, out var bytesRead))
        {
            return default;
        }

        var span = _ioBuffer.AsSpan(0, bytesRead);
        var rchar = 0L;
        var wchar = 0L;
        var readBytes = 0L;
        var writeBytes = 0L;
        var offset = 0;

        while (offset < span.Length)
        {
            var lineEnd = span[offset..].IndexOf((byte)'\n');
            var line = lineEnd >= 0 ? span.Slice(offset, lineEnd) : span[offset..];
            offset = lineEnd >= 0 ? offset + lineEnd + 1 : span.Length;

            var colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon];
            if (!TryParseLeadingInt64(line[(colon + 1)..], out var parsed))
            {
                continue;
            }

            if (key.SequenceEqual("rchar"u8))
            {
                rchar = parsed;
            }
            else if (key.SequenceEqual("wchar"u8))
            {
                wchar = parsed;
            }
            else if (key.SequenceEqual("read_bytes"u8))
            {
                readBytes = parsed;
            }
            else if (key.SequenceEqual("write_bytes"u8))
            {
                writeBytes = parsed;
            }
        }

        return new ProcIoSnapshot(rchar, wchar, readBytes, writeBytes);
    }

    public static long ReadRssBytesStatic()
    {
        using var sampler = new ResourceSampler();
        return sampler.ReadMemoryBytes().RssBytes;
    }

    public static ProcIoSnapshot ReadProcIoStatic()
    {
        using var sampler = new ResourceSampler();
        return sampler.ReadProcIo();
    }

    public static int ReadOpenFdCount()
    {
        if (!Directory.Exists(ProcFdPath))
        {
            return 0;
        }

        return TryCountDirectoryEntries(ProcFdPath);
    }

    public static string ReadCpuModelName()
    {
        var buffer = new byte[65536];
        if (!TryReadFileUtf8(ProcCpuInfoPath, buffer, out var bytesRead))
        {
            return "unknown";
        }

        var span = buffer.AsSpan(0, bytesRead);
        if (TryExtractCpuInfoField(span, "model name"u8, out var model)
            || TryExtractCpuInfoField(span, "Model"u8, out model)
            || TryExtractCpuInfoField(span, "Hardware"u8, out model))
        {
            return model;
        }

        return "unknown";
    }

    public static string ReadGitHeadSha()
    {
        var buffer = new byte[4096];
        if (TryResolveGitHeadFrom(Environment.CurrentDirectory, buffer, out var sha))
        {
            return sha;
        }

        if (TryResolveGitHeadFrom(AppContext.BaseDirectory, buffer, out sha))
        {
            return sha;
        }

        return string.Empty;
    }

    public void Dispose() => _process.Dispose();

    private static bool TryResolveGitHeadFrom(string startDirectory, byte[] buffer, out string sha)
    {
        sha = string.Empty;
        var current = startDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            var dotGit = Path.Combine(current, ".git");
            if (Directory.Exists(dotGit))
            {
                return TryReadHeadFromGitDir(dotGit, dotGit, buffer, out sha);
            }

            if (File.Exists(dotGit) && TryReadFileUtf8(dotGit, buffer, out var gitFileBytes))
            {
                var line = TrimAsciiWhitespace(buffer.AsSpan(0, gitFileBytes));
                if (line.StartsWith("gitdir:"u8))
                {
                    var rawDir = Encoding.UTF8.GetString(TrimAsciiWhitespace(line[7..]));
                    var resolvedGitDir = Path.IsPathRooted(rawDir) ? rawDir : Path.GetFullPath(Path.Combine(current, rawDir));
                    var commonDir = ResolveGitCommonDir(resolvedGitDir, buffer);
                    return TryReadHeadFromGitDir(resolvedGitDir, commonDir, buffer, out sha);
                }
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }

    private static string ResolveGitCommonDir(string gitDir, byte[] buffer)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!TryReadFileUtf8(commonDirFile, buffer, out var bytesRead))
        {
            return gitDir;
        }

        var raw = Encoding.UTF8.GetString(TrimAsciiWhitespace(buffer.AsSpan(0, bytesRead)));
        if (raw.Length == 0)
        {
            return gitDir;
        }

        return Path.IsPathRooted(raw) ? raw : Path.GetFullPath(Path.Combine(gitDir, raw));
    }

    private static bool TryReadHeadFromGitDir(string gitDir, string commonDir, byte[] buffer, out string sha)
    {
        sha = string.Empty;
        var headPath = Path.Combine(gitDir, "HEAD");
        if (!TryReadFileUtf8(headPath, buffer, out var bytesRead))
        {
            return false;
        }

        var line = TrimAsciiWhitespace(buffer.AsSpan(0, bytesRead));
        if (line.StartsWith("ref:"u8))
        {
            var refBytes = TrimAsciiWhitespace(line[4..]);
            var refName = Encoding.UTF8.GetString(refBytes);
            var refPath = Path.Combine(gitDir, refName);
            if (!File.Exists(refPath))
            {
                refPath = Path.Combine(commonDir, refName);
            }

            if (TryReadFileUtf8(refPath, buffer, out var refRead))
            {
                var hash = TrimAsciiWhitespace(buffer.AsSpan(0, refRead));
                if (hash.Length >= 7)
                {
                    sha = Encoding.ASCII.GetString(hash);
                    return true;
                }
            }

            return TryReadPackedRef(Path.Combine(commonDir, "packed-refs"), refName, out sha);
        }

        if (line.Length >= 7)
        {
            sha = Encoding.ASCII.GetString(line);
            return true;
        }

        return false;
    }

    private static bool TryReadPackedRef(string packedRefsPath, string refName, out string sha)
    {
        sha = string.Empty;
        var packedBuffer = new byte[131072];
        if (!TryReadFileUtf8(packedRefsPath, packedBuffer, out var bytesRead))
        {
            return false;
        }

        Span<byte> target = stackalloc byte[Encoding.UTF8.GetByteCount(refName)];
        _ = Encoding.UTF8.GetBytes(refName, target);
        var span = packedBuffer.AsSpan(0, bytesRead);
        var offset = 0;

        while (offset < span.Length)
        {
            var lineEnd = span[offset..].IndexOf((byte)'\n');
            var line = TrimAsciiWhitespace(lineEnd >= 0 ? span.Slice(offset, lineEnd) : span[offset..]);
            offset = lineEnd >= 0 ? offset + lineEnd + 1 : span.Length;

            if (line.Length == 0 || line[0] == (byte)'#' || line[0] == (byte)'^')
            {
                continue;
            }

            var space = line.IndexOf((byte)' ');
            if (space <= 0 || space + 1 >= line.Length)
            {
                continue;
            }

            if (line[(space + 1)..].SequenceEqual(target))
            {
                sha = Encoding.ASCII.GetString(line[..space]);
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractCpuInfoField(ReadOnlySpan<byte> span, ReadOnlySpan<byte> fieldName, out string value)
    {
        value = string.Empty;
        var offset = 0;
        while (offset < span.Length)
        {
            var lineEnd = span[offset..].IndexOf((byte)'\n');
            var line = lineEnd >= 0 ? span.Slice(offset, lineEnd) : span[offset..];
            offset = lineEnd >= 0 ? offset + lineEnd + 1 : span.Length;

            var colon = line.IndexOf((byte)':');
            if (colon <= 0)
            {
                continue;
            }

            var key = TrimAsciiWhitespace(line[..colon]);
            if (!key.SequenceEqual(fieldName))
            {
                continue;
            }

            var val = TrimAsciiWhitespace(line[(colon + 1)..]);
            if (val.Length > 0)
            {
                value = Encoding.UTF8.GetString(val);
                return true;
            }
        }

        return false;
    }

    private static bool TryParseLeadingInt64(ReadOnlySpan<byte> span, out long value)
    {
        var trimmed = TrimLeadingAsciiWhitespace(span);
        return Utf8Parser.TryParse(trimmed, out value, out _);
    }

    private static ReadOnlySpan<byte> TrimLeadingAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && IsAsciiWhitespace(span[start]))
        {
            start++;
        }

        return span[start..];
    }

    private static ReadOnlySpan<byte> TrimAsciiWhitespace(ReadOnlySpan<byte> span)
    {
        var start = 0;
        while (start < span.Length && IsAsciiWhitespace(span[start]))
        {
            start++;
        }

        var end = span.Length;
        while (end > start && IsAsciiWhitespace(span[end - 1]))
        {
            end--;
        }

        return span[start..end];
    }

    private static bool IsAsciiWhitespace(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

    private static bool TryReadFileUtf8(string path, byte[] buffer, out int bytesRead)
    {
        bytesRead = 0;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var total = 0;
            while (total < buffer.Length)
            {
                var read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            bytesRead = total;
            return total > 0;
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

    private static int TryCountDirectoryEntries(string path)
    {
        try
        {
            using var enumerator = new FdCountEnumerator(path);
            var count = 0;
            while (enumerator.MoveNext())
            {
                count++;
            }

            return count > 0 ? count - 1 : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private sealed class FdCountEnumerator(string directory) : FileSystemEnumerator<byte>(directory)
    {
        protected override byte TransformEntry(ref FileSystemEntry entry) => 0;
    }
}

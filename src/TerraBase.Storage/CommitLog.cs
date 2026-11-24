using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Storage;

internal sealed record CommitLogOptions(long SegmentMaxBytes, TimeSpan SegmentMaxAge, bool Fsync);

internal readonly union OriginLogOpenResult(OriginLog, StorageCorrupted);

internal readonly union LogReadResult(IReadOnlyList<CommitMetadata>, CommitLogGap, StorageCorrupted);

public static class SqliteCommitLog
{
    public const int MaxSegmentBytes = 128 * 1024 * 1024;

    public static StorageBusy? PruneArchivedSegments(ISqlDatabaseSession session, Func<CommitLogSegment, bool> canPrune) =>
        session is DatabaseSession sqlite ? sqlite.PruneArchivedSegments(canPrune) : null;
}

internal sealed class CommitLogCipher : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("terrabase-commitlog-v1");

    private readonly Lock _gate = new();
    private readonly AesGcm _aes;

    public CommitLogCipher(ReadOnlySpan<byte> rawKey, Ulid databaseId)
    {
        Span<byte> key = stackalloc byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, rawKey, key, databaseId.ToByteArray(), Info);
        _aes = new AesGcm(key, TagSize);
        key.Clear();
    }

    public static int Overhead => NonceSize + TagSize;

    public void Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associated, Span<byte> destination)
    {
        var nonce = destination[..NonceSize];
        RandomNumberGenerator.Fill(nonce);
        lock (_gate)
        {
            _aes.Encrypt(nonce, plaintext, destination[(NonceSize + TagSize)..], destination.Slice(NonceSize, TagSize), associated);
        }
    }

    public bool TryOpen(ReadOnlySpan<byte> sealedBytes, ReadOnlySpan<byte> associated, Span<byte> plaintext)
    {
        if (sealedBytes.Length < Overhead || plaintext.Length != sealedBytes.Length - Overhead)
        {
            return false;
        }

        lock (_gate)
        {
            try
            {
                _aes.Decrypt(sealedBytes[..NonceSize], sealedBytes[(NonceSize + TagSize)..], sealedBytes.Slice(NonceSize, TagSize), plaintext, associated);
                return true;
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _aes.Dispose();
        }
    }
}

internal static class CommitFrame
{
    public const int PrefixSize = 4;
    public const int SequenceSize = 8;
    public const int CrcSize = 4;
    public const int MaxBodySize = 64 * 1024 * 1024;

    public static byte[] Encode(CommitMetadata commit, CommitLogCipher cipher)
    {
        var plain = new ArrayBufferWriter<byte>(96 + commit.DeltaPayload.Length);
        Span<byte> txId = stackalloc byte[16];
        _ = commit.TxId.TryWriteBytes(txId);
        plain.Write(txId);
        var origin = Encoding.UTF8.GetBytes(commit.OriginRegion);
        WireWriter.UInt16(plain, (ushort)origin.Length);
        plain.Write(origin);
        WireWriter.Hlc(plain, commit.Hlc);
        Span<byte> number = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(number, commit.SchemaEpoch);
        plain.Write(number);
        WireWriter.Byte(plain, commit.Mode == ConsistencyMode.Strong ? (byte)1 : (byte)0);
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)commit.DeltaPayload.Length);
        plain.Write(number[..4]);
        plain.Write(commit.DeltaPayload.Span);
        var bookmark = new ArrayBufferWriter<byte>(32);
        commit.Bookmark.WriteTo(bookmark);
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)bookmark.WrittenCount);
        plain.Write(number[..4]);
        plain.Write(bookmark.WrittenSpan);

        var bodyLength = SequenceSize + CommitLogCipher.Overhead + plain.WrittenCount;
        var frame = new byte[PrefixSize + bodyLength + CrcSize];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bodyLength);
        var body = frame.AsSpan(PrefixSize, bodyLength);
        BinaryPrimitives.WriteInt64LittleEndian(body, commit.Sequence);
        cipher.Seal(plain.WrittenSpan, Associated(commit.Sequence, commit.OriginRegion), body[SequenceSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(PrefixSize + bodyLength), Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body));
        return frame;
    }

    public static bool TryDecode(ReadOnlySpan<byte> body, string expectedOrigin, CommitLogCipher cipher, out CommitMetadata commit)
    {
        commit = Empty;
        if (body.Length < SequenceSize + CommitLogCipher.Overhead)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(body);
        var sealedBytes = body[SequenceSize..];
        var plain = new byte[sealedBytes.Length - CommitLogCipher.Overhead];
        if (!cipher.TryOpen(sealedBytes, Associated(sequence, expectedOrigin), plain))
        {
            return false;
        }

        var reader = new WireReader(plain);
        var txId = reader.Take(16);
        var origin = Encoding.UTF8.GetString(reader.Take(reader.UInt16()));
        var hlc = reader.Hlc();
        var epoch = reader.Take(8);
        var mode = reader.Byte();
        var payloadLength = reader.Take(4);
        if (reader.Failed)
        {
            return false;
        }

        var payload = reader.Take((int)BinaryPrimitives.ReadUInt32LittleEndian(payloadLength)).ToArray();
        var bookmarkLength = reader.Take(4);
        if (reader.Failed)
        {
            return false;
        }

        var bookmarkBytes = reader.Take((int)BinaryPrimitives.ReadUInt32LittleEndian(bookmarkLength));
        if (reader.Failed || !reader.End || !CausalBookmark.TryRead(ref bookmarkBytes, out var bookmark) || !string.Equals(origin, expectedOrigin, StringComparison.Ordinal))
        {
            return false;
        }

        commit = new CommitMetadata(
            new Ulid(txId),
            origin,
            sequence,
            hlc,
            BinaryPrimitives.ReadUInt64LittleEndian(epoch),
            mode == 1 ? ConsistencyMode.Strong : ConsistencyMode.Fast,
            bookmark,
            payload);
        return true;
    }

    public static bool TryParseAll(ReadOnlySpan<byte> bytes, string origin, CommitLogCipher cipher, List<CommitMetadata> commits, long fromSequence = 0, int maxEntries = int.MaxValue)
    {
        var decoded = 0;
        while (!bytes.IsEmpty && decoded < maxEntries)
        {
            if (bytes.Length < PrefixSize)
            {
                return false;
            }

            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            if (bodyLength > MaxBodySize || PrefixSize + bodyLength + CrcSize > (uint)bytes.Length)
            {
                return false;
            }

            var body = bytes.Slice(PrefixSize, (int)bodyLength);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(PrefixSize + (int)bodyLength)..]);
            if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body) != crc || body.Length < SequenceSize)
            {
                return false;
            }

            bytes = bytes[(PrefixSize + (int)bodyLength + CrcSize)..];
            if (BinaryPrimitives.ReadInt64LittleEndian(body) < fromSequence)
            {
                continue;
            }

            if (!TryDecode(body, origin, cipher, out var commit))
            {
                return false;
            }

            commits.Add(commit);
            decoded++;
        }

        return true;
    }

    private static CommitMetadata Empty { get; } = new(Ulid.Empty, string.Empty, 0, HlcTimestamp.Zero, 0, ConsistencyMode.Fast, CausalBookmark.Empty, ReadOnlyMemory<byte>.Empty);

    private static byte[] Associated(long sequence, string origin)
    {
        var originBytes = Encoding.UTF8.GetBytes(origin);
        var associated = new byte[SequenceSize + originBytes.Length];
        BinaryPrimitives.WriteInt64LittleEndian(associated, sequence);
        originBytes.CopyTo(associated, SequenceSize);
        return associated;
    }
}

internal sealed class OriginLog : IDisposable
{
    private const string ActiveExtension = ".tblog";
    private const string SealedExtension = ".tbseg";

    private readonly Lock _gate = new();
    private readonly List<(long Offset, long Sequence)> _frames = [];
    private readonly List<(CommitLogSegment Segment, string Path)> _sealed;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly CommitLogCipher _cipher;
    private readonly CommitLogOptions _options;
    private readonly TimeProvider _time;
    private readonly Ulid _databaseId;
    private SafeFileHandle? _active;
    private long _length;
    private long _lastSequence;
    private long _firstAvailable;
    private int _sealedReaders;
    private DateTimeOffset _activeSince;
    private bool _disposed;

    private OriginLog(
        string directory,
        string prefix,
        string origin,
        Ulid databaseId,
        CommitLogCipher cipher,
        CommitLogOptions options,
        TimeProvider time,
        SafeFileHandle active,
        List<(CommitLogSegment Segment, string Path)> sealedSegments)
    {
        _directory = directory;
        _prefix = prefix;
        Origin = origin;
        _databaseId = databaseId;
        _cipher = cipher;
        _options = options;
        _time = time;
        _active = active;
        _sealed = sealedSegments;
        _activeSince = time.GetUtcNow();
    }

    public string Origin { get; }

    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _lastSequence;
            }
        }
    }

    public static string FilePrefix(Ulid databaseId, string origin) => string.Concat(databaseId.ToString(), ".", origin);

    public static IEnumerable<string> DiscoverOrigins(string directory, Ulid databaseId)
    {
        var prefix = string.Concat(databaseId.ToString(), ".");
        HashSet<string> origins = new(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = name[prefix.Length..];
            var dot = rest.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && (rest.EndsWith(ActiveExtension, StringComparison.Ordinal) || rest.EndsWith(SealedExtension, StringComparison.Ordinal)))
            {
                origins.Add(rest[..dot]);
            }
        }

        return origins;
    }

    public static OriginLogOpenResult Open(string directory, Ulid databaseId, string origin, long baseSequence, CommitLogCipher cipher, CommitLogOptions options, TimeProvider time)
    {
        var prefix = FilePrefix(databaseId, origin);
        List<(CommitLogSegment Segment, string Path)> sealedSegments = [];
        foreach (var file in Directory.EnumerateFiles(directory, string.Concat(prefix, ".*", SealedExtension)))
        {
            var range = Path.GetFileName(file)[(prefix.Length + 1)..^SealedExtension.Length].Split('-');
            if (range.Length != 2
                || !long.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out var first)
                || !long.TryParse(range[1], NumberStyles.None, CultureInfo.InvariantCulture, out var last))
            {
                return new StorageCorrupted(databaseId, string.Concat("malformed commit log segment name ", file));
            }

            var info = new FileInfo(file);
            sealedSegments.Add((new CommitLogSegment(databaseId, origin, first, last, info.Length, info.LastWriteTimeUtc), file));
        }

        sealedSegments.Sort(static (left, right) => left.Segment.FirstSequence.CompareTo(right.Segment.FirstSequence));
        for (var index = 1; index < sealedSegments.Count; index++)
        {
            if (sealedSegments[index].Segment.FirstSequence != sealedSegments[index - 1].Segment.LastSequence + 1)
            {
                return new StorageCorrupted(databaseId, string.Concat("commit log segment gap for origin ", origin));
            }
        }

        var activePath = Path.Combine(directory, string.Concat(prefix, ActiveExtension));
        if (FileIo.OpenHandle(activePath, FileMode.OpenOrCreate) is not { } active)
        {
            return new StorageCorrupted(databaseId, string.Concat("commit log could not be opened: ", activePath));
        }

        var log = new OriginLog(directory, prefix, origin, databaseId, cipher, options, time, active, sealedSegments);
        var lastSealed = sealedSegments.Count > 0 ? sealedSegments[^1].Segment.LastSequence : 0;
        if (log.Recover(lastSealed > 0 ? lastSealed : baseSequence, baseSequence) is { } failure)
        {
            log.Dispose();
            return new StorageCorrupted(databaseId, failure);
        }

        log._firstAvailable = sealedSegments.Count > 0 ? sealedSegments[0].Segment.FirstSequence
            : log._frames.Count > 0 ? log._frames[0].Sequence
            : log._lastSequence + 1;
        return log;
    }

    public SqliteStatus Append(IReadOnlyList<CommitMetadata> commits, out long previousLength)
    {
        lock (_gate)
        {
            previousLength = _length;
            ObjectDisposedException.ThrowIf(_disposed, this);
            var offset = _length;
            List<(long Offset, long Sequence)> added = [];
            using var buffer = new MemoryStream();
            foreach (var commit in commits)
            {
                if (commit.Sequence != _lastSequence + 1 + added.Count)
                {
                    return SqliteStatus.Fail(string.Concat("commit log sequence gap for origin ", Origin));
                }

                var frame = CommitFrame.Encode(commit, _cipher);
                added.Add((offset + buffer.Length, commit.Sequence));
                buffer.Write(frame);
            }

            if (added.Count == 0)
            {
                return SqliteStatus.Success;
            }

            if (EnsureActiveHandle() is not { } active)
            {
                return new SqliteStatus(SqliteNative.IoError, "active commit log could not be opened");
            }

            if (FileIo.Write(active, buffer.GetBuffer().AsSpan(0, (int)buffer.Length), offset, _options.Fsync) is { } error)
            {
                return new SqliteStatus(SqliteNative.IoError, error);
            }

            if (_frames.Count == 0)
            {
                _activeSince = _time.GetUtcNow();
            }

            _frames.AddRange(added);
            _length = offset + buffer.Length;
            _lastSequence = added[^1].Sequence;
            if (_firstAvailable > _lastSequence || _firstAvailable == 0)
            {
                _firstAvailable = added[0].Sequence;
            }

            return SqliteStatus.Success;
        }
    }

    public void SealIfDue()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                SealIfDue(force: false);
            }
        }
    }

    public void Truncate(long length, long lastSequence)
    {
        lock (_gate)
        {
            if (_disposed || length > _length)
            {
                return;
            }

            _frames.RemoveAll(frame => frame.Offset >= length);
            if (_active is { } active)
            {
                _ = FileIo.SetLength(active, length);
            }

            _length = length;
            _lastSequence = lastSequence;
        }
    }

    public LogReadResult Read(long fromSequence, int maxEntries)
    {
        lock (_gate)
        {
            _sealedReaders++;
        }

        try
        {
            return ReadCore(fromSequence, maxEntries);
        }
        finally
        {
            lock (_gate)
            {
                _sealedReaders--;
            }
        }
    }

    private LogReadResult ReadCore(long fromSequence, int maxEntries)
    {
        List<(CommitLogSegment Segment, string Path)> segments;
        long firstAvailable;
        long last;
        lock (_gate)
        {
            segments = [.. _sealed];
            firstAvailable = _firstAvailable;
            last = _lastSequence;
        }

        if (fromSequence < firstAvailable && fromSequence <= last)
        {
            return new CommitLogGap(_databaseId, Origin, fromSequence, firstAvailable);
        }

        List<CommitMetadata> entries = [];
        if (ReadSealed(segments, fromSequence, maxEntries, entries) is { } unreadable)
        {
            return unreadable;
        }

        if (entries.Count < maxEntries && last >= fromSequence)
        {
            byte[] bytes;
            List<(CommitLogSegment Segment, string Path)> late;
            lock (_gate)
            {
                late = [.. _sealed.Skip(segments.Count)];
                var first = _frames.FindIndex(frame => frame.Sequence >= fromSequence);
                var offset = first < 0 ? _length : _frames[first].Offset;
                bytes = new byte[_length - offset];
                if (bytes.Length > 0 && (_active is not { } active || FileIo.Read(active, bytes, offset) is not null))
                {
                    return new StorageCorrupted(_databaseId, "active commit log unreadable");
                }
            }

            if (ReadSealed(late, fromSequence, maxEntries, entries) is { } lateUnreadable)
            {
                return lateUnreadable;
            }

            if (entries.Count < maxEntries && !CommitFrame.TryParseAll(bytes, Origin, _cipher, entries, fromSequence, maxEntries - entries.Count))
            {
                return new StorageCorrupted(_databaseId, "active commit log failed verification");
            }
        }

        IReadOnlyList<CommitMetadata> page = entries;
        return page;
    }

    private StorageCorrupted? ReadSealed(List<(CommitLogSegment Segment, string Path)> segments, long fromSequence, int maxEntries, List<CommitMetadata> entries)
    {
        foreach (var (segment, path) in segments)
        {
            if (segment.LastSequence < fromSequence || entries.Count >= maxEntries)
            {
                continue;
            }

            if (FileIo.ReadAll(path) is not { } bytes || !CommitFrame.TryParseAll(bytes, Origin, _cipher, entries, fromSequence, maxEntries - entries.Count))
            {
                return new StorageCorrupted(_databaseId, string.Concat("commit log segment unreadable: ", path));
            }
        }

        return null;
    }

    public IReadOnlyList<CommitLogSegment> ListSealed(long afterSequence)
    {
        lock (_gate)
        {
            SealIfDue(force: false);
            return [.. _sealed.Where(entry => entry.Segment.LastSequence > afterSequence).Select(static entry => entry.Segment)];
        }
    }

    public long FirstAvailable
    {
        get
        {
            lock (_gate)
            {
                return _firstAvailable;
            }
        }
    }

    public bool SealCommitted(long committedThrough)
    {
        lock (_gate)
        {
            if (_disposed || _lastSequence > committedThrough)
            {
                return false;
            }

            SealIfDue(force: true);
            return _frames.Count == 0;
        }
    }

    public OpenSegmentOutcome OpenSegment(CommitLogSegment segment)
    {
        lock (_gate)
        {
            foreach (var (candidate, path) in _sealed)
            {
                if (candidate.FirstSequence == segment.FirstSequence && candidate.LastSequence == segment.LastSequence)
                {
                    return FileIo.OpenRead(path) is { } content
                        ? content
                        : new StorageBusy("commit log segment could not be opened", TimeSpan.FromMilliseconds(50));
                }
            }

            return new SegmentNotFound(segment.DatabaseId, segment.OriginRegion, segment.FirstSequence, segment.LastSequence);
        }
    }

    public StorageBusy? PruneSealed(long committedThrough, Func<CommitLogSegment, bool> canPrune)
    {
        lock (_gate)
        {
            if (_disposed || _sealedReaders > 0)
            {
                return null;
            }

            var removed = 0;
            string? failure = null;
            foreach (var (segment, path) in _sealed)
            {
                if (segment.LastSequence > committedThrough || !canPrune(segment) || (failure = DeleteSealedFile(path)) is not null)
                {
                    break;
                }

                removed++;
            }

            if (removed > 0)
            {
                _sealed.RemoveRange(0, removed);
                _firstAvailable = _sealed.Count > 0 ? _sealed[0].Segment.FirstSequence
                    : _frames.Count > 0 ? _frames[0].Sequence
                    : _lastSequence + 1;
                FileIo.SyncDirectory(_directory);
            }

            return failure is null ? null : new StorageBusy(failure, TimeSpan.FromMilliseconds(50));
        }
    }

    private static string? DeleteSealedFile(string path)
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

    public bool TryParse(ReadOnlySpan<byte> bytes, List<CommitMetadata> commits) => CommitFrame.TryParseAll(bytes, Origin, _cipher, commits);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _active?.Dispose();
            _active = null;
        }
    }

    private string ActivePath() => Path.Combine(_directory, string.Concat(_prefix, ActiveExtension));

    private SafeFileHandle? EnsureActiveHandle()
    {
        if (_active is not null)
        {
            return _active;
        }

        _active = FileIo.OpenHandle(ActivePath(), FileMode.OpenOrCreate);
        return _active;
    }

    private void SealIfDue(bool force)
    {
        if (_active is null || _frames.Count == 0 || (!force && _length < _options.SegmentMaxBytes && _time.GetUtcNow() - _activeSince < _options.SegmentMaxAge))
        {
            return;
        }

        var first = _frames[0].Sequence;
        var activePath = ActivePath();
        var sealedPath = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"{_prefix}.{first:D19}-{_lastSequence:D19}{SealedExtension}"));
        if (FileIo.Flush(_active) is not null)
        {
            return;
        }

        if (FileIo.Move(activePath, sealedPath) is not null)
        {
            return;
        }

        if (FileIo.OpenHandle(activePath, FileMode.CreateNew) is not { } fresh)
        {
            if (FileIo.Move(sealedPath, activePath) is null)
            {
                return;
            }

            _active.Dispose();
            _active = null;
            _sealed.Add((new CommitLogSegment(_databaseId, Origin, first, _lastSequence, _length, _time.GetUtcNow()), sealedPath));
            _frames.Clear();
            _length = 0;
            _activeSince = _time.GetUtcNow();
            return;
        }

        _active.Dispose();
        _active = fresh;
        _sealed.Add((new CommitLogSegment(_databaseId, Origin, first, _lastSequence, _length, _time.GetUtcNow()), sealedPath));
        _frames.Clear();
        _length = 0;
        _activeSince = _time.GetUtcNow();
    }

    private string? Recover(long baseSequence, long committedSequence)
    {
        _lastSequence = baseSequence;
        if (_active is null)
        {
            return "active commit log handle missing";
        }

        var fileLength = RandomAccess.GetLength(_active);
        if (fileLength == 0)
        {
            return null;
        }

        var bytes = new byte[fileLength];
        if (FileIo.Read(_active, bytes, 0) is { } readError)
        {
            return readError;
        }

        long offset = 0;
        var expected = baseSequence + 1;
        var first = true;
        var verifiedCommitted = committedSequence == 0 || (_sealed.Count > 0 && _sealed[^1].Segment.LastSequence >= committedSequence);
        while (offset + CommitFrame.PrefixSize <= fileLength)
        {
            var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)offset));
            var end = offset + CommitFrame.PrefixSize + bodyLength + CommitFrame.CrcSize;
            if (bodyLength > CommitFrame.MaxBodySize || end > fileLength)
            {
                if (verifiedCommitted)
                {
                    break;
                }

                return "commit log frame truncated or corrupt before committed sequence";
            }

            var body = bytes.AsSpan((int)offset + CommitFrame.PrefixSize, (int)bodyLength);
            var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan((int)(end - CommitFrame.CrcSize)));
            if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body) != crc)
            {
                if (verifiedCommitted)
                {
                    break;
                }

                return "commit log frame failed CRC32C verification";
            }

            if (!CommitFrame.TryDecode(body, Origin, _cipher, out var commit))
            {
                if (verifiedCommitted)
                {
                    break;
                }

                return "commit log frame failed authentication";
            }

            if (first && _sealed.Count == 0)
            {
                expected = commit.Sequence;
            }

            first = false;
            if (commit.Sequence != expected)
            {
                if (verifiedCommitted)
                {
                    break;
                }

                return "commit log sequence gap";
            }

            _frames.Add((offset, commit.Sequence));
            _lastSequence = commit.Sequence;
            if (_lastSequence >= committedSequence)
            {
                verifiedCommitted = true;
            }

            expected++;
            offset = end;
        }

        if (!verifiedCommitted)
        {
            return "commit log ended before committed sequence";
        }

        _length = offset;
        if (offset != fileLength)
        {
            _ = FileIo.SetLength(_active, offset);
        }

        return null;
    }
}

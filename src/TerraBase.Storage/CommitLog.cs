using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
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

    public static CausalBookmark PrunableThrough(ISqlDatabaseSession session, DateTimeOffset cutoff, CausalBookmark archiveFloor) =>
        session is DatabaseSession sqlite ? sqlite.PrunableThrough(cutoff, archiveFloor) : CausalBookmark.Empty;

    public static StorageBusy? PruneArchivedSegments(
        ISqlDatabaseSession session, DateTimeOffset cutoff, CausalBookmark archiveFloor, CausalBookmark confirmed) =>
        session is DatabaseSession sqlite ? sqlite.PruneArchivedSegments(cutoff, archiveFloor, confirmed) : null;
}

internal sealed class CommitLogCipher : IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int NoncePoolBytes = 4092;
    private const int StackAssociatedLimit = 256;
    private static readonly byte[] Info = Encoding.ASCII.GetBytes("terrabase-commitlog-v1");

    private readonly Lock _gate = new();
    private readonly AesGcm _aes;
    private readonly byte[] _noncePool = new byte[NoncePoolBytes];
    private int _nonceOffset = NoncePoolBytes;
    private bool _disposed;

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
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_nonceOffset + NonceSize > _noncePool.Length)
            {
                RandomNumberGenerator.Fill(_noncePool);
                _nonceOffset = 0;
            }

            var pooledNonce = _noncePool.AsSpan(_nonceOffset, NonceSize);
            pooledNonce.CopyTo(nonce);
            pooledNonce.Clear();
            _nonceOffset += NonceSize;
            _aes.Encrypt(nonce, plaintext, destination[(NonceSize + TagSize)..], destination.Slice(NonceSize, TagSize), associated);
        }
    }

    public void Seal(ReadOnlySpan<byte> plaintext, long sequence, ReadOnlySpan<byte> originUtf8, Span<byte> destination)
    {
        var associatedLength = sizeof(long) + originUtf8.Length;
        byte[]? rented = null;
        Span<byte> buffer = associatedLength <= StackAssociatedLimit
            ? stackalloc byte[StackAssociatedLimit]
            : (rented = ArrayPool<byte>.Shared.Rent(associatedLength));
        try
        {
            var associated = buffer[..associatedLength];
            BinaryPrimitives.WriteInt64LittleEndian(associated, sequence);
            originUtf8.CopyTo(associated[sizeof(long)..]);
            Seal(plaintext, associated, destination);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
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
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    public bool TryOpen(ReadOnlySpan<byte> sealedBytes, long sequence, ReadOnlySpan<byte> originUtf8, Span<byte> plaintext)
    {
        var associatedLength = sizeof(long) + originUtf8.Length;
        byte[]? rented = null;
        Span<byte> buffer = associatedLength <= StackAssociatedLimit
            ? stackalloc byte[StackAssociatedLimit]
            : (rented = ArrayPool<byte>.Shared.Rent(associatedLength));
        try
        {
            var associated = buffer[..associatedLength];
            BinaryPrimitives.WriteInt64LittleEndian(associated, sequence);
            originUtf8.CopyTo(associated[sizeof(long)..]);
            return TryOpen(sealedBytes, associated, plaintext);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CryptographicOperations.ZeroMemory(_noncePool);
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
    public const int MaxEncodedSize = PrefixSize + MaxBodySize + CrcSize;

    private const int StackPlainLimit = 512;
    private const int StackOriginLimit = 256;

    public static long EncodedSize(CommitMetadata commit) =>
        EncodedSize(commit, Encoding.UTF8.GetByteCount(commit.OriginRegion));

    public static long EncodedSize(CommitMetadata commit, int originUtf8Length) =>
        PrefixSize + SequenceSize + CommitLogCipher.Overhead + 16 + sizeof(ushort)
        + HlcTimestamp.EncodedSize + sizeof(ulong) + 1 + sizeof(uint) + sizeof(uint) + CrcSize
        + (long)originUtf8Length + commit.DeltaPayload.Length + commit.Bookmark.GetBinarySize();

    public static IEnumerable<IReadOnlyList<CommitMetadata>> Batches(IReadOnlyList<CommitMetadata> commits, long maxBytes)
    {
        List<CommitMetadata> batch = [];
        var bytes = 0L;
        foreach (var commit in commits)
        {
            var size = EncodedSize(commit);
            if (batch.Count > 0 && bytes + size > maxBytes)
            {
                yield return batch;
                batch = [];
                bytes = 0;
            }

            batch.Add(commit);
            bytes += size;
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }

    public static void EncodeInto(
        ArrayBufferWriter<byte> destination,
        ArrayBufferWriter<byte> plainBuffer,
        CommitMetadata commit,
        ReadOnlySpan<byte> originUtf8,
        CommitLogCipher cipher)
    {
        plainBuffer.ResetWrittenCount();
        _ = commit.TxId.TryWriteBytes(plainBuffer.GetSpan(16));
        plainBuffer.Advance(16);
        WireWriter.UInt16(plainBuffer, (ushort)originUtf8.Length);
        plainBuffer.Write(originUtf8);
        WireWriter.Hlc(plainBuffer, commit.Hlc);
        BinaryPrimitives.WriteUInt64LittleEndian(plainBuffer.GetSpan(8), commit.SchemaEpoch);
        plainBuffer.Advance(8);
        WireWriter.Byte(plainBuffer, commit.Mode == ConsistencyMode.Strong ? (byte)1 : (byte)0);
        BinaryPrimitives.WriteUInt32LittleEndian(plainBuffer.GetSpan(4), (uint)commit.DeltaPayload.Length);
        plainBuffer.Advance(4);
        plainBuffer.Write(commit.DeltaPayload.Span);
        var bookmarkSize = commit.Bookmark.GetBinarySize();
        BinaryPrimitives.WriteUInt32LittleEndian(plainBuffer.GetSpan(4), (uint)bookmarkSize);
        plainBuffer.Advance(4);
        var beforeBookmark = plainBuffer.WrittenCount;
        commit.Bookmark.WriteTo(plainBuffer);
        if (plainBuffer.WrittenCount - beforeBookmark != bookmarkSize)
        {
            throw new UnreachableException();
        }

        var bodyLength = SequenceSize + CommitLogCipher.Overhead + plainBuffer.WrittenCount;
        var totalLength = PrefixSize + bodyLength + CrcSize;
        var frame = destination.GetSpan(totalLength)[..totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bodyLength);
        var body = frame.Slice(PrefixSize, bodyLength);
        BinaryPrimitives.WriteInt64LittleEndian(body, commit.Sequence);
        cipher.Seal(plainBuffer.WrittenSpan, commit.Sequence, originUtf8, body[SequenceSize..]);
        BinaryPrimitives.WriteUInt32LittleEndian(frame[(PrefixSize + bodyLength)..], Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body));
        destination.Advance(totalLength);
    }

    public static bool TryDecode(ReadOnlySpan<byte> body, string expectedOrigin, CommitLogCipher cipher, out CommitMetadata commit)
    {
        var originLength = Encoding.UTF8.GetByteCount(expectedOrigin);
        byte[]? rentedOrigin = null;
        Span<byte> originBuffer = originLength <= StackOriginLimit
            ? stackalloc byte[StackOriginLimit]
            : (rentedOrigin = ArrayPool<byte>.Shared.Rent(originLength));
        try
        {
            var originUtf8 = originBuffer[..originLength];
            _ = Encoding.UTF8.GetBytes(expectedOrigin, originUtf8);
            return TryDecode(body, expectedOrigin, originUtf8, cipher, out commit);
        }
        finally
        {
            if (rentedOrigin is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedOrigin);
            }
        }
    }

    public static bool TryDecode(
        ReadOnlySpan<byte> body,
        string expectedOrigin,
        ReadOnlySpan<byte> expectedOriginUtf8,
        CommitLogCipher cipher,
        out CommitMetadata commit)
    {
        commit = Empty;
        if (body.Length < SequenceSize + CommitLogCipher.Overhead)
        {
            return false;
        }

        var sequence = BinaryPrimitives.ReadInt64LittleEndian(body);
        var sealedBytes = body[SequenceSize..];
        var plainLength = sealedBytes.Length - CommitLogCipher.Overhead;
        byte[]? rentedPlain = null;
        Span<byte> plainBuffer = plainLength <= StackPlainLimit
            ? stackalloc byte[StackPlainLimit]
            : (rentedPlain = ArrayPool<byte>.Shared.Rent(plainLength));
        try
        {
            var plain = plainBuffer[..plainLength];
            if (!cipher.TryOpen(sealedBytes, sequence, expectedOriginUtf8, plain))
            {
                return false;
            }

            var reader = new WireReader(plain);
            var txId = reader.Take(16);
            var originBytes = reader.Take(reader.UInt16());
            var hlc = reader.Hlc();
            var epoch = reader.Take(8);
            var mode = reader.Byte();
            var payloadLengthBytes = reader.Take(4);
            if (reader.Failed || !originBytes.SequenceEqual(expectedOriginUtf8))
            {
                return false;
            }

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(payloadLengthBytes);
            if (payloadLength > int.MaxValue)
            {
                return false;
            }

            var payloadSpan = reader.Take((int)payloadLength);
            var bookmarkLengthBytes = reader.Take(4);
            if (reader.Failed)
            {
                return false;
            }

            var bookmarkLength = BinaryPrimitives.ReadUInt32LittleEndian(bookmarkLengthBytes);
            if (bookmarkLength > int.MaxValue)
            {
                return false;
            }

            var bookmarkBytes = reader.Take((int)bookmarkLength);
            if (reader.Failed || !reader.End || !CausalBookmark.TryRead(ref bookmarkBytes, out var bookmark) || !bookmarkBytes.IsEmpty)
            {
                return false;
            }

            var payload = payloadSpan.IsEmpty ? ReadOnlyMemory<byte>.Empty : payloadSpan.ToArray();
            commit = new CommitMetadata(
                new Ulid(txId),
                expectedOrigin,
                sequence,
                hlc,
                BinaryPrimitives.ReadUInt64LittleEndian(epoch),
                mode == 1 ? ConsistencyMode.Strong : ConsistencyMode.Fast,
                bookmark,
                payload);
            return true;
        }
        finally
        {
            if (rentedPlain is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedPlain);
            }
        }
    }

    public static bool TryParseAll(ReadOnlySpan<byte> bytes, string origin, CommitLogCipher cipher, List<CommitMetadata> commits, long fromSequence = 0, int maxEntries = int.MaxValue)
    {
        var originLength = Encoding.UTF8.GetByteCount(origin);
        byte[]? rentedOrigin = null;
        Span<byte> originBuffer = originLength <= StackOriginLimit
            ? stackalloc byte[StackOriginLimit]
            : (rentedOrigin = ArrayPool<byte>.Shared.Rent(originLength));
        try
        {
            var originUtf8 = originBuffer[..originLength];
            _ = Encoding.UTF8.GetBytes(origin, originUtf8);
            return TryParseAll(bytes, origin, originUtf8, cipher, commits, fromSequence, maxEntries);
        }
        finally
        {
            if (rentedOrigin is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedOrigin);
            }
        }
    }

    public static bool TryParseAll(
        ReadOnlySpan<byte> bytes,
        string origin,
        ReadOnlySpan<byte> originUtf8,
        CommitLogCipher cipher,
        List<CommitMetadata> commits,
        long fromSequence = 0,
        int maxEntries = int.MaxValue)
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

            if (!TryDecode(body, origin, originUtf8, cipher, out var commit))
            {
                return false;
            }

            commits.Add(commit);
            decoded++;
        }

        return true;
    }

    public static bool TryDecodeChecked(ReadOnlySpan<byte> bodyAndCrc, string origin, CommitLogCipher cipher, out CommitMetadata commit)
    {
        commit = Empty;
        if (bodyAndCrc.Length < CrcSize)
        {
            return false;
        }

        var body = bodyAndCrc[..^CrcSize];
        return BinaryPrimitives.ReadUInt32LittleEndian(bodyAndCrc[^CrcSize..]) == Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body)
            && TryDecode(body, origin, cipher, out commit);
    }

    private static CommitMetadata Empty { get; } = new(Ulid.Empty, string.Empty, 0, HlcTimestamp.Zero, 0, ConsistencyMode.Fast, CausalBookmark.Empty, ReadOnlyMemory<byte>.Empty);
}

internal sealed class OriginLog : IDisposable
{
    private const string ActiveExtension = ".tblog";
    private const string SealedExtension = ".tbseg";
    private const int TailMaxCount = 256;
    private const long TailMaxPayloadBytes = 4L * 1024 * 1024;
    private const int InitialBufferCapacity = 4096;
    private const int MaxRetainedBufferBytes = 256 * 1024;

    private readonly Lock _gate = new();
    private readonly List<(long Offset, long Sequence)> _frames = [];
    private readonly List<(long Offset, long Sequence)> _addedOffsets = [];
    private readonly List<CommitMetadata> _pendingTail = [];
    private readonly CommitMetadata?[] _tail = new CommitMetadata?[TailMaxCount];
    private readonly List<(CommitLogSegment Segment, string Path)> _sealed;
    private readonly string _directory;
    private readonly string _prefix;
    private readonly byte[] _originUtf8;
    private readonly CommitLogCipher _cipher;
    private readonly CommitLogOptions _options;
    private readonly TimeProvider _time;
    private readonly Ulid _databaseId;
    private ArrayBufferWriter<byte> _frameBuffer = new(InitialBufferCapacity);
    private ArrayBufferWriter<byte> _plainBuffer = new(InitialBufferCapacity);
    private SafeFileHandle? _active;
    private long _length;
    private long _committedLength;
    private long _lastSequence;
    private long _committedSequence;
    private long _firstAvailable;
    private long _tailPayloadBytes;
    private int _committedFrameCount;
    private int _tailHead;
    private int _tailCount;
    private int _sealedReaders;
    private DateTimeOffset _activeSince;
    private DateTimeOffset? _activeFirstCommittedAtUtc;
    private DateTimeOffset? _activeLastCommittedAtUtc;
    private bool _uncommitted;
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
        _originUtf8 = Encoding.UTF8.GetBytes(origin);
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

    public SqliteStatus Append(IReadOnlyList<CommitMetadata> commits, out long previousLength) =>
        AppendCore(commits, sync: _options.Fsync, out previousLength);

    public SqliteStatus AppendNoSync(IReadOnlyList<CommitMetadata> commits, out long previousLength) =>
        AppendCore(commits, sync: false, out previousLength);

    public SqliteStatus Sync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_options.Fsync)
            {
                return SqliteStatus.Success;
            }

            if (_active is not { } active)
            {
                return new SqliteStatus(SqliteNative.IoError, "active commit log handle missing");
            }

            return FileIo.FlushData(active) is { } error
                ? new SqliteStatus(SqliteNative.IoError, error)
                : SqliteStatus.Success;
        }
    }

    private SqliteStatus AppendCore(IReadOnlyList<CommitMetadata> commits, bool sync, out long previousLength)
    {
        lock (_gate)
        {
            previousLength = _length;
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (commits.Count == 0)
            {
                return SqliteStatus.Success;
            }

            _frameBuffer.ResetWrittenCount();
            _addedOffsets.Clear();
            try
            {
                var originUtf8Length = _originUtf8.Length;
                foreach (var commit in commits)
                {
                    if (!string.Equals(commit.OriginRegion, Origin, StringComparison.Ordinal))
                    {
                        return SqliteStatus.Fail(string.Concat("commit log origin mismatch for origin ", Origin));
                    }

                    if (commit.Sequence != _lastSequence + 1 + _addedOffsets.Count)
                    {
                        return SqliteStatus.Fail(string.Concat("commit log sequence gap for origin ", Origin));
                    }

                    var frameSize = CommitFrame.EncodedSize(commit, originUtf8Length);
                    if (frameSize > CommitFrame.MaxEncodedSize)
                    {
                        return SqliteStatus.Fail("commit log frame exceeds the maximum frame size");
                    }

                    if (_frameBuffer.WrittenCount + frameSize > SqliteCommitLog.MaxSegmentBytes)
                    {
                        return SqliteStatus.Fail("commit log batch exceeds the maximum segment size");
                    }

                    _addedOffsets.Add((_frameBuffer.WrittenCount, commit.Sequence));
                    CommitFrame.EncodeInto(_frameBuffer, _plainBuffer, commit, _originUtf8, _cipher);
                }

                var writtenBytes = _frameBuffer.WrittenSpan;
                if (_frames.Count > 0 && _length + writtenBytes.Length > SqliteCommitLog.MaxSegmentBytes)
                {
                    SealIfDue(force: true);
                    if (_frames.Count > 0)
                    {
                        return new SqliteStatus(SqliteNative.IoError, "active commit log could not be sealed before append");
                    }
                }

                var offset = _length;
                previousLength = offset;
                if (EnsureActiveHandle() is not { } active)
                {
                    return new SqliteStatus(SqliteNative.IoError, "active commit log could not be opened");
                }

                if (FileIo.Write(active, writtenBytes, offset, sync) is { } error)
                {
                    return new SqliteStatus(SqliteNative.IoError, error);
                }

                if (_frames.Count == 0)
                {
                    _activeSince = _time.GetUtcNow();
                }

                foreach (var (frameOffset, sequence) in _addedOffsets)
                {
                    _frames.Add((offset + frameOffset, sequence));
                }

                foreach (var commit in commits)
                {
                    _pendingTail.Add(commit);
                }

                _length = offset + writtenBytes.Length;
                _lastSequence = _addedOffsets[^1].Sequence;
                _uncommitted = true;
                if (_firstAvailable > _lastSequence || _firstAvailable == 0)
                {
                    _firstAvailable = _addedOffsets[0].Sequence;
                }

                return SqliteStatus.Success;
            }
            finally
            {
                TrimBuffersIfOversized();
            }
        }
    }

    public void MarkCommitted()
    {
        lock (_gate)
        {
            _uncommitted = false;
            _committedLength = _length;
            _committedSequence = _lastSequence;
            _committedFrameCount = _frames.Count;
            foreach (var commit in _pendingTail)
            {
                RecordActiveCommitTime(commit.Hlc.WallTime);
                PushTail(commit);
            }

            _pendingTail.Clear();
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

    public bool Truncate(long length, long lastSequence)
    {
        lock (_gate)
        {
            _uncommitted = false;
            _pendingTail.Clear();
            if (_disposed || length > _length)
            {
                return !_disposed;
            }

            _frames.RemoveAll(frame => frame.Offset >= length);
            var truncated = _active is { } active && FileIo.SetLength(active, length) is null;
            _length = length;
            _lastSequence = lastSequence;
            if (_committedLength > length)
            {
                _committedLength = length;
            }

            if (_committedSequence > lastSequence)
            {
                _committedSequence = lastSequence;
                TruncateTailAbove(lastSequence);
            }

            if (_committedFrameCount > _frames.Count)
            {
                _committedFrameCount = _frames.Count;
            }

            if (_frames.Count == 0)
            {
                _activeFirstCommittedAtUtc = null;
                _activeLastCommittedAtUtc = null;
            }

            return truncated;
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
        if (maxEntries <= 0)
        {
            IReadOnlyList<CommitMetadata> empty = [];
            return empty;
        }

        List<(CommitLogSegment Segment, string Path)>? segments;
        int sealedSnapshotCount;
        long committed;
        lock (_gate)
        {
            var firstAvailable = _firstAvailable;
            committed = _committedSequence;
            if (fromSequence < firstAvailable && fromSequence <= committed)
            {
                return new CommitLogGap(_databaseId, Origin, fromSequence, firstAvailable);
            }

            if (fromSequence > committed)
            {
                IReadOnlyList<CommitMetadata> empty = [];
                return empty;
            }

            if (TryReadFromTailLocked(fromSequence, maxEntries) is { } tailHits)
            {
                return tailHits;
            }

            sealedSnapshotCount = _sealed.Count;
            segments = SnapshotSealedFromLocked(fromSequence, 0);
        }

        List<CommitMetadata> entries = [];
        if (segments is not null && ReadSealed(segments, fromSequence, maxEntries, entries) is { } unreadable)
        {
            return unreadable;
        }

        if (entries.Count < maxEntries && committed >= fromSequence)
        {
            var nextSequence = entries.Count > 0 ? entries[^1].Sequence + 1 : fromSequence;
            var remaining = maxEntries - entries.Count;
            byte[]? rented = null;
            var byteCount = 0;
            List<(CommitLogSegment Segment, string Path)>? late = null;
            IReadOnlyList<CommitMetadata>? tailSuffix = null;
            var activeError = false;

            lock (_gate)
            {
                if (_sealed.Count > sealedSnapshotCount)
                {
                    late = SnapshotSealedFromLocked(nextSequence, sealedSnapshotCount);
                }

                if (late is null && TryReadFromTailLocked(nextSequence, remaining) is { } tailSlice)
                {
                    tailSuffix = tailSlice;
                }
                else
                {
                    var activeFrom = late is { Count: > 0 }
                        ? Math.Max(nextSequence, late[^1].Segment.LastSequence + 1)
                        : nextSequence;
                    var first = FindFirstFrameAtOrAfter(activeFrom, _committedFrameCount);
                    if (first < _committedFrameCount)
                    {
                        var startOffset = _frames[first].Offset;
                        var availableFrames = _committedFrameCount - first;
                        var endFrame = remaining >= availableFrames ? _committedFrameCount : first + remaining;
                        var endOffset = endFrame < _committedFrameCount ? _frames[endFrame].Offset : _committedLength;
                        byteCount = (int)(endOffset - startOffset);
                        if (byteCount > 0)
                        {
                            rented = ArrayPool<byte>.Shared.Rent(byteCount);
                            if (_active is not { } active || FileIo.Read(active, rented.AsSpan(0, byteCount), startOffset) is not null)
                            {
                                activeError = true;
                            }
                        }
                    }
                }
            }

            try
            {
                if (activeError)
                {
                    return new StorageCorrupted(_databaseId, "active commit log unreadable");
                }

                if (tailSuffix is not null)
                {
                    entries.AddRange(tailSuffix);
                }
                else
                {
                    if (late is not null && ReadSealed(late, nextSequence, maxEntries, entries) is { } lateUnreadable)
                    {
                        return lateUnreadable;
                    }

                    if (entries.Count < maxEntries && byteCount > 0 && rented is not null)
                    {
                        var parseFrom = entries.Count > 0 ? entries[^1].Sequence + 1 : nextSequence;
                        if (!CommitFrame.TryParseAll(rented.AsSpan(0, byteCount), Origin, _originUtf8, _cipher, entries, parseFrom, maxEntries - entries.Count))
                        {
                            return new StorageCorrupted(_databaseId, "active commit log failed verification");
                        }
                    }
                }
            }
            finally
            {
                if (rented is not null)
                {
                    ArrayPool<byte>.Shared.Return(rented);
                }
            }
        }

        IReadOnlyList<CommitMetadata> page = entries;
        return page;
    }

    private StorageCorrupted? ReadSealed(List<(CommitLogSegment Segment, string Path)> segments, long fromSequence, int maxEntries, List<CommitMetadata> entries)
    {
        foreach (var (segment, path) in segments)
        {
            if (entries.Count >= maxEntries)
            {
                break;
            }

            if (segment.LastSequence < fromSequence)
            {
                continue;
            }

            if (FileIo.ReadAll(path) is not { } bytes || !CommitFrame.TryParseAll(bytes, Origin, _originUtf8, _cipher, entries, fromSequence, maxEntries - entries.Count))
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
            if (afterSequence == long.MaxValue)
            {
                return [];
            }

            var start = FindFirstSealedAtOrAfter(afterSequence + 1, 0);
            if (start >= _sealed.Count)
            {
                return [];
            }

            var result = new CommitLogSegment[_sealed.Count - start];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = _sealed[start + index].Segment;
            }

            return result;
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
            var index = FindSealedByFirstSequence(segment.FirstSequence);
            if (index >= 0)
            {
                var (candidate, path) = _sealed[index];
                if (candidate.DatabaseId == segment.DatabaseId
                    && string.Equals(candidate.OriginRegion, segment.OriginRegion, StringComparison.Ordinal)
                    && candidate.FirstSequence == segment.FirstSequence
                    && candidate.LastSequence == segment.LastSequence
                    && candidate.SizeBytes == segment.SizeBytes)
                {
                    return FileIo.OpenRead(path) is { } content
                        ? content
                        : new StorageBusy("commit log segment could not be opened", TimeSpan.FromMilliseconds(50));
                }
            }

            return new SegmentNotFound(segment.DatabaseId, segment.OriginRegion, segment.FirstSequence, segment.LastSequence);
        }
    }

    public long PrunableThrough(DateTimeOffset cutoff, long through)
    {
        lock (_gate)
        {
            if (_disposed || _sealedReaders > 0)
            {
                return 0;
            }

            var last = 0L;
            foreach (var (segment, _) in _sealed)
            {
                if (!CanPrune(segment, cutoff, through))
                {
                    break;
                }

                last = segment.LastSequence;
            }

            return last;
        }
    }

    public StorageBusy? PruneSealed(DateTimeOffset cutoff, long through)
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
                if (!CanPrune(segment, cutoff, through) || (failure = FileIo.DeleteFile(path)) is not null)
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
                EvictTailBelow(_firstAvailable);
                FileIo.SyncDirectory(_directory);
            }

            return failure is null ? null : new StorageBusy(failure, TimeSpan.FromMilliseconds(50));
        }
    }

    private static bool CanPrune(CommitLogSegment segment, DateTimeOffset cutoff, long through) =>
        segment.SealedAtUtc < cutoff && segment.LastSequence <= through;

    public bool TryParse(ReadOnlySpan<byte> bytes, List<CommitMetadata> commits) => CommitFrame.TryParseAll(bytes, Origin, _originUtf8, _cipher, commits);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            ClearTail();
            _pendingTail.Clear();
            _plainBuffer.Clear();
            _frameBuffer.ResetWrittenCount();
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
        if (_active is null || _uncommitted || _frames.Count == 0 || (!force && _length < _options.SegmentMaxBytes && _time.GetUtcNow() - _activeSince < _options.SegmentMaxAge))
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
            CompleteSealLocked(first, sealedPath);
            return;
        }

        _active.Dispose();
        _active = fresh;
        CompleteSealLocked(first, sealedPath);
    }

    private void CompleteSealLocked(long first, string sealedPath)
    {
        var sealedAt = _time.GetUtcNow();
        var segment = new CommitLogSegment(_databaseId, Origin, first, _lastSequence, _length, sealedAt)
        {
            FirstCommittedAtUtc = _activeFirstCommittedAtUtc,
            LastCommittedAtUtc = _activeLastCommittedAtUtc,
        };
        _sealed.Add((segment, sealedPath));
        _frames.Clear();
        _length = 0;
        _committedLength = 0;
        _committedFrameCount = 0;
        _activeSince = sealedAt;
        _activeFirstCommittedAtUtc = null;
        _activeLastCommittedAtUtc = null;
    }

    private string? Recover(long baseSequence, long committedSequence)
    {
        _lastSequence = baseSequence;
        _committedSequence = baseSequence;
        if (_active is null)
        {
            return "active commit log handle missing";
        }

        var fileLength = RandomAccess.GetLength(_active);
        if (fileLength == 0)
        {
            return null;
        }

        if (fileLength > int.MaxValue)
        {
            return "active commit log exceeds maximum supported length";
        }

        var rented = ArrayPool<byte>.Shared.Rent((int)fileLength);
        try
        {
            var span = rented.AsSpan(0, (int)fileLength);
            if (FileIo.Read(_active, span, 0) is { } readError)
            {
                return readError;
            }

            long offset = 0;
            var expected = baseSequence + 1;
            var first = true;
            var verifiedCommitted = committedSequence == 0 || (_sealed.Count > 0 && _sealed[^1].Segment.LastSequence >= committedSequence);
            while (offset + CommitFrame.PrefixSize <= fileLength)
            {
                var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)offset..]);
                var end = offset + CommitFrame.PrefixSize + bodyLength + CommitFrame.CrcSize;
                if (bodyLength > CommitFrame.MaxBodySize || end > fileLength)
                {
                    if (verifiedCommitted)
                    {
                        break;
                    }

                    return "commit log frame truncated or corrupt before committed sequence";
                }

                var body = span.Slice((int)offset + CommitFrame.PrefixSize, (int)bodyLength);
                var crc = BinaryPrimitives.ReadUInt32LittleEndian(span[(int)(end - CommitFrame.CrcSize)..]);
                if (Crc32.HashToUInt32(Crc32ParameterSet.Crc32C, body) != crc)
                {
                    if (verifiedCommitted)
                    {
                        break;
                    }

                    return "commit log frame failed CRC32C verification";
                }

                if (!CommitFrame.TryDecode(body, Origin, _originUtf8, _cipher, out var commit))
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
                RecordActiveCommitTime(commit.Hlc.WallTime);
                PushTail(commit);
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
            _committedLength = offset;
            _committedSequence = _lastSequence;
            _committedFrameCount = _frames.Count;
            if (offset != fileLength)
            {
                _ = FileIo.SetLength(_active, offset);
            }

            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private void RecordActiveCommitTime(DateTimeOffset committedAt)
    {
        _activeFirstCommittedAtUtc = _activeFirstCommittedAtUtc is { } first && first <= committedAt ? first : committedAt;
        _activeLastCommittedAtUtc = _activeLastCommittedAtUtc is { } last && last >= committedAt ? last : committedAt;
    }

    private void PushTail(CommitMetadata commit)
    {
        var payloadBytes = commit.DeltaPayload.Length;
        if (payloadBytes > TailMaxPayloadBytes)
        {
            ClearTail();
            return;
        }

        if (_tailCount > 0)
        {
            var newestIndex = (_tailHead + _tailCount - 1) % TailMaxCount;
            if (_tail[newestIndex] is not { } newest || commit.Sequence != newest.Sequence + 1)
            {
                ClearTail();
            }
        }

        while (_tailCount > 0 && (_tailCount >= TailMaxCount || _tailPayloadBytes + payloadBytes > TailMaxPayloadBytes))
        {
            PopOldestTail();
        }

        var slot = (_tailHead + _tailCount) % TailMaxCount;
        _tail[slot] = commit;
        _tailCount++;
        _tailPayloadBytes += payloadBytes;
    }

    private void PopOldestTail()
    {
        if (_tail[_tailHead] is { } oldest)
        {
            _tailPayloadBytes -= oldest.DeltaPayload.Length;
            _tail[_tailHead] = null;
        }

        _tailHead = (_tailHead + 1) % TailMaxCount;
        _tailCount--;
        if (_tailCount == 0)
        {
            _tailHead = 0;
            _tailPayloadBytes = 0;
        }
    }

    private void EvictTailBelow(long firstAvailable)
    {
        while (_tailCount > 0 && _tail[_tailHead] is { } oldest && oldest.Sequence < firstAvailable)
        {
            PopOldestTail();
        }
    }

    private void TruncateTailAbove(long lastSequence)
    {
        while (_tailCount > 0)
        {
            var newestIndex = (_tailHead + _tailCount - 1) % TailMaxCount;
            if (_tail[newestIndex] is not { } newest || newest.Sequence <= lastSequence)
            {
                break;
            }

            _tailPayloadBytes -= newest.DeltaPayload.Length;
            _tail[newestIndex] = null;
            _tailCount--;
        }

        if (_tailCount == 0)
        {
            _tailHead = 0;
            _tailPayloadBytes = 0;
        }
    }

    private void ClearTail()
    {
        Array.Clear(_tail);
        _tailHead = 0;
        _tailCount = 0;
        _tailPayloadBytes = 0;
    }

    private CommitMetadata[]? TryReadFromTailLocked(long fromSequence, int maxEntries)
    {
        if (_tailCount == 0 || _tail[_tailHead] is not { } oldest)
        {
            return null;
        }

        var newestIndex = (_tailHead + _tailCount - 1) % TailMaxCount;
        if (_tail[newestIndex] is not { } newest || fromSequence < oldest.Sequence || fromSequence > newest.Sequence)
        {
            return null;
        }

        var startOffset = (int)(fromSequence - oldest.Sequence);
        var available = _tailCount - startOffset;
        var count = Math.Min(maxEntries, available);
        var result = new CommitMetadata[count];
        for (var index = 0; index < count; index++)
        {
            var slot = (_tailHead + startOffset + index) % TailMaxCount;
            if (_tail[slot] is not { } commit)
            {
                return null;
            }

            result[index] = commit;
        }

        return result;
    }

    private List<(CommitLogSegment Segment, string Path)>? SnapshotSealedFromLocked(long fromSequence, int minIndex)
    {
        var start = FindFirstSealedAtOrAfter(fromSequence, minIndex);
        if (start >= _sealed.Count)
        {
            return null;
        }

        var count = _sealed.Count - start;
        var slice = new List<(CommitLogSegment Segment, string Path)>(count);
        for (var index = start; index < _sealed.Count; index++)
        {
            slice.Add(_sealed[index]);
        }

        return slice;
    }

    private int FindFirstSealedAtOrAfter(long fromSequence, int minIndex)
    {
        var low = minIndex;
        var high = _sealed.Count;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_sealed[mid].Segment.LastSequence < fromSequence)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int FindSealedByFirstSequence(long firstSequence)
    {
        var low = 0;
        var high = _sealed.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var candidate = _sealed[mid].Segment.FirstSequence;
            if (candidate == firstSequence)
            {
                return mid;
            }

            if (candidate < firstSequence)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return -1;
    }

    private int FindFirstFrameAtOrAfter(long fromSequence, int limit)
    {
        var low = 0;
        var high = limit;
        while (low < high)
        {
            var mid = low + ((high - low) / 2);
            if (_frames[mid].Sequence < fromSequence)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private void TrimBuffersIfOversized()
    {
        _frameBuffer.ResetWrittenCount();
        if (_frameBuffer.Capacity > MaxRetainedBufferBytes)
        {
            _frameBuffer = new ArrayBufferWriter<byte>(InitialBufferCapacity);
        }

        if (_plainBuffer.Capacity > MaxRetainedBufferBytes)
        {
            _plainBuffer.Clear();
            _plainBuffer = new ArrayBufferWriter<byte>(InitialBufferCapacity);
        }
        else
        {
            _plainBuffer.ResetWrittenCount();
        }
    }
}

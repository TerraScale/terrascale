using System.Buffers;
using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Reads the committed frames of one database's write-ahead log (<c>&lt;file&gt;-wal</c>, the SQLite WAL format) for
/// continuous backup (TS-633, <c>docs/backup.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// While backup ships from this node, sessions run with <c>wal_autocheckpoint = 0</c> and only a tap checkpoints: it
/// holds the store's writer gate, reads every committed frame, then runs a passive checkpoint. The log restarts (new
/// salts, checkpoint sequence + 1) only after a checkpoint copied every frame back, so a restart that follows the tap's
/// own complete checkpoint, with no frame appended since, loses nothing. Anything else means another connection or
/// process checkpointed or rewrote the log, as Litestream checks ("wal overwritten/truncated by another process"): a
/// restart without that checkpoint or by more than one sequence, a log that vanished or shrank below what was read, or a
/// last read frame whose checksum changed. The read then reports a hole. <see cref="_keepAlive"/> keeps a connection
/// open, so the last connection never closes and SQLite never checkpoints and deletes the log on its own.
/// </para>
/// <para>
/// Frames are validated as SQLite recovers them: salts equal to the header's and a cumulative checksum, and only up to the
/// last commit frame. The log is read with ordinary file reads; SQLite keeps no POSIX lock on it (WAL locks live in
/// <c>-shm</c>), so opening and closing it here releases none of SQLite's locks.
/// </para>
/// </remarks>
internal sealed class SqliteWalTap(SqliteDatabaseStore store, SqliteIoPool io, SqliteSession keepAlive, TimeProvider time) : IWalTap
{
    private const int HeaderBytes = 32;
    private const int FrameHeaderBytes = 24;
    private const uint Magic = 0x377f0682;
    private const uint FormatVersion = 3007000;

    // SQLite's default automatic checkpoint, which the tap takes over.
    private const long CheckpointFrames = 1000;

    private readonly string _log = store.FilePath + "-wal";
    private readonly SqliteSession _keepAlive = keepAlive;

    // The log generation (between restarts) being read, and where its committed frames end.
    private Header? _header;
    private long _offset;
    private uint _sum1;
    private uint _sum2;

    // Whether the tap's last checkpoint copied every frame back and no frame was read since: only then may the log restart.
    private bool _checkpointed;

    // Instance-local barrier for integration tests of writes while the pinned snapshot is copied.
    internal Func<CancellationToken, Task>? AfterSnapshotPinned { get; set; }

    public async ValueTask<WalRead> ReadAsync(Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var read = await AdmittedAsync(() => ReadCoreAsync(destination, cancellationToken)).ConfigureAwait(false);
        if (_header is not { } header || (_offset - HeaderBytes) / (FrameHeaderBytes + header.PageSize) < CheckpointFrames)
        {
            return read;
        }

        // Every frame read so far is in destination; read the rest under the writer gate, then copy them all back.
        var rest = await AdmittedAsync(() => store.WithWritersHeldAsync(async () =>
        {
            var tail = await ReadCoreAsync(destination, cancellationToken).ConfigureAwait(false);
            _checkpointed = await _keepAlive.ExecuteAsync(io, () =>
            {
                using var command = _keepAlive.Connection.CreateCommand();
                command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
                using var result = command.ExecuteReader();
                // busy, frames in the log, frames copied back.
                return result.Read() && result.GetInt64(0) == 0 && result.GetInt64(1) >= 0 && result.GetInt64(1) == result.GetInt64(2);
            }, cancellationToken).ConfigureAwait(false);
            return tail;
        }, cancellationToken)).ConfigureAwait(false);
        return new(read.Frames + rest.Frames, read.Continuous && rest.Continuous);
    }

    public async ValueTask<WalSnapshot> SnapshotAsync(Stream destination, string snapshotPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        return await AdmittedAsync(async () =>
        {
            var reader = await store.RentSessionAsync(cancellationToken).ConfigureAwait(false);
            SqliteTransaction? snapshot = null;
            var failed = true;
            try
            {
                // No writer runs while the frames are read and the read transaction starts, so it sees exactly them.
                var read = await store.WithWritersHeldAsync(async () =>
                {
                    var frames = await ReadCoreAsync(destination, cancellationToken).ConfigureAwait(false);
                    var at = await io.RunAsync(() =>
                    {
                        var transaction = reader.Connection.BeginTransaction(deferred: true);
                        using var command = reader.Connection.CreateCommand();
                        command.Transaction = transaction;
                        // Any read starts the transaction's snapshot of the log.
                        command.CommandText = "PRAGMA schema_version;";
                        command.ExecuteScalar();
                        snapshot = transaction;
                        return time.GetUtcNow();
                    }, cancellationToken).ConfigureAwait(false);
                    return new WalSnapshot(frames, at);
                }, cancellationToken).ConfigureAwait(false);

                if (AfterSnapshotPinned is { } pinned)
                {
                    await pinned(cancellationToken).ConfigureAwait(false);
                }

                // The online backup API copies page for page within the open read transaction, so frames apply to the copy.
                await io.RunAsync(() =>
                {
                    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = snapshotPath,
                        Mode = SqliteOpenMode.ReadWriteCreate,
                        Pooling = false,
                    }.ToString());
                    reader.Connection.BackupDatabase(target);
                }, cancellationToken).ConfigureAwait(false);
                failed = false;
                return read;
            }
            finally
            {
                if (snapshot is not null)
                {
                    await io.RunAsync(snapshot.Dispose, CancellationToken.None).ConfigureAwait(false);
                }

                if (failed)
                {
                    await io.RunAsync(() => store.Discard(reader), CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await store.ReturnSessionAsync(reader).ConfigureAwait(false);
                }
            }
        }).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync() => await store.ReturnSessionAsync(_keepAlive).ConfigureAwait(false);

    private async Task<T> AdmittedAsync<T>(Func<Task<T>> call)
    {
        io.Admit();
        try
        {
            return await call().ConfigureAwait(false);
        }
        finally
        {
            io.Release();
        }
    }

    // Appends the frames committed after _offset and moves _offset past them.
    private async Task<WalRead> ReadCoreAsync(Stream destination, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(store.Closed, store);
        SafeFileHandle log;
        try
        {
            log = File.OpenHandle(_log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            // No log yet: nothing was written since the file was created or last checkpointed on close. A log that vanished
            // after frames were read was checkpointed and deleted by someone else.
            return Hole(_header is null);
        }

        using (log)
        {
            var length = RandomAccess.GetLength(log);
            if (_header is not null && length < _offset)
            {
                // Truncated below what was read (a TRUNCATE checkpoint): the frames after the last read may be gone.
                return Hole(false);
            }

            var headerBytes = new byte[HeaderBytes];
            if (length < HeaderBytes
                || await RandomAccess.ReadAsync(log, headerBytes, 0, cancellationToken).ConfigureAwait(false) < HeaderBytes
                || Header.Parse(headerBytes) is not { } header)
            {
                return new(0, true);
            }

            var continuous = true;
            var frameBytes = FrameHeaderBytes + header.PageSize;
            if (_header is not { } current)
            {
                Start(header);
            }
            else if (header.Salt1 != current.Salt1 || header.Salt2 != current.Salt2 || header.Sequence != current.Sequence)
            {
                continuous = _checkpointed && header.Sequence == unchecked(current.Sequence + 1);
                Start(header);
            }
            else if (_offset > HeaderBytes && !await LastFrameMatchesAsync(log, frameBytes, cancellationToken).ConfigureAwait(false))
            {
                // Same salts, but what was read is gone or different: start over on this log.
                continuous = false;
                Start(header);
            }

            var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
            try
            {
                var frame = buffer.AsMemory(0, frameBytes);
                // First pass: validate up to the last commit frame. Committed frames cannot change until the next checkpoint.
                var (end, sum1, sum2) = (_offset, _sum1, _sum2);
                var (position, s1, s2) = (_offset, _sum1, _sum2);
                while (position + frameBytes <= length
                    && await RandomAccess.ReadAsync(log, frame, position, cancellationToken).ConfigureAwait(false) == frameBytes)
                {
                    var bytes = frame.Span;
                    if (BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]) != header.Salt1 || BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]) != header.Salt2)
                    {
                        break;
                    }

                    Checksum(bytes[..8], header.BigEndian, ref s1, ref s2);
                    Checksum(bytes[FrameHeaderBytes..], header.BigEndian, ref s1, ref s2);
                    if (s1 != BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]) || s2 != BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]))
                    {
                        break;
                    }

                    position += frameBytes;
                    if (BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != 0)
                    {
                        (end, sum1, sum2) = (position, s1, s2);
                    }
                }

                // Second pass: append the committed frames.
                for (position = _offset; position < end; position += frameBytes)
                {
                    if (await RandomAccess.ReadAsync(log, frame, position, cancellationToken).ConfigureAwait(false) != frameBytes)
                    {
                        throw new IOException("the write-ahead log shrank while it was read");
                    }

                    await destination.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                }

                var frames = (end - _offset) / frameBytes;
                (_offset, _sum1, _sum2) = (end, sum1, sum2);
                _checkpointed &= frames == 0;
                return new(frames, continuous);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void Start(Header header)
    {
        _header = header;
        (_offset, _sum1, _sum2) = (HeaderBytes, header.Sum1, header.Sum2);
        _checkpointed = false;
    }

    // Forgets the log read so far; the next read starts from whatever log it finds.
    private WalRead Hole(bool continuous)
    {
        _header = null;
        _checkpointed = false;
        return new(0, continuous);
    }

    // The last frame read still carries the cumulative checksum it had: nothing rewrote the log under the same salts.
    private async Task<bool> LastFrameMatchesAsync(SafeFileHandle log, int frameBytes, CancellationToken cancellationToken)
    {
        var checksum = new byte[8];
        return await RandomAccess.ReadAsync(log, checksum, _offset - frameBytes + 16, cancellationToken).ConfigureAwait(false) == checksum.Length
            && BinaryPrimitives.ReadUInt32BigEndian(checksum) == _sum1 && BinaryPrimitives.ReadUInt32BigEndian(checksum.AsSpan(4)) == _sum2;
    }

    // SQLite's log checksum: pairs of 32-bit words, big-endian when the magic's low bit is set.
    private static void Checksum(ReadOnlySpan<byte> data, bool bigEndian, ref uint s1, ref uint s2)
    {
        unchecked
        {
            for (var i = 0; i < data.Length; i += 8)
            {
                var x0 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[i..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[i..]);
                var x1 = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(i + 4)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(i + 4)..]);
                s1 += x0 + s2;
                s2 += x1 + s1;
            }
        }
    }

    /// <summary>Rolls a database file forward through log frames as a checkpoint would; <see langword="false"/> on malformed frames.</summary>
    internal static async Task<bool> ApplyAsync(string file, IAsyncEnumerable<Stream> segments, CancellationToken cancellationToken)
    {
        using var database = File.OpenHandle(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
        var first = new byte[100];
        if (await RandomAccess.ReadAsync(database, first, 0, cancellationToken).ConfigureAwait(false) < first.Length)
        {
            return false;
        }

        // The page size at offset 16; 1 means 65536.
        var size = BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(16));
        var pageSize = size == 1 ? 65536 : size;
        if (pageSize < 512 || !int.IsPow2(pageSize))
        {
            return false;
        }

        var frameBytes = FrameHeaderBytes + pageSize;
        var buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
        try
        {
            var frame = buffer.AsMemory(0, frameBytes);
            uint? pages = null;
            // Pages the file holds now; a commit that shrinks the database cuts it there, as a checkpoint at that commit would.
            var held = RandomAccess.GetLength(database) / pageSize;
            await foreach (var segment in segments.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await using (segment.ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // Avoid allocating an empty segment's FileStream buffer just to read EOF.
                    if (segment.CanSeek && segment.Position == segment.Length)
                    {
                        continue;
                    }

                    int read;
                    while ((read = await segment.ReadAtLeastAsync(frame, frameBytes, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false)) == frameBytes)
                    {
                        var page = BinaryPrimitives.ReadUInt32BigEndian(frame.Span);
                        if (page == 0)
                        {
                            return false;
                        }

                        await RandomAccess.WriteAsync(database, frame[FrameHeaderBytes..], (page - 1L) * pageSize, cancellationToken).ConfigureAwait(false);
                        held = Math.Max(held, page);
                        if (BinaryPrimitives.ReadUInt32BigEndian(frame.Span[4..]) is var commit and not 0)
                        {
                            pages = commit;
                            if (commit < held)
                            {
                                RandomAccess.SetLength(database, commit * (long)pageSize);
                                held = commit;
                            }
                        }
                    }

                    if (read != 0)
                    {
                        return false;
                    }
                }
            }

            if (pages is { } count)
            {
                RandomAccess.SetLength(database, count * (long)pageSize);
            }

            RandomAccess.FlushToDisk(database);
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private readonly record struct Header(bool BigEndian, int PageSize, uint Sequence, uint Salt1, uint Salt2, uint Sum1, uint Sum2)
    {
        public static Header? Parse(ReadOnlySpan<byte> bytes)
        {
            var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes);
            var pageSize = BinaryPrimitives.ReadUInt32BigEndian(bytes[8..]);
            if ((magic & ~1u) != Magic || BinaryPrimitives.ReadUInt32BigEndian(bytes[4..]) != FormatVersion
                || pageSize is < 512 or > 65536 || !uint.IsPow2(pageSize))
            {
                return null;
            }

            var bigEndian = (magic & 1) == 1;
            uint s1 = 0, s2 = 0;
            Checksum(bytes[..24], bigEndian, ref s1, ref s2);
            return s1 == BinaryPrimitives.ReadUInt32BigEndian(bytes[24..]) && s2 == BinaryPrimitives.ReadUInt32BigEndian(bytes[28..])
                ? new(bigEndian, (int)pageSize, BinaryPrimitives.ReadUInt32BigEndian(bytes[12..]),
                    BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]), BinaryPrimitives.ReadUInt32BigEndian(bytes[20..]), s1, s2)
                : null;
        }
    }
}

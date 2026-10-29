using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Turso.Data.Sqlite;
using TerraBase.Engine.Revisions;

namespace TerraBase.Engine.Turso;

internal sealed class TursoRevisionLedger(string path)
{
    internal const ulong CheckpointFrames = 1024;
    private const int FrameBatch = 1024;
    private const int PullAttempts = 3;

    private readonly Lock _gate = new();
    private RevisionManifest? _manifest;
    private LiveGeneration _live = new();
    private SqliteConnection? _attached;
    private string? _fenced;

    internal bool CheckpointDue
    {
        get
        {
            lock (_gate)
            {
                return _attached is not null && _live.MaxFrame - _live.BaseFrame >= CheckpointFrames;
            }
        }
    }

    internal DatabaseRevision? Current
    {
        get
        {
            lock (_gate)
            {
                return _attached is null || _manifest is null ? null : _manifest.Current(_live.MaxFrame);
            }
        }
    }

    internal bool IsAttachedTo(SqliteConnection writer)
    {
        lock (_gate)
        {
            return ReferenceEquals(_attached, writer);
        }
    }

    internal void Attach(SqliteConnection writer)
    {
        lock (_gate)
        {
            ThrowIfFenced();
            _attached = null;
            if (_manifest is null)
            {
                try
                {
                    _manifest = RevisionManifestFile.Load(path);
                }
                catch (InvalidDataException)
                {
                    _manifest = null;
                }

                _manifest ??= RevisionManifest.NewIncarnation();
            }

            var full = (_manifest.WalSalt is not null && _manifest.WalSalt != ReadWalSalt(path + "-wal"))
                || (_manifest.DatabaseFile is { } known && known != Stamp(path));
            Snapshot(writer, () =>
            {
                var state = writer.GetWalState();
                _live = LiveGeneration.After(0, state.CheckpointSequence, PageCount(writer));
                _live.Full = full;
                Index(writer, state.MaxFrame);
            });
            _attached = writer;
            CheckpointLocked(writer);
            _manifest.DatabaseFile = Stamp(path);
            Store();
        }
    }

    internal void Detach()
    {
        lock (_gate)
        {
            _attached = null;
        }
    }

    internal void StartIncarnation()
    {
        lock (_gate)
        {
            _attached = null;
            _fenced = null;
            _manifest = RevisionManifest.NewIncarnation();
            Store();
        }
    }

    internal void Catch(SqliteConnection writer)
    {
        if (!IsAttachedTo(writer))
        {
            Attach(writer);
            return;
        }

        lock (_gate)
        {
            ThrowIfFenced();
            Refresh(writer);
        }
    }

    internal void AfterWrite(SqliteConnection writer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_attached, writer))
            {
                return;
            }

            Refresh(writer);
            if (_live.MaxFrame - _live.BaseFrame >= CheckpointFrames)
            {
                CheckpointLocked(writer);
            }
        }
    }

    internal bool Checkpoint(SqliteConnection writer)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_attached, writer))
            {
                return false;
            }

            return CheckpointLocked(writer);
        }
    }

    internal RevisionPullResult? Pull(SqliteConnection reader, string? clientRevision, Action<uint, ReadOnlySpan<byte>> page)
    {
        for (var attempt = 0; attempt < PullAttempts; attempt++)
        {
            Execute(reader, "BEGIN; SELECT count(*) FROM sqlite_schema;");
            try
            {
                var snapshot = reader.GetWalState();
                var size = PageCount(reader);
                RevisionPlan plan;
                DatabaseRevision revision;
                lock (_gate)
                {
                    ThrowIfFenced();
                    if (_attached is null || _manifest is null)
                    {
                        return null;
                    }

                    if (snapshot.CheckpointSequence != _live.CheckpointSequence
                        || snapshot.MaxFrame > _live.MaxFrame
                        || snapshot.MaxFrame < _live.BaseFrame)
                    {
                        continue;
                    }

                    plan = RevisionPlanner.Plan(_manifest, _live, clientRevision);
                    revision = _manifest.Current(snapshot.MaxFrame);
                }

                uint[] pages = plan.Full is null
                    ? [.. plan.Pages.Where(number => number <= size).Order()]
                    : [.. Enumerable.Range(1, (int)size).Select(number => (uint)number)];
                var buffer = ArrayPool<byte>.Shared.Rent(reader.PageSize);
                try
                {
                    var span = buffer.AsSpan(0, reader.PageSize);
                    foreach (var number in pages)
                    {
                        if (!reader.TryReadSnapshotPage(number, span))
                        {
                            throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"page {number} is missing at frame {revision.Frame}"));
                        }

                        page(number, span);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                return new RevisionPullResult(revision, plan.Full, pages.Length, size);
            }
            finally
            {
                Execute(reader, "ROLLBACK;");
            }
        }

        throw new TimeoutException("the database's revision moved during every pull attempt");
    }

    private bool CheckpointLocked(SqliteConnection writer)
    {
        ThrowIfFenced();
        var manifest = _manifest!;
        Refresh(writer);
        if (_live.MaxFrame == 0 && !_live.Full)
        {
            return true;
        }

        var end = _live.MaxFrame;
        manifest.Seal(_live);
        Store();
        try
        {
            Execute(writer, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
        {
        }

        var (state, size) = Snapshot(writer, () => (writer.GetWalState(), PageCount(writer)));
        var truncated = state.MaxFrame == 0;
        _live = truncated
            ? LiveGeneration.After(0, state.CheckpointSequence, size)
            : LiveGeneration.After(end, state.CheckpointSequence, _live.DatabaseSize);
        manifest.DatabaseFile = Stamp(path);
        Store();
        return truncated;
    }

    private void Refresh(SqliteConnection writer)
    {
        var manifest = _manifest!;
        var restarted = Snapshot(writer, () =>
        {
            var state = writer.GetWalState();
            if (state.CheckpointSequence == _live.CheckpointSequence && state.MaxFrame >= _live.MaxFrame)
            {
                Index(writer, state.MaxFrame);
                return false;
            }

            _live.Full = true;
            manifest.Seal(_live);
            _live = LiveGeneration.After(0, state.CheckpointSequence, PageCount(writer));
            Index(writer, state.MaxFrame);
            return true;
        });
        if (restarted)
        {
            Store();
        }

        if (_live.MaxFrame > 0 && manifest.WalSalt is null)
        {
            manifest.WalSalt = ReadWalSalt(path + "-wal");
            Store();
        }
    }

    private void Index(SqliteConnection writer, ulong upTo)
    {
        if (upTo <= _live.MaxFrame)
        {
            return;
        }

        var pages = ArrayPool<uint>.Shared.Rent(FrameBatch);
        var sizes = ArrayPool<uint>.Shared.Rent(FrameBatch);
        try
        {
            for (var first = _live.MaxFrame + 1; first <= upTo; first += FrameBatch)
            {
                var count = (int)Math.Min(FrameBatch, upTo - first + 1);
                writer.ReadWalFramePages(first, pages.AsSpan(0, count), sizes.AsSpan(0, count));
                for (var i = 0; i < count; i++)
                {
                    _live.Pages[pages[i]] = first + (ulong)i;
                    if (sizes[i] != 0)
                    {
                        _live.DatabaseSize = sizes[i];
                    }
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pages);
            ArrayPool<uint>.Shared.Return(sizes);
        }

        _live.MaxFrame = upTo;
    }

    private void Store()
    {
        try
        {
            RevisionManifestFile.Store(path, _manifest!);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _fenced = error.GetType().Name;
            throw new IOException("the revision manifest could not be written; the database is fenced until it reopens", error);
        }
    }

    private void ThrowIfFenced()
    {
        if (_fenced is not null)
        {
            throw new IOException("the database is fenced after a failed revision manifest write");
        }
    }

    private static void Snapshot(SqliteConnection connection, Action body) =>
        Snapshot(connection, () =>
        {
            body();
            return true;
        });

    private static T Snapshot<T>(SqliteConnection connection, Func<T> body)
    {
        Execute(connection, "BEGIN; SELECT count(*) FROM sqlite_schema;");
        try
        {
            return body();
        }
        finally
        {
            Execute(connection, "ROLLBACK;");
        }
    }

    private static uint PageCount(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA page_count;";
        return Convert.ToUInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static FileStamp Stamp(string file)
    {
        var info = new FileInfo(file);
        return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private static (uint, uint)? ReadWalSalt(string wal)
    {
        Span<byte> header = stackalloc byte[32];
        try
        {
            using var file = File.OpenHandle(wal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (RandomAccess.Read(file, header, 0) != header.Length
                || (BinaryPrimitives.ReadUInt32BigEndian(header) & 0xFFFF_FFFE) != 0x377F_0682)
            {
                return null;
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        return (BinaryPrimitives.ReadUInt32BigEndian(header[16..]), BinaryPrimitives.ReadUInt32BigEndian(header[20..]));
    }
}

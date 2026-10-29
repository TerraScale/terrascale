using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Turso.Data.Sqlite;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Turso;

internal sealed class TursoRevisionLedger(string path, IMemoryBudget? memory, TenantContext? tenant) : IDisposable
{
    internal const ulong CheckpointFrames = 1024;
    private const int FrameBatch = 1024;
    private const long BaseBytes = 4096;
    private const long IndexEntryBytes = 24;
    private const long PageNumberBytes = 8;

    private readonly Lock _gate = new();
    private RevisionManifest? _manifest;
    private LiveGeneration _live = new();
    private SqliteConnection? _attached;
    private string? _fenced;
    private IMemoryReservation? _retained;

    internal long RetainedBytes
    {
        get
        {
            lock (_gate)
            {
                return _retained?.Bytes ?? 0;
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

    internal static void BeginSnapshot(SqliteConnection reader) => Execute(reader, "BEGIN; SELECT count(*) FROM sqlite_schema;");

    internal static void EndSnapshot(SqliteConnection reader) => Execute(reader, "ROLLBACK;");

    internal RevisionPullPlan PlanPull(SqliteConnection reader, string? clientRevision)
    {
        var snapshot = reader.GetWalState();
        var size = PageCount(reader);
        lock (_gate)
        {
            ThrowIfFenced();
            if (_attached is null || _manifest is null
                || snapshot.CheckpointSequence != _live.CheckpointSequence
                || snapshot.MaxFrame != _live.MaxFrame)
            {
                throw new InvalidOperationException("a pull snapshot must match the revision index it was planned against");
            }

            var reservation = memory?.TryReserve(MemoryLane.Bulk, BaseBytes + (IndexedPages() * PageNumberBytes * 2), tenant);
            if (memory is not null && reservation is null)
            {
                throw new StorageBusyException();
            }

            try
            {
                var plan = RevisionPlanner.Plan(_manifest, _live, clientRevision);
                uint[]? pages = plan.Full is null ? [.. plan.Pages.Where(number => number <= size).Order()] : null;
                return new RevisionPullPlan(_manifest.Current(snapshot.MaxFrame), plan.Full, pages, size, reservation);
            }
            catch
            {
                reservation?.Dispose();
                throw;
            }
        }
    }

    internal static RevisionPullResult Stream(SqliteConnection reader, RevisionPullPlan plan, Action<uint, ReadOnlySpan<byte>> page, CancellationToken cancellationToken)
    {
        var pages = plan.Pages ?? Enumerable.Range(1, (int)plan.DatabaseSize).Select(number => (uint)number);
        var sent = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(reader.PageSize);
        try
        {
            var span = buffer.AsSpan(0, reader.PageSize);
            foreach (var number in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!reader.TryReadSnapshotPage(number, span))
                {
                    throw StoredDataCorruption.Create(string.Create(CultureInfo.InvariantCulture, $"page {number} of {plan.DatabaseSize} is missing at frame {plan.Revision.Frame}"));
                }

                page(number, span);
                sent++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new RevisionPullResult(plan.Revision, plan.Full, sent, plan.DatabaseSize);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _retained?.Dispose();
            _retained = null;
        }
    }

    private long IndexedPages() =>
        _live.Pages.Count + _manifest!.Sealed.Sum(entry => (long)entry.Pages.Count) + _manifest.Horizon.Sum(bucket => (long)bucket.Pages.Count);

    private void Account()
    {
        if (memory is null || _manifest is null)
        {
            return;
        }

        var needed = BaseBytes + (IndexedPages() * IndexEntryBytes);
        if (_retained is { } held && held.Bytes >= needed)
        {
            return;
        }

        if (_retained is { } grown && memory.TryGrow(grown, needed - grown.Bytes))
        {
            return;
        }

        if (memory.TryReserve(MemoryLane.Bulk, needed, tenant) is { } fresh)
        {
            _retained?.Dispose();
            _retained = fresh;
            return;
        }

        _manifest.Horizon.Clear();
        for (var index = 0; index < _manifest.Sealed.Count; index++)
        {
            _manifest.Sealed[index] = _manifest.Sealed[index] with { Full = true, Pages = new Dictionary<uint, ulong>() };
        }

        _live.Full = true;
        _live.Pages.Clear();
        _live.Pages.TrimExcess();
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
        bool truncated;
        try
        {
            truncated = writer.TryCheckpointTruncate(end);
        }
        catch
        {
            _fenced = "the checkpoint failed";
            throw;
        }

        var (state, size) = Snapshot(writer, () => (writer.GetWalState(), PageCount(writer)));
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
                    if (!_live.Full)
                    {
                        _live.Pages[pages[i]] = first + (ulong)i;
                    }

                    if (sizes[i] != 0)
                    {
                        _live.DatabaseSize = sizes[i];
                    }
                }

                if (_live.Pages.Count > RevisionManifest.MaxGenerationPages)
                {
                    _live.Full = true;
                    _live.Pages.Clear();
                    _live.Pages.TrimExcess();
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(pages);
            ArrayPool<uint>.Shared.Return(sizes);
        }

        _live.MaxFrame = upTo;
        Account();
    }

    private void Store()
    {
        Account();
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

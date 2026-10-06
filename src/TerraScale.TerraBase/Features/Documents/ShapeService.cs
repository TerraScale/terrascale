using System.Buffers;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>
/// Shape reads over append-only document history: snapshots read the state at their offset, and
/// logs replay every committed write after it (docs/shape-reads.md).
/// </summary>
/// <remarks>
/// Requests with the same scope, handle, offset and mode share one in-flight read (a flight), live
/// waits included, so N concurrent pollers cost one storage read. The flight runs on its own token,
/// so one requester leaving does not cancel it; it is cancelled when its last requester leaves.
/// Every requester gets the same serialized body.
/// ponytail: fixed admission caps bound concurrent reads; use the shared memory budget when it
/// exists to charge these reads alongside other features.
/// </remarks>
internal sealed class ShapeService : IShapeService
{
    /// <summary>Longest live wait.</summary>
    public static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(20);

    internal const int MaxFlights = 32;
    internal const int MaxRequesters = 256;

    private const int SnapshotPageDocuments = 1000;
    private const long ChunkBytes = 1024 * 1024;
    private const long ChunkCommits = 4096;

    private static readonly QuotaExceeded Busy = new("shape reads are at capacity; retry later", "shape_busy");
    private static readonly NotFound UnknownDatabase = new("the database does not exist", "unknown_database");
    private static readonly NotFound UnknownCollection = new("the collection does not exist", "unknown_collection");

    // Same answer as a document query over the budget (TS-459): the shape's filter is too expensive.
    private static readonly Validation TooExpensive = new("query too expensive", "invalid_query");

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Flight> _flights = [];
    private readonly IStorageEngine _engine;
    private readonly TimeProvider _time;
    private readonly CancellationToken _stopping;
    private readonly Counter<long> _reads;
    private readonly UpDownCounter<long> _waiting;
    private readonly UpDownCounter<long> _active;
    private readonly UpDownCounter<long> _live;
    private int _running;
    private int _requesters;

    public ShapeService(IStorageEngine engine, TimeProvider time, IHostApplicationLifetime lifetime, IMeterFactory meters)
    {
        _engine = engine;
        _time = time;
        _stopping = lifetime.ApplicationStopping;
        var meter = meters.Create(Telemetry.MeterName);
        _reads = meter.CreateCounter<long>("terrabase.shape.reads", "{read}", "Storage reads made for shape requests, after collapsing.");
        _waiting = meter.CreateUpDownCounter<long>("terrabase.shape.requests.waiting", "{request}", "Shape requests waiting on a shared read.");
        _active = meter.CreateUpDownCounter<long>("terrabase.shape.flights.active", "{flight}", "Admitted shape reads, including cancellation cleanup.");
        _live = meter.CreateUpDownCounter<long>("terrabase.shape.live.waits", "{flight}", "Live shape reads waiting at the head for a commit.");
    }

    public async ValueTask<ShapeReadResult> ReadAsync(ShapeRequest request, CancellationToken cancellationToken)
    {
        var key = string.Join('\0', request.Scope, request.Handle, request.Offset, request.Live);
        Flight? flight;
        var start = false;
        lock (_gate)
        {
            if (_requesters >= MaxRequesters)
            {
                return Busy;
            }

            if (!_flights.TryGetValue(key, out flight))
            {
                if (_running >= MaxFlights)
                {
                    return Busy;
                }

                _running++;
                _active.Add(1);
                flight = new Flight();
                _flights[key] = flight;
                start = true;
            }

            flight.Requesters++;
            _requesters++;
        }

        if (start)
        {
            _ = RunAsync(request, key, flight);
        }

        _waiting.Add(1);
        try
        {
            return await flight.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Leave(key, flight);
            _waiting.Add(-1);
        }
    }

    // The last requester to leave an unfinished flight cancels it; a finished flight is already gone.
    private void Leave(string key, Flight flight)
    {
        lock (_gate)
        {
            _requesters--;
            if (--flight.Requesters > 0 || !_flights.TryGetValue(key, out var current) || current != flight)
            {
                return;
            }

            _flights.Remove(key);
        }

        flight.Cancel.Cancel();
    }

    private async Task RunAsync(ShapeRequest request, string key, Flight flight)
    {
        try
        {
            using var token = CancellationTokenSource.CreateLinkedTokenSource(_stopping, flight.Cancel.Token);
            flight.Result.SetResult(await ReadCoreAsync(request, token.Token).ConfigureAwait(false));
        }
        catch (Exception error)
        {
            // Only unexpected failures: expected ones are results.
            flight.Result.SetException(error);
            // Every requester may have left; the exception is theirs, not the process's.
            _ = flight.Result.Task.Exception;
        }
        finally
        {
            lock (_gate)
            {
                if (_flights.TryGetValue(key, out var current) && current == flight)
                {
                    _flights.Remove(key);
                }

                _running--;
                _active.Add(-1);
            }
        }
    }

    private async Task<ShapeReadResult> ReadCoreAsync(ShapeRequest request, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(request.Live ? LiveTimeout : Timeout.InfiniteTimeSpan, _time);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            // A wake can come from a writer still finishing on a deleted file. Resolve the current
            // store every time, so its incarnation, not that old writer's, validates the handle.
            if (await _engine.OpenDatabaseAsync(request.Database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return UnknownDatabase;
            }

            ShapeReadResult result;
            long sequence;
            try
            {
                (result, sequence) = await ReadOnceAsync(store, request, cancellationToken).ConfigureAwait(false);
            }
            catch (QueryCapacityException)
            {
                return TooExpensive;
            }

            if (result is not ShapeResult { Status: ShapeStatus.OpenTail, Body.IsEmpty: true })
            {
                return result;
            }

            // Live at the head: wait for any write commit after the read began, then read again. A commit
            // elsewhere in the database still advances the offset, and a collection drop or creation is
            // revalidated by the next read.
            try
            {
                // Counts only the wait itself, never the fallback read below.
                _live.Add(1);
                try
                {
                    await store.WaitForCommitAsync(sequence, deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    _live.Add(-1);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The database may have been replaced during the wait, which never signals this store:
                // answer from a fresh, non-waiting read, which revalidates the database and handle.
                return await ReadCoreAsync(request with { Live = false }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Returns the result and the commit sequence from before the read; an open tail without a body
    // means a live read must wait for a commit.
    private async Task<(ShapeReadResult Result, long Sequence)> ReadOnceAsync(IDatabaseStore store, ShapeRequest request, CancellationToken cancellationToken)
    {
        _reads.Add(1);
        // Taken before the snapshot: a commit racing the read wakes the wait instead of being missed.
        var sequence = store.CommitSequence;
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            if (await transaction.GetCollectionAsync(request.Collection, cancellationToken).ConfigureAwait(false) is not { } collection)
            {
                return (UnknownCollection, sequence);
            }

            var (shapeId, handle) = Identify(request, store.Incarnation, collection);
            if (request.Handle is { } requested && requested != handle)
            {
                return (MustRefetch(shapeId, handle), sequence);
            }

            // Validate the position against this snapshot before reading anything: below the history floor or past
            // the head, a shape answers must_refetch without evaluating its filter.
            var offset = request.Offset;
            if (offset.Kind != ShapeOffsetKind.Initial
                && (offset.Position < await transaction.GetHistoryFloorAsync(cancellationToken).ConfigureAwait(false)
                    || offset.Position > await transaction.GetOffsetAsync(cancellationToken).ConfigureAwait(false)))
            {
                return (MustRefetch(shapeId, handle), sequence);
            }

            switch (offset.Kind)
            {
                case ShapeOffsetKind.Initial:
                    {
                        var head = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        var first = new ShapeOffset(ShapeOffsetKind.Snapshot, head).ToString();
                        return (new ShapeResult(ShapeStatus.Redirect, shapeId, handle, first, false, default, ""), sequence);
                    }

                case ShapeOffsetKind.Snapshot:
                    {
                        var after = offset.AfterId is { } id ? new QueryPosition([], id) : null;
                        var page = await transaction.SnapshotAsync(
                            new DocumentQuery(request.Collection, request.Filter, [], after, SnapshotPageDocuments, ChunkBytes),
                            offset.Position, cancellationToken).ConfigureAwait(false);
                        var head = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        // Too many ids were written after S to read this page within bounded work: start again at -1,
                        // whose snapshot is the head. A page never answers with other boundaries instead.
                        if (offset.Position > head || page.Expired)
                        {
                            return (MustRefetch(shapeId, handle), sequence);
                        }

                        // Measure complete serialized bodies, including the actual offsets and framing.
                        // Search the largest prefix that fits; an oversized first document travels alone.
                        var count = page.Rows.Count;
                        var result = SnapshotPage(request, shapeId, handle, page, count);
                        if (result.Body.Length > ChunkBytes && count > 1)
                        {
                            var low = 1;
                            var high = count - 1;
                            while (low < high)
                            {
                                var middle = (low + high + 1) / 2;
                                if (SnapshotPage(request, shapeId, handle, page, middle).Body.Length <= ChunkBytes)
                                {
                                    low = middle;
                                }
                                else
                                {
                                    high = middle - 1;
                                }
                            }

                            result = SnapshotPage(request, shapeId, handle, page, low);
                        }

                        return (result, sequence);
                    }

                default:
                    {
                        var (changes, endedAt) = await ChunkAsync(transaction, request, shapeId, handle, cancellationToken).ConfigureAwait(false);
                        var head = await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                        if (offset.Position > head)
                        {
                            return (MustRefetch(shapeId, handle), sequence);
                        }

                        if (request.Live && head == offset.Position)
                        {
                            return (new ShapeResult(ShapeStatus.OpenTail, shapeId, handle, offset.ToString(), true, default, ""), sequence);
                        }

                        // Closed: ended at the byte limit, or the head already passed the chunk's last commit.
                        var end = endedAt ?? Math.Min(WindowEnd(offset.Position), head);
                        var closed = endedAt is not null || head >= WindowEnd(offset.Position);
                        // Only a short-lived open tail says up to date, for the same reason as snapshot pages.
                        var result = Page(
                            closed ? ShapeStatus.Immutable : ShapeStatus.OpenTail, shapeId, handle, offset.ToString(), DocumentRoute.Decimal(end),
                            !closed && end == head, changes);
                        return (result, sequence);
                    }
            }
        }
    }

    // Reads the commits after the offset, up to 4096 of them, stopping before the commit whose
    // serialized bytes would take the body past 1 MiB (a commit larger than that alone is a chunk
    // of its own). EndedAt is the last covered offset when the byte limit ended the chunk.
    // A commit is held in memory whole: storage caps all writers at 7 MiB including operation
    // framing, so the stored bytes plus outer framing stay within 8 MiB.
    private static async Task<(IReadOnlyList<ReadOnlyMemory<byte>> Changes, long? EndedAt)> ChunkAsync(
        IStorageTransaction transaction, ShapeRequest request, string shapeId, string handle, CancellationToken cancellationToken)
    {
        var after = request.Offset.Position;
        var changes = new List<ReadOnlyMemory<byte>>();
        var commit = new List<Op>();
        long commitOffset = 0;
        // Serialize the actual empty ShapeBody at the fixed window end. Its next_offset has at
        // least as many digits as any chunk end, and false is longer than true. This makes byte
        // boundaries independent of the current head, so a closed chunk never changes later.
        long bytes = Page(ShapeStatus.Immutable, shapeId, handle, request.Offset.ToString(),
            DocumentRoute.Decimal(WindowEnd(after)), upToDate: false, changes).Body.Length;
        var rows = transaction.ChangesAsync(request.Collection, request.Filter, after, WindowEnd(after), cancellationToken);
        await foreach (var row in rows.ConfigureAwait(false))
        {
            var historyOffset = row.HistoryOffset
                ?? throw new InvalidOperationException("a shape change needs its local history offset");
            if (historyOffset != commitOffset && commit.Count > 0)
            {
                if (!Add(changes, Serialize(commitOffset, commit), ref bytes))
                {
                    return (changes, commitOffset - 1);
                }

                commit = [];
            }

            commitOffset = historyOffset;
            commit.Add(row.Deleted ? new Op("delete", row.Id, null, default) : Upsert(request.Collection, row));
        }

        return commit.Count > 0 && !Add(changes, Serialize(commitOffset, commit), ref bytes)
            ? (changes, commitOffset - 1)
            : (changes, null);
    }

    private static long WindowEnd(long after) => after > long.MaxValue - ChunkCommits ? long.MaxValue : after + ChunkCommits;

    // Adds a serialized commit unless it would take a non-empty chunk past the byte limit.
    private static bool Add(List<ReadOnlyMemory<byte>> changes, ReadOnlyMemory<byte> commit, ref long bytes)
    {
        // The empty body already includes []; only later commits add a comma.
        var size = commit.Length + (changes.Count > 0 ? 1 : 0);
        if (changes.Count > 0 && bytes + size > ChunkBytes)
        {
            return false;
        }

        changes.Add(commit);
        bytes += size;
        return true;
    }

    private static ShapeResult SnapshotPage(ShapeRequest request, string shapeId, string handle, SnapshotPage page, int count)
    {
        var offset = request.Offset;
        // A trimmed page resumes after its last kept row; a full or work-limited one where the engine stopped.
        var next = count < page.Rows.Count
            ? new ShapeOffset(ShapeOffsetKind.Snapshot, offset.Position, page.Rows[count - 1].Document.Id)
            : page.HasMore
                ? new ShapeOffset(ShapeOffsetKind.Snapshot, offset.Position, page.ResumeAfter)
                : new ShapeOffset(ShapeOffsetKind.Log, offset.Position);
        var ops = page.Rows.Take(count).Select(row => Upsert(request.Collection, row.Document)).ToList();
        // A cached snapshot cannot vouch for the current head; only the fresh tail says up to date.
        return Page(ShapeStatus.Immutable, shapeId, handle, offset.ToString(), next.ToString(),
            upToDate: false, [Serialize(offset.Position, ops)]);
    }

    // The shape id and handle bind the scope, the database incarnation and the collection generation,
    // so a recreated database or collection starts a new lineage.
    private static (string ShapeId, string Handle) Identify(ShapeRequest request, Guid incarnation, CollectionInfo collection)
    {
        var scope = string.Join('\0', request.Scope, incarnation.ToString("N"), DocumentRoute.Decimal(collection.Generation));
        var shapeId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-V1\0" + scope))[..16]);
        var handle = "h1-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("TB-SHAPE-HANDLE-V1\0" + scope))[..8]);
        return (shapeId, handle);
    }

    private static ShapeResult MustRefetch(string shapeId, string handle) =>
        new(ShapeStatus.MustRefetch, shapeId, handle, ShapeOffset.Initial.ToString(), false, default, "");

    // Writes a ShapeBody around commits serialized by Serialize, which keeps each document's stored
    // bytes (a serializer would re-escape characters such as '<', so the size bounds would not hold).
    // The digest names these exact bytes, so a validator never covers two different bodies.
    private static ShapeResult Page(
        ShapeStatus status, string shapeId, string handle, string offset, string nextOffset, bool upToDate, IReadOnlyList<ReadOnlyMemory<byte>> changes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("shape_id", shapeId);
            writer.WriteString("handle", handle);
            writer.WriteString("offset", offset);
            writer.WriteString("next_offset", nextOffset);
            writer.WriteBoolean("up_to_date", upToDate);
            writer.WriteStartArray("changes");
            foreach (var commit in changes)
            {
                writer.WriteRawValue(commit.Span, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var body = buffer.WrittenMemory;
        var digest = Convert.ToHexStringLower(SHA256.HashData(body.Span)[..8]);
        return new ShapeResult(status, shapeId, handle, nextOffset, upToDate, body, digest);
    }

    // One commit's changes, {"offset":…,"ops":[…]}, with every document as stored.
    private static ReadOnlyMemory<byte> Serialize(long offset, IReadOnlyList<Op> ops)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("offset", DocumentRoute.Decimal(offset));
            writer.WriteStartArray("ops");
            foreach (var op in ops)
            {
                writer.WriteStartObject();
                writer.WriteString("op", op.Kind);
                writer.WriteString("id", op.Id);
                if (op.Version is { } version)
                {
                    writer.WriteString("version", version);
                    writer.WritePropertyName("document");
                    // Storage shape reads also validate retained/restored JSON; preserve its exact bytes.
                    writer.WriteRawValue(op.Document.Span, skipInputValidation: true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenMemory;
    }

    private static Op Upsert(string collection, StoredDocument document) =>
        new("upsert", document.Id, ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json), document.Json);

    // One op of a body: an upsert carries the version and the stored document bytes.
    private sealed record Op(string Kind, string Id, string? Version, ReadOnlyMemory<byte> Document);

    private sealed class Flight
    {
        public TaskCompletionSource<ShapeReadResult> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // Never disposed: it has no timer, and a late Cancel must not throw.
        public CancellationTokenSource Cancel { get; } = new();

        public int Requesters { get; set; }
    }
}

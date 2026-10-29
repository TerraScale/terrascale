using System.Buffers;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>Serves a database's changes feed as Server-Sent Events.</summary>
public interface IDatabaseChanges
{
    /// <summary>
    /// Streams the feed of <paramref name="database"/> after <paramref name="cursor"/>, or from the feed's head when
    /// <paramref name="head"/>, or else from the start of retention, only
    /// events of <paramref name="collections"/> when given, until the client leaves, the connection's lifetime ends or the
    /// feed fails. Before the first byte, failures answer with their status.
    /// </summary>
    ValueTask<IResult> StreamAsync(
        HttpContext context, TenantContext database, FeedCursor? cursor, bool head, IReadOnlySet<string>? collections, CancellationToken cancellationToken);
}

/// <summary>
/// One page at a time from the feed (docs/terrabase/multi-region-replication.md section 14), woken by the database's next
/// commit or the 15 s heartbeat. No per-connection queue: a slow client holds back only its own reads, and one that takes
/// no bytes for 10 s is dropped.
/// </summary>
internal sealed partial class DatabaseChanges(IStorageEngine engine, IMemoryBudget memory, TimeProvider time, IHostApplicationLifetime lifetime, IMeterFactory meters, TerraBaseOptions options, IMembership membership, TerraBase.Features.Health.IPeerTopology topology)
    : IDatabaseChanges
{
    /// <summary>Open streams per database on one machine.</summary>
    internal const int MaxPerDatabase = 128;

    private const int MaxConnections = 1024;
    private const int PageEvents = 256;
    private const long PageBytes = 4 * 1024 * 1024;

    /// <summary>How often an idle stream sends its cursor.</summary>
    internal static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(15);

    // A stream re-authenticates by reconnecting with Last-Event-ID: a revoked API key streams at most this long.
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly QuotaExceeded Busy = new("the database has as many change streams as it allows; retry later", "changes_busy");
    private static readonly Gone Expired = new("events after the cursor are no longer retained here; start again with cursor=now and re-read", "cursor_expired");

    private readonly Lock _gate = new();
    private readonly Dictionary<DatabaseKey, DatabaseState> _open = [];
    private int _total;
    private int _running;
    private readonly UpDownCounter<long> _streams = meters.Create(Telemetry.MeterName)
        .CreateUpDownCounter<long>("terrabase.changes.streams", "{connection}", "Open changes feed streams.");

    public async ValueTask<IResult> StreamAsync(
        HttpContext context, TenantContext database, FeedCursor? cursor, bool head, IReadOnlySet<string>? collections, CancellationToken cancellationToken)
    {
        if (ProjectAccess.Current is { IsEndUser: true } access)
        {
            return await StreamEndUserAsync(context, database, cursor, head, collections, access, cancellationToken).ConfigureAwait(false);
        }

        var id = database.DatabaseId ?? throw new ArgumentException("a client database has an id", nameof(database));
        var key = new DatabaseKey(database.OrganizationId, database.ProjectId, id);
        if (!TryOpen(key))
        {
            context.Response.Headers.RetryAfter = "1";
            return ErrorResponse.From(Busy);
        }

        _streams.Add(1);
        var lasts = MaxLifetime;
        if (context.Features.Get<ProjectToken>() is { } token && token.ExpiresAt - time.GetUtcNow() is var left && left < lasts)
        {
            lasts = left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }

        using var expiry = new CancellationTokenSource(lasts, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, lifetime.ApplicationStopping);
        var buffer = new ArrayBufferWriter<byte>();
        using var bufferMemory = new QosContext.ResultMemory(memory);
        // Documents a page reads are charged to the tenant until the page is sent.
        var results = QosContext.Enter(database, memory);
        try
        {
            bufferMemory.Reserve(4096, database);
            // A cursor older than the retention may lack an origin whose events all aged out since and were forgotten.
            if (cursor is not null && cursor.Issued.Value < FeedCursor.At(time.GetUtcNow() - FeedCursor.Retention).Value)
            {
                return ErrorResponse.From(Expired);
            }

            // An issue instant in the future would weaken the cursor's own promise about origins it lacks: it counts as now.
            if (cursor is not null && cursor.Issued.Value > FeedCursor.At(time.GetUtcNow()).Value)
            {
                cursor = cursor with { Issued = FeedCursor.At(time.GetUtcNow()) };
            }

            // The edge replays to the issuer's region; an issuer no longer listed is answered here, verifying the prefix.
            if (cursor?.Machine is { Length: > 0 } machine && machine != options.MachineId
                && Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && !context.Request.Headers.ContainsKey("ts-replay-src") && !context.Request.Headers.ContainsKey("ts-replay-failed")
                && await IssuerRegionAsync(machine, context.RequestAborted).ConfigureAwait(false) is { } issuerRegion)
            {
                context.Response.Headers["ts-replay"] = $"region={issuerRegion};instance={machine};fallback=self";
                context.Response.Headers.CacheControl = "no-store";
                return new IssuerReplay();
            }

            if (await ReadAsync(database, key, cursor, head, collections, stop.Token).ConfigureAwait(false) is not { } answer)
            {
                return ErrorResponse.From(DocumentRoute.UnknownDatabase);
            }

            if (answer.Page.Expired)
            {
                return ErrorResponse.From(Expired);
            }

            // Streaming from here on: the stream gives back its slot in the organization's concurrent requests.
            context.Features.Get<QosRequestLease>()?.Lease.Dispose();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await SendAsync(context, answer.Initial, stop.Token).ConfigureAwait(false);
            var beat = time.GetUtcNow() + Heartbeat;
            while (true)
            {
                for (var i = 0; i < answer.Chunks.Count; i++)
                {
                    await SendAsync(context, answer.Chunks[i], stop.Token).ConfigureAwait(false);
                }

                if (!answer.Page.More)
                {
                    await WaitAsync(answer.Store, answer.Sequence, beat, stop.Token).ConfigureAwait(false);
                }

                if (time.GetUtcNow() >= beat)
                {
                    await SendAsync(context, answer.Heartbeat, stop.Token).ConfigureAwait(false);
                    beat = time.GetUtcNow() + Heartbeat;
                }

                cursor = answer.Cursor;
                answer = null!;
                results.Dispose();
                results = QosContext.Enter(database, memory);
                if (await ReadAsync(database, key, cursor, head: false, collections, stop.Token).ConfigureAwait(false) is not { } next)
                {
                    await FailAsync(context, buffer, DocumentRoute.UnknownDatabase.Code, DocumentRoute.UnknownDatabase.Message, stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                if (next.Page.Expired)
                {
                    await FailAsync(context, buffer, Expired.Code, Expired.Message, stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                answer = next;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested || lifetime.ApplicationStopping.IsCancellationRequested || !topology.IsCurrent)
        {
            if (!topology.IsCurrent)
            {
                RefuseTopology(context);
            }

            // A shared producer can observe host cancellation before this stream's linked-token callback runs.
            // The client left, the lifetime or token ended, or the host stops: the client resumes with Last-Event-ID.
            // Shared producers use ApplicationStopping directly. Their newer cancellation callbacks can finish a
            // page before this stream's linked-token callback runs, so also observe the original shutdown token.
        }
        catch (QosMemoryException) when (!stop.IsCancellationRequested)
        {
            context.Response.Headers.RetryAfter = "1";
            var busy = new QuotaExceeded("organization memory budget exceeded", "tenant_memory");
            if (!context.Response.HasStarted)
            {
                return ErrorResponse.From(busy);
            }

            await FailAsync(context, buffer, busy.Code, busy.Message, stop.Token).ConfigureAwait(false);
        }
        catch (StorageBusyException error) when (!stop.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter = "1";
                return ErrorResponse.From(new Unavailable(error.Message, "storage_busy"));
            }

            await FailAsync(context, buffer, "storage_busy", error.Message, stop.Token).ConfigureAwait(false);
        }
        catch (IOException) when (context.Response.HasStarted)
        {
            context.Abort();
        }
        finally
        {
            results.Dispose();
            Close(key);
            _streams.Add(-1);
        }

        return TypedResults.Empty;
    }

    private bool TryOpen(DatabaseKey database)
    {
        lock (_gate)
        {
            var state = _open.GetValueOrDefault(database);
            if (state?.Open >= MaxPerDatabase || _total >= MaxConnections)
            {
                return false;
            }

            state ??= new DatabaseState();
            state.Open++;
            _open[database] = state;
            _total++;
            return true;
        }
    }

    private void Close(DatabaseKey database)
    {
        lock (_gate)
        {
            _total--;
            var state = _open[database];
            if (--state.Open == 0)
            {
                foreach (var flight in state.Pages)
                {
                    flight.Detached = true;
                    flight.Memory.Dispose();
                }

                _ = state.Pages.RemoveAll(flight => flight.Result.Task.IsCompleted);
                if (state.Pages.Count == 0)
                {
                    _ = _open.Remove(database);
                }
            }
        }
    }

    // The commit sequence keys current-document snapshots as well as feed history; a new write never reuses a ready page.
    private async ValueTask<Answer?> ReadAsync(
        TenantContext database, DatabaseKey databaseKey, FeedCursor? cursor, bool head, IReadOnlySet<string>? collections, CancellationToken cancellationToken)
    {
        QosContext.ReserveResult((5L * (81 + (56 * (cursor?.Stamps.Count ?? 0)))) + (collections?.Sum(value => 4L * value.Length + 16) ?? 0) + 1024);
        var text = cursor is null ? null : ChangeCursor.Encode(cursor);
        var filter = collections is null ? null : string.Concat(collections.Order(StringComparer.Ordinal).Select(value => $"{value.Length}:{value}"));
        while (true)
        {
            if (!topology.IsCurrent)
            {
                throw new OperationCanceledException("peer topology is not verified");
            }

            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return null;
            }

            Flight? flight = null;
            Task<Answer>? drain = null;
            var start = false;
            try
            {
                lock (_gate)
                {
                    var pages = _open[databaseKey].Pages;
                    var now = time.GetUtcNow();
                    var window = now.ToUnixTimeSeconds() / (long)Heartbeat.TotalSeconds;
                    var incarnation = store.Incarnation;
                    var sequence = store.CommitSequence;
                    // Cohorts emit the same issue instant and converge; idle cursors renew within one heartbeat.
                    var issued = pages.Find(value => value.Key.Incarnation == incarnation && value.Key.Sequence == sequence && value.Key.Window == window)?.Key.Issued
                        ?? FeedCursor.At(now);
                    var key = new PageKey(database, incarnation, sequence, window, issued, text, filter, head);
                    // In-flight pages keep their slots until they finish, even if their commit sequence is now old.
                    for (var i = pages.Count - 1; i >= 0; i--)
                    {
                        if (pages[i].Result.Task.IsCompleted && (pages[i].Result.Task.IsFaulted || pages[i].Store.IsClosed
                            || pages[i].Key.Incarnation != key.Incarnation || pages[i].Key.Sequence != key.Sequence || pages[i].Key.Window != window))
                        {
                            pages[i].Memory.Dispose();
                            pages.RemoveAt(i);
                        }
                    }

                    flight = pages.Find(value => !value.Detached && !value.Store.IsClosed && value.Key == key);
                    if (flight is null && pages.Count == 4)
                    {
                        var ready = pages.FindIndex(value => value.Result.Task.IsCompleted);
                        if (ready < 0)
                        {
                            drain = pages[0].Result.Task;
                        }
                        else
                        {
                            pages[ready].Memory.Dispose();
                            pages.RemoveAt(ready);
                        }
                    }

                    if (flight is null && drain is null)
                    {
                        if (_running == 4 * MaxConnections)
                        {
                            throw new QosMemoryException();
                        }

                        var admission = new QosContext.ResultMemory(memory);
                        var producer = new QosContext.ResultMemory(memory);
                        try
                        {
                            admission.Reserve((4L * ChangeCursor.MaxBytes) + (filter?.Length * 2L ?? 0) + 4096, database);
                            producer.Attach(admission);
                            flight = new Flight(key, store, admission, producer);
                            pages.Add(flight);
                            _running++;
                            start = true;
                        }
                        catch
                        {
                            admission.Dispose();
                            producer.Dispose();
                            throw;
                        }
                    }

                    if (flight is not null)
                    {
                        QosContext.AttachResults(flight.Memory);
                    }
                }
            }
            finally
            {
                if (start)
                {
                    _ = ProduceAsync(databaseKey, flight!, cursor, collections);
                }
            }

            if (drain is not null)
            {
                // Its own readers observe failure; waiting for capacity must not inherit another cursor's failure.
                try
                {
                    _ = await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (drain.IsFaulted && !cancellationToken.IsCancellationRequested)
                {
                }

                continue;
            }

            return await flight!.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProduceAsync(DatabaseKey databaseKey, Flight flight, FeedCursor? cursor, IReadOnlySet<string>? collections)
    {
        // A producer holds admission independently of the cache and readers, including when the last stream leaves.
        using var hold = flight.Producer;
        try
        {
            using var scope = QosContext.EnterFlight(flight.Key.Database, flight.Memory);
            var issued = flight.Key.Issued;
            var transaction = await flight.Store.BeginTransactionAsync(write: false, lifetime.ApplicationStopping).ConfigureAwait(false);
            FeedPage page;
            await using (transaction.ConfigureAwait(false))
            {
                if (flight.Key.Head)
                {
                    cursor = new FeedCursor(await transaction.GetFeedHeadAsync(lifetime.ApplicationStopping).ConfigureAwait(false), issued);
                }

                page = await transaction.ReadFeedAsync(cursor, collections, PageEvents, PageBytes, lifetime.ApplicationStopping).ConfigureAwait(false);
            }

            flight.Result.SetResult(Serialize(flight, page, cursor, issued, ChangeCursor.Scope(flight.Key.Database, collections)));
        }
        catch (Exception error)
        {
            flight.Result.SetException(error);
            _ = flight.Result.Task.Exception;
        }
        finally
        {
            lock (_gate)
            {
                _running--;
                if (_open.TryGetValue(databaseKey, out var state))
                {
                    if (flight.Detached || flight.Result.Task.IsFaulted)
                    {
                        _ = state.Pages.Remove(flight);
                        flight.Memory.Dispose();
                    }

                    if (state.Open == 0 && state.Pages.Count == 0)
                    {
                        _ = _open.Remove(databaseKey);
                    }
                }
            }
        }
    }

    private Answer Serialize(Flight flight, FeedPage page, FeedCursor? input, Hlc issued, ReadOnlyMemory<byte> scope)
    {
        if (page.Expired)
        {
            return new Answer(flight.Store, flight.Key.Sequence, page, new FeedCursor(page.Scanned, issued), default, default, []);
        }

        var origins = Math.Max(input?.Stamps.Count ?? 0, page.Scanned.Count);
        var cursorBytes = 4L * ((128 + (56 * origins) + 2) / 3);
        var bytes = page.Events.Sum(change => (long)(change.Document?.Length ?? 0) + (6L * (change.Collection.Length + change.Id.Length)) + 256 + (4 * cursorBytes));
        QosContext.ReserveResult((3 * bytes) + (16 * cursorBytes) + (256 * 1024));
        var position = new Dictionary<ReplicaId, Hlc>(input?.Stamps ?? new Dictionary<ReplicaId, Hlc>());
        var prefixes = page.Before.ToDictionary();
        FeedCursor Current()
        {
            return new FeedCursor(position, issued) { Issuer = page.Issuer, Machine = options.MachineId, Prefixes = prefixes };
        }
        var buffer = new ArrayBufferWriter<byte>();
        Cursor(buffer, Current(), scope);
        var initial = buffer.WrittenMemory.ToArray();
        buffer.ResetWrittenCount();
        foreach (var origin in position.Keys.Where(origin => !page.Scanned.ContainsKey(origin)).ToList())
        {
            _ = position.Remove(origin);
            _ = prefixes.Remove(origin);
        }

        var chunks = new List<ReadOnlyMemory<byte>>();
        var chunk = new ArrayBufferWriter<byte>(4096);
        for (var i = 0; i < page.Events.Count; i++)
        {
            var change = page.Events[i];
            position[change.Origin] = change.Stamp;
            prefixes[change.Origin] = change.Prefix!;
            // Advance only after the final event of the stamp, preserving at-least-once resumes inside a transaction.
            var last = i + 1 == page.Events.Count || (page.Events[i + 1].Origin, page.Events[i + 1].Stamp) != (change.Origin, change.Stamp);
            Change(buffer, change, last ? ChangeCursor.Encode(Current(), scope) : null);
            if (chunk.WrittenCount > 0 && chunk.WrittenCount + buffer.WrittenCount > 64 * 1024)
            {
                chunks.Add(chunk.WrittenMemory.ToArray());
                chunk.ResetWrittenCount();
            }

            if (buffer.WrittenCount > 64 * 1024)
            {
                chunks.Add(buffer.WrittenMemory.ToArray());
            }
            else
            {
                chunk.Write(buffer.WrittenSpan);
            }

            buffer.ResetWrittenCount();
        }

        if (chunk.WrittenCount > 0)
        {
            chunks.Add(chunk.WrittenMemory.ToArray());
        }

        var next = new FeedCursor(page.Scanned, issued) { Issuer = page.Issuer, Machine = options.MachineId, Prefixes = page.Prefixes };
        Cursor(buffer, next, scope);
        return new Answer(flight.Store, flight.Key.Sequence, page, next, initial, buffer.WrittenMemory.ToArray(), chunks);
    }

    private readonly record struct DatabaseKey(string Organization, string? Project, string Database);
    private sealed record PageKey(TenantContext Database, Ulid Incarnation, long Sequence, long Window, Hlc Issued, string? Cursor, string? Collections, bool Head);
    private sealed record Answer(IDatabaseStore Store, long Sequence, FeedPage Page, FeedCursor Cursor,
        ReadOnlyMemory<byte> Initial, ReadOnlyMemory<byte> Heartbeat, IReadOnlyList<ReadOnlyMemory<byte>> Chunks);
    private sealed class DatabaseState
    {
        public int Open { get; set; }

        public List<Flight> Pages { get; } = [];
    }

    private sealed class Flight(PageKey key, IDatabaseStore store, QosContext.ResultMemory memory, QosContext.ResultMemory producer)
    {
        public PageKey Key { get; } = key;

        public IDatabaseStore Store { get; } = store;

        public QosContext.ResultMemory Memory { get; } = memory;

        public QosContext.ResultMemory Producer { get; } = producer;

        public bool Detached { get; set; }

        public TaskCompletionSource<Answer> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Until the database's next commit after `sequence`, or the heartbeat.
    private async Task WaitAsync(IDatabaseStore store, long sequence, DateTimeOffset beat, CancellationToken cancellationToken)
    {
        var until = beat - time.GetUtcNow();
        if (until <= TimeSpan.Zero)
        {
            return;
        }

        using var heartbeat = new CancellationTokenSource(until, time);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, heartbeat.Token);
        try
        {
            await store.WaitForCommitAsync(sequence, wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static void Change(ArrayBufferWriter<byte> buffer, FeedEvent change, string? id)
    {
        if (id is null)
        {
            buffer.Write("event: change\n"u8);
        }
        else
        {
            Line(buffer, "event: change\nid: ", id);
        }

        buffer.Write("data: "u8);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("collection", change.Collection);
            json.WriteString("id", change.Id);
            json.WriteString("op", change.Deleted ? "delete" : "upsert");
            json.WriteString("version", change.Stamp.Value.ToString(CultureInfo.InvariantCulture));
            json.WriteString("origin", change.Origin.Value.ToString("x16", CultureInfo.InvariantCulture));
            json.WritePropertyName("document");
            if (change.Document is { } document)
            {
                // Stored documents are compact canonical JSON, validated when written: no line breaks outside escapes.
                json.WriteRawValue(document.Span, skipInputValidation: true);
            }
            else
            {
                json.WriteNullValue();
            }

            json.WriteEndObject();
        }

        buffer.Write("\n\n"u8);
    }

    private static void Cursor(ArrayBufferWriter<byte> buffer, FeedCursor cursor, ReadOnlyMemory<byte> scope)
    {
        var text = ChangeCursor.Encode(cursor, scope);
        Line(buffer, "event: cursor\nid: ", text);
        buffer.Write("data: "u8);
        using (var json = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(json, new DatabaseChangeCursor(text), TerraBaseJsonContext.Default.DatabaseChangeCursor);
        }

        buffer.Write("\n\n"u8);
    }

    private static void Line(ArrayBufferWriter<byte> buffer, string prefix, string value)
    {
        buffer.Write(Encoding.ASCII.GetBytes(prefix));
        buffer.Write(Encoding.ASCII.GetBytes(value));
        buffer.Write("\n"u8);
    }

    // A terminal error after the stream started: an `error` event with the usual error body, then the end of the stream.
    private static async Task FailAsync(HttpContext context, ArrayBufferWriter<byte> buffer, string code, string? message, CancellationToken cancellationToken)
    {
        buffer.Write("event: error\ndata: "u8);
        using (var json = new Utf8JsonWriter(buffer))
        {
            JsonSerializer.Serialize(json, new ErrorBody(new ErrorDetail(code, message)), TerraBaseJsonContext.Default.ErrorBody);
        }

        buffer.Write("\n\n"u8);
        try
        {
            await SendAsync(context, buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException || (error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            context.Abort();
        }
    }

    private async ValueTask<string?> IssuerRegionAsync(string machine, CancellationToken cancellationToken)
    {
        var view = await membership.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return view.Members.Concat(view.Elsewhere.IsDefault ? [] : view.Elsewhere).FirstOrDefault(member => member.Name == machine)?.Region;
    }

    private sealed class IssuerReplay : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) =>
            BadRequests.WriteAsync(httpContext, StatusCodes.Status421MisdirectedRequest, "cursor_elsewhere", "the cursor resumes on its issuing machine", op: null);
    }

    private static async Task SendAsync(HttpContext context, ArrayBufferWriter<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.WrittenCount == 0)
        {
            return;
        }

        try
        {
            await SendAsync(context, buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            buffer.ResetWrittenCount();
        }
    }

    private static void RefuseTopology(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
        }
        else
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }

    private static async Task SendAsync(HttpContext context, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.RequestServices.GetRequiredService<TerraBase.Features.Health.IPeerTopology>().IsCurrent)
        {
            throw new OperationCanceledException("peer topology is not verified");
        }

        using var timeout = new CancellationTokenSource(SendTimeout);
        using var send = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, timeout.Token);
        try
        {
            await context.Response.Body.WriteAsync(bytes, send.Token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(send.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            throw new IOException("the client took no bytes for 10 s");
        }
    }
}

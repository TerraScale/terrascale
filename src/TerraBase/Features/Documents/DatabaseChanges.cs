using System.Buffers;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Engine;
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
internal sealed class DatabaseChanges(IStorageEngine engine, IMemoryBudget memory, TimeProvider time, IHostApplicationLifetime lifetime, IMeterFactory meters)
    : IDatabaseChanges
{
    /// <summary>Open streams per database on one machine.</summary>
    internal const int MaxPerDatabase = 32;

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
    private readonly Dictionary<string, int> _open = [with(StringComparer.Ordinal)];
    private int _total;
    private readonly UpDownCounter<long> _streams = meters.Create(Telemetry.MeterName)
        .CreateUpDownCounter<long>("terrabase.changes.streams", "{connection}", "Open changes feed streams.");

    public async ValueTask<IResult> StreamAsync(
        HttpContext context, TenantContext database, FeedCursor? cursor, bool head, IReadOnlySet<string>? collections, CancellationToken cancellationToken)
    {
        var key = database.DatabaseId ?? throw new ArgumentException("a client database has an id", nameof(database));
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
        var buffer = new ArrayBufferWriter<byte>(4096);
        // Documents a page reads are charged to the tenant until the page is sent.
        var results = QosContext.Enter(database, memory);
        try
        {
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

            if (head)
            {
                if (await HeadAsync(database, stop.Token).ConfigureAwait(false) is not { } stamps)
                {
                    return ErrorResponse.From(DocumentRoute.UnknownDatabase);
                }

                cursor = new FeedCursor(stamps, FeedCursor.At(time.GetUtcNow()));
            }

            // Without a cursor the first page reads from the start of retention; every later read resumes the stream's own
            // cursor, issued when the read before it began.
            var issued = FeedCursor.At(time.GetUtcNow());
            if (await ReadAsync(database, cursor, collections, stop.Token).ConfigureAwait(false) is not var (store, sequence, page))
            {
                return ErrorResponse.From(DocumentRoute.UnknownDatabase);
            }

            if (page.Expired)
            {
                return ErrorResponse.From(Expired);
            }

            // Streaming from here on: the stream gives back its slot in the organization's concurrent requests.
            context.Features.Get<QosRequestLease>()?.Lease.Dispose();
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            var position = new Dictionary<ReplicaId, Hlc>(cursor?.Stamps ?? new Dictionary<ReplicaId, Hlc>());
            Cursor(buffer, new FeedCursor(position, issued));
            await SendAsync(context, buffer, stop.Token).ConfigureAwait(false);
            var beat = time.GetUtcNow() + Heartbeat;
            while (true)
            {
                // Origins this machine forgot leave the cursor before the page's events carry it.
                foreach (var origin in position.Keys.Where(origin => !page.Scanned.ContainsKey(origin)).ToList())
                {
                    _ = position.Remove(origin);
                }

                for (var i = 0; i < page.Events.Count; i++)
                {
                    var change = page.Events[i];
                    position[change.Origin] = change.Stamp;
                    // Only the last event of an (origin, stamp) carries the advanced cursor: a reader that leaves inside one
                    // resumes before it, never past events of that stamp it did not get.
                    var last = i + 1 == page.Events.Count || (page.Events[i + 1].Origin, page.Events[i + 1].Stamp) != (change.Origin, change.Stamp);
                    Change(buffer, change, last ? ChangeCursor.Encode(new FeedCursor(position, issued)) : null);
                    if (buffer.WrittenCount >= 64 * 1024)
                    {
                        await SendAsync(context, buffer, stop.Token).ConfigureAwait(false);
                    }
                }

                // Past events the collection filter left out, without origins this machine forgot: the next event or
                // heartbeat carries it.
                position = page.Scanned.ToDictionary();
                await SendAsync(context, buffer, stop.Token).ConfigureAwait(false);
                results.Dispose();
                if (!page.More)
                {
                    await WaitAsync(store, sequence, beat, stop.Token).ConfigureAwait(false);
                }

                if (time.GetUtcNow() >= beat)
                {
                    Cursor(buffer, new FeedCursor(position, issued));
                    await SendAsync(context, buffer, stop.Token).ConfigureAwait(false);
                    beat = time.GetUtcNow() + Heartbeat;
                }

                results = QosContext.Enter(database, memory);
                cursor = new FeedCursor(position, issued);
                issued = FeedCursor.At(time.GetUtcNow());
                if (await ReadAsync(database, cursor, collections, stop.Token).ConfigureAwait(false) is not var (nextStore, nextSequence, nextPage))
                {
                    await FailAsync(context, buffer, DocumentRoute.UnknownDatabase.Code, DocumentRoute.UnknownDatabase.Message, stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                if (nextPage.Expired)
                {
                    await FailAsync(context, buffer, Expired.Code, Expired.Message, stop.Token).ConfigureAwait(false);
                    return TypedResults.Empty;
                }

                (store, sequence, page) = (nextStore, nextSequence, nextPage);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The client left, the lifetime or token ended, or the host stops: the client resumes with Last-Event-ID.
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

    private bool TryOpen(string database)
    {
        lock (_gate)
        {
            var open = _open.GetValueOrDefault(database);
            if (open >= MaxPerDatabase || _total >= MaxConnections)
            {
                return false;
            }

            _open[database] = open + 1;
            _total++;
            return true;
        }
    }

    private void Close(string database)
    {
        lock (_gate)
        {
            _total--;
            if (--_open[database] == 0)
            {
                _ = _open.Remove(database);
            }
        }
    }

    private async ValueTask<Dictionary<ReplicaId, Hlc>?> HeadAsync(TenantContext database, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return (await transaction.GetFeedHeadAsync(cancellationToken).ConfigureAwait(false)).ToDictionary();
        }
    }

    // The next page, with the store and its commit sequence from before the read: a commit after it wakes the wait.
    private async ValueTask<(IDatabaseStore Store, long Sequence, FeedPage Page)?> ReadAsync(
        TenantContext database, FeedCursor? cursor, IReadOnlySet<string>? collections, CancellationToken cancellationToken)
    {
        if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
        {
            return null;
        }

        var sequence = store.CommitSequence;
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return (store, sequence, await transaction.ReadFeedAsync(cursor, collections, PageEvents, PageBytes, cancellationToken).ConfigureAwait(false));
        }
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

    private static void Cursor(ArrayBufferWriter<byte> buffer, FeedCursor cursor)
    {
        var text = ChangeCursor.Encode(cursor);
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

    private static async Task SendAsync(HttpContext context, ArrayBufferWriter<byte> buffer, CancellationToken cancellationToken)
    {
        if (buffer.WrittenCount == 0)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(SendTimeout);
        using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await context.Response.Body.WriteAsync(buffer.WrittenMemory, send.Token).ConfigureAwait(false);
            await context.Response.Body.FlushAsync(send.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new IOException("the client took no bytes for 10 s");
        }
        finally
        {
            buffer.ResetWrittenCount();
        }
    }
}

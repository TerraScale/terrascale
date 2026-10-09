using System.Buffers;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>One page at a time, with no per-subscriber document queue.</summary>
internal sealed class ShapeSubscriptionService(
    IShapeService shapes, ITenantDirectory tenants, IMemoryBudget memory, IUsageMeter usage, TimeProvider time, IHostApplicationLifetime lifetime, IMeterFactory meters)
    : IShapeSubscriptionService
{
    internal static readonly QuotaExceeded Busy = new("subscriptions are at capacity; retry later", "subscription_busy");
    private const int MaxConnections = 128;
    // Process safety for bounded shape serialization; actual rows/results and send scratch also charge the tenant.
    private const long PageMemory = 32 * 1024 * 1024;
    private const int SendBufferBytes = 16 * 1024;
    private const int EventMemory = SendBufferBytes + 2048;
    private static readonly byte[] EventEnd = "\n\n"u8.ToArray();
    private readonly UpDownCounter<long> _connections = meters.Create(Telemetry.MeterName)
        .CreateUpDownCounter<long>("terrabase.subscriptions.active", "{connection}", "Active SSE subscriptions.");
    private int _active;

    public async ValueTask<IResult> StreamAsync(HttpContext context, ShapeRequest request, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _active) > MaxConnections)
        {
            Interlocked.Decrement(ref _active);
            return ErrorResponse.From(Busy);
        }

        _connections.Add(1);
        using var expiry = new CancellationTokenSource(MaxDelay(expiresAt - time.GetUtcNow()), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, expiry.Token, lifetime.ApplicationStopping);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var reservation = memory.TryReserve(MemoryLane.Foreground, PageMemory, tenant: null);
                if (reservation is null)
                {
                    return await FailAsync(context, Busy, stop.Token).ConfigureAwait(false);
                }

                // A flushed event releases its flight reference before the next live wait, rather than at disconnect.
                using var results = QosContext.Enter(request.Database, memory);
                var current = await tenants.RegisteredAsync(request.Database, stop.Token).ConfigureAwait(false) == true
                    ? await tenants.FindDatabaseAsync(request.Database.OrganizationId, request.Database.ProjectId!,
                        (string)context.Request.RouteValues["database"]!, stop.Token).ConfigureAwait(false)
                    : null;
                if (current is not { } database || database.DatabaseId != request.Database.DatabaseId)
                {
                    return await FailAsync(context, DocumentRoute.UnknownDatabase, stop.Token).ConfigureAwait(false);
                }

                using var tenant = QosContext.Enter(database);
                request = request with { Database = database };
                var read = await shapes.ReadAsync(request, stop.Token, drainOnCancellation: true).ConfigureAwait(false);
                if (read is not ShapeResult page)
                {
                    return read switch
                    {
                        NotFound error => await FailAsync(context, error, stop.Token).ConfigureAwait(false),
                        Validation error => await FailAsync(context, error, stop.Token).ConfigureAwait(false),
                        QuotaExceeded error => await FailAsync(context, error, stop.Token).ConfigureAwait(false),
                        _ => throw new InvalidOperationException("the shape read returned no result"),
                    };
                }

                if (page.Status == ShapeStatus.MustRefetch)
                {
                    return await FailAsync(context, new Conflict("the shape handle is stale; restart at offset -1", "must_refetch"), stop.Token).ConfigureAwait(false);
                }

                // Placement can change during a live wait; do not deliver a page read under the old placement.
                if (await tenants.RegisteredAsync(request.Database, stop.Token).ConfigureAwait(false) != true)
                {
                    return await FailAsync(context, DocumentRoute.UnknownDatabase, stop.Token).ConfigureAwait(false);
                }

                usage.RecordRead(request.Database);

                if (page.Status != ShapeStatus.Redirect)
                {
                    if (!context.Response.HasStarted)
                    {
                        context.Response.ContentType = "text/event-stream";
                        context.Response.Headers.CacheControl = "no-store";
                        context.Response.Headers["X-Accel-Buffering"] = "no";
                        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                    }

                    await EventAsync(context, "shape", page.Body, $"{page.Handle}:{page.NextOffset}", stop.Token).ConfigureAwait(false);
                }

                request = request with { Handle = page.Handle, Offset = ShapeOffset.Parse(page.NextOffset)!.Value, Live = page.UpToDate };
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Cancellation ends the connection; all admission and memory reservations return through finally/using.
        }
        catch (QosMemoryException) when (!stop.IsCancellationRequested)
        {
            using var tenant = QosContext.Enter(request.Database);
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter = "1";
            }

            return await FailAsync(context, new QuotaExceeded("organization memory budget exceeded", "tenant_memory"), stop.Token).ConfigureAwait(false);
        }
        catch (StorageBusyException error) when (!stop.IsCancellationRequested)
        {
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.RetryAfter = "1";
            }

            return await FailAsync(context, new Unavailable(error.Message, "storage_busy"), stop.Token).ConfigureAwait(false);
        }
        catch (IOException) when (context.Response.HasStarted)
        {
            context.Abort();
        }
        finally
        {
            Interlocked.Decrement(ref _active);
            _connections.Add(-1);
        }

        return TypedResults.Empty;
    }

    private async ValueTask<IResult> FailAsync(HttpContext context, Failure failure, CancellationToken cancellationToken)
    {
        if (!context.Response.HasStarted)
        {
            return ErrorResponse.From(failure);
        }

        var detail = failure switch
        {
            NotFound error => new ErrorDetail(error.Code, error.Message),
            Validation error => new ErrorDetail(error.Code, error.Message),
            QuotaExceeded error => new ErrorDetail(error.Code, error.Message),
            Conflict error => new ErrorDetail(error.Code, error.Message),
            Unavailable error => new ErrorDetail(error.Code, error.Message),
            _ => new ErrorDetail("subscription_closed", "the subscription closed"),
        };
        try
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new ErrorBody(detail), TerraBaseJsonContext.Default.ErrorBody);
            await EventAsync(context, "error", body, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or QosMemoryException || (error is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            context.Abort();
        }

        return TypedResults.Empty;
    }

    private async Task EventAsync(HttpContext context, string kind, ReadOnlyMemory<byte> body, string? id, CancellationToken cancellationToken)
    {
        // Reserve before encoding the bounded header or renting the compaction buffer; release after flush.
        using var scratch = memory.TryReserve(MemoryLane.Foreground, EventMemory) ?? throw new QosMemoryException();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10), time);
        using var send = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var stream = context.Response.Body;
            await stream.WriteAsync(Encoding.UTF8.GetBytes($"event: {kind}\n{(id is null ? "" : $"id: {id}\n")}data: "), send.Token).ConfigureAwait(false);
            var buffer = ArrayPool<byte>.Shared.Rent(SendBufferBytes);
            try
            {
                // Physical CR/LF in validated JSON are insignificant whitespace; string line breaks are escaped.
                // Compact into bounded chunks so arbitrary document formatting cannot amplify network writes.
                var written = 0;
                while (!body.IsEmpty)
                {
                    var length = Math.Min(body.Length, SendBufferBytes - written);
                    foreach (var value in body.Span[..length])
                    {
                        if (value is not (byte)'\r' and not (byte)'\n')
                        {
                            buffer[written++] = value;
                        }
                    }

                    body = body[length..];
                    if (written == SendBufferBytes || (body.IsEmpty && written > 0))
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, written), send.Token).ConfigureAwait(false);
                        written = 0;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await stream.WriteAsync(EventEnd, send.Token).ConfigureAwait(false);
            await stream.FlushAsync(send.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            context.Abort();
            throw new IOException("subscription send timed out");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A cancelled event write can leave a partial frame; only idle feed cancellation may finish normally.
            context.Abort();
            throw;
        }
    }

    // CTS timers accept at most uint.MaxValue-1 milliseconds; bound a connection even if a token has an extreme exp.
    private static TimeSpan MaxDelay(TimeSpan remaining) => remaining <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Min(remaining.TotalMilliseconds, uint.MaxValue - 1));
}

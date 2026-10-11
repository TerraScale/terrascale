using System.Buffers;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

internal sealed class ChangeFeedService : IChangeFeedService
{
    private const int MaxResponses = 16;
    private const long PageBytes = 2 * 1024 * 1024;
    // Covers stored UTF-16/UTF-8, validation, serialization growth and the final response copy.
    private const long ReservedBytes = 32 * 1024 * 1024;
    private static readonly QuotaExceeded Busy = new("change feed reads are at capacity; retry later", "change_feed_busy");
    private static readonly Conflict Expired = new("resume token no longer names this collection's retained local history", "resume_expired");
    private readonly IStorageEngine _engine;
    private readonly IMemoryBudget _memory;
    private readonly Counter<long> _pages;
    private readonly Counter<long> _refused;
    private int _active;

    public ChangeFeedService(IStorageEngine engine, IMemoryBudget memory, IMeterFactory meters)
    {
        _engine = engine;
        _memory = memory;
        var meter = meters.Create(Telemetry.MeterName);
        _pages = meter.CreateCounter<long>("terrabase.change_feed.pages", "{page}", "Collection change feed pages prepared.");
        _refused = meter.CreateCounter<long>("terrabase.change_feed.refused", "{request}", "Collection change feed reads refused at capacity.");
        meter.CreateObservableGauge("terrabase.change_feed.active", () => Volatile.Read(ref _active), "{response}", "Change feed responses holding admission.");
    }

    public async ValueTask<IResult> ReadAsync(
        TenantContext database, string collection, string? resumeToken, int limit, CancellationToken cancellationToken)
    {
        if (resumeToken is not null && Parse(resumeToken) is null)
        {
            return ErrorResponse.From(new Validation("resume_token must be an opaque token returned by this feed", "invalid_resume_token"));
        }

        if (Interlocked.Increment(ref _active) > MaxResponses)
        {
            Interlocked.Decrement(ref _active);
            _refused.Add(1);
            return ErrorResponse.From(Busy);
        }

        var reservation = _memory.TryReserve(MemoryLane.Foreground, ReservedBytes);
        var handedOff = false;
        try
        {
            if (reservation is null)
            {
                _refused.Add(1);
                return ErrorResponse.From(Busy);
            }

            if (await _engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return ErrorResponse.From(DocumentRoute.UnknownDatabase);
            }

            byte[] body;
            var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                if (await transaction.GetCollectionAsync(collection, cancellationToken).ConfigureAwait(false) is not { } info)
                {
                    return ErrorResponse.From(new NotFound("the collection does not exist", "unknown_collection"));
                }

                var lineage = Identify(database, collection, store.Incarnation.ToString(), info.Generation);
                var floor = await transaction.GetHistoryFloorAsync(cancellationToken).ConfigureAwait(false);
                var head = await transaction.GetOffsetAsync(cancellationToken).ConfigureAwait(false);
                var position = resumeToken is null ? new Position(lineage, floor, long.MaxValue) : Parse(resumeToken)!;
                if (position.Lineage != lineage || position.Offset < floor || position.Offset > head)
                {
                    return ErrorResponse.From(Expired);
                }

                var page = await transaction.ReadCollectionChangesAsync(
                    collection, position.Offset, position.Sequence, limit, PageBytes, cancellationToken).ConfigureAwait(false);
                var next = page.HasMore
                    ? new Position(lineage, page.Changes[^1].Offset, page.Changes[^1].Sequence)
                    : new Position(lineage, head, long.MaxValue);
                body = Serialize(page, next);
            }

            _pages.Add(1);
            var result = new AdmittedPage(this, reservation, body);
            handedOff = true;
            return result;
        }
        catch (QueryCapacityException)
        {
            _refused.Add(1);
            return ErrorResponse.From(Busy);
        }
        finally
        {
            if (!handedOff)
            {
                reservation?.Dispose();
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static string Identify(TenantContext database, string collection, string incarnation, long generation) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0',
            "TB-CHANGE-FEED-V1", database.OrganizationId, database.ProjectId, database.DatabaseId, collection,
            incarnation, DocumentRoute.Decimal(generation)))));

    private static Position? Parse(string token)
    {
        if (token.Length > 128)
        {
            return null;
        }

        var parts = token.Split('.');
        return parts is ["v1", var lineage, var offset, var sequence]
            && lineage.Length == 64 && lineage.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            && Number(offset) is { } o && Number(sequence) is { } s ? new Position(lineage, o, s) : null;
    }

    private static long? Number(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
        && value >= 0 && DocumentRoute.Decimal(value) == text ? value : null;

    private static byte[] Serialize(CollectionChangePage page, Position next)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("changes");
            foreach (var change in page.Changes)
            {
                var document = change.Document;
                writer.WriteStartObject();
                writer.WriteString("operation", document.Deleted ? "delete" : "upsert");
                writer.WriteString("id", document.Id);
                writer.WriteString("offset", DocumentRoute.Decimal(change.Offset));
                writer.WriteString("sequence", DocumentRoute.Decimal(change.Sequence));
                writer.WritePropertyName("document");
                if (document.Deleted)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    // Storage validates restored JSON too. Preserve the exact stored bytes.
                    writer.WriteRawValue(document.Json.Span, skipInputValidation: true);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("resume_token", next.ToString());
            writer.WriteBoolean("has_more", page.HasMore);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private sealed record Position(string Lineage, long Offset, long Sequence)
    {
        public override string ToString() => $"v1.{Lineage}.{DocumentRoute.Decimal(Offset)}.{DocumentRoute.Decimal(Sequence)}";
    }

    private sealed class AdmittedPage(ChangeFeedService service, IMemoryReservation reservation, byte[] body) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            try
            {
                httpContext.Response.Headers.CacheControl = "no-store";
                await TypedResults.Bytes(body, DocumentRoute.JsonMediaType).ExecuteAsync(httpContext).ConfigureAwait(false);
            }
            finally
            {
                reservation.Dispose();
                Interlocked.Decrement(ref service._active);
            }
        }
    }
}

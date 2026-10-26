using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Tenancy;

namespace TerraBase.Features.Documents;

/// <summary>A collection's declared expiry member.</summary>
/// <param name="Path">JSON Pointer of the expiry member, or null when documents never expire.</param>
public sealed record CollectionTtlBody(string? Path);

/// <summary>A collection's TTL or why its request failed.</summary>
public readonly union CollectionTtlResult(CollectionTtlBody, NotFound, PayloadTooLarge, Unavailable);

internal sealed partial class DocumentService
{
    private const int SweepPage = 100;
    private static readonly string[] TtlDateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd't'HH:mm:ss"];

    public ValueTask<CollectionTtlResult> GetCollectionTtlAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        ReadCertifiedAsync<CollectionTtlResult>(database, async _ =>
        {
            if (await BeginAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
            {
                return DocumentRoute.UnknownDatabase;
            }

            await using (transaction.ConfigureAwait(false))
            {
                return await transaction.GetCollectionAsync(name, cancellationToken).ConfigureAwait(false) is null
                    ? UnknownCollection
                    : new CollectionTtlBody(await transaction.GetCollectionTtlAsync(name, cancellationToken).ConfigureAwait(false));
            }
        }, static () => DocumentRoute.UnknownDatabase, cancellationToken);

    public async ValueTask<CollectionTtlResult> SetCollectionTtlAsync(TenantContext database, string name, string? path, CancellationToken cancellationToken)
    {
        if (await BeginAsync(database, write: true, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return DocumentRoute.UnknownDatabase;
        }

        await using (transaction.ConfigureAwait(false))
        {
            try
            {
                if (!await transaction.SetCollectionTtlAsync(name, path, cancellationToken).ConfigureAwait(false))
                {
                    return UnknownCollection;
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChangeGroupCapacityException)
            {
                return ChangeGroupCapacity;
            }
            catch (StorageWriteLimitException)
            {
                return WriteLimits;
            }
            catch (OverflowException)
            {
                return OffsetExhausted;
            }
        }

        return new CollectionTtlBody(path);
    }

    /// <summary>
    /// Deletes, through <see cref="WriteAsync"/>, every document of <paramref name="database"/> whose collection declares a TTL
    /// member holding an RFC 3339 UTC time before <paramref name="now"/>; returns how many it deleted. A document changed
    /// since it was read keeps its new version: each delete is conditional on the version the sweep saw.
    /// </summary>
    public async ValueTask<int> SweepExpiredAsync(TenantContext database, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deleted = 0;
        string? afterCollection = null;
        bool moreCollections;
        do
        {
            if (await ListCollectionsAsync(database, afterCollection, SweepPage, cancellationToken).ConfigureAwait(false) is not CollectionPage collections)
            {
                return deleted;
            }

            foreach (var collection in collections.Collections)
            {
                if (await GetCollectionTtlAsync(database, collection.Name, cancellationToken).ConfigureAwait(false) is CollectionTtlBody { Path: { } path })
                {
                    deleted += await SweepCollectionAsync(database, collection.Name, path, now, cancellationToken).ConfigureAwait(false);
                }
            }

            afterCollection = collections.Collections.Count > 0 ? collections.Collections[^1].Name : afterCollection;
            moreCollections = collections.HasMore;
        }
        while (moreCollections);

        return deleted;
    }

    private async ValueTask<int> SweepCollectionAsync(TenantContext database, string collection, string path, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (QueryParser.CheckPointer(path, "invalid_ttl", "invalid") is not IReadOnlyList<string> segments)
        {
            return 0;
        }

        // Include the whole UTC day so lowercase 't' timestamps are candidates too; parsing confirms the exact expiry.
        var member = new DocumentPath(segments);
        var filter = new CompareFilter(member, Comparison.Less,
            new ScalarValue(ScalarKind.Text, now.UtcDateTime.Date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        var deleted = 0;
        QueryPosition? after = null;
        QueryPage page;
        do
        {
            if (await engine.OpenDatabaseAsync(database, cancellationToken).ConfigureAwait(false) is not { } store)
            {
                return deleted;
            }

            // Read through the engine, not the metered query path: the sweep is not the customer's read.
            var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
            await using (transaction.ConfigureAwait(false))
            {
                page = await transaction.QueryAsync(new DocumentQuery(collection, filter, [], after, SweepPage, 1024 * 1024), cancellationToken).ConfigureAwait(false);
            }

            foreach (var row in page.Rows)
            {
                var document = row.Document;
                if (!Expired(document.Json, segments, now))
                {
                    continue;
                }

                var version = ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json);
                if (await WriteAsync(database, [new DeleteOperation(collection, document.Id, new Precondition([version], IfMatchAny: false, IfNoneMatch: false))],
                    indexErrors: false, cancellationToken).ConfigureAwait(false) is WriteResult)
                {
                    deleted++;
                }
            }

            after = page.Rows.Count > 0 ? new QueryPosition([], page.Rows[^1].Document.Id) : after;
        }
        while (page.HasMore);

        return deleted;
    }

    private static bool Expired(ReadOnlyMemory<byte> json, IReadOnlyList<string> segments, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var value = document.RootElement;
        foreach (var segment in segments)
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value))
            {
                return false;
            }
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } timestamp)
        {
            return false;
        }

        var suffix = timestamp.EndsWith('Z') || timestamp.EndsWith('z') ? 1 : timestamp.EndsWith("+00:00", StringComparison.Ordinal) ? 6 : 0;
        var end = timestamp.Length - suffix;
        // Validate the exact date/time and optional decimal fraction before parsing, preserving supported sub-tick fractions.
        return suffix > 0 && end >= 19
            && DateTimeOffset.TryParseExact(timestamp.AsSpan(0, 19), TtlDateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _)
            && (end == 19 || (timestamp[19] == '.' && end > 20 && timestamp.AsSpan(20, end - 20).IndexOfAnyExceptInRange('0', '9') < 0))
            && DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires)
            && expires < now;
    }
}

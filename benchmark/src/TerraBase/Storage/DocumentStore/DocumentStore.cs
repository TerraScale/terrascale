using System.Diagnostics;
using System.Text.Json;

namespace TerraBase;

/// <summary>A document store that owns the engine's storage port.</summary>
public sealed class DocumentStore : IDocumentStore
{
    private readonly IStorageProvider provider;
    private readonly MetricsRegistry metrics;

    /// <summary>Creates a store over an abstract storage provider.</summary>
    /// <param name="provider">The storage provider.</param>
    /// <param name="metrics">The metrics registry receiving operation facts.</param>
    public DocumentStore(IStorageProvider provider, MetricsRegistry metrics)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    /// <inheritdoc />
    public async ValueTask<Document?> ReadAsync(DocumentKey key, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.ReadAsync(key, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Read, result is null ? Outcome.NotFound : Outcome.Success, Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Read, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Read, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<StorageWriteResult> UpsertAsync(
        DocumentKey key,
        JsonElement body,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.UpsertAsync(key, body, precondition == default ? DocumentPrecondition.Any : precondition, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Write, ToMetricOutcome(result.Outcome), Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Write, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Write, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask<StorageWriteResult> DeleteAsync(
        DocumentKey key,
        DocumentPrecondition precondition = default,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var result = await provider.DeleteAsync(key, precondition == default ? DocumentPrecondition.Any : precondition, cancellationToken).ConfigureAwait(false);
            metrics.RecordRequest(Region.Unknown, Operation.Delete, ToMetricOutcome(result.Outcome), Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (OperationCanceledException)
        {
            metrics.RecordRequest(Region.Unknown, Operation.Delete, Outcome.Timeout, Stopwatch.GetElapsedTime(started));
            throw;
        }
        catch
        {
            metrics.RecordRequest(Region.Unknown, Operation.Delete, Outcome.Error, Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    /// <inheritdoc />
    public IAsyncEnumerable<Document> ScanAsync(
        TenantId tenant,
        CollectionId collection,
        CancellationToken cancellationToken = default) => provider.ScanAsync(tenant, collection, cancellationToken);

    /// <summary>Releases the underlying storage provider.</summary>
    public ValueTask DisposeAsync() => provider.DisposeAsync();

    private static Outcome ToMetricOutcome(WriteOutcome outcome) => outcome switch
    {
        WriteOutcome.Conflict => Outcome.Conflict,
        WriteOutcome.NotFound => Outcome.NotFound,
        _ => Outcome.Success,
    };
}

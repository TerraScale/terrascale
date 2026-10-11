using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Auth.Store;

/// <summary>Result of one storage-authority readiness probe.</summary>
/// <param name="Healthy">Whether the authority can serve ts-auth reads and conditional writes.</param>
/// <param name="Region">The authority region that answered, when known.</param>
public sealed record StorageHealthResult(bool Healthy, string? Region);

/// <summary>Probes the TerraBase authority that backs every ts-auth replica.</summary>
public interface IStorageHealth
{
    /// <summary>Checks the authority. Implementations must not report healthy without a successful round trip.</summary>
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Verifies the configured database, required collections, and real create/read/conditional-delete access.
/// No setup or identity repair happens during probes. Exceptions are handled by the host readiness boundary.
/// </summary>
public sealed class TerraBaseStorageHealth(TerraBaseDatabase database) : IStorageHealth
{
    private static readonly JsonElement ProbeDocument = JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["probe"] = true });
    // Reuse one synthetic address so repeated probes do not accumulate tombstones.
    private readonly string probeId = Guid.NewGuid().ToString("D");

    /// <inheritdoc />
    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        // Concurrently, so the list can grow without the probe outgrowing its deadline.
        await Task.WhenAll(AuthStore.RequiredCollections.Select(collection => database.GetCollectionAsync(collection, cancellationToken))).ConfigureAwait(false);

        // An interrupted probe may have committed its create. Only this synthetic address
        // is safe to clean up; never compensate an ambiguous identity write this way.
        var previous = await database.GetDocumentAsync(AuthStore.HealthCollection, this.probeId, cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            await database.DeleteDocumentAsync(AuthStore.HealthCollection, this.probeId, TerraBasePrecondition.Matches(previous.Version), cancellationToken).ConfigureAwait(false);
        }

        var created = await database.PutDocumentAsync(AuthStore.HealthCollection, this.probeId, ProbeDocument, TerraBasePrecondition.Absent, cancellationToken).ConfigureAwait(false);
        var read = await database.GetDocumentAsync(AuthStore.HealthCollection, this.probeId, cancellationToken).ConfigureAwait(false);
        if (read is null || read.Version != created.Version || !read.Document.TryGetProperty("probe", out var probe) || probe.ValueKind != JsonValueKind.True)
        {
            return new StorageHealthResult(false, null);
        }

        await database.DeleteDocumentAsync(AuthStore.HealthCollection, this.probeId, TerraBasePrecondition.Matches(read.Version), cancellationToken).ConfigureAwait(false);
        return new StorageHealthResult(true, null);
    }
}

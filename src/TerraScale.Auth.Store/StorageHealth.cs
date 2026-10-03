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
/// Fails closed until the native-v1 store (TS-428) supplies a real probe:
/// readiness must never claim a dependency it has not reached.
/// </summary>
public sealed class UnavailableStorageHealth : IStorageHealth
{
    /// <inheritdoc />
    public Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new StorageHealthResult(false, null));
}

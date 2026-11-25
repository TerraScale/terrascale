namespace TerraScale.Authentication.Storage;

public sealed record StorageHealthResult(bool Healthy, string? Region);

public interface IStorageHealth
{
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed class TerraBaseStorageHealth(AuthSchema schema)
{
    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken) =>
        new(await schema.CurrentAsync(cancellationToken), null);
}

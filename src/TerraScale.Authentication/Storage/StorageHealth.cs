using Dapper;

namespace TerraScale.Authentication.Storage;

public sealed record StorageHealthResult(bool Healthy, string? Region);

public interface IStorageHealth
{
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record StorageProbe(string Id, DateTimeOffset CheckedAt)
{
    public string? Version { get; init; }
}

public sealed class TerraBaseStorageHealth(AuthSchema schema, AuthData data, TimeProvider clock)
{
    private readonly string probeId = AuthIds.New();

    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!await schema.CurrentAsync(cancellationToken))
        {
            return new(false, null);
        }

        if (await ReadAsync(cancellationToken) is { } previous)
        {
            await data.SaveAsync(Change.Delete(previous), cancellationToken);
        }

        var probe = new StorageProbe(probeId, clock.GetUtcNow());
        var created = Change.Insert(probe);
        return await data.SaveAsync(created, cancellationToken)
            && await ReadAsync(cancellationToken) is { } read && read.Version == created.Version
            && await data.SaveAsync(Change.Delete(read), cancellationToken)
                ? new(true, null)
                : new(false, null);
    }

    private Task<Versioned<StorageProbe>?> ReadAsync(CancellationToken cancellationToken) =>
        data.ReadAsync(async connection => Versioned.Of(
            await connection.QuerySingleOrDefaultAsync<StorageProbe>("SELECT * FROM storage_probes WHERE id = @probeId", new { probeId })), cancellationToken);
}

using Microsoft.EntityFrameworkCore;

namespace TerraScale.Authentication.Storage;

public sealed record StorageHealthResult(bool Healthy, string? Region);

public interface IStorageHealth
{
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed record StorageProbe(string Id, DateTimeOffset CheckedAt);

public sealed class TerraBaseStorageHealth(IDbContextFactory<AuthDbContext> contexts, AuthData data, TimeProvider clock)
{
    private readonly string probeId = AuthIds.New();

    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        await using (var db = await contexts.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if ((await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
            {
                return new(false, null);
            }
        }

        if (await this.ReadAsync(cancellationToken).ConfigureAwait(false) is { } previous)
        {
            _ = await data.SaveAsync(Change.Delete(previous), cancellationToken).ConfigureAwait(false);
        }

        var probe = new StorageProbe(this.probeId, clock.GetUtcNow());
        var created = Change.Insert(probe);
        return await data.SaveAsync(created, cancellationToken).ConfigureAwait(false)
            && await this.ReadAsync(cancellationToken).ConfigureAwait(false) is { } read && read.Version == created.Version
            && await data.SaveAsync(Change.Delete(read), cancellationToken).ConfigureAwait(false)
                ? new(true, null)
                : new(false, null);
    }

    private Task<Versioned<StorageProbe>?> ReadAsync(CancellationToken cancellationToken) =>
        data.ReadAsync((db, token) => db.StorageProbes.Where(probe => probe.Id == this.probeId).WithVersion().SingleOrDefaultAsync(token), cancellationToken);
}

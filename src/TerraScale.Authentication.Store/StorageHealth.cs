using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Store;

public sealed record StorageHealthResult(bool Healthy, string? Region);

public interface IStorageHealth
{
    Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken);
}

public sealed class TerraBaseStorageHealth(TerraBaseSqlDatabase database) : IStorageHealth
{
    private static readonly JsonElement ProbeDocument = JsonSerializer.SerializeToElement(new Dictionary<string, bool> { ["probe"] = true });
    private readonly SqlDocuments sql = new(database);
    private readonly string probeId = AuthStore.NewId();

    public async Task<StorageHealthResult> CheckAsync(CancellationToken cancellationToken)
    {
        var tables = this.sql.TablesAsync(cancellationToken);
        var previousRead = this.sql.GetAsync(AuthStore.HealthCollection, this.probeId, cancellationToken);
        await Task.WhenAll(tables, previousRead).ConfigureAwait(false);
        var present = await tables.ConfigureAwait(false);
        if (!present.Contains(SqlDocuments.ChecksTable) || !AuthStore.RequiredCollections.All(present.Contains))
        {
            return new StorageHealthResult(false, null);
        }

        if (await previousRead.ConfigureAwait(false) is { } previous)
        {
            _ = await this.sql.WriteAsync([SqlWrite.Delete(AuthStore.HealthCollection, this.probeId, previous.Version)], null, cancellationToken).ConfigureAwait(false);
        }

        var created = await this.sql.WriteAsync([SqlWrite.Create(AuthStore.HealthCollection, ProbeDocument, this.probeId)], null, cancellationToken).ConfigureAwait(false);
        var read = await this.sql.GetAsync(AuthStore.HealthCollection, this.probeId, cancellationToken).ConfigureAwait(false);
        if (created is null || read is null || read.Version != created[0] || !read.Document.TryGetProperty("probe", out var probe) || probe.ValueKind != JsonValueKind.True)
        {
            return new StorageHealthResult(false, null);
        }

        return await this.sql.WriteAsync([SqlWrite.Delete(AuthStore.HealthCollection, this.probeId, read.Version)], null, cancellationToken).ConfigureAwait(false) is null
            ? new StorageHealthResult(false, null)
            : new StorageHealthResult(true, null);
    }
}

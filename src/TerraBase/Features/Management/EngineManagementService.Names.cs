using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private const int MaxNameChain = 8;

    public ValueTask<bool> ReconcileDatabaseNameAsync(
        TenantContext database, string name, string home, long version, Func<TenantContext, CancellationToken, Task<string?>> resolve, CancellationToken cancellationToken) =>
        WriteAsync<bool>(async transaction =>
        {
            if (database.ProjectId is null || database.DatabaseId is null || !DatabaseName.TryParse(name, out _))
            {
                return false;
            }

            var chain = new Dictionary<string, string>(StringComparer.Ordinal) { [database.DatabaseId] = name };
            var stored = new HashSet<string>(StringComparer.Ordinal);
            if (await CatalogTables.DatabaseAsync(transaction, database.ProjectId, database.DatabaseId, cancellationToken).ConfigureAwait(false) is { } root)
            {
                if (root.Home(options.Region) != home || home == options.Region || root.PlacementVersion > version)
                {
                    return false;
                }

                if (root.Name == name)
                {
                    return true;
                }

                stored.Add(root.Id);
            }

            var target = name;
            while (await CatalogTables.DatabaseNamedAsync(transaction, database.ProjectId, target, cancellationToken).ConfigureAwait(false) is { } holder
                && !chain.ContainsKey(holder))
            {
                if (chain.Count == MaxNameChain
                    || await CatalogTables.DatabaseAsync(transaction, database.ProjectId, holder, cancellationToken).ConfigureAwait(false) is not { } record
                    || record.Home(options.Region) != home || home == options.Region)
                {
                    return false;
                }

                var next = await resolve(database with { DatabaseId = holder }, cancellationToken).ConfigureAwait(false);
                if (!DatabaseName.TryParse(next, out _) || chain.ContainsValue(next))
                {
                    return false;
                }

                chain.Add(holder, next);
                stored.Add(holder);
                target = next;
            }

            foreach (var id in stored)
            {
                await CatalogTables.RenameDatabaseAsync(transaction, id, $":{id}", cancellationToken).ConfigureAwait(false);
            }

            foreach (var id in stored)
            {
                await CatalogTables.RenameDatabaseAsync(transaction, id, chain[id], cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken);
}

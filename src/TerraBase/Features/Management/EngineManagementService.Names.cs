using TerraBase.Engine;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private const int MaxNameChain = 8;

    private sealed record NameMember(string Id, string Name, string Home, long Version);

    private sealed record NamePlan(IReadOnlyDictionary<string, NameMember?> Members, IReadOnlyDictionary<string, string> Names);

    public async ValueTask<bool> ReconcileDatabaseNameAsync(
        TenantContext database, string name, string home, long version, Func<TenantContext, CancellationToken, Task<string?>> resolve, CancellationToken cancellationToken)
    {
        if (database.ProjectId is null || database.DatabaseId is null || !DatabaseName.TryParse(name, out _) || home == options.Region)
        {
            return false;
        }

        var plan = await PrepareDatabaseNamesAsync(database, name, home, version, resolve, cancellationToken).ConfigureAwait(false);
        var applied = plan is not null && await ApplyDatabaseNamesAsync(database, plan, cancellationToken).ConfigureAwait(false);
        if (applied)
        {
            ClearKeptNames(database.DatabaseId);
        }
        else
        {
            KeepDatabaseName(database.DatabaseId, name);
        }

        return applied;
    }

    private async ValueTask<NamePlan?> PrepareDatabaseNamesAsync(
        TenantContext database, string name, string home, long version, Func<TenantContext, CancellationToken, Task<string?>> resolve, CancellationToken cancellationToken)
    {
        var members = new Dictionary<string, NameMember?>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal) { [database.DatabaseId!] = name };
        var target = name;
        while (true)
        {
            var snapshot = await ReadAsync<(ManagedDatabase? Root, ManagedDatabase? Holder)?>(async transaction =>
            {
                var root = await CatalogTables.DatabaseAsync(transaction, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false);
                var holder = await CatalogTables.DatabaseNamedAsync(transaction, database.ProjectId!, target, cancellationToken).ConfigureAwait(false);
                return (root, holder is null ? null : await CatalogTables.DatabaseAsync(transaction, database.ProjectId!, holder, cancellationToken).ConfigureAwait(false));
            }, static _ => null, cancellationToken).ConfigureAwait(false);
            if (snapshot is not { } read)
            {
                return null;
            }

            var root = read.Root is { } row ? new NameMember(row.Id, row.Name, row.Home(options.Region), row.PlacementVersion) : null;
            if (members.TryGetValue(database.DatabaseId!, out var original) && original != root
                || root is not null && (root.Home != home || root.Version > version))
            {
                return null;
            }

            members[database.DatabaseId!] = root;
            if (read.Holder is not { } holder || names.ContainsKey(holder.Id))
            {
                return new NamePlan(members, names);
            }

            if (names.Count == MaxNameChain || holder.Home(options.Region) != home)
            {
                return null;
            }

            members.Add(holder.Id, new NameMember(holder.Id, holder.Name, holder.Home(options.Region), holder.PlacementVersion));
            var next = await resolve(database with { DatabaseId = holder.Id }, cancellationToken).ConfigureAwait(false);
            if (!DatabaseName.TryParse(next, out _) || names.ContainsValue(next))
            {
                return null;
            }

            names.Add(holder.Id, next);
            target = next;
        }
    }

    private ValueTask<bool> ApplyDatabaseNamesAsync(TenantContext database, NamePlan plan, CancellationToken cancellationToken)
    {
        if (plan.Members.All(member => member.Value is null || member.Value.Name == plan.Names[member.Key]))
        {
            return ValueTask.FromResult(true);
        }

        return WriteAsync<bool>(async transaction =>
        {
            foreach (var (id, expected) in plan.Members)
            {
                var current = await CatalogTables.DatabaseAsync(transaction, database.ProjectId!, id, cancellationToken).ConfigureAwait(false);
                var member = current is null ? null : new NameMember(current.Id, current.Name, current.Home(options.Region), current.PlacementVersion);
                if (member != expected)
                {
                    return false;
                }
            }

            foreach (var target in plan.Names.Values)
            {
                if (await CatalogTables.DatabaseNamedAsync(transaction, database.ProjectId!, target, cancellationToken).ConfigureAwait(false) is { } holder
                    && !plan.Members.ContainsKey(holder))
                {
                    return false;
                }
            }

            foreach (var (id, member) in plan.Members)
            {
                if (member is not null)
                {
                    await CatalogTables.RenameDatabaseAsync(transaction, id, $":{id}", cancellationToken).ConfigureAwait(false);
                }
            }

            foreach (var (id, member) in plan.Members)
            {
                if (member is not null)
                {
                    await CatalogTables.RenameDatabaseAsync(transaction, id, plan.Names[id], cancellationToken).ConfigureAwait(false);
                    ClearKeptNames(id);
                }
            }

            return true;
        }, static _ => false, cancellationToken);
    }
}

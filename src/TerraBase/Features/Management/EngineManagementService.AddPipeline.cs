using System.Diagnostics.Metrics;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Catalog;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Management;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.Regions;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{

    private async ValueTask<AddDatabaseResult> AddDatabaseAsync(
        string organizationId, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string name, string? sourceId, string? branchedFrom, CancellationToken cancellationToken,
        string? id = null, DatabasePlacement? placement = null, DateTimeOffset? createdAt = null, long? ttlSeconds = null, DatabaseIdentity? identity = null)
    {
        var allowance = !PlatformDatabases.IsPlatform(organizationId) && (sourceId is not null || (placement?.Home ?? options.Region) == options.Region)
            ? await AllowanceAsync(organizationId, cancellationToken).ConfigureAwait(false)
            : null;

        var precheck = await PrecheckAsync(project, name, allowance, cancellationToken).ConfigureAwait(false);

        if (precheck is not TenantContext tenant)
        {
            return Refused(precheck);
        }

        StorageQuotaGroupLease? lease = null;
        if (sourceId is not null)
        {
            var entered = await quota.Value.EnterOrganizationsAsync([tenant.OrganizationId], cancellationToken).ConfigureAwait(false);
            if (entered is not StorageQuotaGroupLease granted)
            {
                return entered switch
                {
                    Unavailable error => error,
                    StorageQuotaGroupLease => throw new UnreachableException(),
                };
            }

            lease = granted;
        }

        using var admission = lease;
        placement ??= new DatabasePlacement(options.Region, [], 1);

        var database = new ManagedDatabase(
            id ?? NewId(), tenant.ProjectId!, name, identity is null ? branchedFrom : identity.BranchedFrom, identity?.CreatedAt ?? createdAt ?? time.GetUtcNow(), placement.Home, placement.Replicas.Count == 0 ? null : [.. placement.Replicas], placement.Version,
            placement.AllRegions, ExpiresAt: identity is null ? DatabaseTtl.ExpiresAt(time.GetUtcNow(), ttlSeconds) : identity.ExpiresAt);
        var storage = tenant with { DatabaseId = database.Id };
        var preparation = await PrepareDatabaseAsync(storage, sourceId is null ? null : tenant with { DatabaseId = sourceId }, cancellationToken).ConfigureAwait(false);
        if (preparation is not bool created || !created)
        {
            return preparation switch
            {
                bool => ManagementErrors.NotFound,
                Unavailable error => error,
            };
        }

        AddDatabasePrecheck registered;
        try
        {
            if (admission is not null
                && admission.RequiresAccounting(tenant.OrganizationId)
                && await admission.CheckAsync(tenant.OrganizationId,
                    await quota.Value.ReadBytesAsync(storage, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false) is not Done and var exceeded)
            {
                await AbandonDatabaseAsync(storage).ConfigureAwait(false);
                return exceeded switch
                {
                    QuotaExceeded error => error,
                    Unavailable error => error,
                    Done => throw new UnreachableException(),
                };
            }

            registered = await RegisterAsync(project, database, allowance, cancellationToken).ConfigureAwait(false);

            if (registered is TenantContext)
            {
                checkpoint("create.committed", storage);
            }
        }
        catch (QueryCapacityException)
        {
            await AbandonDatabaseAsync(storage).ConfigureAwait(false);
            return StorageQuota.AccountingUnavailable;
        }
        catch
        {
            await AbandonDatabaseAsync(storage).ConfigureAwait(false);
            throw;
        }

        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is not TenantContext)
        {
            return await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false) switch
            {
                Done => Refused(registered),
                Unavailable error => error,
            };
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        checkpoint("create.registered", storage);
        admission?.Dispose();
        await atlas.QueueAsync(database.ProjectId, database.Name, database.Home(options.Region), CancellationToken.None).ConfigureAwait(false);
        return new AddedDatabase(database, storage);
    }

    private ValueTask<AddDatabasePrecheck> PrecheckAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string? name, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
        ReadAsync<AddDatabasePrecheck>(
            async transaction => await CanAddDatabaseAsync(transaction, project, name, cancellationToken, allowance).ConfigureAwait(false) switch
            {
                TenantContext free => free,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            },
            static error => error, cancellationToken);

    private ValueTask<AddDatabasePrecheck> RegisterAsync(
        Func<IStorageTransaction, ValueTask<ProjectTenant>> project, ManagedDatabase database, AllowanceCheck? allowance, CancellationToken cancellationToken) =>
        WriteAsync<AddDatabasePrecheck>(async transaction =>
        {
            var registration = await RegisterDatabaseAsync(transaction, project, database, allowance, cancellationToken)
                .ConfigureAwait(false);

            return registration switch
            {
                TenantContext free => free,
                NotFound error => error,
                Forbidden error => error,
                Conflict error => error,
            };
        }, static error => error, cancellationToken);

    private static AddDatabaseResult Refused(AddDatabasePrecheck check) => check switch
    {
        NotFound error => error,
        Forbidden error => error,
        Conflict error => error,
        Unavailable error => error,
        TenantContext => throw new UnreachableException(),
    };

    private async ValueTask<RecordedDeletion> PrepareDatabaseAsync(TenantContext storage, TenantContext? source, CancellationToken cancellationToken)
    {
        checkpoint("create.prepare", storage);
        var recording = false;
        var publishing = false;
        RecordedDeletion decision = false;
        bool created;
        try
        {
            created = await engine.CreateDatabaseWithIntentAsync(storage, source, async token =>
            {
                recording = true;
                decision = await WriteAsync<RecordedDeletion>(async transaction =>
                {
                    if (await DatabasePurge.IsDeletedAsync(transaction, storage.DatabaseId!, token).ConfigureAwait(false)
                        || source is not null && await DatabasePurge.IsDeletedAsync(transaction, source.DatabaseId!, token).ConfigureAwait(false))
                    {
                        return false;
                    }

                    _ = await CatalogTables.PutPendingCleanupAsync(transaction, storage, token).ConfigureAwait(false);
                    return true;
                }, static error => error, token).ConfigureAwait(false);
                if (decision is not bool accepted || !accepted)
                {
                    return false;
                }

                checkpoint("create.intent", storage);
                publishing = true;
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (publishing)
            {
                _ = await FinishDatabaseCleanupAsync(storage, CancellationToken.None).ConfigureAwait(false);
            }
            else if (recording)
            {
                await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            }

            throw;
        }

        if (!created)
        {
            return decision;
        }

        checkpoint("create.files", storage);
        return true;
    }

    private async ValueTask<AddDatabaseCheck> RegisterDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, ManagedDatabase database,
        AllowanceCheck? allowance, CancellationToken cancellationToken)
    {
        if (await DatabasePurge.IsDeletedAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false))
        {
            return ManagementErrors.IdTaken;
        }

        var check = await CanAddDatabaseAsync(transaction, project, database.Name, cancellationToken, allowance).ConfigureAwait(false);
        if (check is TenantContext tenant)
        {
            await CatalogTables.PutDatabaseAsync(transaction, database, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, EngineManagedBackupService.BackedUpDatabases, database.Id, DatabaseOwner.Of(tenant with { DatabaseId = database.Id }),
                ManagementJsonContext.Default.DatabaseOwner, cancellationToken).ConfigureAwait(false);
            _ = await CatalogTables.DeletePendingCleanupAsync(transaction, database.Id, cancellationToken).ConfigureAwait(false);
        }

        return check;
    }

    private async ValueTask AbandonDatabaseAsync(TenantContext storage)
    {
        var registered = await ReadAsync<RecordedDeletion>(async transaction =>
            await CatalogTables.DatabaseAsync(transaction, storage.ProjectId!, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null
            && await CatalogTables.PendingCleanupAsync(transaction, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is null,
            static error => error, CancellationToken.None).ConfigureAwait(false);
        if (registered is not bool accepted)
        {
            return;
        }

        if (accepted)
        {
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            _ = await FinishDatabaseCleanupAsync(storage, CancellationToken.None, abandonedCreation: true).ConfigureAwait(false);
        }
    }

    private async ValueTask<AddDatabaseCheck> CanAddDatabaseAsync(
        IStorageTransaction transaction, Func<IStorageTransaction, ValueTask<ProjectTenant>> project, string? name, CancellationToken cancellationToken,
        AllowanceCheck? allowance = null)
    {
        if (ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseName != name)
        {
            return ManagementErrors.NotFound;
        }

        var resolved = await project(transaction).ConfigureAwait(false);
        if (resolved is not TenantContext tenant)
        {
            return resolved switch
            {
                NotFound error => error,
                Forbidden error => error,
                TenantContext => throw new UnreachableException(),
            };
        }

        if (await IsProjectClosedAsync(transaction, tenant.ProjectId!, cancellationToken).ConfigureAwait(false))
        {
            return ManagementErrors.NotFound;
        }

        if (name is not null && await CatalogTables.DatabaseNamedAsync(transaction, tenant.ProjectId!, name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return ManagementErrors.NameTaken;
        }

        return allowance is not null && await DatabaseLimitAsync(transaction, tenant, allowance, cancellationToken).ConfigureAwait(false) is { } limit
            ? limit
            : tenant;
    }

    private async ValueTask<Conflict?> DatabaseLimitAsync(IStorageTransaction transaction, TenantContext tenant, AllowanceCheck allowance, CancellationToken cancellationToken)
    {
        var allowed = allowance.Limit(tenant);
        if (allowed == long.MaxValue)
        {
            return null;
        }

        var homed = allowance.HomeCounts.Keys.ToList();
        var local = await CatalogTables.CountOrganizationDatabasesAsync(
            transaction, tenant.OrganizationId, cancellationToken, options.Region, homed).ConfigureAwait(false);
        var live = local + allowance.HomeCounts.Values.Sum();
        return live >= allowed
            ? ManagementErrors.DatabaseLimit(PlanName(tenant.Plan), allowed)
            : null;
    }

    private async ValueTask<AllowanceCheck> AllowanceAsync(string organizationId, CancellationToken cancellationToken)
    {
        var peers = await regionPeers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<string, long> homed = new(StringComparer.Ordinal);
        if (peers.Count > 0)
        {
            var counts = await Task.WhenAll(peers.Select(peer => remote.HomeCountAsync(peer.Address, organizationId, cancellationToken))).ConfigureAwait(false);
            for (var index = 0; index < peers.Count; index++)
            {
                if (counts[index] is { } count)
                {
                    homed[peers[index].Region] = count;
                }
            }
        }

        return new AllowanceCheck(databaseAllowance.Limit, homed);
    }


    private sealed record AllowanceCheck(Func<TenantContext, long> Limit, IReadOnlyDictionary<string, long> HomeCounts);

    private sealed record AddedDatabase(ManagedDatabase Database, TenantContext Tenant, bool Existing = false);

    private readonly union ProjectTenant(TenantContext, NotFound, Forbidden);

    private readonly union AddDatabaseCheck(TenantContext, NotFound, Forbidden, Conflict);

    private readonly union AddDatabasePrecheck(TenantContext, NotFound, Forbidden, Conflict, Unavailable);

    private readonly union AddDatabaseResult(AddedDatabase, NotFound, Forbidden, Conflict, QuotaExceeded, Unavailable);
}

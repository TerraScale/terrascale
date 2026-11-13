using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Backup;
using TerraBase.Features.Management.Backups;
using TerraBase.Features.Platform;
using TerraBase.Features.Qos;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Regions;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService(
    IStorageEngine engine, IOrphanDatabaseFiles orphanFiles, TimeProvider time, TerraBaseOptions options, ILogger<EngineManagementService> logger, DatabaseLifecycleCheckpoint checkpoint, IMeterFactory meters,
    RetentionAuthority retention, IProjectAuthorityVerifier projectAuthority, Lazy<IStorageQuota> quota, IRegionPeers regionPeers,
    Lazy<ICatalogGate> catalogGate, DatabaseAllowance databaseAllowance, Lazy<IBackupKeyShredding> shredding, RemoteDatabases remote,
    Lazy<IProjectFileErasure> fileErasure, ClosureFanout closureFanout)
    : IManagementService, ITenantDirectory, IHostedService, ICatalogGuard, IDatabaseFileCatalog, IShreddedDatabases
{
    private const string Catalog = ManagementCatalog.Kind;
    private const string CatalogVersionId = ManagementCatalog.VersionId;

    private const string CatalogVersion = ManagementCatalog.Version;

    internal const string RetiredDatabases = "retired_databases";
    private const long MaxPageBytes = 1024 * 1024;

    private const int CatalogPage = 100;

    private static readonly JsonTypeInfo<string> Id = TerraBaseJsonContext.Default.String;

    private volatile bool _catalogCurrent;


    private static async ValueTask<TenantContext?> ProjectTenantAsync(
        IStorageTransaction transaction, Organization? organization, string projectId, CancellationToken cancellationToken) =>
        organization is not null
        && (ProjectAccess.Current is not { } access || access.ProjectId == projectId && access.OrganizationId == organization.Id)
        && await CatalogTables.OrganizationProjectAsync(transaction, organization.Id, projectId, cancellationToken).ConfigureAwait(false) is not null
            ? new TenantContext(organization.Id, organization.Plan, projectId)
            : null;

    private static async ValueTask<ProjectTenant> AuthorizedProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken)
    {
        var access = await WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
        if (access is not Organization)
        {
            return access switch
            {
                NotFound error => error,
                Forbidden error => error,
                Organization => throw new UnreachableException(),
            };
        }

        return await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false) is { } tenant
            ? tenant
            : ManagementErrors.NotFound;
    }

    private static async ValueTask<TenantContext?> DatabaseTenantAsync(
        IStorageTransaction transaction, TenantContext? project, string databaseId, CancellationToken cancellationToken) =>
        project is not null
        && (ProjectAccess.Current is not { DatabaseName: not null } access || access.DatabaseId == databaseId)
        && await CatalogTables.DatabaseAsync(transaction, project.ProjectId!, databaseId, cancellationToken).ConfigureAwait(false) is not null
            ? project with { DatabaseId = databaseId }
            : null;

    internal static async ValueTask<TenantContext?> AuthorizedDatabaseTenantAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction,
            await ProjectTenantAsync(transaction, await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false), projectId, cancellationToken)
                .ConfigureAwait(false),
            databaseId, cancellationToken)
            .ConfigureAwait(false);

    internal static async ValueTask<TenantContext?> DatabaseTenantUncheckedAsync(
        IStorageTransaction transaction, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        await DatabaseTenantAsync(
            transaction, await OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false), databaseId, cancellationToken)
            .ConfigureAwait(false);

    internal static ValueTask<ManagedDatabase?> DatabaseUncheckedAsync(
        IStorageTransaction transaction, string projectId, string databaseId, CancellationToken cancellationToken) =>
        CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken);

    internal static async ValueTask<PlanTier> PlanUncheckedAsync(IStorageTransaction transaction, string organizationId, CancellationToken cancellationToken) =>
        (await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false))?.Plan ?? PlanTier.Hobby;

    private static async ValueTask<Organization?> GetOrganizationAsync(
        IStorageTransaction transaction, string userId, string organizationId, CancellationToken cancellationToken) =>
        await CatalogTables.OrganizationAsync(transaction, organizationId, cancellationToken).ConfigureAwait(false) is { } organization
            && (organization.CreatedBy == userId || ProjectActor(userId, organizationId)) ? organization : null;

    internal static async ValueTask<Project?> GetProjectAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { } access && access.ProjectId != projectId
            || await GetOrganizationAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await CatalogTables.OrganizationProjectAsync(transaction, organizationId, projectId, cancellationToken).ConfigureAwait(false);

    internal static async ValueTask<ManagedDatabase?> GetDatabaseAsync(
        IStorageTransaction transaction, string userId, string organizationId, string projectId, string databaseId, CancellationToken cancellationToken) =>
        ProjectAccess.Current is { DatabaseName: not null } access && access.DatabaseId != databaseId
            || await GetProjectAsync(transaction, userId, organizationId, projectId, cancellationToken).ConfigureAwait(false) is null
            ? null
            : await CatalogTables.DatabaseAsync(transaction, projectId, databaseId, cancellationToken).ConfigureAwait(false);

    private static async ValueTask<bool> AnyAsync(IStorageTransaction transaction, string kind, CancellationToken cancellationToken) =>
        (await transaction.ListAsync(kind, afterId: null, limit: 1, MaxPageBytes, cancellationToken).ConfigureAwait(false)).Records.Count > 0;

    private static async ValueTask<T?> GetAsync<T>(
        IStorageTransaction transaction, string kind, string id, JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        return await SystemCatalog.GetAsync(transaction, kind, id, type, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask PutAsync<T>(
        IStorageTransaction transaction, string kind, string id, T value, JsonTypeInfo<T> type, CancellationToken cancellationToken) =>
        await transaction.PutAsync(kind, id, EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(value, type), id, nested: true), cancellationToken).ConfigureAwait(false);

    private async ValueTask<T> ReadAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var accepted = await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false);
        if (accepted is not Done)
        {
            return accepted switch
            {
                Done => throw new UnreachableException(),
                Unavailable refused => unavailable(refused),
            };
        }

        return await ReadCatalogAsync(async transaction =>
            await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
            {
                Done => await read(transaction).ConfigureAwait(false),
                Unavailable stale => unavailable(stale),
            }, cancellationToken, file, line).ConfigureAwait(false);
    }

    public async ValueTask<CatalogCheck> CheckReadAsync(IStorageTransaction transaction, CancellationToken cancellationToken) =>
        await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false) != CatalogVersion
            ? ManagementErrors.CatalogUnsupported
            : new Done();

    private async ValueTask<T> ReadCatalogAsync<T>(Func<IStorageTransaction, ValueTask<T>> read, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: false, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    private async ValueTask<T> WriteAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, Func<Unavailable, T> unavailable, CancellationToken cancellationToken,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        return await EnsureCatalogAsync(cancellationToken).ConfigureAwait(false) switch
        {
            Done => await WriteCatalogAsync(async transaction =>
                await CheckReadAsync(transaction, cancellationToken).ConfigureAwait(false) switch
                {
                    Done => await write(transaction).ConfigureAwait(false),
                    Unavailable refused => unavailable(refused),
                }, cancellationToken, file: file, line: line).ConfigureAwait(false),
            Unavailable refused => unavailable(refused),
        };
    }

    private async ValueTask<T> WriteCatalogAsync<T>(
        Func<IStorageTransaction, ValueTask<T>> write, CancellationToken cancellationToken, Func<T, bool>? keep = null,
        [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await store.BeginTransactionAsync(write: true, cancellationToken, file, line).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var result = await write(transaction).ConfigureAwait(false);
            if (keep?.Invoke(result) != false)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    public async ValueTask<CatalogCheck> EnsureCatalogAsync(CancellationToken cancellationToken)
    {
        if (!catalogGate.Value.IsOpen)
        {
            return new Unavailable("this machine's catalog has not passed the restore gate", "catalog_unverified");
        }

        if (_catalogCurrent)
        {
            return new Done();
        }

        if (await ReadCatalogAsync(transaction => CheckReadAsync(transaction, cancellationToken), cancellationToken).ConfigureAwait(false) is Done)
        {
            _catalogCurrent = true;
            return new Done();
        }

        var accepted = await WriteCatalogAsync<CatalogCheck>(async transaction =>
        {
            var version = await GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken).ConfigureAwait(false);
            if (version == CatalogVersion)
            {
                return new Done();
            }

            if (version is not null || await CatalogTables.ExistAsync(transaction, cancellationToken).ConfigureAwait(false))
            {
                logger.ZLogError($"The management catalog has a format this build does not support; discard the affected data");
                return ManagementErrors.CatalogUnsupported;
            }

            await CatalogTables.CreateAsync(transaction, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, Catalog, CatalogVersionId, CatalogVersion, Id, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, cancellationToken, keep: static result => result is Done).ConfigureAwait(false);
        if (accepted is Done)
        {
            _catalogCurrent = true;
        }

        return accepted;
    }

    private string NewId() => EntityIds.New(time).ToString();

    private static string Key(string name) => CatalogTables.Key(name);
}


internal sealed record DatabaseOwner(string OrganizationId, string ProjectId, string DatabaseId)
{
    public static DatabaseOwner Of(TenantContext database)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new(database.OrganizationId, database.ProjectId!, database.DatabaseId!);
    }
}

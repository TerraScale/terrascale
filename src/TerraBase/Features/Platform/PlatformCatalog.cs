using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Platform;

internal sealed record PlatformDatabase(Ulid Id, Ulid ProjectId, string Name, string ProjectName);

internal sealed class PlatformCatalog(CatalogStore catalog, TenantDatabases databases, TerraBaseOptions options, TimeProvider time)
{
    public static readonly Ulid OrganizationId = Ulid.Parse(PlatformIdentity.OrganizationId);

    public static readonly Ulid AuthProjectId = Ulid.Parse(PlatformIdentity.AuthProjectId);

    public static readonly Ulid DashboardProjectId = Ulid.Parse(PlatformIdentity.DashboardProjectId);

    public static readonly Ulid PlatformProjectId = Ulid.Parse(PlatformIdentity.PlatformProjectId);

    public static IReadOnlyList<PlatformDatabase> Databases { get; } =
    [
        new(Ulid.Parse(PlatformIdentity.AuthDatabaseId), AuthProjectId, PlatformIdentity.AuthDatabaseName, PlatformIdentity.AuthProjectName),
        new(Ulid.Parse(PlatformIdentity.DashboardDatabaseId), DashboardProjectId, PlatformIdentity.DashboardDatabaseName, PlatformIdentity.DashboardProjectName),
        new(Ulid.Parse(PlatformIdentity.PlatformDatabaseId), PlatformProjectId, PlatformIdentity.PlatformDatabaseName, PlatformIdentity.PlatformProjectName),
    ];

    public static bool IsPlatformProject(Ulid projectId) => projectId == AuthProjectId || projectId == DashboardProjectId || projectId == PlatformProjectId;

    public async ValueTask<SystemSetupOutcome> EnsureAsync(CancellationToken cancellationToken, bool allowFastFallback = false)
    {
        var now = time.GetUtcNow();
        var mode = allowFastFallback ? ConsistencyMode.Fast : ConsistencyMode.Strong;
        var organization = await catalog.UpsertOrganizationAsync(new OrganizationRecord(OrganizationId, PlatformIdentity.OrganizationName, "platform", now, Plans.TerraScale, false), cancellationToken, mode);
        if (organization switch { Completed => null, ResourceNotFound missing => missing.Message, BackendUnavailable error => error.Message } is { } failed)
        {
            return new ConfigurationError(string.Concat("the platform organization could not be seeded: ", failed));
        }

        foreach (var platform in Databases)
        {
            var project = await catalog.UpsertProjectAsync(new ProjectRecord(platform.ProjectId, OrganizationId, platform.ProjectName, now, Plans.TerraScale), cancellationToken, mode);
            if (project switch { Completed => null, BackendUnavailable error => error.Message } is { } projectFailure)
            {
                return new ConfigurationError(string.Concat("the platform project could not be seeded: ", projectFailure));
            }

            var seededOutcome = await catalog.GetDatabaseAsync(platform.Id, cancellationToken);
            var seeded = seededOutcome switch
            {
                DatabaseRecord existing => await LocalAsync(existing, cancellationToken),
                ResourceNotFound => await CreateAsync(platform, now, mode, cancellationToken),
                BackendUnavailable error => error.Message,
            };
            if (seeded is not null)
            {
                return new ConfigurationError(string.Concat("the platform database ", platform.Name, " could not be seeded: ", seeded));
            }
        }

        return new Completed();
    }

    private async ValueTask<string?> LocalAsync(DatabaseRecord record, CancellationToken cancellationToken)
    {
        var outcome = await databases.OpenOrCreateAsync(record.Id, record.KeyId, record.Strict, cancellationToken);
        return outcome switch
        {
            ISqlDatabaseSession => null,
            BackendUnavailable error => error.Message,
        };
    }

    private async ValueTask<string?> CreateAsync(PlatformDatabase platform, DateTimeOffset now, ConsistencyMode mode, CancellationToken cancellationToken)
    {
        var record = Record(platform, databases.ActiveKeyId, now);
        var outcome = await catalog.UpsertDatabaseAsync(record, null, cancellationToken, mode);
        return outcome switch
        {
            Completed => await LocalAsync(record, cancellationToken),
            TombstoneKept => "the platform database is deleted and awaits its purge",
            StateConflict => await catalog.GetDatabaseAsync(platform.Id, cancellationToken) switch
            {
                DatabaseRecord existing => await LocalAsync(existing, cancellationToken),
                ResourceNotFound missing => missing.Message,
                BackendUnavailable error => error.Message,
            },
            BackendUnavailable error => error.Message,
        };
    }

    private DatabaseRecord Record(PlatformDatabase platform, string keyId, DateTimeOffset now) =>
        new(platform.Id, platform.ProjectId, platform.Name, [options.Region], AllRegions: true, ConsistencyMode.Strong, Strict: false,
            ControlDatabaseState.Active, keyId, null, null, now, null, 1, 0);
}

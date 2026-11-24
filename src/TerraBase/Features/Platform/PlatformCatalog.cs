using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.Platform;

internal sealed record PlatformDatabase(Ulid Id, Ulid ProjectId, string Name, string ProjectName);

internal sealed class PlatformCatalog(CatalogStore catalog, TenantDatabases databases, TerraBaseOptions options, TimeProvider time)
{
    public static readonly Ulid OrganizationId = Ulid.Parse("00000000000000000000000001");

    public static readonly Ulid AuthProjectId = Ulid.Parse("00000000000000000000000002");

    public static readonly Ulid DashboardProjectId = Ulid.Parse("00000000000000000000000005");

    public static readonly Ulid PlatformProjectId = Ulid.Parse("00000000000000000000000006");

    public static IReadOnlyList<PlatformDatabase> Databases { get; } =
    [
        new(Ulid.Parse("00000000000000000000000003"), AuthProjectId, "ts-auth", "TerraScale Authentication"),
        new(Ulid.Parse("00000000000000000000000004"), DashboardProjectId, "ts-dashboard", "TerraScale Dashboard"),
        new(Ulid.Parse("00000000000000000000000007"), PlatformProjectId, "ts-platform", "TerraScale Platform"),
    ];

    public static bool IsPlatformProject(Ulid projectId) => projectId == AuthProjectId || projectId == DashboardProjectId || projectId == PlatformProjectId;

    public async ValueTask<SystemSetupOutcome> EnsureAsync(CancellationToken cancellationToken, bool allowFastFallback = false)
    {
        var now = time.GetUtcNow();
        var mode = allowFastFallback ? ConsistencyMode.Fast : ConsistencyMode.Strong;
        var organization = await catalog.UpsertOrganizationAsync(new OrganizationRecord(OrganizationId, "TerraScale", "platform", now, "enterprise", false), cancellationToken, mode);
        if (organization switch { Completed => null, ResourceNotFound missing => missing.Message, BackendUnavailable error => error.Message } is { } failed)
        {
            return new ConfigurationError(string.Concat("the platform organization could not be seeded: ", failed));
        }

        foreach (var platform in Databases)
        {
            var project = await catalog.UpsertProjectAsync(new ProjectRecord(platform.ProjectId, OrganizationId, platform.ProjectName, now, "enterprise"), cancellationToken, mode);
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
        var outcome = await catalog.UpsertDatabaseAsync(record, cancellationToken, mode);
        return outcome switch
        {
            Completed => await LocalAsync(record, cancellationToken),
            BackendUnavailable error => error.Message,
        };
    }

    private DatabaseRecord Record(PlatformDatabase platform, string keyId, DateTimeOffset now) =>
        new(platform.Id, platform.ProjectId, platform.Name, [options.Region], AllRegions: true, ConsistencyMode.Strong, Strict: false,
            ControlDatabaseState.Active, keyId, null, null, now, null, 1, 0);
}

using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
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

    public async ValueTask<Result<Done, ConfigurationError>> EnsureAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var organization = await catalog.UpsertOrganizationAsync(new OrganizationRecord(OrganizationId, "TerraScale", "platform", now, "enterprise", false), cancellationToken);
        if (organization switch { Done => null, NotFound missing => missing.Message, Unavailable error => error.Message } is { } failed)
        {
            return new ConfigurationError(string.Concat("the platform organization could not be seeded: ", failed));
        }

        foreach (var platform in Databases)
        {
            var project = await catalog.UpsertProjectAsync(new ProjectRecord(platform.ProjectId, OrganizationId, platform.ProjectName, now, "enterprise"), cancellationToken);
            if (project switch { Done => null, Unavailable error => error.Message } is { } projectFailure)
            {
                return new ConfigurationError(string.Concat("the platform project could not be seeded: ", projectFailure));
            }

            var seeded = await catalog.GetDatabaseAsync(platform.Id, cancellationToken) switch
            {
                DatabaseRecord existing => await LocalAsync(existing, cancellationToken),
                NotFound => await CreateAsync(platform, now, cancellationToken),
                Unavailable error => error.Message,
            };
            if (seeded is not null)
            {
                return new ConfigurationError(string.Concat("the platform database ", platform.Name, " could not be seeded: ", seeded));
            }
        }

        return new Done();
    }

    private async ValueTask<string?> LocalAsync(DatabaseRecord record, CancellationToken cancellationToken) =>
        await databases.OpenOrCreateAsync(record.Id, record.KeyId, record.Strict, cancellationToken) switch
        {
            ISqlDatabaseSession => null,
            Unavailable error => error.Message,
        };

    private async ValueTask<string?> CreateAsync(PlatformDatabase platform, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var record = Record(platform, databases.ActiveKeyId, now);
        return await catalog.UpsertDatabaseAsync(record, cancellationToken) switch
        {
            Done => await LocalAsync(record, cancellationToken),
            Unavailable error => error.Message,
        };
    }

    private DatabaseRecord Record(PlatformDatabase platform, string keyId, DateTimeOffset now) =>
        new(platform.Id, platform.ProjectId, platform.Name, [options.Region], AllRegions: true, ConsistencyMode.Strong, Strict: false,
            ControlDatabaseState.Active, keyId, null, null, now, null, 1, 0);
}

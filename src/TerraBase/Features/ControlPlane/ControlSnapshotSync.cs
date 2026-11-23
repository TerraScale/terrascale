using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.ControlPlane;

internal sealed class ControlSnapshotSync(CatalogStore catalog, TenantDatabases databases, DatabaseKeyring keyring, TerraBaseOptions options)
{
    public async ValueTask<Result<Done, Unavailable>> ApplyAsync(ControlSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var organization in snapshot.Organizations)
        {
            var record = new OrganizationRecord(organization.Id, organization.Name, "control-plane", snapshot.IssuedAtUtc, organization.Plan, organization.Suspended);
            if (await catalog.UpsertOrganizationAsync(record, cancellationToken) switch { Done => null, NotFound missing => missing.Message, Unavailable error => error.Message } is { } failed)
            {
                return new Unavailable(failed, "control_sync_failed");
            }
        }

        foreach (var project in snapshot.Projects)
        {
            var failure = await catalog.UpsertProjectAsync(new ProjectRecord(project.Id, project.OrganizationId, project.Name, snapshot.IssuedAtUtc, null), cancellationToken) switch
            {
                Done => null,
                Unavailable error => error,
            };
            if (failure is not null)
            {
                return failure;
            }
        }

        foreach (var database in snapshot.Databases)
        {
            var applied = database.State == ControlDatabaseState.Deleted
                ? await DeleteAsync(database, cancellationToken)
                : await UpsertAsync(database, cancellationToken);
            if (applied is { } failure)
            {
                return failure;
            }
        }

        return new Done();
    }

    private async ValueTask<Unavailable?> DeleteAsync(ControlDatabase database, CancellationToken cancellationToken) =>
        await catalog.GetDatabaseAsync(database.Id, cancellationToken) switch
        {
            DatabaseRecord record => await catalog.DeleteDatabaseAsync(record, cancellationToken) switch
            {
                Done => await databases.DeleteAsync(record.Id, cancellationToken) switch
                {
                    Done => null,
                    Unavailable error => error,
                },
                Unavailable error => error,
            },
            NotFound => null,
            Unavailable error => error,
        };

    private async ValueTask<Unavailable?> UpsertAsync(ControlDatabase database, CancellationToken cancellationToken)
    {
        var keyId = database.DataKeyId is { Length: > 0 } configured && keyring.KeyIds.Contains(configured, StringComparer.Ordinal) ? configured : keyring.ActiveKeyId;
        var existing = await catalog.GetDatabaseAsync(database.Id, cancellationToken) switch
        {
            DatabaseRecord found => found,
            NotFound or Unavailable => null,
        };
        var record = new DatabaseRecord(database.Id, database.ProjectId, database.Name, database.Regions, AllRegions: false, database.DefaultConsistency, database.Strict,
            database.State, existing?.KeyId ?? keyId, existing?.BranchedFrom, existing?.PreviousName, database.CreatedAtUtc, existing?.ExpiresAt,
            (existing?.Version ?? 0) + 1, database.StorageLimitBytes);
        return await catalog.UpsertDatabaseAsync(record, cancellationToken) switch
        {
            Done when !record.PlacedIn(options.Region) => null,
            Done => await databases.OpenOrCreateAsync(record.Id, record.KeyId, record.Strict, cancellationToken) switch
            {
                ISqlDatabaseSession => null,
                Unavailable error => error,
            },
            Unavailable error => error,
        };
    }
}

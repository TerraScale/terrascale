using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;

namespace TerraBase.Features.ControlPlane;

internal readonly union SnapshotApplyOutcome(Completed, BackendUnavailable);

internal sealed class ControlSnapshotSync(CatalogStore catalog, TenantDatabases databases, DatabaseKeyring keyring, TerraBaseOptions options)
{
    public async ValueTask<SnapshotApplyOutcome> ApplyAsync(ControlSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var organization in snapshot.Organizations)
        {
            var record = new OrganizationRecord(organization.Id, organization.Name, "control-plane", snapshot.IssuedAtUtc, organization.Plan, organization.Suspended);
            if (await catalog.UpsertOrganizationAsync(record, cancellationToken) switch { Completed => null, ResourceNotFound missing => missing.Message, BackendUnavailable error => error.Message } is { } failed)
            {
                return new BackendUnavailable(failed, "control_sync_failed");
            }
        }

        foreach (var project in snapshot.Projects)
        {
            var failureOutcome = await catalog.UpsertProjectAsync(new ProjectRecord(project.Id, project.OrganizationId, project.Name, snapshot.IssuedAtUtc, null), cancellationToken);
            var failure = failureOutcome switch
            {
                Completed => null,
                BackendUnavailable error => error,
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

        return new Completed();
    }

    private async ValueTask<BackendUnavailable?> DeleteAsync(ControlDatabase database, CancellationToken cancellationToken)
    {
        var outcome = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
        return outcome switch
        {
            DatabaseRecord record => await catalog.DeleteDatabaseAsync(record, cancellationToken) switch
            {
                Completed => await databases.DeleteAsync(record.Id, cancellationToken) switch
                {
                    Completed => await catalog.PurgeDatabaseAsync(record.Id, cancellationToken) switch
                    {
                        Completed => null,
                        BackendUnavailable error => error,
                    },
                    BackendUnavailable error => error,
                },
                BackendUnavailable error => error,
            },
            ResourceNotFound => null,
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<BackendUnavailable?> UpsertAsync(ControlDatabase database, CancellationToken cancellationToken)
    {
        var keyId = database.DataKeyId is { Length: > 0 } configured && keyring.KeyIds.Contains(configured, StringComparer.Ordinal) ? configured : keyring.ActiveKeyId;
        var existingOutcome = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
        var existing = existingOutcome switch
        {
            DatabaseRecord found => found,
            ResourceNotFound or BackendUnavailable => null,
        };
        var record = new DatabaseRecord(database.Id, database.ProjectId, database.Name, database.Regions, AllRegions: false, database.DefaultConsistency, database.Strict,
            database.State, existing?.KeyId ?? keyId, existing?.BranchedFrom, existing?.PreviousName, database.CreatedAtUtc, existing?.ExpiresAt,
            (existing?.Version ?? 0) + 1, database.StorageLimitBytes);
        var outcome = await catalog.UpsertDatabaseAsync(record, cancellationToken);
        return outcome switch
        {
            Completed when !record.PlacedIn(options.Region) => null,
            Completed => await databases.OpenOrCreateAsync(record.Id, record.KeyId, record.Strict, cancellationToken) switch
            {
                ISqlDatabaseSession => null,
                BackendUnavailable error => error,
            },
            BackendUnavailable error => error,
        };
    }
}

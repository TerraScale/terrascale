using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts.ControlPlane;
using ZLogger;

namespace TerraBase.Features.ControlPlane;

internal sealed record SnapshotKeyRefused(Ulid DatabaseId, string Message, string Code);

internal readonly union SnapshotApplyOutcome(Completed, SnapshotKeyRefused, BackendUnavailable);

internal readonly union SnapshotDatabaseCheck(Completed, SnapshotKeyRefused, BackendUnavailable);

internal sealed class ControlSnapshotSync(
    CatalogStore catalog, TenantDatabases databases, DatabaseKeyring keyring, TerraBaseOptions options, ILogger<ControlSnapshotSync> logger) : IDisposable
{
    public SemaphoreSlim Serialized { get; } = new(1, 1);

    public void Dispose() => Serialized.Dispose();

    public async ValueTask<SnapshotApplyOutcome> ApplyAsync(ControlSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        foreach (var database in snapshot.Databases)
        {
            var check = await CheckAsync(database, cancellationToken);
            SnapshotApplyOutcome? refusal = check switch
            {
                Completed => null,
                SnapshotKeyRefused refused => refused,
                BackendUnavailable unavailable => unavailable,
            };
            if (refusal is { } failed)
            {
                return failed;
            }
        }

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
            DatabaseRecord record => await catalog.ForgetDatabaseAsync(record, cancellationToken) switch
            {
                Completed => await ShredLocalAsync(record.Id, cancellationToken) ?? await CleanupAsync(record.Id, cancellationToken),
                ResourceNotFound => await ForgottenAsync(record.Id, cancellationToken),
                StateConflict conflict => new BackendUnavailable(conflict.Message, conflict.Code),
                BackendUnavailable error => error,
            },
            ResourceNotFound => await ForgottenAsync(database.Id, cancellationToken),
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<BackendUnavailable?> ForgottenAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        if (await ShredLocalAsync(databaseId, cancellationToken) is { } failure)
        {
            return failure;
        }

        var row = await catalog.HasDatabaseRowAsync(databaseId, cancellationToken);
        return row switch
        {
            bool present when present => null,
            bool => await CleanupAsync(databaseId, cancellationToken),
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<BackendUnavailable?> CleanupAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var cleaned = await catalog.CleanupDatabaseAsync(databaseId, purged: true, cancellationToken);
        return cleaned switch
        {
            Completed => null,
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<BackendUnavailable?> ShredLocalAsync(Ulid databaseId, CancellationToken cancellationToken)
    {
        var local = await databases.DeleteAsync(databaseId, cancellationToken);
        return local switch
        {
            Completed => null,
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<SnapshotDatabaseCheck> CheckAsync(ControlDatabase database, CancellationToken cancellationToken)
    {
        if (database.State == ControlDatabaseState.Deleted)
        {
            var stored = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
            return stored switch
            {
                DatabaseRecord or ResourceNotFound => new Completed(),
                BackendUnavailable unavailable => unavailable,
            };
        }

        if (!keyring.KeyIds.Contains(database.DataKeyId, StringComparer.Ordinal))
        {
            logger.ZLogError($"Control snapshot refused: database {database.Id:@DatabaseId} names data key {database.DataKeyId:@DataKeyId} that this node does not hold");
            return new SnapshotKeyRefused(database.Id, "the control snapshot names a data key this node does not hold", "unknown_data_key");
        }

        var existing = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
        return existing switch
        {
            DatabaseRecord found when !string.Equals(found.KeyId, database.DataKeyId, StringComparison.Ordinal) => Mismatch(database, found),
            DatabaseRecord or ResourceNotFound => new Completed(),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private SnapshotKeyRefused Mismatch(ControlDatabase database, DatabaseRecord found)
    {
        logger.ZLogError($"Control snapshot refused: database {database.Id:@DatabaseId} is encrypted with data key {found.KeyId:@StoredKeyId} but the snapshot names {database.DataKeyId:@DataKeyId}");
        return new SnapshotKeyRefused(database.Id, "the control snapshot names a different data key than the stored database", "data_key_mismatch");
    }

    private async ValueTask<BackendUnavailable?> UpsertAsync(ControlDatabase database, CancellationToken cancellationToken)
    {
        var existing = await catalog.GetDatabaseAsync(database.Id, cancellationToken);
        return existing switch
        {
            DatabaseRecord found => await UpsertAsync(database, found, cancellationToken),
            ResourceNotFound => await UpsertAsync(database, null, cancellationToken),
            BackendUnavailable unavailable => unavailable,
        };
    }

    private async ValueTask<BackendUnavailable?> UpsertAsync(ControlDatabase database, DatabaseRecord? existing, CancellationToken cancellationToken)
    {
        var desired = new DatabaseRecord(database.Id, database.ProjectId, database.Name, database.Regions, AllRegions: false, database.DefaultConsistency, database.Strict,
            database.State, database.DataKeyId, existing?.BranchedFrom, existing?.PreviousName, database.CreatedAtUtc, existing?.ExpiresAt,
            existing?.Version ?? 1, database.StorageLimitBytes);
        if (existing is not null && Same(existing, desired))
        {
            return await OpenAsync(existing, cancellationToken);
        }

        var record = desired with { Version = existing is null ? 1 : existing.Version + 1 };
        var outcome = await catalog.UpsertDatabaseAsync(record, existing?.Name, cancellationToken);
        return outcome switch
        {
            Completed => await OpenAsync(record, cancellationToken),
            TombstoneKept => null,
            BackendUnavailable error => error,
        };
    }

    private async ValueTask<BackendUnavailable?> OpenAsync(DatabaseRecord record, CancellationToken cancellationToken)
    {
        if (!record.PlacedIn(options.Region))
        {
            return null;
        }

        var opened = await databases.OpenOrCreateAsync(record.Id, record.KeyId, record.Strict, cancellationToken);
        return opened switch
        {
            ISqlDatabaseSession => null,
            BackendUnavailable error => error,
        };
    }

    private static bool Same(DatabaseRecord existing, DatabaseRecord desired) =>
        existing with { Regions = desired.Regions } == desired && existing.Regions.SequenceEqual(desired.Regions, StringComparer.Ordinal);
}

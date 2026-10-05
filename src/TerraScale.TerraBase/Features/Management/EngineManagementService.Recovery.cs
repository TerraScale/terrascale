using System.Diagnostics.Metrics;
using System.Text.Json;
using TerraScale.TerraBase.Features.Management.Backups;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private readonly Meter _recoveryMeter = meters.Create(Telemetry.MeterName);
    private long _unknownDatabaseFiles;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_files", () => _unknownDatabaseFiles,
            description: "Files found at startup without a registered database; preserved for operator recovery.");

        // Older catalogs contain no intents. Keep their existing lazy migration/503 behavior.
        var hasCatalog = engine.SystemDatabaseExists;
        var version = hasCatalog
            ? await ReadCatalogAsync(transaction => GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null;
        if (version is IntentCatalogVersion or CatalogVersion)
        {
            while (true)
            {
                // One bounded page at a time; release the catalog connection before deleting storage.
                var page = await ReadCatalogAsync(transaction => transaction.ListAsync(
                    PendingDatabaseCleanup, afterId: null, MemberPage, MaxPageBytes, cancellationToken), cancellationToken).ConfigureAwait(false);
                if (page.Documents.Count == 0)
                {
                    break;
                }

                foreach (var document in page.Documents)
                {
                    var storage = JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.TenantContext)
                        ?? throw new InvalidOperationException("a database cleanup intent is invalid");
                    if (document.Id != storage.DatabaseId)
                    {
                        // Inconsistent metadata cannot authorize deletion of registered data.
                        throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
                    }

                    checkpoint("recovery.intent", storage);
                    await FinishDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // ponytail: one page of fixed-size UUID keys bounds items/bytes; partition the scan if interleaved files defeat reuse.
        var registrations = new Dictionary<(string Organization, string Project, string Database), bool>();
        foreach (var file in engine.EnumerateDatabaseFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registered = false;
            // Future catalogs may change row schemas; preserve their files without interpreting those rows.
            if (hasCatalog && version is null or MembershipCatalogVersion or "3" or IntentCatalogVersion or CatalogVersion
                && file.Database is { } database)
            {
                var key = (database.OrganizationId, database.ProjectId!, database.DatabaseId!);
                if (!registrations.TryGetValue(key, out registered))
                {
                    checkpoint("audit.registration", database);
                    registered = await IsRegisteredAsync(database, cancellationToken).ConfigureAwait(false);
                    if (registrations.Count == MemberPage)
                    {
                        registrations.Clear();
                    }

                    registrations.Add(key, registered);
                }
            }

            if (!registered)
            {
                _unknownDatabaseFiles++;
                LogUnknownDatabaseFile(logger, file.Path);
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // A database of the project, or a backup snapshot its organization still tracks there (the storage sweep deletes it).
    private ValueTask<bool> IsRegisteredAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadCatalogAsync(async transaction =>
            await OrganizationProjectAsync(transaction, storage.OrganizationId, storage.ProjectId!, cancellationToken).ConfigureAwait(false) is not null
            && (await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null
                || await GetAsync(transaction, BackupCatalog.Records(storage.OrganizationId), storage.DatabaseId!, TerraBaseJsonContext.Default.BackupRecord, cancellationToken)
                    .ConfigureAwait(false) is { } backup && backup.ProjectId == storage.ProjectId),
            cancellationToken);

    private ValueTask<bool> HasDatabaseRowAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadCatalogAsync(async transaction =>
            await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null,
            cancellationToken);

    private async ValueTask FinishDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        // This also protects a create whose registration committed before a later error was reported.
        if (await HasDatabaseRowAsync(storage, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
        }

        await engine.DeleteDatabaseAsync(storage, cancellationToken, retainIntentOwnership: true).ConfigureAwait(false);
        checkpoint("cleanup.files", storage);
        await ClearDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ClearDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        try
        {
            checkpoint("cleanup.clearing", storage);
            _ = await WriteCatalogAsync(async transaction =>
            {
                await transaction.DeleteAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false);
                return 0;
            }, cancellationToken).ConfigureAwait(false);
            checkpoint("cleanup.committed", storage);
        }
        catch
        {
            // Intent deletion can commit before transaction disposal reports an error.
            await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            throw;
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
    }

    private async ValueTask ReleaseUnrecordedIntentAsync(TenantContext storage)
    {
        var intent = await ReadCatalogAsync(transaction => transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!,
            CancellationToken.None), CancellationToken.None).ConfigureAwait(false);
        if (intent is null)
        {
            // No durable intent remains to retain the destination reservation.
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preserved database file without a catalog registration: {Path}")]
    private static partial void LogUnknownDatabaseFile(ILogger logger, string path);
}

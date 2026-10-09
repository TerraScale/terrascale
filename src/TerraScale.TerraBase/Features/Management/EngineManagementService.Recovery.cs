using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management.Backups;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private readonly Meter _recoveryMeter = meters.Create(Telemetry.MeterName);
    private long _unknownDatabaseFiles;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _recoveryMeter.CreateObservableGauge("terrabase.storage.unknown_files", () => _unknownDatabaseFiles,
            description: "Files found at startup without a registered database; preserved for operator recovery.");
        if (options.CatalogPrimary is not null)
        {
            // A follower's intents are the primary's: it acts on them as it applies them (AppliedAsync) and before each stream
            // (ResumeAsync). Its catalog may still be catching up, so it audits its files once it caught up (CaughtUpAsync).
            return;
        }

        // Only the current catalog owns cleanup intents; older formats are refused.
        var (hasCatalog, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
        if (version == CatalogVersion)
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
                    if (await FinishDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false) is Unavailable)
                    {
                        return;
                    }
                }
            }
        }

        await AuditFilesAsync(hasCatalog, version, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The catalog collection of database cleanup intents, which a catalog follower acts on.</summary>
    public string Collection => PendingDatabaseCleanup;

    /// <summary>
    /// On a catalog follower: deletes the local files of each database whose cleanup intent the primary's catalog recorded and
    /// that it no longer registers (a deleted database, or one whose creation was abandoned). An intent of a creation in
    /// progress names a database no follower has files of yet. The primary clears the intents. A failed deletion fails the
    /// call once every intent was tried: the follower then applies nothing more, so the intent stays in its catalog, until
    /// <see cref="ResumeAsync"/> succeeds.
    /// </summary>
    public async ValueTask AppliedAsync(IReadOnlyList<DocumentChange> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        // Rows of a catalog version this build does not read could be anywhere: never delete on their absence.
        if (documents.Count == 0 || (await CatalogStateAsync(cancellationToken).ConfigureAwait(false)).Version != CatalogVersion)
        {
            return;
        }

        var cleanups = new List<TenantContext>(documents.Count);
        foreach (var document in documents)
        {
            TenantContext? storage;
            try
            {
                storage = JsonSerializer.Deserialize(document.Json!.Value.Span, TerraBaseJsonContext.Default.TenantContext);
            }
            catch (JsonException)
            {
                storage = null;
            }

            if (storage is null || document.Id != storage.DatabaseId || !IsId(storage.OrganizationId) || !IsId(storage.ProjectId) || !IsId(storage.DatabaseId))
            {
                // Inconsistent metadata cannot authorize deletion of registered data, and no retry would make it valid.
                LogInvalidReplicatedIntent(logger, null);
            }
            else
            {
                cleanups.Add(storage);
            }
        }

        Exception? failed = null;
        foreach (var storage in cleanups)
        {
            try
            {
                if (await HasDatabaseRowAsync(storage, cancellationToken).ConfigureAwait(false) is bool registered && !registered)
                {
                    checkpoint("follower.cleanup", storage);
                    _ = await engine.DeleteDatabaseAsync(storage, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                // One cleanup never holds back the others.
                failed ??= error;
            }
        }

        if (failed is not null)
        {
            ExceptionDispatchInfo.Throw(failed);
        }
    }

    /// <summary>On a catalog follower, before each stream: acts on the cleanup intents its catalog holds (see <see cref="AppliedAsync"/>).</summary>
    public async ValueTask ResumeAsync(CancellationToken cancellationToken)
    {
        if (!engine.SystemDatabaseExists)
        {
            return;
        }

        // Every page is acted on, so one failing cleanup holds back none of the others; the first failure is reported after.
        string? after = null;
        DocumentPage page;
        Exception? failed = null;
        do
        {
            page = await ReadCatalogAsync(transaction => transaction.ListAsync(PendingDatabaseCleanup, after, MemberPage, MaxPageBytes, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            try
            {
                await AppliedAsync([.. page.Documents.Select(document => new DocumentChange(PendingDatabaseCleanup, document.Id, 1, document.Json))],
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested)
            {
                failed ??= error;
            }

            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);
        if (failed is not null)
        {
            ExceptionDispatchInfo.Throw(failed);
        }
    }

    /// <summary>On a catalog follower that caught up with its primary: the startup file audit a primary runs.</summary>
    public async ValueTask CaughtUpAsync(CancellationToken cancellationToken)
    {
        var (hasCatalog, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
        await AuditFilesAsync(hasCatalog, version, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<(bool HasCatalog, string? Version)> CatalogStateAsync(CancellationToken cancellationToken)
    {
        var hasCatalog = engine.SystemDatabaseExists;
        return (hasCatalog, hasCatalog
            ? await ReadCatalogAsync(transaction => GetAsync(transaction, Catalog, CatalogVersionId, Id, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null);
    }

    // Counts and logs files without a registered database; they are preserved for operator recovery.
    private async ValueTask AuditFilesAsync(bool hasCatalog, string? version, CancellationToken cancellationToken)
    {
        // ponytail: one page of fixed-size ULID keys bounds items/bytes; partition the scan if interleaved files defeat reuse.
        var registrations = new Dictionary<(string Organization, string Project, string Database), bool>();
        foreach (var file in engine.EnumerateDatabaseFiles(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var registered = false;
            // Future catalogs may change row schemas; preserve their files without interpreting those rows.
            if (hasCatalog && version == CatalogVersion
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

    // A database of the project, or a backup snapshot its organization still tracks there (the storage sweep deletes it).
    private ValueTask<bool> IsRegisteredAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadCatalogAsync(async transaction =>
            await OrganizationProjectAsync(transaction, storage.OrganizationId, storage.ProjectId!, cancellationToken).ConfigureAwait(false) is not null
            && (await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null
                || await GetAsync(transaction, BackupCatalog.Records(storage.OrganizationId), storage.DatabaseId!, TerraBaseJsonContext.Default.BackupRecord, cancellationToken)
                    .ConfigureAwait(false) is { } backup && backup.ProjectId == storage.ProjectId),
            cancellationToken);

    private ValueTask<RecordedDeletion> HasDatabaseRowAsync(TenantContext storage, CancellationToken cancellationToken) =>
        ReadAsync<RecordedDeletion>(async transaction =>
            await GetAsync(transaction, Databases(storage.ProjectId!), storage.DatabaseId!, TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false) is not null,
            static error => error, cancellationToken);

    private async ValueTask<CatalogCheck> FinishDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        // This also protects a create whose registration committed before a later error was reported.
        var registered = await HasDatabaseRowAsync(storage, cancellationToken).ConfigureAwait(false);
        if (registered is Unavailable refused)
        {
            return refused;
        }

        if (registered is bool exists && exists)
        {
            throw new InvalidOperationException("a database cleanup intent conflicts with the catalog");
        }

        await engine.DeleteDatabaseAsync(storage, cancellationToken, retainIntentOwnership: true).ConfigureAwait(false);
        checkpoint("cleanup.files", storage);
        return await ClearDatabaseCleanupAsync(storage, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CatalogCheck> ClearDatabaseCleanupAsync(TenantContext storage, CancellationToken cancellationToken)
    {
        try
        {
            checkpoint("cleanup.clearing", storage);
            var cleared = await WriteAsync<CatalogCheck>(async transaction =>
            {
                await transaction.DeleteAsync(PendingDatabaseCleanup, storage.DatabaseId!, cancellationToken).ConfigureAwait(false);
                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false);
            if (cleared is Unavailable refused)
            {
                return refused;
            }

            checkpoint("cleanup.committed", storage);
        }
        catch
        {
            // Intent deletion can commit before transaction disposal reports an error.
            await ReleaseUnrecordedIntentAsync(storage).ConfigureAwait(false);
            throw;
        }

        await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        return new Done();
    }

    private async ValueTask ReleaseUnrecordedIntentAsync(TenantContext storage)
    {
        var recorded = await ReadAsync<RecordedDeletion>(async transaction =>
            await transaction.GetAsync(PendingDatabaseCleanup, storage.DatabaseId!, CancellationToken.None).ConfigureAwait(false) is not null,
            static error => error, CancellationToken.None).ConfigureAwait(false);
        if (recorded is bool exists && !exists)
        {
            // No durable intent remains to retain the destination reservation.
            await engine.CompleteDatabaseIntentAsync(storage, CancellationToken.None).ConfigureAwait(false);
        }
    }

    // The engine's tenant ids: canonical uppercase ULIDs.
    private static bool IsId([NotNullWhen(true)] string? id) =>
        EntityIds.IsValid(id);

    [LoggerMessage(Level = LogLevel.Error, Message = "A replicated database cleanup intent is invalid; nothing was deleted for it")]
    private static partial void LogInvalidReplicatedIntent(ILogger logger, Exception? error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Preserved database file without a catalog registration: {Path}")]
    private static partial void LogUnknownDatabaseFile(ILogger logger, string path);
}

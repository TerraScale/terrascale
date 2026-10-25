using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed record OrphanDatabaseFile(TenantContext Database, DateTimeOffset FirstSeen, Ulid Seen, int FormatVersion = 1);

internal sealed partial class EngineManagementService
{
    private int _fileAudit;
    private DateTimeOffset _nextFileAudit;
    private string OrphanFiles => $"orphan_database_files/{options.MachineId}";
    private static string OrphanId(TenantContext database) => $"{database.OrganizationId}:{database.ProjectId}:{database.DatabaseId}";

    internal async ValueTask AuditFilesAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _fileAudit, 1) != 0)
        {
            return;
        }
        try
        {
            if (!catalogGate.Value.IsOpen)
            {
                return;
            }
            var (hasCatalog, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
            if (hasCatalog && version == CatalogVersion)
            {
                var pass = EntityIds.New();
                var observations = new Dictionary<TenantContext, OrphanDatabaseFile?>();
                foreach (var file in engine.EnumerateDatabaseFiles(cancellationToken))
                {
                    if (file.Database is not { } database || !new FileInfo(file.Path).Exists)
                    {
                        continue; // Unrecognized paths, directories, links and reserved root files are never deleted.
                    }
                    var id = OrphanId(database);
                    var first = !observations.TryGetValue(database, out var observation);
                    if (first)
                    {
                        if (!await ReadAsync<bool>(transaction => OwnedFileAsync(transaction, database, cancellationToken), static _ => true, cancellationToken).ConfigureAwait(false))
                        {
                            observation = await WriteAsync<OrphanDatabaseFile?>(async transaction =>
                            {
                                if (await OwnedFileAsync(transaction, database, cancellationToken).ConfigureAwait(false))
                                {
                                    await transaction.DeleteAsync(OrphanFiles, id, cancellationToken).ConfigureAwait(false);
                                    return null;
                                }
                                var seen = await GetAsync(transaction, OrphanFiles, id, TerraBaseJsonContext.Default.OrphanDatabaseFile, cancellationToken).ConfigureAwait(false);
                                if (seen is not null && (seen.FormatVersion != 1 || seen.Database != database))
                                {
                                    return null;
                                }
                                var next = seen is null ? new OrphanDatabaseFile(database, time.GetUtcNow(), pass) : seen with { Seen = pass };
                                if (seen?.Seen != pass)
                                {
                                    await PutAsync(transaction, OrphanFiles, id, next, TerraBaseJsonContext.Default.OrphanDatabaseFile, cancellationToken).ConfigureAwait(false);
                                }
                                return next;
                            }, static _ => null, cancellationToken).ConfigureAwait(false);
                        }
                        if (observations.Count == MemberPage)
                        {
                            observations.Clear();
                        }
                        observations[database] = observation;
                    }
                    if (observation is null)
                    {
                        continue;
                    }
                    var information = new FileInfo(file.Path);
                    logger.ZLogWarning($"Unregistered database file {file.Path:@Path}: {(information.Exists ? information.Length : 0):@Bytes} bytes, first seen {observation.FirstSeen:@FirstSeen}");
                    if (!first || time.GetUtcNow() - observation.FirstSeen < TimeSpan.FromHours(24))
                    {
                        continue;
                    }
                    try
                    {
                        _ = await orphanFiles.DeleteAsync(database, async (transaction, token) =>
                        {
                            if (!catalogGate.Value.IsOpen || await CheckReadAsync(transaction, token).ConfigureAwait(false) is not Done
                                || await OwnedFileAsync(transaction, database, token).ConfigureAwait(false)
                                || await GetAsync(transaction, OrphanFiles, id, TerraBaseJsonContext.Default.OrphanDatabaseFile, token).ConfigureAwait(false) != observation)
                            {
                                return false;
                            }
                            await transaction.DeleteAsync(OrphanFiles, id, token).ConfigureAwait(false);
                            return true;
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested && error is IOException or OperationCanceledException or StorageBusyException)
                    {
                        logger.ZLogDebug($"Orphan cleanup deferred: {error.GetType().Name:@Reason}");
                    }
                }
                // Bound both catalog reads and cleanup; records disappear when files vanish or become owned.
                string? after = null;
                while (true)
                {
                    var page = await WriteAsync<DocumentPage?>(async transaction =>
                    {
                        var records = await transaction.ListAsync(OrphanFiles, after, MemberPage, MaxPageBytes, cancellationToken).ConfigureAwait(false);
                        foreach (var document in records.Documents)
                        {
                            if (JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.OrphanDatabaseFile) is { FormatVersion: 1 } record && record.Seen != pass)
                            {
                                await transaction.DeleteAsync(OrphanFiles, document.Id, cancellationToken).ConfigureAwait(false);
                            }
                        }
                        return records;
                    }, static _ => null, cancellationToken).ConfigureAwait(false);
                    if (page is not { HasMore: true })
                    {
                        break;
                    }
                    after = page.Documents[^1].Id;
                }
            }
            await AuditFilesAsync(hasCatalog, version, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _nextFileAudit = time.GetUtcNow() + TimeSpan.FromMinutes(15);
            Volatile.Write(ref _fileAudit, 0);
        }
    }

    private static async ValueTask<bool> OwnedFileAsync(IStorageTransaction transaction, TenantContext database, CancellationToken token) =>
        await IsRegisteredAsync(transaction, database, token).ConfigureAwait(false)
        || await transaction.GetAsync(PendingDatabaseCleanup, database.DatabaseId!, token).ConfigureAwait(false) is not null
        || await transaction.GetAsync(DatabasePurge.Collection, database.DatabaseId!, token).ConfigureAwait(false) is not null
        || await HasDeletionAuthorityAsync(transaction, database, token).ConfigureAwait(false);
}

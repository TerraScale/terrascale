using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed record OrphanDatabaseFile(TenantContext Database, DateTimeOffset FirstSeen, Ulid Seen, long Files, long Bytes, int FormatVersion = 1);

internal sealed partial class EngineManagementService
{
    private static readonly TimeSpan OrphanGrace = TimeSpan.FromHours(24);
    private const int MaxOrphanDeletions = 16;
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
            var (_, version) = await CatalogStateAsync(cancellationToken).ConfigureAwait(false);
            var complete = version == CatalogVersion;
            var pass = EntityIds.New();
            var started = time.GetUtcNow();
            long count = 0, bytes = 0;
            var ownership = new Dictionary<TenantContext, bool>();
            foreach (var file in engine.EnumerateDatabaseFiles(cancellationToken))
            {
                var info = new FileInfo(file.Path);
                var size = info.Exists && info.LinkTarget is null ? info.Length : 0;
                if (file.Database is not { } database || version != CatalogVersion)
                {
                    count++;
                    bytes += size;
                    if (file.Database is null)
                    {
                        logger.ZLogWarning($"Preserved database file without a catalog registration: {file.Path:@Path}");
                    }
                    continue;
                }
                if (!ownership.TryGetValue(database, out var owned))
                {
                    checkpoint("audit.registration", database);
                    owned = await ReadAsync<bool>(transaction => OwnedFileAsync(transaction, database, cancellationToken), _ =>
                    {
                        complete = false;
                        return true;
                    }, cancellationToken).ConfigureAwait(false);
                    if (!owned)
                    {
                        var busy = await orphanFiles.IsBusyAsync(database, cancellationToken).ConfigureAwait(false);
                        complete &= busy.HasValue;
                        owned = busy != false;
                    }
                    if (ownership.Count == MemberPage)
                    {
                        ownership.Clear();
                    }
                    ownership[database] = owned;
                }
                if (owned)
                {
                    continue;
                }
                // Only orphan paths write. Per-pass totals live in the catalog, so arbitrary sidecar ordering and
                // bounded cache eviction cannot duplicate a warning or require an unbounded in-memory inventory.
                var firstSeen = await WriteAsync<bool>(async transaction =>
                {
                    if (await OwnedFileAsync(transaction, database, cancellationToken).ConfigureAwait(false))
                    {
                        return false;
                    }
                    var id = OrphanId(database);
                    var seen = await GetAsync(transaction, OrphanFiles, id, TerraBaseJsonContext.Default.OrphanDatabaseFile, cancellationToken).ConfigureAwait(false);
                    if (seen is not null && (seen.FormatVersion != 1 || seen.Database != database))
                    {
                        count++;
                        bytes += size;
                        return false;
                    }
                    var next = new OrphanDatabaseFile(database, seen?.FirstSeen ?? time.GetUtcNow(), pass,
                        (seen?.Seen == pass ? seen.Files : 0) + 1, (seen?.Seen == pass ? seen.Bytes : 0) + size);
                    await PutAsync(transaction, OrphanFiles, id, next, TerraBaseJsonContext.Default.OrphanDatabaseFile, cancellationToken).ConfigureAwait(false);
                    // A sidecar listed before its main file may have created the record earlier in this pass.
                    return next.FirstSeen >= started;
                }, _ => { complete = false; return false; }, cancellationToken).ConfigureAwait(false);
                // The main file names the database on disk: an operator finds it by this path, once. Every pass after that
                // reports the database in its summary below.
                if (firstSeen && file.Path.EndsWith(".db", StringComparison.Ordinal))
                {
                    logger.ZLogWarning($"Preserved database file without a catalog registration: {file.Path:@Path}");
                }
            }
            var expired = new List<OrphanDatabaseFile>();
            if (complete && catalogGate.Value.IsOpen)
            {
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
                    if (page is null)
                    {
                        complete = false;
                        break;
                    }
                    foreach (var document in page.Documents)
                    {
                        if (JsonSerializer.Deserialize(document.Json.Span, TerraBaseJsonContext.Default.OrphanDatabaseFile) is not { FormatVersion: 1 } record || record.Seen != pass)
                        {
                            continue;
                        }
                        count += record.Files;
                        bytes += record.Bytes;
                        if (record.FirstSeen + OrphanGrace <= time.GetUtcNow() && expired.Count < MaxOrphanDeletions)
                        {
                            expired.Add(record);
                            continue;
                        }
                        logger.ZLogWarning($"Preserved database without a catalog registration: {record.Database.DatabaseId:@Database}, {record.Bytes:@Bytes} bytes in {record.Files:@Files} files, first seen {record.FirstSeen:@FirstSeen}");
                    }
                    if (!page.HasMore)
                    {
                        break;
                    }
                    after = page.Documents[^1].Id;
                }
            }
            if (complete && version == CatalogVersion)
            {
                foreach (var record in expired)
                {
                    if (await DeleteOrphanAsync(record, cancellationToken).ConfigureAwait(false))
                    {
                        count -= record.Files;
                        bytes -= record.Bytes;
                        logger.ZLogWarning($"Deleted database without a catalog registration: {record.Database.DatabaseId:@Database}, {record.Bytes:@Bytes} bytes in {record.Files:@Files} files, first seen {record.FirstSeen:@FirstSeen}");
                    }
                    else
                    {
                        logger.ZLogWarning($"Preserved database without a catalog registration: {record.Database.DatabaseId:@Database}, {record.Bytes:@Bytes} bytes in {record.Files:@Files} files, first seen {record.FirstSeen:@FirstSeen}");
                    }
                }
            }
            if (!complete)
            {
                logger.ZLogWarning($"Database file audit could not read the current catalog; preserved files without changing their first-seen records");
            }
            if (!complete && version == CatalogVersion)
            {
                return;
            }
            Interlocked.Exchange(ref _unknownDatabaseFiles, count);
            Interlocked.Exchange(ref _unknownDatabaseBytes, bytes);
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning(error, $"Database file audit failed; preserved files and first-seen records for the next pass");
        }
        finally
        {
            _nextFileAudit = time.GetUtcNow() + TimeSpan.FromMinutes(15);
            Volatile.Write(ref _fileAudit, 0);
        }
    }

    // An orphan unregistered for the whole grace period is deleted like an abandoned creation: a cleanup intent, recorded
    // under the engine's lifecycle reservation only while the file is still unowned and unchanged since this pass, then
    // the engine's close-then-delete path. A registration that arrives meanwhile keeps the files.
    private async ValueTask<bool> DeleteOrphanAsync(OrphanDatabaseFile record, CancellationToken cancellationToken)
    {
        var database = record.Database;
        if (await orphanFiles.IsBusyAsync(database, cancellationToken).ConfigureAwait(false) != false)
        {
            return false;
        }
        var reserved = await engine.RecordDatabaseDeleteIntentAsync(database, token => WriteAsync<bool>(async transaction =>
        {
            var id = OrphanId(database);
            if (!catalogGate.Value.IsOpen || await OwnedFileAsync(transaction, database, token).ConfigureAwait(false)
                || await GetAsync(transaction, OrphanFiles, id, TerraBaseJsonContext.Default.OrphanDatabaseFile, token).ConfigureAwait(false) != record)
            {
                return false;
            }
            await PutAsync(transaction, PendingDatabaseCleanup, database.DatabaseId!, database, TerraBaseJsonContext.Default.TenantContext, token).ConfigureAwait(false);
            await transaction.DeleteAsync(OrphanFiles, id, token).ConfigureAwait(false);
            return true;
        }, static _ => false, token), cancellationToken).ConfigureAwait(false);
        if (!reserved)
        {
            return false;
        }
        checkpoint("audit.delete", database);
        _ = await FinishDatabaseCleanupAsync(database, cancellationToken, abandonedCreation: true).ConfigureAwait(false);
        // Deleted only when the intent cleared without a registration; anything else keeps the files for the next pass.
        return !await ReadAsync<bool>(transaction => OwnedFileAsync(transaction, database, cancellationToken), static _ => true, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<bool> OwnedFileAsync(IStorageTransaction transaction, TenantContext database, CancellationToken token) =>
        await IsRegisteredAsync(transaction, database, token).ConfigureAwait(false)
        || await transaction.GetAsync(PendingDatabaseCleanup, database.DatabaseId!, token).ConfigureAwait(false) is not null
        || await transaction.GetAsync(DatabasePurge.Collection, database.DatabaseId!, token).ConfigureAwait(false) is not null
        || await HasDeletionAuthorityAsync(transaction, database, token).ConfigureAwait(false);
}

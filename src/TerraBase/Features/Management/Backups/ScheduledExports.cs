using System.Diagnostics;
using System.Globalization;
using Cronos;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Backup;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using ZLogger;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Backups;

/// <summary>One scheduled export attempt.</summary>
/// <param name="StartedAt">When the attempt started; the schedule counts its next run from here.</param>
/// <param name="FinishedAt">When the attempt ended.</param>
/// <param name="Status"><c>succeeded</c> or <c>failed</c>.</param>
/// <param name="Key">The object written to the customer bucket; null when the attempt failed.</param>
/// <param name="Documents">Documents written.</param>
/// <param name="Bytes">Bytes uploaded.</param>
/// <param name="Error">Why the attempt failed; never carries bucket responses or credentials.</param>
public sealed record ExportRun(DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Status, string? Key, long Documents, long Bytes, string? Error);

/// <summary>When a database is exported to its project's customer bucket, and the last attempt.</summary>
/// <param name="Cron">Standard 5-field cron expression (UTC), or <see langword="null"/> for no scheduled exports.</param>
/// <param name="UpdatedAt">When the schedule was last saved; null until it is.</param>
/// <param name="LastRun">The last attempt, or null before the first.</param>
public sealed record ExportSchedule(string? Cron, DateTimeOffset? UpdatedAt, ExportRun? LastRun);

/// <summary>The body of <c>PUT …/export-schedule</c>.</summary>
/// <param name="Cron">Standard 5-field cron expression (UTC); null or empty stops scheduled exports.</param>
public sealed record ExportScheduleRequest(string? Cron);

/// <summary>A scheduled database in the catalog index <see cref="ScheduledExports.Collection"/>.</summary>
internal sealed record ExportScheduleRecord(string OrganizationId, string ProjectId, string DatabaseId, ExportSchedule Schedule);

/// <summary>A saved schedule, or why it was not saved.</summary>
public readonly union SetExportScheduleResult(ExportSchedule, NotFound, Conflict, Forbidden, Unavailable);

/// <summary>A database's export schedule.</summary>
public readonly union GetExportScheduleResult(ExportSchedule, Unavailable);

/// <summary>Scheduled exports of databases to their project's customer bucket.</summary>
public interface IScheduledExports
{
    /// <summary>Reads the schedule of a database; an unsaved schedule has no cron.</summary>
    ValueTask<GetExportScheduleResult> GetAsync(string databaseId, CancellationToken cancellationToken);

    /// <summary>Saves the schedule of a database; the cron is validated by the caller, and needs the project's customer bucket.</summary>
    ValueTask<SetExportScheduleResult> SetAsync(string userId, string organizationId, TenantContext database, string? cron, CancellationToken cancellationToken);

    /// <summary>One pass: every due export this member ships, one at a time; passes never overlap.</summary>
    Task RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Scheduled exports: once a minute, every database whose schedule is due is streamed as NDJSON (the export endpoint's
/// format, which NDJSON import reads back) to <c>terrabase-exports/{project}/{database}/{time}.ndjson</c> in its project's
/// customer bucket (<see cref="IProjectBackupDestinations"/>, credentials sealed). The member that ships a database exports
/// it, so each run happens once across regions. A failed attempt is recorded and the next one waits for the next occurrence.
/// </summary>
internal sealed class ScheduledExports(
    SystemCatalog catalog, IDocumentService documents, IShipperLeases leases, IProjectBackupDestinations destinations, IServiceProvider services,
    TerraBaseOptions options, TimeProvider time, ILogger<ScheduledExports> logger, IStorageEngine engine, IDiskBudget disk) : BackgroundService, IScheduledExports
{
    /// <summary>Catalog collection: database id → <see cref="ExportScheduleRecord"/>; a deleted database's entry is dropped by the next pass.</summary>
    public const string Collection = "export_schedules";

    private const string Succeeded = "succeeded";
    private const string Failed = "failed";
    private const int PageSize = 100;
    private readonly SemaphoreSlim _pass = new(1, 1);

    public async ValueTask<GetExportScheduleResult> GetAsync(string databaseId, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<GetExportScheduleResult>(async transaction =>
            (await SystemCatalog.GetAsync(transaction, Collection, databaseId, TerraBaseJsonContext.Default.ExportScheduleRecord, cancellationToken).ConfigureAwait(false))?.Schedule
                ?? new ExportSchedule(null, null, null), static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<SetExportScheduleResult> SetAsync(string userId, string organizationId, TenantContext database, string? cron, CancellationToken cancellationToken)
    {
        if (cron is not null && await destinations.ReadAsync(organizationId, database.ProjectId!, cancellationToken).ConfigureAwait(false) is not ProjectBackupDestinationRecord)
        {
            return new Conflict("configure the project's backup destination first");
        }

        return await catalog.WriteAsync<SetExportScheduleResult>(async transaction =>
        {
            if (await EngineManagementService.GetDatabaseAsync(transaction, userId, organizationId, database.ProjectId!, database.DatabaseId!, cancellationToken).ConfigureAwait(false) is null)
            {
                return new NotFound();
            }

            var access = await EngineManagementService.WritableAsync(transaction, userId, organizationId, cancellationToken).ConfigureAwait(false);
            if (access is not Organization)
            {
                return access switch
                {
                    Forbidden error => error,
                    NotFound => new NotFound(),
                    Organization => throw new UnreachableException(),
                };
            }

            var previous = await SystemCatalog.GetAsync(transaction, Collection, database.DatabaseId!, TerraBaseJsonContext.Default.ExportScheduleRecord, cancellationToken).ConfigureAwait(false);
            var schedule = new ExportSchedule(cron, time.GetUtcNow(), previous?.Schedule.LastRun);

            await PutAsync(transaction, Collection, database.DatabaseId!, new ExportScheduleRecord(organizationId, database.ProjectId!, database.DatabaseId!, schedule),
                TerraBaseJsonContext.Default.ExportScheduleRecord, cancellationToken).ConfigureAwait(false);
            return schedule;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _pass.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? after = null;
            do
            {
                var listed = await catalog.ReadAsync<Listed>(
                    async transaction => await ListAsync(transaction, Collection, after, PageSize, TerraBaseJsonContext.Default.ExportScheduleRecord, item => item.DatabaseId, cancellationToken)
                        .ConfigureAwait(false), static error => error, cancellationToken).ConfigureAwait(false);
                if (listed is not ManagementPage<ExportScheduleRecord> page)
                {
                    return;
                }

                foreach (var item in page.Items.Where(item => Due(item.Schedule, time.GetUtcNow())))
                {
                    try
                    {
                        await ExportAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error) when (!cancellationToken.IsCancellationRequested)
                    {
                        logger.ZLogWarning(error, $"Scheduled export of database {item.DatabaseId:@DatabaseId} failed");
                    }
                }

                after = page.Next;
            }
            while (after is not null);
        }
        finally
        {
            _pass.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                // A failed pass must not stop the host; the next tick retries.
                logger.ZLogWarning(error, $"Scheduled export pass failed");
            }
        }
    }

    public override void Dispose()
    {
        _pass.Dispose();
        base.Dispose();
    }

    private readonly union Listed(ManagementPage<ExportScheduleRecord>, Unavailable);

    private readonly union Tenant(TenantContext, NotFound, Unavailable);

    private static bool Due(ExportSchedule schedule, DateTimeOffset now) =>
        schedule is { Cron: { } cron, UpdatedAt: { } saved }
        && CronExpression.Parse(cron, CronFormat.Standard).GetNextOccurrence(Max(saved, schedule.LastRun?.StartedAt ?? saved), TimeZoneInfo.Utc) is { } next
        && next <= now;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right) => left > right ? left : right;

    private async Task ExportAsync(ExportScheduleRecord item, CancellationToken cancellationToken)
    {
        var tenant = await catalog.ReadAsync<Tenant>(async transaction =>
            await EngineManagementService.DatabaseTenantUncheckedAsync(transaction, item.OrganizationId, item.ProjectId, item.DatabaseId, cancellationToken).ConfigureAwait(false)
                is { } found ? found : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);
        if (tenant is Unavailable)
        {
            return;
        }

        if (tenant is not TenantContext database)
        {
            _ = await catalog.WriteAsync<CatalogCheck>(async transaction =>
            {
                await transaction.DeleteAsync(Collection, item.DatabaseId, cancellationToken).ConfigureAwait(false);
                return new Done();
            }, static error => error, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (await leases.ShipperEpochAsync(database, cancellationToken).ConfigureAwait(false) is null)
        {
            return;
        }

        var started = time.GetUtcNow();
        var key = $"terrabase-exports/{item.ProjectId}/{item.DatabaseId}/{started.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.ndjson";
        long count = 0, bytes = 0;
        string? error = null;
        try
        {
            if (await destinations.ReadAsync(item.OrganizationId, item.ProjectId, cancellationToken).ConfigureAwait(false) is not ProjectBackupDestinationRecord destination
                || services.GetService<BackupTargets>() is not { } targets)
            {
                error = "the project's backup destination is not set";
            }
            else
            {
                using var reservation = disk.Reserve(DiskBudget.WorkspaceBytes(await engine.GetDatabaseSizeAsync(database, cancellationToken).ConfigureAwait(false) ?? 0));
                var scratch = Path.Combine(options.DataDirectory, ".exports");
                Directory.CreateDirectory(scratch);
                var file = new FileStream(Path.Combine(scratch, $"{Ulid.NewUlid()}.ndjson"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose);
                await using (file.ConfigureAwait(false))
                {
                    await ExportDatabaseEndpoint.WriteAsync(file, Counted(documents.ExportAsync(database, null, null, cancellationToken)), cancellationToken).ConfigureAwait(false);
                    await file.FlushAsync(cancellationToken).ConfigureAwait(false);
                    bytes = file.Length;
                    file.Position = 0;
                    using var bucket = await targets.PurgeStoreAsync(destination, cancellationToken).ConfigureAwait(false);
                    await bucket.PutAsync(key, file, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (DiskCapacityException failure) when (!cancellationToken.IsCancellationRequested)
        {
            error = "disk_capacity: " + failure.Message;
        }
        catch (Exception failure) when (!cancellationToken.IsCancellationRequested)
        {
            // Bucket errors can include tenant input and credentials. Only a fixed error is recorded, and any failure is
            // recorded so the next attempt waits for the next occurrence instead of retrying every pass.
            logger.ZLogWarning($"Scheduled export of database {item.DatabaseId:@DatabaseId} failed: {failure.GetType().Name}");
            error = "the export could not be written to the customer bucket";
        }

        var run = error is null ? new ExportRun(started, time.GetUtcNow(), Succeeded, key, count, bytes, null) : new ExportRun(started, time.GetUtcNow(), Failed, null, count, 0, error);
        _ = await catalog.WriteAsync<CatalogCheck>(async transaction =>
        {
            if (await SystemCatalog.GetAsync(transaction, Collection, item.DatabaseId, TerraBaseJsonContext.Default.ExportScheduleRecord, cancellationToken).ConfigureAwait(false) is { } current)
            {
                await PutAsync(transaction, Collection, item.DatabaseId, current with { Schedule = current.Schedule with { LastRun = run } },
                    TerraBaseJsonContext.Default.ExportScheduleRecord, cancellationToken).ConfigureAwait(false);
            }

            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

        async IAsyncEnumerable<ExportRow> Counted(IAsyncEnumerable<ExportRow> rows)
        {
            await foreach (var row in rows.ConfigureAwait(false))
            {
                count++;
                yield return row;
            }
        }
    }
}

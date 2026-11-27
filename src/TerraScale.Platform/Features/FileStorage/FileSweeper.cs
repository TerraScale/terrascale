using ZLogger;

namespace TerraScale.Platform.Features.FileStorage;

public sealed class FileSweeper(
    PlatformStore store,
    S3FileObjectStore objects,
    PlatformOptions options,
    PlatformReadiness readiness,
    TimeProvider timeProvider,
    ILogger<FileSweeper> logger) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Files is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!readiness.SchemaReady)
            {
                continue;
            }

            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception error) when (!stoppingToken.IsCancellationRequested)
            {
                logger.ZLogWarning(error, $"File cleanup pass failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var nowMs = now.UnixMs();
        var settledMs = (now - FileStorageService.UploadSettle).UnixMs();
        var after = "";
        while (true)
        {
            var statement = SqlValues.Statement(
                "SELECT f.organization_id, f.project_id, f.bucket_id, f.id, f.status, COALESCE(b.region, r.region), CASE WHEN b.id IS NULL THEN 1 ELSE 0 END "
                + "FROM platform_files f "
                + "LEFT JOIN platform_buckets b ON b.project_id = f.project_id AND b.id = f.bucket_id "
                + "LEFT JOIN platform_bucket_regions r ON r.project_id = f.project_id AND r.bucket_id = f.bucket_id "
                + "WHERE ((((f.status = 'deleted' AND f.delete_after <= ?) OR f.status = 'pending') AND b.id IS NOT NULL) OR b.id IS NULL) "
                + "AND f.upload_expires_at <= ? AND f.id > ? "
                + "ORDER BY f.id LIMIT ?",
                SqlValues.Number(nowMs),
                SqlValues.Number(settledMs),
                SqlValues.Text(after),
                SqlValues.Number(BatchSize));
            var expiredOutcome = await store.ReadAsync(statement, cancellationToken);
            var expired = expiredOutcome switch
            {
                SqlExecuteResponse { Results: var results } => results[0].RowsOf().ToArray(),
                PlatformConflict conflict => Skip(conflict.Message),
                PlatformUnavailable unavailable => Skip(unavailable.Message),
            };
            foreach (var row in expired)
            {
                after = row.Text(3);
                await EraseAsync(row, nowMs, settledMs, cancellationToken);
            }

            if (expired.Length < BatchSize)
            {
                break;
            }
        }

        await SweepPurgesAsync(nowMs, cancellationToken);
    }

    private async Task SweepPurgesAsync(long nowMs, CancellationToken cancellationToken)
    {
        var afterProject = "";
        var afterBucket = "";
        var afterId = "";
        while (true)
        {
            var statement = SqlValues.Statement(
                "SELECT organization_id, project_id, bucket_id, id, region FROM platform_file_purges "
                + "WHERE purge_after <= ? AND (project_id, bucket_id, id) > (?, ?, ?) "
                + "ORDER BY project_id, bucket_id, id LIMIT ?",
                SqlValues.Number(nowMs),
                SqlValues.Text(afterProject),
                SqlValues.Text(afterBucket),
                SqlValues.Text(afterId),
                SqlValues.Number(BatchSize));
            var dueOutcome = await store.ReadAsync(statement, cancellationToken);
            var due = dueOutcome switch
            {
                SqlExecuteResponse { Results: var results } => results[0].RowsOf().ToArray(),
                PlatformConflict conflict => Skip(conflict.Message),
                PlatformUnavailable unavailable => Skip(unavailable.Message),
            };
            foreach (var row in due)
            {
                afterProject = row.Text(1);
                afterBucket = row.Text(2);
                afterId = row.Text(3);
                await ErasePurgeAsync(row, cancellationToken);
            }

            if (due.Length < BatchSize)
            {
                return;
            }
        }
    }

    private async Task ErasePurgeAsync(SqlRow row, CancellationToken cancellationToken)
    {
        var key = S3FileObjectStore.Key(Ulid.Parse(row.Text(0)), Ulid.Parse(row.Text(1)), row.Text(2), row.Text(3));
        var erasedOutcome = await objects.DeleteAsync(row.Text(4), key, cancellationToken);
        var erased = erasedOutcome switch
        {
            FileObjectDeleted => true,
            PlatformUnavailable unavailable => Deferred(unavailable.Message),
        };
        if (!erased)
        {
            return;
        }

        var forget = SqlValues.Statement(
            "DELETE FROM platform_file_purges WHERE project_id = ? AND bucket_id = ? AND id = ?",
            SqlValues.Text(row.Text(1)),
            SqlValues.Text(row.Text(2)),
            SqlValues.Text(row.Text(3)));
        var forgottenOutcome = await store.ExecuteAsync([forget], SqlContext.Fast, cancellationToken);
        var failure = forgottenOutcome switch
        {
            SqlExecuteResponse => null,
            PlatformConflict conflict => conflict.Message,
            PlatformUnavailable unavailable => unavailable.Message,
        };
        if (failure is not null)
        {
            _ = Deferred(failure);
        }
    }

    private async Task EraseAsync(SqlRow row, long now, long settled, CancellationToken cancellationToken)
    {
        var status = row.Text(4);
        var isOrphan = row.Number(6) != 0;
        if (isOrphan)
        {
            if (status != "deleted" && !await ClaimOrphanAsync(row, now, settled, cancellationToken))
            {
                return;
            }
        }
        else if (status == "pending" && !await ClaimPendingAsync(row, now, settled, cancellationToken))
        {
            return;
        }

        var key = S3FileObjectStore.Key(Ulid.Parse(row.Text(0)), Ulid.Parse(row.Text(1)), row.Text(2), row.Text(3));
        var erased = row.OptionalText(5) is { } region
            ? await DeleteObjectAsync(region, key, cancellationToken)
            : await DeleteObjectAcrossRegionsAsync(key, cancellationToken);
        if (!erased)
        {
            return;
        }

        var forget = SqlValues.Statement(
            "DELETE FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = 'deleted' AND upload_expires_at <= ?",
            SqlValues.Text(row.Text(1)),
            SqlValues.Text(row.Text(2)),
            SqlValues.Text(row.Text(3)),
            SqlValues.Number(settled));
        var forgottenOutcome = await store.ExecuteAsync([forget], SqlContext.Fast, cancellationToken);
        var failure = forgottenOutcome switch
        {
            SqlExecuteResponse => null,
            PlatformConflict conflict => conflict.Message,
            PlatformUnavailable unavailable => unavailable.Message,
        };
        if (failure is not null)
        {
            _ = Deferred(failure);
        }
    }

    private async Task<bool> DeleteObjectAsync(string region, string key, CancellationToken cancellationToken)
    {
        var erasedOutcome = await objects.DeleteAsync(region, key, cancellationToken);
        return erasedOutcome switch
        {
            FileObjectDeleted => true,
            PlatformUnavailable unavailable => Deferred(unavailable.Message),
        };
    }

    private async Task<bool> DeleteObjectAcrossRegionsAsync(string key, CancellationToken cancellationToken)
    {
        var known = await store.ReadAsync(SqlValues.Statement(ProjectFileErasure.RegionsQuery), cancellationToken);
        IReadOnlyList<string>? regions = known switch
        {
            SqlExecuteResponse { Results: var results } => ProjectFileErasure.Regions(results[0]),
            PlatformConflict conflict => DeferRegions(conflict.Message),
            PlatformUnavailable unavailable => DeferRegions(unavailable.Message),
        };
        if (regions is not { Count: > 0 })
        {
            return false;
        }

        var erased = true;
        foreach (var region in regions)
        {
            erased &= await DeleteObjectAsync(region, key, cancellationToken);
        }

        return erased;
    }

    private IReadOnlyList<string>? DeferRegions(string message)
    {
        _ = Deferred(message);
        return null;
    }

    private Task<bool> ClaimPendingAsync(SqlRow row, long now, long settled, CancellationToken cancellationToken) =>
        ClaimAsync(
            SqlValues.Statement(
                "UPDATE platform_files SET status = 'deleted', delete_after = ? "
                + "WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = 'pending' AND upload_expires_at <= ?",
                SqlValues.Number(now),
                SqlValues.Text(row.Text(1)),
                SqlValues.Text(row.Text(2)),
                SqlValues.Text(row.Text(3)),
                SqlValues.Number(settled)),
            cancellationToken);

    private Task<bool> ClaimOrphanAsync(SqlRow row, long now, long settled, CancellationToken cancellationToken) =>
        ClaimAsync(
            SqlValues.Statement(
                "UPDATE platform_files SET status = 'deleted', delete_after = ? "
                + "WHERE project_id = ? AND bucket_id = ? AND id = ? AND status <> 'deleted' AND upload_expires_at <= ? "
                + "AND NOT EXISTS (SELECT 1 FROM platform_buckets b WHERE b.project_id = platform_files.project_id AND b.id = platform_files.bucket_id)",
                SqlValues.Number(now),
                SqlValues.Text(row.Text(1)),
                SqlValues.Text(row.Text(2)),
                SqlValues.Text(row.Text(3)),
                SqlValues.Number(settled)),
            cancellationToken);

    private async Task<bool> ClaimAsync(SqlHttpStatement claim, CancellationToken cancellationToken)
    {
        var outcome = await store.ExecuteAsync([claim], SqlContext.Strong, cancellationToken);
        return outcome switch
        {
            SqlExecuteResponse { Results: var results } => results[0].RowsAffected > 0,
            PlatformConflict conflict => Deferred(conflict.Message),
            PlatformUnavailable unavailable => Deferred(unavailable.Message),
        };
    }

    private SqlRow[] Skip(string message) => Deferred(message) ? [] : [];

    private bool Deferred(string message)
    {
        logger.ZLogWarning($"File cleanup deferred: {message}");
        return false;
    }
}

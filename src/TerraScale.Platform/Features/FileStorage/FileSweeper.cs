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
        var after = "";
        while (true)
        {
            var statement = SqlValues.Statement(
                "SELECT f.organization_id, f.project_id, f.bucket_id, f.id, f.status, b.region FROM platform_files f "
                + "JOIN platform_buckets b ON b.project_id = f.project_id AND b.id = f.bucket_id "
                + "WHERE ((f.status = 'deleted' AND f.delete_after <= ?) OR (f.status = 'pending' AND f.upload_expires_at <= ?)) AND f.id > ? "
                + "ORDER BY f.id LIMIT ?",
                SqlValues.Number(now.UnixMs()),
                SqlValues.Number((now - FileStorageService.UploadSettle).UnixMs()),
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
                if (!await EraseAsync(row, cancellationToken))
                {
                    return;
                }
            }

            if (expired.Length < BatchSize)
            {
                return;
            }
        }
    }

    private async Task<bool> EraseAsync(SqlRow row, CancellationToken cancellationToken)
    {
        var key = S3FileObjectStore.Key(Ulid.Parse(row.Text(0)), Ulid.Parse(row.Text(1)), row.Text(2), row.Text(3));
        var erasedOutcome = await objects.DeleteAsync(row.Text(5), key, cancellationToken);
        var erased = erasedOutcome switch
        {
            FileObjectDeleted => true,
            PlatformUnavailable unavailable => Deferred(unavailable.Message),
        };
        if (!erased)
        {
            return true;
        }

        var forget = SqlValues.Statement(
            "DELETE FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = ?",
            SqlValues.Text(row.Text(1)),
            SqlValues.Text(row.Text(2)),
            SqlValues.Text(row.Text(3)),
            SqlValues.Text(row.Text(4)));
        var forgottenOutcome = await store.ExecuteAsync([forget], SqlContext.Fast, cancellationToken);
        return forgottenOutcome switch
        {
            SqlExecuteResponse => true,
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

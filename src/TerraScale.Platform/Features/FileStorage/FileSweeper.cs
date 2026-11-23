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
        var now = timeProvider.GetUtcNow().UnixMs();
        var statement = SqlValues.Statement(
            "SELECT f.organization_id, f.project_id, f.bucket_id, f.id, f.status, b.region FROM platform_files f "
            + "JOIN platform_buckets b ON b.project_id = f.project_id AND b.id = f.bucket_id "
            + "WHERE (f.status = 'deleted' AND f.delete_after <= ?) OR (f.status = 'pending' AND f.upload_expires_at <= ?) LIMIT ?",
            SqlValues.Number(now),
            SqlValues.Number(now - 60_000),
            SqlValues.Number(BatchSize));
        var expired = await store.ReadAsync(statement, cancellationToken) switch
        {
            IReadOnlyList<SqlHttpStatementResult> results => results[0].RowsOf().ToArray(),
            Conflict conflict => Skip(conflict.Message),
            Unavailable unavailable => Skip(unavailable.Message),
        };
        foreach (var row in expired)
        {
            var key = S3FileObjectStore.Key(Ulid.Parse(row.Text(0)), Ulid.Parse(row.Text(1)), row.Text(2), row.Text(3));
            var erased = await objects.DeleteAsync(row.Text(5), key, cancellationToken) switch
            {
                Done => true,
                Unavailable unavailable => Deferred(unavailable.Message),
            };
            if (!erased)
            {
                continue;
            }

            var forget = SqlValues.Statement(
                "DELETE FROM platform_files WHERE project_id = ? AND bucket_id = ? AND id = ? AND status = ?",
                SqlValues.Text(row.Text(1)),
                SqlValues.Text(row.Text(2)),
                SqlValues.Text(row.Text(3)),
                SqlValues.Text(row.Text(4)));
            var forgotten = await store.ExecuteAsync([forget], SqlContext.Fast, cancellationToken) switch
            {
                IReadOnlyList<SqlHttpStatementResult> => true,
                Conflict conflict => Deferred(conflict.Message),
                Unavailable unavailable => Deferred(unavailable.Message),
            };
            if (!forgotten)
            {
                return;
            }
        }
    }

    private SqlRow[] Skip(string message) => Deferred(message) ? [] : [];

    private bool Deferred(string message)
    {
        logger.ZLogWarning($"File cleanup deferred: {message}");
        return false;
    }
}

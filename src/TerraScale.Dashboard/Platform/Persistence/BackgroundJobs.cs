using System.Data;
using System.Data.Common;
using Dapper;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed partial class BackgroundJobs(DashboardDb storage, TimeProvider clock, ILogger<BackgroundJobs> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Renewal = TimeSpan.FromSeconds(20);

    public const string Snapshot = """
        SELECT id, attempted_at, available_at, checkpoint, cursor, delivery_id, failures, lease_until, next_cursor, owner, payload, succeeded_at,
            strftime('%Y-%m-%d %H:%M:%f', 'now') AS database_now
        FROM background_jobs
        """;

    private const string Busy = """
        SELECT count(*) FROM background_jobs
        WHERE id = @Id AND ((@Fence IS NOT NULL AND checkpoint <> @Fence) OR lease_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
            OR (available_at > strftime('%Y-%m-%d %H:%M:%f', 'now') AND (@Target IS NULL OR cursor = @Target OR cursor = @Target || '/projects')))
        """;

    private const string Acquire = """
        INSERT INTO background_jobs (id, owner, checkpoint, available_at, lease_until, failures)
        SELECT @Id, @Owner, @Checkpoint, '0001-01-01 00:00:00', strftime('%Y-%m-%d %H:%M:%f', 'now', '+120 seconds'), 0
        WHERE (@Fence IS NULL OR COALESCE((SELECT checkpoint FROM background_jobs WHERE id = @Id), '') = @Fence)
          AND (@DatabaseId IS NULL OR EXISTS (SELECT 1 FROM project_databases WHERE id = @DatabaseId))
        ON CONFLICT(id) DO UPDATE SET owner = excluded.owner, checkpoint = excluded.checkpoint, lease_until = excluded.lease_until
        WHERE (@Fence IS NULL OR background_jobs.checkpoint = @Fence)
          AND background_jobs.lease_until <= strftime('%Y-%m-%d %H:%M:%f', 'now')
          AND (background_jobs.available_at <= strftime('%Y-%m-%d %H:%M:%f', 'now')
               OR (@Target IS NOT NULL AND (background_jobs.cursor IS NULL OR background_jobs.cursor NOT IN (@Target, @Target || '/projects'))))
        RETURNING id, owner, checkpoint, available_at, lease_until, failures, attempted_at, succeeded_at, cursor, next_cursor, delivery_id, payload
        """;

    private const string Complete = """
        UPDATE background_jobs
        SET lease_until = '0001-01-01 00:00:00',
            available_at = CASE WHEN @Periodic AND @Delay > 0
                THEN strftime('%Y-%m-%d %H:%M:%f', (CAST(unixepoch('subsec') / @Delay AS INTEGER) + 1) * @Delay, 'unixepoch')
                ELSE strftime('%Y-%m-%d %H:%M:%f', 'now', printf('+%f seconds', @Delay)) END
        WHERE id = @Id AND owner = @Owner AND checkpoint = @Checkpoint
          AND lease_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
        """;

    private const string Release = "UPDATE background_jobs SET lease_until = '0001-01-01 00:00:00' WHERE id = @Id AND owner = @Owner AND checkpoint = @Checkpoint";

    private const string Renew = """
        UPDATE background_jobs
        SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '+120 seconds')
        WHERE id = @Id AND owner = @Owner AND lease_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
        """;

    private const string Save = """
        UPDATE background_jobs
        SET checkpoint = @Checkpoint, available_at = @AvailableAt, failures = @Failures, attempted_at = @AttemptedAt, succeeded_at = @SucceededAt,
            cursor = @Cursor, next_cursor = @NextCursor, delivery_id = @DeliveryId, payload = unhex(@PayloadHex)
        WHERE id = @Id AND owner = @OriginalOwner AND checkpoint = @OriginalCheckpoint
        """;

    private const string Forget = "DELETE FROM background_jobs WHERE id = @Id AND owner = @OriginalOwner AND checkpoint = @OriginalCheckpoint";

    public async Task<bool> RunAsync(string id, TimeSpan interval, Func<Claim, CancellationToken, Task> work, CancellationToken cancellationToken, string? target = null,
        string? fence = null, string? databaseId = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var connection = await storage.OpenAsync(cancellationToken);
        if (await connection.ExecuteScalarAsync<long>(Busy, new { Id = id, Fence = fence, Target = target }) > 0)
        {
            return false;
        }

        var owner = AccountService.NewId();
        var checkpoint = AccountService.NewId();
        var started = clock.GetTimestamp();
        var rows = (await connection.QueryAsync<BackgroundJobRow>(Acquire,
            new { Id = id, Owner = owner, Checkpoint = checkpoint, Fence = fence, DatabaseId = databaseId, Target = target })).AsList();
        if (rows is not [var row])
        {
            return false;
        }

        using var stop = new CancellationTokenSource();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewAsync(id, owner, started, active, stop.Token);
        var claim = new Claim(connection, row, DateTime.SpecifyKind(row.LeaseUntil, DateTimeKind.Utc) - Lease);
        var completed = false;
        var held = true;
        try
        {
            await work(claim, active.Token);
            active.Token.ThrowIfCancellationRequested();
            completed = true;
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await renewing;
            }
            finally
            {
                if (!claim.Forgotten)
                {
                    await using var release = await storage.OpenAsync(CancellationToken.None);
                    var periodic = claim.Delay is null && interval > TimeSpan.Zero;
                    var delay = Math.Clamp((claim.Delay ?? interval).TotalSeconds, 0, TimeSpan.FromDays(1).TotalSeconds);
                    held = completed
                        ? await release.ExecuteAsync(Complete, new { Id = id, Owner = owner, row.Checkpoint, Periodic = periodic, Delay = delay }) == 1
                        : await release.ExecuteAsync(Release, new { Id = id, Owner = owner, row.Checkpoint }) >= 0;
                }
            }
        }

        if (!held)
        {
            throw new DBConcurrencyException("The background job claim expired or changed owner.");
        }

        return true;
    }

    private async Task RenewAsync(string id, string owner, long proven, CancellationTokenSource active, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Renewal, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var remaining = Lease - clock.GetElapsedTime(proven) - Renewal;
                if (remaining <= TimeSpan.Zero)
                {
                    await active.CancelAsync();
                    throw new DBConcurrencyException("The background job lease can no longer be proven.");
                }

                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(remaining);
                var started = clock.GetTimestamp();
                try
                {
                    await using var connection = await storage.OpenAsync(budget.Token);
                    if (await connection.ExecuteAsync(Renew, new { Id = id, Owner = owner }) != 1)
                    {
                        await active.CancelAsync();
                        throw new DBConcurrencyException("The background job claim expired or changed owner.");
                    }

                    proven = started;
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested
                    && error is HttpRequestException or IOException or TimeoutException or OperationCanceledException or TerraBaseSqlException)
                {
                    RenewalUnavailable(logger, id, error.GetType().Name);
                    if (Lease - clock.GetElapsedTime(proven) <= Renewal)
                    {
                        await active.CancelAsync();
                        throw new DBConcurrencyException("The background job lease can no longer be proven.", error);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (!stoppingToken.IsCancellationRequested)
            {
                await active.CancelAsync();
            }
        }
    }

    [LoggerMessage(71, LogLevel.Warning, "Background job {JobId} lease renewal is unavailable ({ErrorType}); retaining the last proven lease.")]
    private static partial void RenewalUnavailable(ILogger logger, string jobId, string errorType);

    public sealed class Claim(TerraBaseDbConnection connection, BackgroundJobRow state, DateTime now)
    {
        public TerraBaseDbConnection Connection { get; } = connection;
        public BackgroundJobRow State { get; } = state;
        public DateTime Now { get; } = now;
        public TimeSpan? Delay { get; set; }
        internal bool Forgotten { get; private set; }

        public Task SaveAsync(CancellationToken cancellationToken) => SaveAsync((DbTransaction?)null, cancellationToken);

        public async Task SaveAsync(DbTransaction? transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State.AvailableAt = Now + TimeSpan.FromSeconds(Math.Clamp((Delay ?? TimeSpan.Zero).TotalSeconds, 0, TimeSpan.FromDays(1).TotalSeconds));
            State.OriginalOwner = State.Owner;
            State.OriginalCheckpoint = State.Checkpoint;
            State.Checkpoint = AccountService.NewId();
            DashboardDb.Expect(await Connection.ExecuteAsync(Save, State, transaction));
        }

        public async Task SaveAsync(Func<TerraBaseDbConnection, DbTransaction, Task> stage, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stage);
            await using var transaction = await Connection.BeginTransactionAsync(cancellationToken);
            await stage(Connection, transaction);
            await SaveAsync(transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task ForgetAsync(DbTransaction? transaction, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State.OriginalOwner = State.Owner;
            State.OriginalCheckpoint = State.Checkpoint;
            DashboardDb.Expect(await Connection.ExecuteAsync(Forget, State, transaction));
            Forgotten = true;
        }
    }
}

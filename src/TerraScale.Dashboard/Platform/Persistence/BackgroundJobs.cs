using Microsoft.EntityFrameworkCore;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed partial class BackgroundJobs(IDbContextFactory<DashboardDbContext> storage, TimeProvider clock, ILogger<BackgroundJobs> logger)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Renewal = TimeSpan.FromSeconds(20);

    public async Task<bool> RunAsync(string id, TimeSpan interval, Func<Claim, CancellationToken, Task> work, CancellationToken cancellationToken, string? target = null, string? fence = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        if (await db.BackgroundJobs.AsNoTracking().AnyAsync(row => row.Id == id &&
            (fence != null && row.Checkpoint != fence || row.LeaseUntil > DateTime.UtcNow || row.AvailableAt > DateTime.UtcNow &&
                (target == null || row.Cursor == target || row.Cursor == target + "/projects")), cancellationToken)) { return false; }
        var owner = AccountService.NewId();
        var checkpoint = AccountService.NewId();
        var started = clock.GetTimestamp();
        var rows = await db.BackgroundJobs.FromSqlInterpolated($"""
            INSERT INTO background_jobs (id, owner, checkpoint, available_at, lease_until, failures)
            VALUES ({id}, {owner}, {checkpoint}, '0001-01-01 00:00:00', strftime('%Y-%m-%d %H:%M:%f', 'now', '+120 seconds'), 0)
            ON CONFLICT(id) DO UPDATE SET owner = excluded.owner, checkpoint = excluded.checkpoint, lease_until = excluded.lease_until
            WHERE ({fence} IS NULL OR background_jobs.checkpoint = {fence})
              AND background_jobs.lease_until <= strftime('%Y-%m-%d %H:%M:%f', 'now')
              AND (background_jobs.available_at <= strftime('%Y-%m-%d %H:%M:%f', 'now')
                   OR ({target} IS NOT NULL AND (background_jobs.cursor IS NULL OR background_jobs.cursor NOT IN ({target}, {target} || '/projects'))))
            RETURNING *
            """).ToListAsync(cancellationToken);
        if (rows.Count == 0) { return false; }
        var row = rows.Single();
        using var stop = new CancellationTokenSource();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewAsync(id, owner, started, active, stop.Token);
        var claim = new Claim(db, row, row.LeaseUntil - Lease);
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
            try { await renewing; }
            finally
            {
                if (db.Entry(row).State != EntityState.Detached)
                {
                    await using var release = await storage.CreateDbContextAsync(CancellationToken.None);
                    var periodic = claim.Delay is null && interval > TimeSpan.Zero;
                    var delay = Math.Clamp((claim.Delay ?? interval).TotalSeconds, 0, TimeSpan.FromDays(1).TotalSeconds);
                    if (completed)
                    {
                        held = await release.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE background_jobs
                            SET lease_until = '0001-01-01 00:00:00',
                                available_at = CASE WHEN {periodic} AND {delay} > 0
                                    THEN strftime('%Y-%m-%d %H:%M:%f', (CAST(unixepoch('subsec') / {delay} AS INTEGER) + 1) * {delay}, 'unixepoch')
                                    ELSE strftime('%Y-%m-%d %H:%M:%f', 'now', printf('+%f seconds', {delay})) END
                            WHERE id = {id} AND owner = {owner} AND checkpoint = {row.Checkpoint}
                              AND lease_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
                            """, CancellationToken.None) == 1;
                    }
                    else
                    {
                        await release.Database.ExecuteSqlInterpolatedAsync($"""
                            UPDATE background_jobs SET lease_until = '0001-01-01 00:00:00'
                            WHERE id = {id} AND owner = {owner} AND checkpoint = {row.Checkpoint}
                            """, CancellationToken.None);
                    }
                }
            }
        }
        if (!held) { throw new DbUpdateConcurrencyException("The background job claim expired or changed owner."); }
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
                    throw new DbUpdateConcurrencyException("The background job lease can no longer be proven.");
                }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                budget.CancelAfter(remaining);
                var started = clock.GetTimestamp();
                try
                {
                    await using var db = await storage.CreateDbContextAsync(budget.Token);
                    if (await db.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE background_jobs
                        SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '+120 seconds')
                        WHERE id = {id} AND owner = {owner}
                          AND lease_until > strftime('%Y-%m-%d %H:%M:%f', 'now')
                        """, budget.Token) != 1)
                    {
                        await active.CancelAsync();
                        throw new DbUpdateConcurrencyException("The background job claim expired or changed owner.");
                    }
                    proven = started;
                }
                catch (Exception error) when (!stoppingToken.IsCancellationRequested &&
                    error is HttpRequestException or IOException or TimeoutException or OperationCanceledException or DbUpdateException && error is not DbUpdateConcurrencyException)
                {
                    RenewalUnavailable(logger, id, error.GetType().Name);
                    if (Lease - clock.GetElapsedTime(proven) <= Renewal)
                    {
                        await active.CancelAsync();
                        throw new DbUpdateConcurrencyException("The background job lease can no longer be proven.", error);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        finally
        {
            if (!stoppingToken.IsCancellationRequested) { await active.CancelAsync(); }
        }
    }

    [LoggerMessage(71, LogLevel.Warning, "Background job {JobId} lease renewal is unavailable ({ErrorType}); retaining the last proven lease.")]
    private static partial void RenewalUnavailable(ILogger logger, string jobId, string errorType);

    public sealed class Claim(DashboardDbContext database, BackgroundJobRow state, DateTime now)
    {
        public DashboardDbContext Database { get; } = database;
        public BackgroundJobRow State { get; } = state;
        public DateTime Now { get; } = now;
        public TimeSpan? Delay { get; set; }

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            State.AvailableAt = Now + TimeSpan.FromSeconds(Math.Clamp((Delay ?? TimeSpan.Zero).TotalSeconds, 0, TimeSpan.FromDays(1).TotalSeconds));
            var lease = Database.Entry(State).Property(row => row.LeaseUntil);
            lease.CurrentValue = lease.OriginalValue;
            lease.IsModified = false;
            State.Checkpoint = AccountService.NewId();
            await Database.SaveChangesAsync(cancellationToken);
        }
    }
}

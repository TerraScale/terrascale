using Microsoft.EntityFrameworkCore;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Platform.Persistence;

public sealed class BackgroundJobs(IDbContextFactory<DashboardDbContext> storage, TimeProvider clock)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Renewal = TimeSpan.FromSeconds(20);

    public async Task<bool> RunAsync(string id, TimeSpan interval, Func<Claim, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var owner = AccountService.NewId();
        var row = await db.BackgroundJobs.SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (row is null)
        {
            row = new BackgroundJobRow { Id = id, Owner = owner, Checkpoint = AccountService.NewId(), LeaseUntil = now + Lease };
            db.BackgroundJobs.Add(row);
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException error) when (error.InnerException is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.UniqueViolation or TerraBaseSqlErrorCodes.PrimaryKeyViolation })
            {
                return false;
            }
        }
        else
        {
            if (row.AvailableAt > now || row.LeaseUntil > now) { return false; }
            row.Owner = owner;
            row.LeaseUntil = now + Lease;
            try { await db.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateConcurrencyException) { return false; }
        }

        using var stop = new CancellationTokenSource();
        using var active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewing = RenewAsync(id, owner, active, stop.Token);
        var claim = new Claim(db, row);
        try
        {
            await work(claim, active.Token);
            active.Token.ThrowIfCancellationRequested();
            row.AvailableAt = interval == TimeSpan.Zero ? DateTime.MinValue : new DateTime((now.Ticks / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Utc);
            await claim.SaveAsync(active.Token);
        }
        finally
        {
            await stop.CancelAsync();
            try { await renewing; }
            finally
            {
                await using var release = await storage.CreateDbContextAsync(CancellationToken.None);
                await release.BackgroundJobs.Where(item => item.Id == id && item.Owner == owner)
                    .ExecuteUpdateAsync(update => update.SetProperty(item => item.LeaseUntil, DateTime.MinValue), CancellationToken.None);
            }
        }
        return true;
    }

    private async Task RenewAsync(string id, string owner, CancellationTokenSource active, CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Renewal, clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var db = await storage.CreateDbContextAsync(stoppingToken);
                var now = clock.GetUtcNow().UtcDateTime;
                var until = now + Lease;
                if (await db.BackgroundJobs.Where(row => row.Id == id && row.Owner == owner && row.LeaseUntil > now)
                    .ExecuteUpdateAsync(update => update.SetProperty(row => row.LeaseUntil, until), stoppingToken) != 1)
                {
                    throw new DbUpdateConcurrencyException("The background job claim expired or changed owner.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            await active.CancelAsync();
            throw;
        }
    }

    public sealed class Claim(DashboardDbContext database, BackgroundJobRow state)
    {
        public DashboardDbContext Database { get; } = database;
        public BackgroundJobRow State { get; } = state;

        public async Task SaveAsync(CancellationToken cancellationToken)
        {
            State.Checkpoint = AccountService.NewId();
            await Database.SaveChangesAsync(cancellationToken);
        }
    }
}

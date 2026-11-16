using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

internal static partial class BackgroundJourney
{
    private static async Task ExpireAsync(IDbContextFactory<DashboardDbContext> storage, string id, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE background_jobs SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second'), available_at = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second') WHERE id = {id}", cancellationToken);
    }

    private static async Task ClaimAsync(IServiceProvider services, BackgroundOwner owner, string kind, string path, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var jobs = services.GetRequiredService<BackgroundJobs>();
        var id = kind + "/" + AccountService.NewId();
        var now = TimeProvider.System.GetUtcNow();
        using var first = Start(kind == "contention" ? "claim" : kind, kind == "skew" ? now.AddYears(10) : now, path, id);
        try
        {
            Require(await first.StandardOutput.ReadLineAsync(cancellationToken) == "claimed", "The first dashboard must persist its checkpoint before contention.");
            using var backward = BackgroundHost.Build(owner, clock: new BackgroundClock(now.AddYears(-10)));
            var ran = false;
            Require(!await backward.Services.GetRequiredService<BackgroundJobs>().RunAsync(id, TimeSpan.Zero, (_, _) =>
            {
                ran = true;
                return Task.CompletedTask;
            }, cancellationToken) && !ran, "A contender stole a live lease.");
            if (kind == "stale")
            {
                await ExpireAsync(storage, id, cancellationToken);
                Require(await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
                {
                    claim.State.Cursor = "successor";
                    await claim.SaveAsync(token);
                }, cancellationToken), "A successor failed to acquire an expired lease.");
            }
            if (kind == "skew")
            {
                await using var db = await storage.CreateDbContextAsync(cancellationToken);
                var state = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == id).Select(row => new { State = row, Now = DateTime.UtcNow }).SingleAsync(cancellationToken);
                Require(state.State.LeaseUntil > state.Now && state.State.LeaseUntil <= state.Now.AddMinutes(2) && state.State.AvailableAt <= state.Now.AddDays(1), "A skewed checkpoint extended its lease or availability beyond database-clock bounds.");
            }
            await first.StandardInput.WriteLineAsync("resume".AsMemory(), cancellationToken);
            await FinishedAsync(first, cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var state = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == id).Select(row => new { State = row, Now = DateTime.UtcNow }).SingleAsync(cancellationToken);
                Require(state.State.LeaseUntil <= state.Now, "A completed callback retained its lease.");
                if (kind == "stale") { Require(state.State.Cursor == "successor", "The stale owner changed the successor's checkpoint."); }
                if (kind == "skew") { Require(state.State.AvailableAt > state.Now && state.State.AvailableAt <= state.Now.AddHours(1), "A skewed owner pushed its next run beyond database-clock bounds."); }
            }
            if (kind == "contention")
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    Require(await jobs.RunAsync(id, TimeSpan.Zero, (_, _) => Task.CompletedTask, cancellationToken), "A zero-interval callback without SaveAsync retained its lease.");
                }
                Require(await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
                {
                    claim.Database.BackgroundJobs.Remove(claim.State);
                    await claim.Database.SaveChangesAsync(token);
                }, cancellationToken), "Deleting a claimed subject failed completion.");
            }
        }
        finally
        {
            await StopAsync(first);
            await using var db = await storage.CreateDbContextAsync(CancellationToken.None);
            await db.BackgroundJobs.Where(row => row.Id == id).ExecuteDeleteAsync(CancellationToken.None);
        }
    }

    private static async Task RenewalAsync(IServiceProvider services, BackgroundCommands commands, BackgroundClock clock, CancellationToken cancellationToken)
    {
        commands.RefuseRenewal();
        var id = "renewal/" + AccountService.NewId();
        await services.GetRequiredService<BackgroundJobs>().RunAsync(id, TimeSpan.Zero, async (claim, token) =>
        {
            claim.State.Cursor = "before-renewal";
            await claim.SaveAsync(token);
            clock.Advance(TimeSpan.FromSeconds(20));
            await commands.RenewalObserved.Task.WaitAsync(token);
            Require(!token.IsCancellationRequested, "One transient renewal failure canceled a still-proven lease.");
            claim.State.Cursor = "after-renewal";
            await claim.SaveAsync(token);
        }, cancellationToken);
        await using var db = await services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken);
        Require(await db.BackgroundJobs.AsNoTracking().AnyAsync(row => row.Id == id && row.Cursor == "after-renewal", cancellationToken), "The checkpoint after the lost renewal reply was not saved.");
        await db.BackgroundJobs.Where(row => row.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    private static async Task PlansAsync(IServiceProvider services, BackgroundCommands commands, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        var organization = (await services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken))?.Organization ?? throw new IOException("The isolated organization is missing.");
        var plans = services.GetRequiredService<TerraBasePlanSync>();
        await plans.PublishAsync(organization, cancellationToken);
        await using (var db = await services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            Require(await db.BackgroundJobs.AsNoTracking().AnyAsync(row => row.Id == "plan/" + organization.Id && row.SucceededAt != null, cancellationToken), "The call-count proof requires a successfully published plan.");
        }
        commands.Reset();
        await plans.PublishAsync(organization, cancellationToken);
        var request = commands.Counts;
        Require(request.Writes == 0, "A settled plan view wrote a claim.");
        commands.Reset();
        await ActivatorUtilities.CreateInstance<PlanReconciler>(services).RunOnceAsync(cancellationToken);
        var pass = commands.Counts;
        Require(pass.Writes == 0, "An idle reconciler pass wrote claims.");
        Console.WriteLine($"background calls: settled plan request calls={request.Calls} writes={request.Writes}; idle reconciliation pass calls={pass.Calls} writes={pass.Writes}");
    }

    private static async Task IdleAsync(IServiceProvider services, BackgroundCommands commands, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var drain = await DrainAsync(storage, owner, cancellationToken);
        using var receiver = Receiver();
        await ConfigureAsync(receiver, new Uri(drain.Url).AbsolutePath, false, cancellationToken);
        try
        {
            var drains = services.GetRequiredService<LogDrains>();
            await drains.RunOnceAsync(cancellationToken);
            await using (var db = await storage.CreateDbContextAsync(cancellationToken))
            {
                var cursor = await db.LogDrainCursors.AsNoTracking().SingleAsync(row => row.DrainId == drain.Id && row.DatabaseId == owner.DatabaseId, cancellationToken);
                var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(owner.ProjectId, owner.DatabaseId, cursor.Cursor, cancellationToken);
                Require(batch.Items.Count == 0 && !batch.Gap, $"Idle measurement requires an empty source: items={batch.Items.Count}, gap={batch.Gap}.");
            }
            commands.Reset();
            for (var database = 0; database < 100; database++) { await drains.RunOnceAsync(cancellationToken); }
            var idle = commands.Counts;
            Require(idle.Writes == 0, $"Idle source reads wrote drain claims or cursors: calls={idle.Calls}, writes={idle.Writes}, last={commands.LastWrite}.");
            Console.WriteLine($"background calls: 100 idle drain checks calls={idle.Calls} writes={idle.Writes}; projected 100 idle databases writes/min=0");
            await using var transport = await storage.CreateDbContextAsync(cancellationToken);
            var invalid = await transport.LogDrains.SingleAsync(row => row.Id == drain.Id, cancellationToken);
            invalid.Url = "https://[invalid-stored-url";
            var invalidCursor = await transport.LogDrainCursors.SingleAsync(row => row.DrainId == drain.Id && row.DatabaseId == owner.DatabaseId, cancellationToken);
            invalidCursor.Cursor = "00000000000000000000000000";
            await transport.SaveChangesAsync(cancellationToken);
            await drains.RunOnceAsync(cancellationToken);
            await transport.Entry(invalid).ReloadAsync(cancellationToken);
            Require(invalid.LastErrorStatus == 0 && invalid.LastErrorAt is not null, "An invalid drain URL escaped No response recording.");
            Console.WriteLine("background transport: invalid stored URL records LastErrorAt with LastErrorStatus=0");
        }
        finally { await DeleteDrainAsync(storage, drain, CancellationToken.None); }
    }

    private static async Task GateAsync(IServiceProvider services, BackgroundCommands commands, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<DashboardDbContext>>().CreateDbContextAsync(cancellationToken);
        const string future = "99999999999999_FutureDashboard";
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ({future}, {"11.0.0"})", cancellationToken);
        try
        {
            commands.Reset();
            Require(!await services.GetRequiredService<BillingSchemaGate>().AllowsAsync(cancellationToken), "An unknown schema did not idle background work.");
            await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            await services.GetRequiredService<AlertRules>().RunOnceAsync(cancellationToken);
            Require(commands.Counts.Writes == 0, "Workers claimed work under a newer schema.");
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = {future}", CancellationToken.None); }
    }
}

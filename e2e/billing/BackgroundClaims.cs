using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

internal static partial class BackgroundJourney
{
    private static async Task ExpireAsync(DashboardDb storage, string id, CancellationToken cancellationToken) =>
        await storage.ReadAsync(connection => connection.ExecuteAsync("UPDATE background_jobs SET lease_until = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second'), available_at = strftime('%Y-%m-%d %H:%M:%f', 'now', '-1 second') WHERE id = @Id", new { Id = id }), cancellationToken);

    private static async Task ClaimAsync(IServiceProvider services, BackgroundOwner owner, string kind, string path, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<DashboardDb>();
        var jobs = services.GetRequiredService<BackgroundJobs>();
        var id = kind + "/" + AccountService.NewId();
        var now = TimeProvider.System.GetUtcNow();
        using var first = Start(kind == "contention" ? "claim" : kind, kind == "skew" ? now.AddYears(10) : now, path, id);
        try
        {
            Require(await first.StandardOutput.ReadLineAsync(cancellationToken) == "claimed", "The first dashboard must persist its checkpoint before contention.");
            using var backward = BackgroundHost.Build(owner, clock: BackgroundClock.At(now.AddYears(-10)));
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
                var (state, databaseNow) = await JourneySql.LiveJobAsync(storage, id, cancellationToken);
                Require(state.LeaseUntil > databaseNow && state.LeaseUntil <= databaseNow.AddMinutes(2) && state.AvailableAt <= databaseNow.AddDays(1), "A skewed checkpoint extended its lease or availability beyond database-clock bounds.");
            }
            await first.StandardInput.WriteLineAsync("resume".AsMemory(), cancellationToken);
            await FinishedAsync(first, cancellationToken);
            var (finished, finishedNow) = await JourneySql.LiveJobAsync(storage, id, cancellationToken);
            Require(finished.LeaseUntil <= finishedNow, "A completed callback retained its lease.");
            if (kind == "stale") { Require(finished.Cursor == "successor", "The stale owner changed the successor's checkpoint."); }
            if (kind == "skew") { Require(finished.AvailableAt > finishedNow && finished.AvailableAt <= finishedNow.AddHours(1), "A skewed owner pushed its next run beyond database-clock bounds."); }
            if (kind == "contention")
            {
                for (var pass = 0; pass < 2; pass++)
                {
                    Require(await jobs.RunAsync(id, TimeSpan.Zero, (_, _) => Task.CompletedTask, cancellationToken), "A zero-interval callback without SaveAsync retained its lease.");
                }
                Require(await jobs.RunAsync(id, TimeSpan.Zero, (claim, token) => claim.ForgetAsync(null, token), cancellationToken), "Deleting a claimed subject failed completion.");
            }
        }
        finally
        {
            await StopAsync(first);
            await JourneySql.DeleteJobAsync(storage, id, CancellationToken.None);
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
        var storage = services.GetRequiredService<DashboardDb>();
        Require((await storage.ReadAsync(connection => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM background_jobs WHERE id = @Id AND cursor = 'after-renewal'", new { Id = id }), cancellationToken) > 0), "The checkpoint after the lost renewal reply was not saved.");
        await JourneySql.DeleteJobAsync(storage, id, cancellationToken);
    }

    private static async Task PlansAsync(IServiceProvider services, BackgroundCommands commands, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        var organization = (await services.GetRequiredService<OrganizationStore>().FindAsync(owner.OrganizationId, cancellationToken))?.Organization ?? throw new IOException("The isolated organization is missing.");
        var plans = services.GetRequiredService<TerraBasePlanSync>();
        await plans.PublishAsync(organization, cancellationToken);
        Require((await services.GetRequiredService<DashboardDb>().ReadAsync(connection => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM background_jobs WHERE id = @Id AND succeeded_at IS NOT NULL", new { Id = "plan/" + organization.Id }), cancellationToken) > 0),
            "The call-count proof requires a successfully published plan.");
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
        var storage = services.GetRequiredService<DashboardDb>();
        var drain = await DrainAsync(storage, owner, cancellationToken);
        using var receiver = Receiver();
        await ConfigureAsync(receiver, new Uri(drain.Url).AbsolutePath, false, cancellationToken);
        try
        {
            var drains = services.GetRequiredService<LogDrains>();
            await drains.RunOnceAsync(cancellationToken);
            var cursor = await CursorAsync(storage, drain.Id, owner.DatabaseId, cancellationToken) ?? throw new InvalidOperationException("The first drain pass committed no cursor.");
            var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(owner.ProjectId, owner.DatabaseId, cursor.Cursor, cancellationToken);
            Require(batch.Items.Count == 0 && !batch.Gap, $"Idle measurement requires an empty source: items={batch.Items.Count}, gap={batch.Gap}.");
            commands.Reset();
            for (var database = 0; database < 100; database++) { await drains.RunOnceAsync(cancellationToken); }
            var idle = commands.Counts;
            Require(idle.Writes == 0, $"Idle source reads wrote drain claims or cursors: calls={idle.Calls}, writes={idle.Writes}, last={commands.LastWrite}.");
            Console.WriteLine($"background calls: 100 idle drain checks calls={idle.Calls} writes={idle.Writes}; projected 100 idle databases writes/min=0");
            await storage.ReadAsync(connection => connection.ExecuteAsync("UPDATE log_drains SET url = 'https://[invalid-stored-url' WHERE id = @Id; UPDATE log_drain_cursors SET cursor = '00000000000000000000000000' WHERE drain_id = @Id AND database_id = @DatabaseId", new { drain.Id, owner.DatabaseId }), cancellationToken);
            await drains.RunOnceAsync(cancellationToken);
            var invalid = await StoredDrainAsync(storage, drain.Id, cancellationToken);
            Require(invalid.LastErrorStatus == 0 && invalid.LastErrorAt is not null, "An invalid drain URL escaped No response recording.");
            Console.WriteLine("background transport: invalid stored URL records LastErrorAt with LastErrorStatus=0");
        }
        finally { await DeleteDrainAsync(storage, drain, CancellationToken.None); }
    }

    private static async Task GateAsync(IServiceProvider services, BackgroundCommands commands, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<DashboardDb>();
        const string future = "TerraScale.Dashboard.Schema.999_future_dashboard.sql";
        await JourneySql.InsertSchemaVersionAsync(storage, future, TimeProvider.System.GetUtcNow().UtcDateTime, cancellationToken);
        try
        {
            commands.Reset();
            Require(!await services.GetRequiredService<BillingSchemaGate>().AllowsAsync(cancellationToken), "An unknown schema did not idle background work.");
            await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            await services.GetRequiredService<AlertRules>().RunOnceAsync(cancellationToken);
            Require(commands.Counts.Writes == 0, "Workers claimed work under a newer schema.");
        }
        finally { await JourneySql.DeleteSchemaVersionAsync(storage, future, CancellationToken.None); }
    }
}

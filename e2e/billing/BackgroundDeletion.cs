using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Platform.Persistence;

internal static partial class BackgroundJourney
{
    private static async Task DeletedAsync(IServiceProvider services, BackgroundCommands commands, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        var storage = services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var organizations = services.GetRequiredService<OrganizationStore>();
        var jobs = services.GetRequiredService<BackgroundJobs>();
        using var receiver = Receiver();
        (bool Existing, bool AfterWrite)[] scenarios = [(false, false), (true, false), (false, true)];
        foreach (var (existing, afterWrite) in scenarios)
        {
            var original = (await organizations.FindAsync(owner.OrganizationId, cancellationToken)
                ?? throw new IOException("The isolated organization is missing.")).Organization.Projects[owner.ProjectId].Databases[owner.DatabaseId];
            var drain = await DrainAsync(storage, owner, cancellationToken);
            var route = new Uri(drain.Url).AbsolutePath;
            await ConfigureAsync(receiver, route, false, cancellationToken);
            var id = "log-drain/" + owner.ProjectId + "/" + drain.Revision + "/" + owner.DatabaseId;
            string? checkpoint = null;
            if (existing)
            {
                Require(await jobs.RunAsync(id, TimeSpan.Zero, async (claim, token) =>
                {
                    claim.State.Cursor = "observed";
                    await claim.SaveAsync(token);
                    checkpoint = claim.State.Checkpoint;
                }, cancellationToken, databaseId: owner.DatabaseId), "The observed claim was not created.");
            }
            var pause = commands.PauseClaim(id, afterWrite);
            var worker = services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
            try
            {
                if (await Task.WhenAny(pause.Prefetched.Task, worker) != pause.Prefetched.Task)
                {
                    await worker;
                    throw new InvalidOperationException("The worker did not prefetch a real source batch.");
                }
                var removed = await organizations.FindAsync(owner.OrganizationId, cancellationToken)
                    ?? throw new IOException("The isolated organization is missing.");
                removed.Organization.Projects[owner.ProjectId].Databases.Remove(owner.DatabaseId);
                await organizations.ReplaceAsync(removed, cancellationToken);
                await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                {
                    Require(!await db.ProjectDatabases.AnyAsync(row => row.Id == owner.DatabaseId, cancellationToken) &&
                        !await db.BackgroundJobs.AnyAsync(row => row.Id == id, cancellationToken), "Database removal did not remove its membership and claim.");
                }
                pause.Resume.TrySetResult();
                await worker;
                Require((await DeliveriesAsync(receiver, route, cancellationToken)).Length == 0, "A prefetched batch was sent after database deletion.");
                await using (var db = await storage.CreateDbContextAsync(cancellationToken))
                {
                    Require(!await db.BackgroundJobs.AnyAsync(row => row.Id == id, cancellationToken) &&
                        !await db.LogDrainCursors.AnyAsync(row => row.DrainId == drain.Id, cancellationToken), "The stale worker recreated a claim or committed a deleted database cursor.");
                }
                var restored = await organizations.FindAsync(owner.OrganizationId, cancellationToken)
                    ?? throw new IOException("The isolated organization is missing.");
                restored.Organization.Projects[owner.ProjectId].Databases[owner.DatabaseId] = original;
                await organizations.ReplaceAsync(restored, cancellationToken);
                if (checkpoint is { } stale)
                {
                    var invoked = false;
                    Require(!await jobs.RunAsync(id, TimeSpan.Zero, (_, _) =>
                    {
                        invoked = true;
                        return Task.CompletedTask;
                    }, cancellationToken, fence: stale, databaseId: owner.DatabaseId) && !invoked, "A stale checkpoint inserted a fresh claim after its row was deleted.");
                    await using var db = await storage.CreateDbContextAsync(cancellationToken);
                    Require(!await db.BackgroundJobs.AnyAsync(row => row.Id == id, cancellationToken), "The stale checkpoint left an orphan claim.");
                }
            }
            finally
            {
                pause.Resume.TrySetResult();
                await worker;
                if (await organizations.FindAsync(owner.OrganizationId, CancellationToken.None) is { } restored)
                {
                    restored.Organization.Projects[owner.ProjectId].Databases[owner.DatabaseId] = original;
                    await organizations.ReplaceAsync(restored, CancellationToken.None);
                }
                await DeleteDrainAsync(storage, drain, CancellationToken.None);
            }
            Console.WriteLine($"background deletion: existing={existing}, after-acquire={afterWrite}; no send, no claim, no cursor");
        }
    }
}

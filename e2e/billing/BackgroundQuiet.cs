using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

internal static partial class BackgroundJourney
{
    private static async Task<TerraBaseProjectClient> ProjectAsync(HttpClient client, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        using var authHttp = new HttpClient(TerraBaseHttp.CreateHandler());
        var auth = new TerraScaleAuthClient(authHttp, new() { BaseAddress = new("https://auth.terrascale.tech"), InstanceSlug = "terrascale" });
        var keyId = owner.ApiKey.StartsWith("sk_", StringComparison.Ordinal) ? owner.ApiKey[3..29] : owner.ApiKey[..26];
        var token = await auth.Oidc.RequestClientCredentialsTokenAsync(keyId, owner.ApiKey, project: owner.ProjectId, cancellationToken: cancellationToken);
        var state = Environment.GetEnvironmentVariable("E2E_STATE") ?? throw new IOException("E2E_STATE is required.");
        await File.WriteAllTextAsync(Path.Combine(state, "secrets.d", "background-token-" + AccountService.NewId()), token.AccessToken, cancellationToken);
        return new TerraBaseDatabaseClient(client, new() { AccessTokenProvider = _ => ValueTask.FromResult(token.AccessToken), Region = owner.DatabaseRegion.ToLowerInvariant() }).Project(owner.ProjectId);
    }

    private static async Task QuietAsync(IServiceProvider services, BackgroundCommands commands, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        using var client = new HttpClient(TerraBaseHttp.CreateHandler());
        var project = await ProjectAsync(client, owner, cancellationToken);
        var database = await project.CreateDatabaseAsync("quiet-" + AccountService.NewId().ToLowerInvariant(), cancellationToken: cancellationToken);
        var organizations = services.GetRequiredService<OrganizationStore>();
        var stored = await organizations.FindAsync(owner.OrganizationId, cancellationToken) ?? throw new IOException("The isolated organization is missing.");
        var original = stored.Organization.Projects[owner.ProjectId].Databases[owner.DatabaseId];
        stored.Organization.Projects[owner.ProjectId].Databases = new() { [database.Id] = new() { Id = database.Id, ProjectId = owner.ProjectId, Name = database.Name, Region = owner.DatabaseRegion, Regions = [owner.DatabaseRegion] } };
        await organizations.ReplaceAsync(stored, cancellationToken);
        var storage = services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        var drain = await DrainAsync(storage, owner, cancellationToken);
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            db.LogDrainCursors.Add(new() { Id = AccountService.NewId(), DrainId = drain.Id, DatabaseId = database.Id, Cursor = "00000000000000000000000000" });
            await db.SaveChangesAsync(cancellationToken);
        }
        var drains = services.GetRequiredService<LogDrains>();
        await drains.RunOnceAsync(cancellationToken);
        DateTime gapAt;
        var id = "log-drain/" + owner.ProjectId + "/" + drain.Revision + "/" + database.Id;
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            var recorded = await db.LogDrains.AsNoTracking().SingleAsync(row => row.Id == drain.Id, cancellationToken);
            gapAt = recorded.LastGapAt ?? throw new InvalidOperationException("The unchanged-cursor retention gap was not recorded.");
            var state = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == id).Select(row => new { State = row, Now = DateTime.UtcNow }).SingleAsync(cancellationToken);
            Require(state.State.LeaseUntil <= state.Now && state.State.Cursor == "gap/00000000000000000000000000", "The quiet gap retained its lease or lost its acknowledgment.");
        }
        commands.Reset();
        for (var pass = 0; pass < 100; pass++) { await drains.RunOnceAsync(cancellationToken); }
        Require(commands.Counts.Writes == 0, "An unchanged quiet gap repeatedly wrote claims.");
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            Require(await db.LogDrains.AsNoTracking().AnyAsync(row => row.Id == drain.Id && row.LastGapAt == gapAt, cancellationToken), "An unchanged quiet gap was recorded again.");
        }
        var quiet = new BackgroundQuiet(database.Id, database.Name, drain.Id, drain.Revision, gapAt, original);
        await File.WriteAllTextAsync(path + ".quiet", JsonSerializer.Serialize(quiet, BackgroundJson.Default.BackgroundQuiet), cancellationToken);
        using var receiver = Receiver();
        await ConfigureAsync(receiver, new Uri(drain.Url).AbsolutePath, false, cancellationToken);
        var sql = new TerraBaseSqlClient(client, new() { ApiKey = owner.ApiKey, Region = owner.DatabaseRegion.ToLowerInvariant() }).Database(owner.ProjectId, database.Name);
        await sql.ExecuteAsync("SELECT 1213", cancellationToken: cancellationToken);
        Console.WriteLine("background quiet: old empty cursor records one gap; 100 further checks write zero claims; new SQL activity submitted");
    }

    private static async Task<BackgroundQuiet> QuietStateAsync(string path, CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize(await File.ReadAllTextAsync(path + ".quiet", cancellationToken), BackgroundJson.Default.BackgroundQuiet) ?? throw new IOException("Quiet drain state is missing.");

    private static async Task<bool> QuietReadyAsync(IServiceProvider services, string path, CancellationToken cancellationToken)
    {
        var owner = await OwnerAsync(path, cancellationToken);
        var quiet = await QuietStateAsync(path, cancellationToken);
        var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(owner.ProjectId, quiet.DatabaseId, "00000000000000000000000000", cancellationToken);
        return batch.Items.Count > 0;
    }

    private static async Task QuietNextAsync(IServiceProvider services, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        var quiet = await QuietStateAsync(path, cancellationToken);
        var storage = services.GetRequiredService<IDbContextFactory<DashboardDbContext>>();
        await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
        var id = "log-drain/" + owner.ProjectId + "/" + quiet.Revision + "/" + quiet.DatabaseId;
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            var drain = await db.LogDrains.AsNoTracking().SingleAsync(row => row.Id == quiet.DrainId, cancellationToken);
            Require(drain.LastSuccessAt is not null, $"The quiet gap delayed the next available batch: last error status={drain.LastErrorStatus}, at={drain.LastErrorAt:O}.");
            var cursor = await db.LogDrainCursors.AsNoTracking().SingleAsync(row => row.DrainId == quiet.DrainId && row.DatabaseId == quiet.DatabaseId, cancellationToken);
            var state = await db.BackgroundJobs.AsNoTracking().Where(row => row.Id == id).Select(row => new { State = row, Now = DateTime.UtcNow }).SingleAsync(cancellationToken);
            Require(cursor.Cursor != "00000000000000000000000000" && state.State.Payload is null && state.State.LeaseUntil <= state.Now, "The next batch failed cursor commit or retained its lease.");
            using var receiver = Receiver();
            Require((await DeliveriesAsync(receiver, new Uri(drain.Url).AbsolutePath, cancellationToken)).Length == 1, "The next quiet-drain event was not sent exactly once.");
        }
        var organizations = services.GetRequiredService<OrganizationStore>();
        var stored = await organizations.FindAsync(owner.OrganizationId, cancellationToken) ?? throw new IOException("The isolated organization is missing.");
        stored.Organization.Projects[owner.ProjectId].Databases = new() { [owner.DatabaseId] = quiet.Original };
        await organizations.ReplaceAsync(stored, cancellationToken);
        await using (var db = await storage.CreateDbContextAsync(cancellationToken))
        {
            Require(!await db.BackgroundJobs.AnyAsync(row => row.Id == id, cancellationToken), "Removing one database left its log-drain claim row behind.");
        }
        using var client = new HttpClient(TerraBaseHttp.CreateHandler());
        await (await ProjectAsync(client, owner, cancellationToken)).DeleteDatabaseAsync(quiet.DatabaseName, cancellationToken);
        await using var cleanup = await storage.CreateDbContextAsync(cancellationToken);
        await cleanup.LogDrains.Where(row => row.Id == quiet.DrainId).ExecuteDeleteAsync(cancellationToken);
        File.Delete(path + ".quiet");
    }
}

internal sealed record BackgroundQuiet(string DatabaseId, string DatabaseName, string DrainId, string Revision, DateTime GapAt, DatabaseRecord Original);

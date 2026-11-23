using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.Errors;
using TerraScale.DotNet.TerraBase;

internal static partial class BackgroundJourney
{
    private const string OldestCursor = "00000000000000000000000000";

    private static async Task<TerraBaseProjectClient> ProjectAsync(HttpClient client, BackgroundOwner owner, CancellationToken cancellationToken)
    {
        using var authHttp = new HttpClient(TerraBaseHttp.CreateHandler());
        var auth = new TerraScaleAuthClient(authHttp, new() { BaseAddress = new("https://auth.terrascale.tech"), InstanceSlug = "terrascale" });
        var keyId = owner.ApiKey.StartsWith("sk_", StringComparison.Ordinal) ? owner.ApiKey[3..29] : owner.ApiKey[..26];
        var token = await auth.Oidc.RequestClientCredentialsTokenAsync(keyId, owner.ApiKey, project: owner.ProjectId, cancellationToken: cancellationToken) switch
        {
            TerraScaleAuthTokenResponse answered => answered,
            AuthRefused refused => throw new IOException($"ts-auth refused the background token: {(int?)refused.Status} {refused.ErrorCode}"),
            null => throw new IOException("ts-auth returned no token."),
        };
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
        var storage = services.GetRequiredService<DashboardDb>();
        var drain = await DrainAsync(storage, owner, cancellationToken);
        var quiet = new BackgroundQuiet(database.Id, database.Name, drain.Id, drain.Revision, original);
        await File.WriteAllTextAsync(path + ".quiet", JsonSerializer.Serialize(quiet, BackgroundJson.Default.BackgroundQuiet), cancellationToken);
        stored.Organization.Projects[owner.ProjectId].Databases = new() { [database.Id] = new() { Id = database.Id, ProjectId = owner.ProjectId, Name = database.Name, Region = owner.DatabaseRegion, Regions = [owner.DatabaseRegion] } };
        await organizations.ReplaceAsync(stored, cancellationToken);
        await storage.ReadAsync(connection => connection.ExecuteAsync(JourneySql.InsertCursor, new LogDrainCursorRow { Id = AccountService.NewId(), DrainId = drain.Id, DatabaseId = database.Id, Cursor = OldestCursor }), cancellationToken);
        var drains = services.GetRequiredService<LogDrains>();
        await drains.RunOnceAsync(cancellationToken);
        var id = "log-drain/" + owner.ProjectId + "/" + drain.Revision + "/" + database.Id;
        var gapAt = (await StoredDrainAsync(storage, drain.Id, cancellationToken)).LastGapAt ?? throw new InvalidOperationException("The unchanged-cursor retention gap was not recorded.");
        var (state, databaseNow) = await JourneySql.LiveJobAsync(storage, id, cancellationToken);
        Require(state.LeaseUntil <= databaseNow && state.Cursor == "gap/" + OldestCursor, "The quiet gap retained its lease or lost its acknowledgment.");
        commands.Reset();
        for (var pass = 0; pass < 100; pass++) { await drains.RunOnceAsync(cancellationToken); }
        Require(commands.Counts.Writes == 0, "An unchanged quiet gap repeatedly wrote claims.");
        Require((await StoredDrainAsync(storage, drain.Id, cancellationToken)).LastGapAt == gapAt, "An unchanged quiet gap was recorded again.");
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
        var batch = await services.GetRequiredService<ILogDrainSource>().ReadAsync(owner.ProjectId, quiet.DatabaseId, OldestCursor, cancellationToken);
        return batch.Items.Count > 0;
    }

    private static async Task QuietNextAsync(IServiceProvider services, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        var quiet = await QuietStateAsync(path, cancellationToken);
        var storage = services.GetRequiredService<DashboardDb>();
        await services.GetRequiredService<LogDrains>().RunOnceAsync(cancellationToken);
        var id = "log-drain/" + owner.ProjectId + "/" + quiet.Revision + "/" + quiet.DatabaseId;
        var drain = await StoredDrainAsync(storage, quiet.DrainId, cancellationToken);
        Require(drain.LastSuccessAt is not null, $"The quiet gap delayed the next available batch: last error status={drain.LastErrorStatus}, at={drain.LastErrorAt:O}.");
        var cursor = await CursorAsync(storage, quiet.DrainId, quiet.DatabaseId, cancellationToken) ?? throw new InvalidOperationException("The quiet drain lost its cursor.");
        var (state, databaseNow) = await JourneySql.LiveJobAsync(storage, id, cancellationToken);
        Require(cursor.Cursor != OldestCursor && state.Payload is null && state.LeaseUntil <= databaseNow, "The next batch failed cursor commit or retained its lease.");
        using var receiver = Receiver();
        Require((await DeliveriesAsync(receiver, new Uri(drain.Url).AbsolutePath, cancellationToken)).Length == 1, "The next quiet-drain event was not sent exactly once.");
    }

    private static async Task QuietCleanupAsync(IServiceProvider services, BackgroundOwner owner, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path + ".quiet")) { return; }
        var quiet = await QuietStateAsync(path, cancellationToken);
        var storage = services.GetRequiredService<DashboardDb>();
        var organizations = services.GetRequiredService<OrganizationStore>();
        var stored = await organizations.FindAsync(owner.OrganizationId, cancellationToken) ?? throw new IOException("The isolated organization is missing.");
        stored.Organization.Projects[owner.ProjectId].Databases = new() { [owner.DatabaseId] = quiet.Original };
        await organizations.ReplaceAsync(stored, cancellationToken);
        Require(!await JourneySql.JobExistsAsync(storage, "log-drain/" + owner.ProjectId + "/" + quiet.Revision + "/" + quiet.DatabaseId, cancellationToken), "Removing one database left its log-drain claim row behind.");
        using var client = new HttpClient(TerraBaseHttp.CreateHandler());
        await (await ProjectAsync(client, owner, cancellationToken)).DeleteDatabaseAsync(quiet.DatabaseName, cancellationToken);
        await storage.ReadAsync(connection => connection.ExecuteAsync("DELETE FROM log_drain_cursors WHERE drain_id = @Id; DELETE FROM log_drains WHERE id = @Id", new { Id = quiet.DrainId }), cancellationToken);
        File.Delete(path + ".quiet");
    }
}

internal sealed record BackgroundQuiet(string DatabaseId, string DatabaseName, string DrainId, string Revision, DatabaseRecord Original);

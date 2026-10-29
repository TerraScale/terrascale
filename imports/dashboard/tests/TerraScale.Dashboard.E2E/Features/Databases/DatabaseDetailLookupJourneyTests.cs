using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.E2E.Features.Databases;

[ClassDataSource<DashboardRootFixture>]
public sealed class DatabaseDetailLookupJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("backups")]
    [Arguments("branches")]
    public async Task DirectLinksDiscoverOnlyTheRequestedExternalPlacement(string tab)
    {
        await using var probe = await Probe.StartAsync(fixture.Root);
        await probe.ExternalAsync("not-requested");
        var id = await probe.ExternalAsync("external_valid-");
        await probe.SeedAsync();
        probe.Reset();
        using var page = await probe.Client.GetAsync($"/databases/{id}/{tab}");
        await Assert.That(page.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var html = await page.Content.ReadAsStringAsync();
        await Assert.That(html).Contains("external_valid-");
        if (tab == "backups")
        {
            await Assert.That(html).Contains("No restore points yet.");
            await Assert.That(html).Contains("Scheduled full backups enabled");
            await Assert.That(probe.BackupHosts.ToArray()).IsEquivalentTo(new[] { "sjc.api.terrascale.tech" });
        }
        var records = await probe.RecordsAsync();
        await Assert.That(records.Keys.Order().ToArray()).IsEquivalentTo(new[] { Probe.Sentinel, id });
        await Assert.That(records[id].ProjectId).IsEqualTo(probe.Project);
        await Assert.That(records[id].Region).IsEqualTo("SJC");
        await Assert.That(records[id].Regions.ToArray()).IsEquivalentTo(new[] { "SJC", "FRA" });
        await Assert.That(JsonSerializer.Serialize(records[Probe.Sentinel])).IsEqualTo(probe.SentinelJson);
        await Assert.That(probe.Lists.Select(item => item.Region).Distinct().ToArray()).IsEquivalentTo(new[] { "gru", "sjc", "fra" });
        probe.Reset();
        foreach (var knownTab in new[] { "branches", "danger", "branches" })
        {
            using var known = await probe.Client.GetAsync($"/databases/{id}/{knownTab}");
            await Assert.That(known.StatusCode).IsEqualTo(HttpStatusCode.OK);
        }
        await Assert.That(probe.CatalogReads).IsEqualTo(0);
        await Assert.That(probe.Lists.Count).IsEqualTo(0);
        await probe.NoProtocolFailuresAsync();
    }

    [Test]
    [Arguments("invalid", 404)]
    [Arguments("missing", 404)]
    [Arguments("missing-regional", 503)]
    [Arguments("foreign", 404)]
    [Arguments("platform", 403)]
    public async Task UnreachableIdsNeverImportOrSearchOtherProjects(string kind, int status)
    {
        await using var probe = await Probe.StartAsync(fixture.Root);
        var id = kind switch
        {
            "invalid" => "a-database-name",
            "platform" => "00000000000000000000000003",
            "foreign" => await probe.ExternalAsync("foreign", new TerraScaleIdGenerator().NewId()),
            _ => Probe.Missing
        };
        await probe.SeedAsync();
        probe.Reset(); probe.Fault = kind == "missing-regional" ? "regional" : "";
        using var page = await probe.Client.GetAsync($"/databases/{id}/backups");
        await Assert.That((int)page.StatusCode).IsEqualTo(status);
        await Assert.That((await probe.RecordsAsync()).Keys.ToArray()).IsEquivalentTo(new[] { Probe.Sentinel });
        if (kind is "invalid" or "platform") await Assert.That(probe.CatalogReads + probe.Lists.Count).IsEqualTo(0);
        await probe.NoProtocolFailuresAsync();
    }

    [Test]
    [Arguments("catalog", 503)]
    [Arguments("catalog-json", 503)]
    [Arguments("regional", 503)]
    [Arguments("regional404", 503)]
    [Arguments("replica", 200)]
    [Arguments("replica404", 200)]
    [Arguments("json", 503)]
    [Arguments("unrelated-json", 503)]
    [Arguments("unrelated-identity", 503)]
    [Arguments("identity", 503)]
    [Arguments("bad-id", 503)]
    [Arguments("name", 503)]
    [Arguments("all-regions", 503)]
    [Arguments("branch-id", 503)]
    [Arguments("duplicate", 503)]
    [Arguments("conflict", 503)]
    [Arguments("placement", 503)]
    [Arguments("loop", 503)]
    [Arguments("persist", 503)]
    [Arguments("paged", 200)]
    public async Task IncompleteOrAmbiguousAnswersAreRetryableAndPaginationIsExhausted(string fault, int status)
    {
        await using var probe = await Probe.StartAsync(fixture.Root);
        await probe.ExternalAsync("earlier-page");
        var id = await probe.ExternalAsync("external-valid");
        await probe.SeedAsync();
        probe.Reset(); probe.Fault = fault;
        using (var page = await probe.Client.GetAsync($"/databases/{id}/branches"))
            await Assert.That((int)page.StatusCode).IsEqualTo(status);
        var records = await probe.RecordsAsync();
        await Assert.That(JsonSerializer.Serialize(records[Probe.Sentinel])).IsEqualTo(probe.SentinelJson);
        await Assert.That(records.Count).IsEqualTo(status == 200 ? 2 : 1);
        if (status == 200) await Assert.That(records[id].Region).IsEqualTo("SJC");
        await Assert.That(probe.HealthReads).IsEqualTo(0).Because("A health-only region answer cannot prove absence.");
        if (fault == "paged") await Assert.That(probe.Lists.Count(item => item.After == "lookup-page")).IsEqualTo(2);
        probe.Fault = "";
        using var retry = await probe.Client.GetAsync($"/databases/{id}/branches");
        await Assert.That(retry.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That((await probe.RecordsAsync()).Count).IsEqualTo(2);
        await probe.NoProtocolFailuresAsync();
    }

    [Test]
    public async Task ParallelHostsPreserveTheWinningMetadataOnCasRetry()
    {
        await using var probe = await Probe.StartAsync(fixture.Root);
        var id = await probe.ExternalAsync("parallel");
        using var replica = probe.Scenario.CreateClient(replica: true);
        using var login = await HttpJourney.LoginAsync(replica, probe.Account.Email, probe.Account.Password);
        await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
        probe.Reset(); probe.Fault = "pause"; probe.WinnerId = id;
        var first = probe.Client.GetAsync($"/databases/{id}/branches");
        var second = replica.GetAsync($"/databases/{id}/danger");
        try { await probe.BothPaused.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { probe.Release.TrySetResult(); }
        using var one = await first; using var two = await second;
        await Assert.That(one.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(two.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var records = await probe.RecordsAsync();
        await Assert.That(records.Count).IsEqualTo(1);
        await Assert.That(records[id].SpendCapCents).IsEqualTo(71L);
        await Assert.That(records[id].Regions.ToArray()).IsEquivalentTo(new[] { "SJC", "FRA", "GRU" });
        await Assert.That(probe.Conflicts > 0).IsTrue();
        await probe.NoProtocolFailuresAsync();
    }

    // Both hosts retain production service/backend/SDK; only external HTTP and state storage are fixture-owned.
    private sealed class Probe : IDashboardState, IAsyncDisposable
    {
        public const string Sentinel = "01JA0000000000000000000008", Missing = "01JA0000000000000000000009";
        public DatabaseSdkScenario Scenario = null!;
        public HttpClient Client = null!, Producer = null!;
        public (string Email, string Password) Account;
        public string Organization = "", Project = "", Target = "", Fault = "", WinnerId = "", SentinelJson = "";
        public int CatalogReads, HealthReads, Conflicts;
        public ConcurrentQueue<(string Region, string After)> Lists = new();
        public ConcurrentQueue<string> BackupHosts = new();
        public TaskCompletionSource BothPaused = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int paused;
        private readonly SemaphoreSlim writes = new(1, 1);
        private IDashboardState inner = null!;
        private TestAuth auth = null!;
        public static async Task<Probe> StartAsync(string root)
        {
            var probe = new Probe();
            probe.Scenario = await DatabaseSdkScenario.StartAsync(root, builder =>
            {
                probe.auth = (TestAuth)builder.Services.Last(item => item.ServiceType == typeof(IAuthGateway)).ImplementationInstance!;
                probe.inner = (IDashboardState)builder.Services.Last(item => item.ServiceType == typeof(IDashboardState)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardState>(probe));
            }, probe.InterceptAsync);
            probe.Client = probe.Scenario.CreateClient(); probe.Producer = probe.Scenario.CreateProviderClient();
            probe.Account = await DatabaseSdkJourneyTests.NativeWorkspaceAsync(probe.Client);
            var identity = await probe.ReadAsync(probe.Client, "/api/v1/auth/me");
            probe.Organization = identity.GetProperty("organizationId").GetString()!; probe.Project = identity.GetProperty("projectId").GetString()!;
            await probe.ControlAsync(new { command = "catalog", value = new[] { "gru", "sjc", "fra" } });
            return probe;
        }
        public void Reset() { Lists.Clear(); CatalogReads = HealthReads = Conflicts = 0; }
        private async Task<IResult?> InterceptAsync(HttpContext http)
        {
            if (http.Request.Headers.ContainsKey(DatabaseSdkJourneyTests.Forwarded)) return null;
            var path = http.Request.Path.Value!;
            if (Project.Length > 0 && path.StartsWith($"/v1/projects/{Project}/databases/", StringComparison.Ordinal) && path.EndsWith("/backups", StringComparison.Ordinal))
                BackupHosts.Enqueue(http.Request.Host.Host);
            if (path == "/healthz") Interlocked.Increment(ref HealthReads);
            if (path == "/v1/regions")
            {
                Interlocked.Increment(ref CatalogReads);
                if (Fault == "catalog") return Results.StatusCode(503);
                if (Fault == "catalog-json") return Results.Json(new { regions = new[] { "gru", "gru" } });
            }
            if (Project.Length == 0 || http.Request.Method != "GET" || !path.StartsWith("/v1/projects/", StringComparison.Ordinal) || !path.EndsWith("/databases", StringComparison.Ordinal)) return null;
            if (path != $"/v1/projects/{Project}/databases") throw new InvalidOperationException("Lookup searched another project.");
            var grant = auth.ResolveProjectToken(http.Request.Headers.Authorization.ToString()[7..]);
            var region = http.Request.Host.Host.Split('.')[0];
            if (grant is not { } token || token.OrganizationId != Organization || token.ProjectId != Project ||
                !token.Scopes.SequenceEqual(new[] { "databases:read" }) || http.Request.Headers["X-TerraScale-Region-Pin"] != region)
                throw new InvalidOperationException("Lookup changed project authority or regional pin.");
            var after = http.Request.Query["after"].ToString(); Lists.Enqueue((region, after));
            if (Fault == "pause" && region == "sjc")
            { if (Interlocked.Increment(ref paused) == 2) BothPaused.TrySetResult(); await Release.Task.WaitAsync(http.RequestAborted); }
            if (region == "sjc" && Fault is "regional" or "regional404" || region == "fra" && Fault is "replica" or "replica404")
                return Results.StatusCode(Fault.EndsWith("404", StringComparison.Ordinal) ? 404 : 503);
            if (Fault.Length == 0 || Fault is "pause" or "regional" or "regional404" or "replica" or "replica404" || region == "gru") return null;
            if (Fault.StartsWith("unrelated-", StringComparison.Ordinal) && region != "fra") return null;
            if (Fault is "json" or "unrelated-json") return Results.Text("{", "application/json");
            if (Fault == "loop") return Results.Json(new { items = Array.Empty<object>(), next = "loop" });
            if (Fault == "paged") http.Request.QueryString = new("?limit=100");
            var (status, body) = await DatabaseSdkJourneyTests.ForwardAsync(http, Scenario.ProviderOrigin);
            if (status != 200) throw new InvalidOperationException("Fault injection must start with a scoped producer success.");
            var items = body!["items"]!.AsArray(); var target = items.Single(item => item!["id"]!.GetValue<string>() == Target)!;
            if (Fault is "identity" or "unrelated-identity") target["projectId"] = Missing;
            if (Fault == "bad-id") target["id"] = "not-a-ulid";
            if (Fault == "name") target["name"] = "INVALID";
            if (Fault == "all-regions") target["allRegions"] = true;
            if (Fault == "branch-id") target["branchedFrom"] = Target;
            if (Fault == "duplicate") items.Add(target.DeepClone());
            if (Fault == "conflict" && region == "fra") target["name"] = "contradictory";
            if (Fault == "placement") target["replicaRegions"] = new JsonArray("sjc");
            if (Fault == "paged")
            {
                body["items"] = new JsonArray((after.Length == 0 ? items.Take(1) : items.Skip(1)).Select(item => item!.DeepClone()).ToArray());
                body["next"] = after.Length == 0 ? JsonValue.Create("lookup-page") : null;
            }
            return Results.Json(body);
        }
        public async Task<string> ExternalAsync(string name, string? project = null)
        {
            await ControlAsync(new { command = "external-create", projectId = project ?? Project, value = name, home = "sjc", replicas = new[] { "fra" } });
            Target = (await ReadAsync(Producer, "/__test/observations")).GetProperty("databases").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
            return Target;
        }
        public async Task<Dictionary<string, DatabaseRecord>> RecordsAsync() => (await inner.GetAsync(AccountService.OrganizationsCollection, Organization, default))!
            .Document.Deserialize<OrganizationRecord>()!.Projects[Project].Databases;
        public async Task SeedAsync()
        {
            var stored = (await inner.GetAsync(AccountService.OrganizationsCollection, Organization, default))!;
            var org = stored.Document.Deserialize<OrganizationRecord>()!;
            var sentinel = new DatabaseRecord { Id = Sentinel, ProjectId = Project, Name = "stale-kept", Region = "GRU", Status = "failed", SpendCapCents = 43 };
            org.Projects[Project].Databases[Sentinel] = sentinel; SentinelJson = JsonSerializer.Serialize(sentinel);
            await inner.PutAsync(AccountService.OrganizationsCollection, Organization, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(stored.Version), default);
        }
        public async Task NoProtocolFailuresAsync() => await Assert.That((await ReadAsync(Producer, "/__test/observations")).GetProperty("failures").GetArrayLength()).IsEqualTo(0);
        private async Task ControlAsync(object command)
        { using var response = await Producer.PostAsJsonAsync("/__test/control", command); await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent); }
        private async Task<JsonElement> ReadAsync(HttpClient client, string path)
        { using var response = await client.GetAsync(path); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<JsonElement>()); }
        public Task InitializeAsync(CancellationToken ct) => inner.InitializeAsync(ct);
        public Task CheckReadyAsync(CancellationToken ct) => inner.CheckReadyAsync(ct);
        Task<TerraBaseDocumentPage> IDashboardState.ReadPageAsync(string collection, JsonElement? filter, int limit, string? cursor, CancellationToken ct) =>
            inner.ReadPageAsync(collection, filter, limit, cursor, ct);
        Task<TerraBaseTransactionResult> IDashboardState.TransactAsync(IReadOnlyList<DashboardStateOperation> operations, CancellationToken ct) =>
            inner.TransactAsync(operations, ct);
        public Task<TerraBaseDocument?> GetAsync(string collection, string id, CancellationToken ct) => inner.GetAsync(collection, id, ct);
        public Task<IReadOnlyList<TerraBaseDocument>> ListAsync(string collection, CancellationToken ct) => inner.ListAsync(collection, ct);
        public Task<TerraBaseDocumentPage> QueryPageAsync(string collection, TerraBaseQuery query, CancellationToken ct) => inner.QueryPageAsync(collection, query, ct);
        public Task DeleteAsync(string collection, string id, string version, CancellationToken ct) => inner.DeleteAsync(collection, id, version, ct);
        public async Task PutAsync(string collection, string id, JsonElement document, TerraBasePrecondition condition, CancellationToken ct)
        {
            await writes.WaitAsync(ct);
            try
            {
                if (Fault == "persist" && collection == AccountService.OrganizationsCollection && document.GetProperty("Projects").GetProperty(Project)
                    .GetProperty("Databases").TryGetProperty(Target, out _)) throw new IOException("Fixture metadata commit failed.");
                var winner = Volatile.Read(ref WinnerId);
                if (collection == AccountService.OrganizationsCollection && winner.Length > 0 && document.GetProperty("Projects").GetProperty(Project)
                    .GetProperty("Databases").TryGetProperty(winner, out _) && Interlocked.CompareExchange(ref WinnerId, "", winner) == winner)
                {
                    var changed = JsonNode.Parse(document.GetRawText())!;
                    var database = changed["Projects"]![Project]!["Databases"]![winner]!;
                    database["SpendCapCents"] = 71; database["Regions"] = new JsonArray("SJC", "FRA", "GRU");
                    await inner.PutAsync(collection, id, JsonSerializer.SerializeToElement(changed), condition, ct);
                }
                try { await inner.PutAsync(collection, id, document, condition, ct); }
                catch (TerraBasePreconditionFailedException) { Interlocked.Increment(ref Conflicts); throw; }
            }
            finally { writes.Release(); }
        }
        public async ValueTask DisposeAsync()
        { Release.TrySetResult(); Client.Dispose(); Producer.Dispose(); await Scenario.DisposeAsync(); }
    }
}

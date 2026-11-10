using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;

namespace TerraBase.Fast.Sync;

public sealed class HomeApplyTests
{
    private static readonly Faker Faker = new();
    private static readonly string[] Replicas = ["sjc", "fra"];

    private sealed record Sent(string Region, long At, object[] Changes, string State, string? Conflict = null, string? Detail = null);

    private sealed record Case(string Name, string[] Setup, Sent[] Transactions, string Check, string Expected);

    private static object Insert(string table, object values) => new { op = "insert", table, values };

    private static object Update(string table, object key, object values) => new { op = "update", table, key, values };

    private static object Delete(string table, object key) => new { op = "delete", table, key };

    private static IEnumerable<Case> SpikeCases()
    {
        yield return new Case("same row, different columns: both edits kept",
            ["CREATE TABLE profiles (id TEXT PRIMARY KEY, name TEXT, city TEXT)"],
            [
                new("sjc", 0, [Insert("profiles", new { id = "p1", name = "ana", city = "rio" })], "confirmed"),
                new("sjc", 10, [Update("profiles", new { id = "p1" }, new { name = "ana maria" })], "confirmed"),
                new("fra", 20, [Update("profiles", new { id = "p1" }, new { city = "porto" })], "confirmed"),
            ],
            "SELECT name, city FROM profiles", """[["ana maria","porto"]]""");
        yield return new Case("same column, stale region pushes later: the newer write stays, the stale one is recorded",
            ["CREATE TABLE settings (id TEXT PRIMARY KEY, theme TEXT)"],
            [
                new("sjc", 0, [Insert("settings", new { id = "s1", theme = "light" })], "confirmed"),
                new("fra", 30, [Update("settings", new { id = "s1" }, new { theme = "dark" })], "confirmed"),
                new("sjc", 20, [Update("settings", new { id = "s1" }, new { theme = "blue" })], "confirmed", "superseded",
                    """[["theme","\"dark\"","\"blue\"","fra","fra-1","sjc","sjc-1","confirmed","superseded",1,1]]"""),
            ],
            "SELECT theme FROM settings", """[["dark"]]""");
        yield return new Case("a large losing value is recorded as its hash and size",
            ["CREATE TABLE pages (id TEXT PRIMARY KEY, body TEXT)"],
            [
                new("sjc", 0, [Insert("pages", new { id = "g1", body = "draft" })], "confirmed"),
                new("fra", 30, [Update("pages", new { id = "g1" }, new { body = "final" })], "confirmed"),
                new("sjc", 20, [Update("pages", new { id = "g1" }, new { body = new string('x', 5000) })], "confirmed", "superseded",
                    """[["body","\"final\"",null,"fra","fra-1","sjc","sjc-1","confirmed","superseded",1,1]]"""),
            ],
            "SELECT body FROM pages", """[["final"]]""");
        yield return new Case("UNIQUE collision: the losing transaction applies nothing and is recorded",
            ["CREATE TABLE users (id TEXT PRIMARY KEY, email TEXT NOT NULL UNIQUE)", "CREATE TABLE events (id TEXT PRIMARY KEY, what TEXT)"],
            [
                new("sjc", 0, [Insert("users", new { id = "u1", email = "a@x.test" }), Insert("events", new { id = "e1", what = "signup" })], "confirmed"),
                new("fra", 5, [Insert("events", new { id = "e2", what = "signup" }), Insert("users", new { id = "u2", email = "a@x.test" })], "reverted", "unique"),
                new("fra", 6, [Insert("events", new { id = "e3", what = "after" })], "confirmed"),
            ],
            "SELECT (SELECT group_concat(id) FROM users), (SELECT group_concat(id, ',') FROM (SELECT id FROM events ORDER BY id))", """[["u1","e1,e3"]]""");
        yield return new Case("FK: a child of a parent deleted elsewhere is reverted, never orphaned",
            ["CREATE TABLE lists (id TEXT PRIMARY KEY, title TEXT)", "CREATE TABLE items (id TEXT PRIMARY KEY, list TEXT REFERENCES lists (id), body TEXT)"],
            [
                new("sjc", 0, [Insert("lists", new { id = "l1", title = "groceries" })], "confirmed"),
                new("sjc", 10, [Delete("lists", new { id = "l1" })], "confirmed"),
                new("fra", 5, [Insert("items", new { id = "i1", list = "l1", body = "milk" })], "reverted", "foreign_key"),
            ],
            "SELECT (SELECT count(*) FROM lists), (SELECT count(*) FROM items)", "[[0,0]]");
        yield return new Case("balance: two regions' captured values merge by HLC and the stale one is recorded",
            ["CREATE TABLE accounts (id TEXT PRIMARY KEY, balance INTEGER NOT NULL CHECK (balance >= 0))"],
            [
                new("sjc", 0, [Insert("accounts", new { id = "a1", balance = 100 })], "confirmed"),
                new("fra", 12, [Update("accounts", new { id = "a1" }, new { balance = 20 })], "confirmed"),
                new("sjc", 11, [Update("accounts", new { id = "a1" }, new { balance = 20 })], "confirmed", "superseded"),
                new("sjc", 13, [Update("accounts", new { id = "a1" }, new { balance = -60 })], "reverted", "check"),
            ],
            "SELECT balance FROM accounts", "[[20]]");
        yield return new Case("ALTER ADD COLUMN at the home while regions write: additive changes apply",
            ["CREATE TABLE notes (id TEXT PRIMARY KEY, body TEXT)", "ALTER TABLE notes ADD COLUMN pinned INTEGER"],
            [
                new("sjc", 0, [Insert("notes", new { id = "n1", body = "old shape" })], "confirmed"),
                new("fra", 1, [Insert("notes", new { id = "n2", body = "new shape", pinned = 1 })], "confirmed"),
            ],
            "SELECT id, pinned FROM notes ORDER BY id", """[["n1",null],["n2",1]]""");
        yield return new Case("colliding integer ids: the second insert is a visible primary-key conflict, not an overwrite",
            ["CREATE TABLE tickets (id INTEGER PRIMARY KEY AUTOINCREMENT, title TEXT)"],
            [
                new("sjc", 0, [Insert("tickets", new { id = 1, title = "from sjc" })], "confirmed"),
                new("fra", 1, [Insert("tickets", new { id = 1, title = "from fra" })], "reverted", "primary_key"),
            ],
            "SELECT id, title FROM tickets", """[[1,"from sjc"]]""");
        yield return new Case("a home write newer than a region change keeps its value",
            ["CREATE TABLE prefs (id TEXT PRIMARY KEY, lang TEXT)", "INSERT INTO prefs VALUES ('h1', 'pt')"],
            [new("sjc", -5_000, [Update("prefs", new { id = "h1" }, new { lang = "en" })], "confirmed", "superseded")],
            "SELECT lang FROM prefs", """[["pt"]]""");
        yield return new Case("delete tombstone: an older insert after the delete does not resurrect the row",
            ["CREATE TABLE tags (id TEXT PRIMARY KEY, label TEXT)"],
            [
                new("sjc", 0, [Insert("tags", new { id = "t1", label = "a" })], "confirmed"),
                new("sjc", 20, [Delete("tags", new { id = "t1" })], "confirmed"),
                new("fra", 10, [Update("tags", new { id = "t1" }, new { label = "b" })], "confirmed", "superseded"),
            ],
            "SELECT count(*) FROM tags", "[[0]]");
    }

    [Test]
    public async Task Every_spike_case_applies_atomically_with_no_silent_loss_and_no_stalled_stream()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var home = await HomeAsync();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        foreach (var spike in SpikeCases())
        {
            foreach (var sql in spike.Setup)
            {
                await home.SqlAsync(sql);
            }

            foreach (var sent in spike.Transactions)
            {
                var at = sent.At < 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + sent.At : start + sent.At;
                var (id, result) = await home.ApplyAsync(sent.Region, at, sent.Changes);
                await Assert.That(result.GetProperty("state").GetString()).IsEqualTo(sent.State).Because(spike.Name);
                var kinds = (await home.SqlAsync("SELECT kind, loser_state, loser_value FROM terrascale.conflicts WHERE loser_transaction_id = ?", id)).EnumerateArray().ToList();
                await Assert.That(kinds.Select(row => row[0].GetString()).ToList()).IsEquivalentTo(sent.Conflict is null ? [] : new List<string?> { sent.Conflict })
                    .Because(spike.Name);
                if (sent.Conflict is not null)
                {
                    await Assert.That(kinds[0][2].GetString()).IsNotNull().Because($"{spike.Name}: the losing values are kept");
                }

                if (sent.Detail is not null)
                {
                    var detail = await home.SqlAsync(
                        "SELECT column_name, winner_value, CASE WHEN json_extract(loser_value, '$.bytes') IS NULL THEN loser_value END, winner_region, winner_node, loser_region, loser_node, winner_state, loser_state, "
                        + "length(winner_transaction_id) = 26 AND winner_hlc IS NOT NULL AND loser_hlc IS NOT NULL, coalesce(json_extract(loser_value, '$.bytes') = 5002, 1) FROM terrascale.conflicts WHERE loser_transaction_id = ?", id);
                    await Assert.That(JsonSerializer.Serialize(detail)).IsEqualTo(JsonSerializer.Serialize(JsonElement.Parse(sent.Detail))).Because(spike.Name);
                }

                var again = await home.ApplyAsync(sent.Region, at, sent.Changes, id);
                await Assert.That(again.Result.GetProperty("state").GetString()).IsEqualTo(sent.State).Because($"{spike.Name}: sending again is idempotent");
            }

            await Assert.That((await home.SqlAsync(spike.Check)).GetRawText()).IsEqualTo(spike.Expected).Because(spike.Name);
            start += 1_000;
        }

        var shuffled = Enumerable.Range(0, 60).Select(i => (Region: Replicas[i % 2], At: start + i, Id: $"k{i:D3}")).OrderBy(_ => Faker.Random.Int()).ToList();
        await home.SqlAsync("CREATE TABLE partitioned (id TEXT PRIMARY KEY, v TEXT)");
        foreach (var (region, at, key) in shuffled)
        {
            var (_, result) = await home.ApplyAsync(region, at, [Insert("partitioned", new { id = key, v = region })]);
            await Assert.That(result.GetProperty("state").GetString()).IsEqualTo("confirmed");
        }

        await Assert.That((await home.SqlAsync("SELECT count(*) FROM partitioned")).GetRawText()).IsEqualTo("[[60]]");
        using var state = await home.Peer.GetAsync(home.ApplyPath + "/" + EntityIds.New(), token);
        await Assert.That((await state.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("state").GetString()).IsEqualTo("provisional");
        await Assert.That((await home.RefusedAsync("DELETE FROM terrascale.conflicts")).GetProperty("code").GetString()).IsEqualTo("sql_sandbox");
    }

    [Test]
    public async Task A_parent_delete_that_would_orphan_a_child_is_refused_and_no_cascade_runs()
    {
        await using var home = await HomeAsync();
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        await home.SqlAsync("CREATE TABLE folders (id TEXT PRIMARY KEY, name TEXT)");
        await home.SqlAsync("CREATE TABLE files (id TEXT PRIMARY KEY, folder TEXT NOT NULL REFERENCES folders (id) ON DELETE CASCADE, name TEXT)");
        await home.ApplyAsync("sjc", start, [Insert("folders", new { id = "f1", name = "docs" })]);
        await home.ApplyAsync("fra", start + 1, [Insert("files", new { id = "x1", folder = "f1", name = "a.txt" })]);

        var (id, result) = await home.ApplyAsync("sjc", start + 2, [Delete("folders", new { id = "f1" })]);

        await Assert.That(result.GetProperty("state").GetString()).IsEqualTo("reverted");
        await Assert.That(result.GetProperty("rows")[0].GetProperty("values").GetProperty("name").GetString()).IsEqualTo("docs");
        await Assert.That((await home.SqlAsync("SELECT kind, table_name FROM terrascale.conflicts WHERE loser_transaction_id = ?", id)).GetRawText())
            .IsEqualTo("""[["foreign_key","folders"]]""");
        await Assert.That((await home.SqlAsync("SELECT (SELECT count(*) FROM folders), (SELECT count(*) FROM files)")).GetRawText()).IsEqualTo("[[1,1]]");
    }

    [Test]
    public async Task An_applied_change_does_not_fire_triggers_again_while_home_writes_still_do()
    {
        await using var home = await HomeAsync();
        await home.SqlAsync("CREATE TABLE orders (id TEXT PRIMARY KEY, total INTEGER)");
        await home.SqlAsync("CREATE TABLE audit (id INTEGER PRIMARY KEY, order_id TEXT)");
        await home.SqlAsync("CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN INSERT INTO audit (order_id) VALUES (new.id); END");

        var (_, result) = await home.ApplyAsync("sjc", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000,
            [Insert("orders", new { id = "o1", total = 5 }), Insert("audit", new { id = 1, order_id = "o1" })]);
        await home.SqlAsync("INSERT INTO orders VALUES ('o2', 7)");

        await Assert.That(result.GetProperty("state").GetString()).IsEqualTo("confirmed");
        await Assert.That((await home.SqlAsync("SELECT order_id FROM audit ORDER BY id")).GetRawText()).IsEqualTo("""[["o1"],["o2"]]""");
        await Assert.That((await home.SqlAsync("SELECT sql FROM sqlite_schema WHERE name = 'orders_audit'")).GetRawText())
            .IsEqualTo("""[["CREATE TRIGGER orders_audit AFTER INSERT ON orders BEGIN INSERT INTO audit (order_id) VALUES (new.id); END"]]""");
    }

    [Test]
    public async Task A_transient_sqlite_error_during_apply_is_not_stored_as_a_revert()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        await using var home = await HomeAsync();
        await home.SqlAsync("CREATE TABLE gauges (id TEXT PRIMARY KEY, v INTEGER NOT NULL DEFAULT (abs(1, 2)))");
        var id = EntityIds.New().ToString();
        var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1_000;
        var hlc = new { physicalMilliseconds = at, logical = 0, nodeId = "sjc-1" };
        using var content = new StringContent(JsonSerializer.Serialize(new
        {
            id,
            region = "sjc",
            hlc,
            changes = new[] { Insert("gauges", new { id = "g1" }) },
        }), System.Text.Encoding.UTF8, "application/json");
        using var response = await home.Peer.PostAsync(home.ApplyPath, content, token);
        var body = await response.Content.ReadAsStringAsync(token);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(body);
        await Assert.That(JsonElement.Parse(body).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("unavailable");
        using var state = await home.Peer.GetAsync(home.ApplyPath + "/" + id, token);
        var recorded = await state.Content.ReadFromJsonAsync<JsonElement>(token);
        await Assert.That(recorded.GetProperty("state").GetString()).IsEqualTo("provisional");
        await Assert.That((await home.SqlAsync("SELECT count(*) FROM gauges")).GetRawText()).IsEqualTo("[[0]]");
        await home.SqlAsync("DROP TABLE gauges");
        await home.SqlAsync("CREATE TABLE gauges (id TEXT PRIMARY KEY, v INTEGER NOT NULL)");
        var (_, confirmed) = await home.ApplyAsync("sjc", at + 1, [Insert("gauges", new { id = "g1", v = 1 })]);
        await Assert.That(confirmed.GetProperty("state").GetString()).IsEqualTo("confirmed");
    }

    private static async Task<Home> HomeAsync()
    {
        var api = await ManagementApi.StartAsync(
            settings => settings["region"] = "gru",
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new NoRegions()));
                services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new NoReplicas()));
            });
        try
        {
            var organization = await api.CreateOrganizationAsync("Org " + EntityIds.New());
            var project = await api.CreateProjectAsync(organization, "Project");
            using var created = await api.ProjectClient(project).PostAsJsonAsync(
                ManagementApi.Uri($"/v1/projects/{project}/databases"), new { name = "main", replicaRegions = Replicas });
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync());
            var database = new ManagedDatabaseRef(organization, project, (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!, "main");
            var writer = await api.Server.IssueNarrowedAsync(organization, project, null, "sql:read", "sql:write");
            return new Home(api, database, api.DataClient(writer), PeerTls.CreateClient(api.Server.BaseAddress));
        }
        catch
        {
            await api.DisposeAsync();
            throw;
        }
    }

    private sealed class Home(ManagementApi api, ManagedDatabaseRef database, HttpClient writer, HttpClient peer) : IAsyncDisposable
    {
        public HttpClient Peer => peer;

        public string ApplyPath => $"/v1/sync/organizations/{database.Organization}/projects/{database.Project}/databases/{database.Id}/apply";

        public async Task<(string Id, JsonElement Result)> ApplyAsync(string region, long milliseconds, object[] changes, string? id = null)
        {
            id ??= EntityIds.New().ToString();
            var hlc = new { physicalMilliseconds = milliseconds, logical = 0, nodeId = region + "-1" };
            using var content = new StringContent(JsonSerializer.Serialize(new { id, region, hlc, changes }), System.Text.Encoding.UTF8, "application/json");
            using var response = await peer.PostAsync(ApplyPath, content);
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(body);
            return (id, JsonElement.Parse(body));
        }

        public async Task<JsonElement> SqlAsync(string sql, params object[] args)
        {
            using var response = await writer.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new { statements = new[] { new { sql, args } } });
            var body = await response.Content.ReadAsStringAsync();
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because($"{sql}: {body}");
            return JsonElement.Parse(body).GetProperty("results")[0].GetProperty("rows");
        }

        public async Task<JsonElement> RefusedAsync(string sql)
        {
            using var response = await writer.PostAsJsonAsync(ManagementApi.Uri(database.Data + "/sql/execute"), new { statements = new[] { new { sql } } });
            return JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("error");
        }

        public async ValueTask DisposeAsync()
        {
            writer.Dispose();
            peer.Dispose();
            await api.DisposeAsync();
        }
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc", "fra"]);
    }

    private sealed class NoRegions : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<RegionPeer>>([]);
    }

    private sealed class NoReplicas : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([]);
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraBase.Configuration;
using TerraBase.Features.Clock;
using TerraBase.Features.HomePush;
using TerraBase.Features.Regions;
using TerraBase.Features.Replicas;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Sync;

/// <summary>
/// PIVOT R13: one client runs the same random transactions, in order, against two databases homed in gru. Database
/// <c>local</c> takes them in sjc over SQL REST with <c>TerraScale-Durability: home</c>, so the router (R4) commits the
/// writes it routes local in sjc and pushes them to gru (R7, applied by R6) and forwards the rest (R5); database
/// <c>home</c> takes them at gru. Both must end in the same rows, or every differing row is in <c>terrascale.conflicts</c>.
/// </summary>
public sealed class RouterDifferentialTests
{
    private const int Transactions = 10_000;
    private const int Keys = 48;
    private static readonly string[] SjcRegion = ["sjc"];
    private static readonly string[] Tables = ["items", "parents", "children", "counters", "users"];

    private const string Schema = """
        CREATE TABLE items (id TEXT PRIMARY KEY, a TEXT, b INTEGER);
        CREATE TABLE parents (id TEXT PRIMARY KEY, name TEXT);
        CREATE TABLE children (id TEXT PRIMARY KEY, parent TEXT REFERENCES parents(id), v INTEGER);
        CREATE TABLE counters (id TEXT PRIMARY KEY, n INTEGER NOT NULL);
        CREATE TABLE users (id TEXT PRIMARY KEY, email TEXT UNIQUE, name TEXT)
        """;

    [Test]
    public async Task Ten_thousand_random_transactions_end_in_the_same_rows_in_local_and_home_only_mode_or_show_every_difference_as_a_conflict()
    {
        var token = TestContext.Current!.Execution.CancellationToken;
        var seed = Random.Shared.Next();
        var workload = Workload.Generate(seed, Transactions);
        await using var nats = await JetStreamServer.StartAsync(token);
        await using var cluster = await Cluster.StartAsync(nats, token);
        var local = await cluster.CreateDatabaseAsync("local", token);
        var home = await cluster.CreateDatabaseAsync("home", token);

        var clock = Stopwatch.StartNew();
        var runs = await Task.WhenAll(
            Task.Run(() => cluster.RunAsync(local, workload, homeOnly: false, token), token),
            Task.Run(() => cluster.RunAsync(home, workload, homeOnly: true, token), token));
        var elapsed = clock.Elapsed;
        await cluster.SettleAsync(token);

        var localHome = await cluster.RowsAsync(local, atHome: true, token);
        var homeHome = await cluster.RowsAsync(home, atHome: true, token);
        var localRegion = await cluster.RowsAsync(local, atHome: false, token);
        var conflicts = await cluster.ConflictsAsync(local, token);

        var differences = localHome.Keys.Union(homeHome.Keys).Where(key => localHome.GetValueOrDefault(key) != homeHome.GetValueOrDefault(key)).Order().ToList();
        var silent = differences.Where(key => !conflicts.Any(conflict => conflict.Names(key))).ToList();
        var regionDrift = localHome.Keys.Union(localRegion.Keys).Where(key => localHome.GetValueOrDefault(key) != localRegion.GetValueOrDefault(key)).Order().ToList();

        var mode = runs[0];
        Console.WriteLine(
            $"R13 seed {seed}: {Transactions} transactions in {elapsed.TotalSeconds:F1} s | local {mode.Local} committed in sjc, {mode.Forwarded} forwarded, " +
            $"{mode.Failed} failed; home-only {runs[1].Failed} failed | rows {localHome.Count}/{homeHome.Count} | conflicts {conflicts.Count} " +
            $"({string.Join(", ", conflicts.GroupBy(conflict => conflict.Kind).OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key} {group.Count()}"))}) | " +
            $"differences {differences.Count}, silent {silent.Count}, region drift {regionDrift.Count}");
        foreach (var key in silent.Take(20))
        {
            Console.WriteLine($"silent {key}: local={localHome.GetValueOrDefault(key) ?? "absent"} home-only={homeHome.GetValueOrDefault(key) ?? "absent"} | {workload.Touching(key)}");
        }

        foreach (var key in regionDrift.Take(20))
        {
            Console.WriteLine($"region drift {key}: gru={localHome.GetValueOrDefault(key) ?? "absent"} sjc={localRegion.GetValueOrDefault(key) ?? "absent"}");
        }

        await Assert.That(mode.Local).IsGreaterThan(Transactions / 4).Because("the workload exercises the local path");
        await Assert.That(silent).IsEmpty();
        await Assert.That(regionDrift).IsEmpty();
    }

    private sealed record Conflict(string Kind, string? Table, string? Key, string? Loser)
    {
        // A difference is visible when a record names its row, or when a reverted transaction's changes carry its key.
        public bool Names(string row)
        {
            var (table, key) = (row[..row.IndexOf(':', StringComparison.Ordinal)], row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..]);
            return (Table == table && Key?.Contains($"\"{key}\"", StringComparison.Ordinal) == true)
                || (Table is null && Loser?.Contains($"\"{key}\"", StringComparison.Ordinal) == true);
        }
    }

    private sealed record Stats(int Local, int Forwarded, int Failed);

    private sealed class Workload(List<string[]> transactions)
    {
        public List<string[]> Transactions => transactions;

        public static Workload Generate(int seed, int count)
        {
            var random = new Randomizer(seed);
            string Key(string prefix) => prefix + random.Number(Keys - 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            string Word() => random.AlphaNumeric(random.Number(1, 8));
            string Local() => random.Number(10) switch
            {
                0 or 1 => $"INSERT INTO items (id, a, b) VALUES ('{Key("i")}', '{Word()}', {random.Number(100)})",
                2 => $"UPDATE items SET a = '{Word()}', b = {random.Number(100)} WHERE id = '{Key("i")}'",
                3 => $"DELETE FROM items WHERE id = '{Key("i")}'",
                4 => $"INSERT INTO parents (id, name) VALUES ('{Key("p")}', '{Word()}')",
                5 => $"INSERT INTO children (id, parent, v) VALUES ('{Key("c")}', '{Key("p")}', {random.Number(100)})",
                6 => random.Bool() ? $"UPDATE children SET v = {random.Number(100)} WHERE id = '{Key("c")}'" : $"DELETE FROM children WHERE id = '{Key("c")}'",
                7 => $"UPDATE users SET name = '{Word()}' WHERE id = '{Key("u")}'",
                8 => $"INSERT INTO counters (id, n) VALUES ('{Key("n")}', {random.Number(100)})",
                _ => random.Bool() ? $"UPDATE parents SET name = '{Word()}' WHERE id = '{Key("p")}'" : $"DELETE FROM users WHERE id = '{Key("u")}'",
            };
            string Home() => random.Number(6) switch
            {
                0 or 1 => $"UPDATE counters SET n = n + {random.Number(1, 9)} WHERE id = '{Key("n")}'",
                2 => $"INSERT INTO users (id, email, name) VALUES ('{Key("u")}', '{Key("e")}@example.com', '{Word()}')",
                3 => $"DELETE FROM parents WHERE id = '{Key("p")}'",
                4 => random.Bool() ? $"UPDATE items SET b = 0 WHERE b > 90" : $"DELETE FROM items WHERE b < 3",
                5 => $"UPDATE children SET parent = '{Key("p")}' WHERE id = '{Key("c")}'",
                _ => $"UPDATE users SET email = '{Key("e")}@example.com' WHERE id = '{Key("u")}'",
            };
            var list = new List<string[]>(count);
            for (var i = 0; i < count; i++)
            {
                var size = random.Number(1, 3);
                var mixed = random.Number(9) < 3;
                list.Add([.. Enumerable.Range(0, size).Select(index => mixed && index == size - 1 ? Home() : Local())]);
            }

            return new Workload(list);
        }

        public string Touching(string row)
        {
            var key = $"'{row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..]}'";
            return string.Join(" ; ", transactions.Select((sql, index) => (sql, index)).Where(entry => entry.sql.Any(text => text.Contains(key, StringComparison.Ordinal)))
                .TakeLast(6).Select(entry => $"#{entry.index} {string.Join(" / ", entry.sql)}"));
        }
    }

    private sealed class Cluster : IAsyncDisposable
    {
        private readonly TestIssuer _issuer;
        private readonly ManagementApi _gru;
        private readonly ManagementApi _sjc;
        private readonly HttpClient _viaGru;
        private readonly HttpClient _viaSjc;
        private readonly string _organization;
        private readonly string _project;

        private Cluster(TestIssuer issuer, ManagementApi gru, ManagementApi sjc, string organization, string project, string bearer)
        {
            (_issuer, _gru, _sjc, _organization, _project) = (issuer, gru, sjc, organization, project);
            _viaGru = gru.WithToken(bearer);
            _viaSjc = sjc.WithToken(bearer);
        }

        public static async Task<Cluster> StartAsync(JetStreamServer nats, CancellationToken token)
        {
            var issuer = new TestIssuer();
            var regions = new ConcurrentDictionary<string, Uri>(StringComparer.Ordinal);
            var gru = await StartAsync("gru", issuer, regions, nats);
            regions["gru"] = gru.Server.BaseAddress;
            var sjc = await StartAsync("sjc", issuer, regions, nats);
            regions["sjc"] = sjc.Server.BaseAddress;
            var organization = await gru.CreateOrganizationAsync("Org " + EntityIds.New());
            // Home-only mode writes at gru as fast as it can; the Enterprise plan has no request rate budget there.
            await gru.SetPlanAsync(gru.User, organization, "enterprise", token);
            var project = await gru.CreateProjectAsync(organization, "Project");
            var bearer = issuer.Mint(gru.User, descriptor =>
            {
                descriptor.Claims["project_id"] = project;
                descriptor.Claims["scope"] = "databases:read sql:read sql:write";
            });
            var push = sjc.Server.Services.GetRequiredService<HomePush>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(push.Started && gru.Server.Services.GetRequiredService<HomePush>().Started), TimeSpan.FromSeconds(30));
            return new Cluster(issuer, gru, sjc, organization, project, bearer);
        }

        public async Task<ManagedDatabaseRef> CreateDatabaseAsync(string name, CancellationToken token)
        {
            using var created = await _gru.Client.PostAsJsonAsync(
                ManagementApi.Uri($"/v1/management/organizations/{_organization}/projects/{_project}/databases"), new { name, replicaRegions = SjcRegion }, token);
            await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await created.Content.ReadAsStringAsync(token));
            var reference = new ManagedDatabaseRef(_organization, _project, (await created.Content.ReadFromJsonAsync<JsonElement>(token)).GetProperty("id").GetString()!, name);
            var (status, body, _) = await ExecuteAsync(_viaGru, reference, [.. Schema.Split(';').Select(sql => sql.Trim())], token);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
            var replication = _sjc.Server.Services.GetRequiredService<RegionReplication>();
            await replication.SyncCatalogsAsync(token);
            await replication.PullAllAsync(token);
            return reference;
        }

        public async Task<Stats> RunAsync(ManagedDatabaseRef database, Workload workload, bool homeOnly, CancellationToken token)
        {
            var (local, forwarded, failed) = (0, 0, 0);
            foreach (var sql in workload.Transactions)
            {
                var (status, body, route) = await ExecuteAsync(homeOnly ? _viaGru : _viaSjc, database, sql, token, homeDurable: !homeOnly);
                if (status == HttpStatusCode.BadRequest)
                {
                    failed++;
                    continue;
                }

                await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(string.Join(" / ", sql) + ": " + body);
                if (route?.EndsWith("commit=local", StringComparison.Ordinal) == true)
                {
                    local++;
                }
                else
                {
                    forwarded++;
                }
            }

            return new Stats(local, forwarded, failed);
        }

        public async Task SettleAsync(CancellationToken token)
        {
            var push = _sjc.Server.Services.GetRequiredService<HomePush>();
            await TerraBaseTestServer.EventuallyAsync(_ => Task.FromResult(push.Overlay.Count == 0), TimeSpan.FromSeconds(120));
            await _sjc.Server.Services.GetRequiredService<RegionReplication>().PullAllAsync(token);
        }

        public async Task<Dictionary<string, string>> RowsAsync(ManagedDatabaseRef database, bool atHome, CancellationToken token)
        {
            var rows = new Dictionary<string, string>(StringComparer.Ordinal);
            var (status, body, _) = await ExecuteAsync(atHome ? _viaGru : _viaSjc, database, [.. Tables.Select(table => $"SELECT * FROM {table} ORDER BY id")], token);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
            var results = JsonElement.Parse(body).GetProperty("results");
            for (var i = 0; i < Tables.Length; i++)
            {
                foreach (var row in results[i].GetProperty("rows").EnumerateArray())
                {
                    rows[$"{Tables[i]}:{row[0].GetString()}"] = row.GetRawText();
                }
            }

            return rows;
        }

        public async Task<List<Conflict>> ConflictsAsync(ManagedDatabaseRef database, CancellationToken token)
        {
            var (status, body, _) = await ExecuteAsync(_viaGru, database, ["SELECT kind, table_name, row_key, loser_value FROM terrascale.conflicts"], token);
            await Assert.That(status).IsEqualTo(HttpStatusCode.OK).Because(body);
            return [.. JsonElement.Parse(body).GetProperty("results")[0].GetProperty("rows").EnumerateArray()
                .Select(row => new Conflict(row[0].GetString()!, row[1].GetString(), row[2].GetString(), row[3].GetString()))];
        }

        private static async Task<(HttpStatusCode Status, string Body, string? Route)> ExecuteAsync(
            HttpClient client, ManagedDatabaseRef database, string[] sql, CancellationToken token, bool homeDurable = false)
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, ManagementApi.Uri(database.Data + "/sql/execute"))
                {
                    Content = JsonContent.Create(new { statements = sql.Select(text => new { sql = text }).ToArray() }),
                };
                if (homeDurable)
                {
                    request.Headers.Add("TerraScale-Durability", "home");
                }

                using var response = await client.SendAsync(request, token);
                var body = await response.Content.ReadAsStringAsync(token);
                // sjc admits the organization as Hobby (it holds no plan for it): honor its request budget like a client.
                if (response.StatusCode == HttpStatusCode.TooManyRequests && body.Contains("tenant_busy", StringComparison.Ordinal) && attempt < 100)
                {
                    await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1), token);
                    continue;
                }

                return (response.StatusCode, body, response.Headers.TryGetValues("TerraScale-Write-Route", out var route) ? route.Single() : null);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _viaGru.Dispose();
            _viaSjc.Dispose();
            await _sjc.DisposeAsync();
            await _gru.DisposeAsync();
            _issuer.Dispose();
        }

        private static Task<ManagementApi> StartAsync(string region, TestIssuer issuer, ConcurrentDictionary<string, Uri> regions, JetStreamServer nats) => ManagementApi.StartAsync(
            settings => settings["region"] = region,
            services =>
            {
                var registered = services.Single(service => service.ServiceType == typeof(TerraBaseOptions));
                services[services.IndexOf(registered)] = ServiceDescriptor.Singleton(((TerraBaseOptions)registered.ImplementationInstance!) with
                {
                    NatsUrl = nats.Url,
                    NatsToken = nats.Token,
                });
                services.Replace(ServiceDescriptor.Singleton<IRegionPeers>(new RegionView(region, regions)));
                services.Replace(ServiceDescriptor.Singleton<IRegionCatalog>(new Serving()));
                services.Replace(ServiceDescriptor.Singleton<IReplicaPeers>(new ReplicaView(region, regions)));
                services.Replace(ServiceDescriptor.Singleton<ILocalWriteClock>(new Admitted()));
            },
            sharedIssuer: issuer);
    }

    private sealed class Admitted : ILocalWriteClock
    {
        public bool Allowed => true;

        public double SkewMilliseconds => 0;
    }

    private sealed class Serving : IRegionCatalog
    {
        public ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<string>>(["gru", "sjc"]);
    }

    private sealed class RegionView(string region, ConcurrentDictionary<string, Uri> regions) : IRegionPeers
    {
        public ValueTask<IReadOnlyList<RegionPeer>> OtherRegionsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<RegionPeer>>([.. regions.Where(peer => peer.Key != region).Select(peer => new RegionPeer(peer.Key, peer.Value))]);
    }

    private sealed class ReplicaView(string region, ConcurrentDictionary<string, Uri> regions) : IReplicaPeers
    {
        public ValueTask<IReadOnlyList<ReplicaPeer>> ListAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<ReplicaPeer>>([.. regions.Where(peer => peer.Key != region).Select(peer => new ReplicaPeer(peer.Key, peer.Key + "-1", peer.Value))]);
    }
}

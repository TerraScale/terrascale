using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Bogus;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>TS-1333: small real-listener placement and stream-recovery probes; never stops production nodes.</summary>
[Category("local-only")]
public sealed class SqlHranaRoutingE2ETests
{
    private static readonly string[] Selected = [Cluster.Gru, Cluster.Fra];

    [Test]
    [Arguments("sql")]
    [Arguments("hrana")]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task Tiny_counter_and_typed_controls_converge_in_selected_regions_and_pins_fail_closed(string endpoint)
    {
        await Gaps.RunAsync("sql-hrana-placement", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
            using var gru = Direct(cluster, Cluster.Gru);
            using var fra = Direct(cluster, Cluster.Fra);
            using var excluded = Direct(cluster, Cluster.Sjc);
            var path = await CreateSelectedAsync(cluster, gru);
            var id = EntityIds.New().ToString();
            var label = new Faker().Random.AlphaNumeric(12);
            await WriteAsync(gru, path, endpoint,
                "CREATE TABLE controls (id TEXT PRIMARY KEY, n INTEGER NOT NULL, nullable INTEGER, r REAL, p INTEGER NOT NULL, big INTEGER, label TEXT)");
            await WriteAsync(gru, path, endpoint, "INSERT INTO controls VALUES (?, 0, NULL, 0.1, 0, ?, ?)", [id, 9007199254740993L, label]);
            const string read = "SELECT n, nullable, r, p, big, label, typeof(n), typeof(r) FROM controls";
            var seed = JsonSerializer.Serialize(new object?[][] { [0, null, 0.1, 0, 9007199254740993L, label, "integer", "real"] });
            await RowsEqualAsync(fra, path, read, seed);

            // A literal increment on INTEGER NOT NULL adds; the parameter, nullable and REAL controls compute values.
            cluster.Partition(Cluster.Gru, Cluster.Fra);
            const string update = "UPDATE controls SET n = n + 1, nullable = nullable + 1, r = r + 1, p = p + ? WHERE id = ?";
            await Task.WhenAll(WriteAsync(gru, path, endpoint, update, [1, id]), WriteAsync(fra, path, endpoint, update, [1, id]));
            var local = JsonSerializer.Serialize(new object?[][] { [1, null, 1.1, 1, 9007199254740993L, label, "integer", "real"] });
            await RowsEqualAsync(gru, path, read, local);
            await RowsEqualAsync(fra, path, read, local);
            cluster.Heal();
            var merged = JsonSerializer.Serialize(new object?[][] { [2, null, 1.1, 1, 9007199254740993L, label, "integer", "real"] });
            foreach (var client in new[] { gru, fra })
            {
                await RowsEqualAsync(client, path, read, merged);
            }

            _ = await Converge.UntilAsync(async () =>
            {
                using var response = await SendAsync(excluded, path, "sql", "SELECT 1");
                return response.StatusCode;
            }, status => status == HttpStatusCode.MisdirectedRequest, "excluded placement catalog");
            const string refusedWrite = "UPDATE controls SET n = n + 100";
            using (var refusal = await SendAsync(excluded, path, endpoint, refusedWrite))
            {
                await IdentityAsync(refusal, Cluster.Sjc);
                await Assert.That(refusal.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
                var error = (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
                await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("wrong_region");
                await Assert.That(refusal.Headers.GetValues("X-TerraBase-Database-Regions").Single()).IsEqualTo("gru,fra");
            }

            // Direct mismatches refuse before execution. The neutral edge may replay only to the included pin.
            foreach (var pin in new[] { Cluster.Gru, Cluster.Sjc })
            {
                using var refusal = await SendAsync(fra, path, endpoint, refusedWrite, pin: pin);
                await IdentityAsync(refusal, Cluster.Fra);
                await Assert.That(refusal.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
            }

            using var edge = cluster.Sql(Cluster.Sjc);
            using (var replayed = await SendAsync(edge, path, endpoint, "SELECT n FROM controls", pin: Cluster.Fra))
            {
                replayed.EnsureSuccessStatusCode();
                await IdentityAsync(replayed, Cluster.Fra);
            }

            foreach (var client in new[] { gru, fra })
            {
                await RowsEqualAsync(client, path, read, merged);
            }
        }, local: true);
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    public async Task A_long_lived_hrana_stream_recovers_typed_after_controlled_ingress_and_issuer_failover()
    {
        await Gaps.RunAsync("hrana-controlled-failover", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
            using var home = cluster.Sql(Cluster.Gru);
            using var issuer = cluster.Sql(Cluster.Fra);
            using var excluded = cluster.Sql(Cluster.Sjc);
            var path = await CreateSelectedAsync(cluster, home);
            await WriteAsync(home, path, "sql", "CREATE TABLE markers (id TEXT PRIMARY KEY, seq INTEGER NOT NULL)");
            using (var direct = Direct(cluster, Cluster.Fra))
            {
                await RowsEqualAsync(direct, path, "SELECT id FROM markers", "[]");
            }

            var acknowledged = new List<string>();
            string? baton = null;
            string? issuerMachine = null;
            for (var i = 0; i < 5; i++)
            {
                var id = EntityIds.New().ToString();
                var result = await MarkerAsync(issuer, path, baton, id, i, Cluster.Fra);
                issuerMachine ??= result.Machine;
                await Assert.That(result.Machine).IsEqualTo(issuerMachine);
                await Assert.That(result.Baton).IsNotEqualTo(baton);
                baton = result.Baton;
                acknowledged.Add(id);
            }

            var stateless = baton!;
            await Assert.That(stateless).EndsWith(".a");
            // Stored SQL makes the stream stateful without holding a write lock across the failover probe.
            using var stored = await PipelineAsync(issuer, path, baton,
                [new { type = "store_sql", sql_id = 1, sql = "SELECT 1" }]);
            var state = await AcceptedAsync(stored, Cluster.Fra);
            var stateful = state.GetProperty("baton").GetString()!;
            await Assert.That(stateful).EndsWith(".t");
            // An excluded ingress replays to the home, not the replica that issued this baton. Safe failure is typed.
            using (var moved = await PipelineAsync(excluded, path, stateful, [new { type = "get_autocommit" }]))
            {
                await ExpiredAsync(moved, Cluster.Gru);
            }

            await cluster.StopAsync(Cluster.Fra);
            using (var lost = await PipelineAsync(home, path, stateful, [new { type = "get_autocommit" }]))
            {
                await ExpiredAsync(lost, Cluster.Gru);
            }

            // The next global request now lands on the surviving home. A stateless baton can open a fresh stream.
            using (var resumed = await PipelineAsync(home, path, stateless, [new { type = "get_autocommit" }]))
            {
                var fresh = await AcceptedAsync(resumed, Cluster.Gru);
                baton = fresh.GetProperty("baton").GetString()!;
                await Assert.That(baton).IsNotEqualTo(stateless);
            }

            for (var i = 5; i < 8; i++)
            {
                var id = EntityIds.New().ToString();
                baton = (await MarkerAsync(home, path, baton, id, i, Cluster.Gru)).Baton;
                acknowledged.Add(id);
            }

            using (var closed = await PipelineAsync(home, path, baton, [new { type = "close" }]))
            {
                _ = await AcceptedAsync(closed, Cluster.Gru);
            }

            // HTTP/Hrana acknowledge local durability: recovery includes bringing back the issuer's durable copy.
            await cluster.StartAgainAsync(Cluster.Fra);
            var expected = JsonSerializer.Serialize(acknowledged.Select(id => new[] { id }).OrderBy(row => row[0], StringComparer.Ordinal));
            foreach (var region in Selected)
            {
                using var direct = Direct(cluster, region);
                await RowsEqualAsync(direct, path, "SELECT id FROM markers ORDER BY id", expected);
                await RowsEqualAsync(direct, path, "SELECT count(*), count(DISTINCT id) FROM markers", "[[8,8]]");
                using var identity = await SendAsync(direct, path, "sql", "SELECT 1");
                var machine = await IdentityAsync(identity, region);
                if (region == Cluster.Fra)
                {
                    await Assert.That(machine).IsEqualTo(issuerMachine);
                }
            }
        }, local: true);
    }

    private static HttpClient Direct(Cluster cluster, string region)
    {
        var endpoint = cluster.Endpoint(region);
        var client = new HttpClient { BaseAddress = endpoint.Address };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
        return client;
    }

    private static async Task<string> CreateSelectedAsync(Cluster cluster, HttpClient home)
    {
        using var created = await home.PostAsJsonAsync(new Uri(cluster.ProjectPath + "/databases", UriKind.Relative),
            new { name = "selected", homeRegion = Cluster.Gru, replicaRegions = new[] { Cluster.Fra } });
        created.EnsureSuccessStatusCode();
        await IdentityAsync(created, Cluster.Gru);
        return cluster.ProjectPath + "/databases/selected";
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string path, string endpoint, string sql, object?[]? args = null, string? pin = null)
    {
        if (endpoint == "hrana")
        {
            return PipelineAsync(client, path, null, [new { type = "execute", stmt = new { sql, args = (args ?? []).Select(Argument), want_rows = true } }, new { type = "close" }], pin);
        }

        return PostAsync(client, path + "/sql/execute", new { statements = new[] { new { sql, args = args ?? [] } } }, pin);
    }

    private static object Argument(object? value) => value switch
    {
        null => new { type = "null" },
        string text => new { type = "text", value = text },
        _ => new { type = "integer", value = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) },
    };

    private static Task<HttpResponseMessage> PipelineAsync(HttpClient client, string path, string? baton, object[] requests, string? pin = null) =>
        PostAsync(client, path + "/libsql/v2/pipeline", new { baton, requests }, pin);

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body, string? pin)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative)) { Content = JsonContent.Create(body) };
        if (pin is not null)
        {
            request.Headers.Add("X-TerraScale-Region-Pin", pin);
        }

        return await client.SendAsync(request);
    }

    private static async Task WriteAsync(HttpClient client, string path, string endpoint, string sql, object?[]? args = null)
    {
        using var response = await SendAsync(client, path, endpoint, sql, args);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (endpoint == "hrana")
        {
            await Assert.That(body.GetProperty("results").EnumerateArray().All(result => result.GetProperty("type").GetString() == "ok")).IsTrue().Because(body.GetRawText());
        }
    }

    private static Task RowsEqualAsync(HttpClient client, string path, string sql, string expected) => Converge.UntilAsync(async () =>
    {
        using var response = await SendAsync(client, path, "sql", sql);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests or HttpStatusCode.NotFound
            || response.StatusCode == HttpStatusCode.BadRequest && text.Contains("no such table", StringComparison.Ordinal))
        {
            return text;
        }

        response.EnsureSuccessStatusCode();
        return JsonElement.Parse(text).GetProperty("results")[0].GetProperty("rows").GetRawText();
    }, rows => rows == expected, "TS-1333 rows: " + expected);

    private static async Task<(string Baton, string Machine)> MarkerAsync(HttpClient client, string path, string? baton, string id, int sequence, string region)
    {
        using var response = await PipelineAsync(client, path, baton,
            [new { type = "execute", stmt = new { sql = "INSERT INTO markers VALUES (?, ?)", args = new[] { Argument(id), Argument(sequence) } } }]);
        var body = await AcceptedAsync(response, region);
        await Assert.That(body.GetProperty("results")[0].GetProperty("response").GetProperty("result").GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
        return (body.GetProperty("baton").GetString()!, response.Headers.GetValues("X-TerraBase-Machine").Single());
    }

    private static async Task<JsonElement> AcceptedAsync(HttpResponseMessage response, string region)
    {
        response.EnsureSuccessStatusCode();
        await IdentityAsync(response, region);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(body.GetProperty("results").EnumerateArray().All(result => result.GetProperty("type").GetString() == "ok")).IsTrue().Because(body.GetRawText());
        return body;
    }

    private static async Task ExpiredAsync(HttpResponseMessage response, string region)
    {
        await IdentityAsync(response, region);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        await Assert.That(error.GetProperty("code").GetString()).IsEqualTo("STREAM_EXPIRED");
    }

    private static async Task<string> IdentityAsync(HttpResponseMessage response, string region)
    {
        var answered = response.Headers.GetValues("X-TerraBase-Region").Single();
        var machine = response.Headers.GetValues("X-TerraBase-Machine").Single();
        await Assert.That(answered).IsEqualTo(region);
        await Assert.That(machine.Length > 0).IsTrue();
        Console.WriteLine($"TS-1333 answer: region={answered} machine={machine} status={(int)response.StatusCode}");
        return machine;
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

[Category("local-only")]
public sealed class EndpointLwwE2ETests
{
    private static readonly string[] Endpoints = ["documents", "sql", "postgres", "hrana", "wire"];

    [Test]
    [Scenario(Nodes.InProcess, Faults.ClockSkew | Faults.SimulatedPartition)]
    [Arguments("documents", "postgres")]
    [Arguments("sql", "wire")]
    [Arguments("postgres", "hrana")]
    [Arguments("hrana", "documents")]
    [Arguments("wire", "sql")]
    public async Task Concurrent_endpoint_writes_choose_the_skewed_winner_then_a_causal_overwrite(string first, string second)
    {
        await Gaps.RunAsync("endpoint-lww", async () =>
        {
            await using var cluster = await Cluster.StartLocalAsync([Cluster.Gru, Cluster.Sjc, Cluster.Fra],
                new Dictionary<string, TimeSpan> { [Cluster.Sjc] = TimeSpan.FromSeconds(5) });
            var clients = new List<Client>();
            try
            {
                foreach (var region in cluster.Regions)
                {
                    clients.Add(await Client.OpenAsync(cluster, region));
                }

                await clients[0].SqlAsync("sql", "CREATE TABLE rows (id TEXT PRIMARY KEY, value TEXT, gru_write INTEGER, sjc_write INTEGER)");
                var id = EntityIds.New().ToString();
                var uri = clients[0].Document(id);
                await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "seed" });
                await EverywhereAsync(clients, id, "seed");
                foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
                {
                    _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(cluster[region], new Uri("/health/replication", UriKind.Relative)),
                        health => health?["peers"]?.AsArray().Any(peer => peer?["region"]?.GetValue<string>() == Cluster.Fra
                            && peer["status"]?.GetValue<string>() == "ok") == true, $"{region} streams to fra");
                }

                cluster.Partition(Cluster.Gru, Cluster.Sjc);
                await Task.WhenAll(clients[0].WriteAsync(first, id, "gru"), clients[1].WriteAsync(second, id, "sjc"));
                foreach (var client in clients.Take(2))
                {
                    var own = await Docs.ReadAsync(cluster[client.Region], uri);
                    var other = client.Region == Cluster.Gru ? Cluster.Sjc : Cluster.Gru;
                    Expected.Check(own?["value"]?.GetValue<string>() == client.Region
                        && own[client.Region + "_write"]?.GetValue<int>() == 1 && own[other + "_write"] is null,
                        $"{client.Region} has only its own write before heal: {own}");
                }

                cluster.Heal();
                await EverywhereAsync(clients, id, "sjc");
                await MarkersEverywhereAsync(cluster, uri, gru: second != "wire", sjc: true);
                // gru has observed the winner: its causally later write must win despite the slower clock.
                await clients[0].WriteAsync(first, id, "causal");
                await EverywhereAsync(clients, id, "causal");
                await MarkersEverywhereAsync(cluster, uri, gru: true, sjc: first != "wire");
            }
            finally
            {
                foreach (var client in clients)
                {
                    await client.DisposeAsync();
                }
            }
        }, local: true);
    }

    private static Task<bool[]> EverywhereAsync(List<Client> clients, string id, string expected) =>
        Converge.UntilAsync(() => Task.WhenAll(clients.SelectMany(client => Endpoints.Select(endpoint => client.ReadAsync(endpoint, id, expected)))),
            matches => matches.All(match => match), $"every endpoint in all regions reads {expected}");

    private static Task<JsonNode?[]> MarkersEverywhereAsync(Cluster cluster, Uri uri, bool gru, bool sjc) =>
        Converge.UntilAsync(() => Task.WhenAll(cluster.Regions.Select(region => Docs.ReadAsync(cluster[region], uri))),
            copies => copies.All(copy =>
            {
                var gruWritten = copy?["gru_write"]?.GetValue<int>() == 1;
                var sjcWritten = copy?["sjc_write"]?.GetValue<int>() == 1;
                return copy is not null && gruWritten == gru && sjcWritten == sjc;
            }), "Wire replacements clear older markers; field updates retain them");

    private sealed class Client(Cluster cluster, string region, HttpClient http, NpgsqlDataSource postgres, WireClient wire) : IAsyncDisposable
    {
        public string Region => region;

        public Uri Document(string id) => new($"{cluster.DatabasePath}/collections/rows/documents/by-key?id={id}", UriKind.Relative);

        public static async Task<Client> OpenAsync(Cluster cluster, string region)
        {
            var (postgresPort, wirePort, key) = await cluster.SocketsAsync(region);
            var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = postgresPort,
                Username = "wire",
                Password = key,
                Database = "canary",
                SslMode = SslMode.Disable,
            }.ConnectionString);
            WireClient? opened = null;
            try
            {
                await TerraBaseTestServer.EventuallyAsync(async _ =>
                {
                    WireClient candidate;
                    try
                    {
                        candidate = await WireClient.ConnectAsync(wirePort);
                    }
                    catch (SocketException error) when (error.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        return false;
                    }
                    var hello = await candidate.HelloAsync(key, cluster.ProjectPath.Split('/')[3], "canary");
                    if (hello.Status == 200)
                    {
                        opened = candidate;
                        return true;
                    }

                    await candidate.DisposeAsync();
                    Expected.Check(hello.Status is 421 or 503 or 429, $"wire hello: {hello.Status} {hello.Code}");
                    return false;
                }, timeout: TimeSpan.FromSeconds(30));
                return new Client(cluster, region, cluster.Sql(region), source, opened!);
            }
            catch
            {
                if (opened is not null)
                {
                    await opened.DisposeAsync();
                }

                await source.DisposeAsync();
                throw;
            }
        }

        public async Task WriteAsync(string endpoint, string id, string value)
        {
            var body = new JsonObject { ["value"] = value, [region + "_write"] = 1 };
            if (endpoint == "documents")
            {
                await Docs.PatchAsync(cluster[region], Document(id), body);
            }
            else if (endpoint == "wire")
            {
                var answer = await wire.PutAsync("rows", id, body.ToJsonString(), remoteDurable: true);
                Expected.Check(answer.Status == 200 && answer.RemoteDurable, $"wire write: {answer.Status} {answer.Code}");
            }
            else
            {
                await SqlAsync(endpoint, $"UPDATE rows SET value = '{value}', {region}_write = 1 WHERE id = '{id}'");
            }
        }

        public async Task SqlAsync(string endpoint, string sql)
        {
            if (endpoint == "postgres")
            {
                await using var connection = await postgres.OpenConnectionAsync();
                await using var command = new NpgsqlCommand(sql, connection);
                _ = await command.ExecuteNonQueryAsync();
            }
            else
            {
                _ = await HttpSqlAsync(endpoint, sql);
            }
        }

        private async Task<JsonElement> HttpSqlAsync(string endpoint, string sql)
        {
            object body = endpoint == "hrana"
                ? new { requests = new object[] { new { type = "execute", stmt = new { sql, want_rows = true } }, new { type = "close" } } }
                : new { statements = new[] { new { sql } } };
            using var response = await http.PostAsJsonAsync(new Uri(cluster.DatabasePath + (endpoint == "hrana" ? "/libsql/v2/pipeline" : "/sql/execute"), UriKind.Relative), body);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(text, null, response.StatusCode);
            }

            var result = JsonElement.Parse(text).GetProperty("results")[0];
            if (endpoint != "hrana")
            {
                return result;
            }

            if (result.GetProperty("type").GetString() != "ok")
            {
                throw new HttpRequestException(text, null, HttpStatusCode.BadRequest);
            }

            return result.GetProperty("response").GetProperty("result");
        }

        public async Task<bool> ReadAsync(string endpoint, string id, string expected)
        {
            try
            {
                if (endpoint == "documents")
                {
                    return (await Docs.ReadAsync(cluster[region], Document(id)))?["value"]?.GetValue<string>() == expected;
                }

                if (endpoint == "wire")
                {
                    var answer = await wire.GetAsync("rows", id);
                    Expected.Check(answer.Status is 200 or 404 or 421 or 429 or 503, $"wire read: {answer.Status} {answer.Code}");
                    return answer.Status == 200 && JsonElement.Parse(answer.Json).GetProperty("value").GetString() == expected;
                }

                var sql = $"SELECT value FROM rows WHERE id = '{id}'";
                if (endpoint == "postgres")
                {
                    await using var connection = await postgres.OpenConnectionAsync();
                    await using var command = new NpgsqlCommand(sql, connection);
                    return await command.ExecuteScalarAsync() as string == expected;
                }

                var rows = (await HttpSqlAsync(endpoint, sql)).GetProperty("rows");
                return rows.GetArrayLength() > 0 && (endpoint == "hrana" ? rows[0][0].GetProperty("value").GetString() : rows[0][0].GetString()) == expected;
            }
            catch (PostgresException error) when (error.SqlState is PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.UndefinedTable)
            {
                return false;
            }
            catch (NpgsqlException error) when (error.IsTransient)
            {
                return false;
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.MisdirectedRequest or HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                || error.StatusCode == HttpStatusCode.BadRequest && error.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        public async ValueTask DisposeAsync()
        {
            http.Dispose();
            await wire.DisposeAsync();
            await postgres.DisposeAsync();
        }
    }
}

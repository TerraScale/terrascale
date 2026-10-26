using System.Net;
using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Primitives;
using TerraBase.Testing;
using Cell = TerraBase.Fast.Replication.SqlWriteReplicationTests.Cell;
using Machine = TerraBase.Fast.Replication.SqlWriteReplicationTests.Machine;

namespace TerraBase.Fast.Replication;

/// <summary>Real HTTP and socket clients of three regions; every source is checked through every reader.</summary>
public sealed class EndpointReplicationTests
{
    private static readonly string[] Endpoints = ["documents", "sql", "postgres", "hrana", "wire"];

    [Test]
    [Arguments("documents")]
    [Arguments("sql")]
    [Arguments("postgres")]
    [Arguments("hrana")]
    [Arguments("wire")]
    public async Task Every_region_originates_crud_read_through_all_five_endpoints(string endpoint)
    {
        await using var cell = await Cell.StartAsync();
        var readers = new List<Clients>();
        try
        {
            foreach (var machine in cell.Machines)
            {
                readers.Add(await Clients.OpenAsync(machine));
            }

            foreach (var source in readers)
            {
                var table = "rows_" + source.Machine.Region;
                // A ULID TEXT key makes this SQL table addressable by the document and Wire APIs too.
                await source.SqlAsync(endpoint is "postgres" or "hrana" ? endpoint : "sql",
                    $"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)");
                await EverywhereAsync(readers, table, EntityIds.New().ToString(), null);
                var id = EntityIds.New().ToString();
                var value = new Faker().Random.AlphaNumeric(24);
                await source.WriteAsync(endpoint, table, id, value, insert: true);
                await EverywhereAsync(readers, table, id, value);
                await source.WriteAsync(endpoint, table, id, "updated", insert: false);
                await EverywhereAsync(readers, table, id, "updated");
                await source.DeleteAsync(endpoint, table, id);
                await EverywhereAsync(readers, table, id, null);

                if (endpoint is "sql" or "postgres" or "hrana")
                {
                    var other = EntityIds.New().ToString();
                    await source.TransactionAsync(endpoint,
                        $"INSERT INTO {table} VALUES ('{id}', 'transaction')",
                        $"INSERT INTO {table} VALUES ('{other}', 'transaction')",
                        $"UPDATE {table} SET value = 'committed' WHERE id = '{id}'");
                    await EverywhereAsync(readers, table, id, "committed");
                    await EverywhereAsync(readers, table, other, "transaction");
                }
            }
        }
        finally
        {
            foreach (var reader in readers)
            {
                await reader.DisposeAsync();
            }
        }
    }

    private static Task EverywhereAsync(List<Clients> readers, string table, string id, string? expected) =>
        TerraBaseTestServer.EventuallyAsync(async _ =>
        {
            var copies = await Task.WhenAll(readers.SelectMany(reader => Endpoints
                .Select(endpoint => reader.ReadAsync(endpoint, table, id))));
            return copies.All(copy => copy.Ready && copy.Value == expected);
        }, timeout: TimeSpan.FromSeconds(30));

    private sealed class Clients(Machine machine, NpgsqlDataSource postgres, WireClient wire) : IAsyncDisposable
    {
        public Machine Machine => machine;

        private string Path => machine.Cell.Managed.Data;

        public static async Task<Clients> OpenAsync(Machine machine)
        {
            await machine.Api.SetPlanAsync(machine.Api.User, machine.Cell.Database.OrganizationId, "enterprise");
            var tenant = machine.Cell.Database;
            var key = await machine.Api.Server.IssueNarrowedAsync(tenant.OrganizationId, tenant.ProjectId, null, "documents:read", "documents:write");
            var postgres = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
            {
                Host = "127.0.0.1",
                Port = machine.Api.Server.PostgresPort,
                Username = "wire",
                Password = key,
                Database = "main",
                SslMode = SslMode.Disable,
            }.ConnectionString);
            try
            {
                return new Clients(machine, postgres, await WireClient.OpenAsync(machine.Api.Server.WirePort, key, tenant.ProjectId!, "main"));
            }
            catch
            {
                await postgres.DisposeAsync();
                throw;
            }
        }

        public async Task WriteAsync(string endpoint, string table, string id, string value, bool insert)
        {
            if (endpoint == "wire")
            {
                var answer = await wire.PutAsync(table, id, JsonSerializer.Serialize(new { value }));
                await Assert.That(answer.Status).IsEqualTo(insert ? 201 : 200).Because(answer.Code);
            }
            else if (endpoint == "documents")
            {
                using var response = await machine.Data.PutAsJsonAsync(Document(table, id), new { value });
                await Assert.That(response.StatusCode).IsEqualTo(insert ? HttpStatusCode.Created : HttpStatusCode.OK);
            }
            else
            {
                await SqlAsync(endpoint, insert ? $"INSERT INTO {table} VALUES ('{id}', '{value}')" : $"UPDATE {table} SET value = '{value}' WHERE id = '{id}'");
            }
        }

        public async Task DeleteAsync(string endpoint, string table, string id)
        {
            if (endpoint == "wire")
            {
                var answer = await wire.DeleteAsync(table, id, remoteDurable: true);
                await Assert.That(answer.Status).IsEqualTo(200).Because(answer.Code);
                await Assert.That(answer.RemoteDurable).IsTrue();
                await Assert.That(answer.ETag).StartsWith("tbv1-");
            }
            else if (endpoint == "documents")
            {
                using var response = await machine.Data.DeleteAsync(Document(table, id));
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
            else
            {
                await SqlAsync(endpoint, $"DELETE FROM {table} WHERE id = '{id}'");
            }
        }

        public async Task TransactionAsync(string endpoint, params string[] statements)
        {
            if (endpoint == "sql")
            {
                using var response = await machine.Data.PostAsJsonAsync(ManagementApi.Uri(Path + "/sql/execute"),
                    new { statements = statements.Select(sql => new { sql }) });
                await Assert.That(response.IsSuccessStatusCode).IsTrue().Because(await response.Content.ReadAsStringAsync());
                return;
            }

            await SqlAsync(endpoint, ["BEGIN", .. statements, "COMMIT"]);
        }

        public async Task SqlAsync(string endpoint, params string[] statements)
        {
            if (endpoint == "postgres")
            {
                await using var connection = await postgres.OpenConnectionAsync();
                foreach (var sql in statements)
                {
                    await using var command = new NpgsqlCommand(sql, connection);
                    _ = await command.ExecuteNonQueryAsync();
                }
            }
            else
            {
                _ = await HttpSqlAsync(endpoint, statements);
            }
        }

        private async Task<JsonElement> HttpSqlAsync(string endpoint, params string[] statements)
        {
            object body = endpoint == "hrana"
                ? new { requests = statements.Select(sql => (object)new { type = "execute", stmt = new { sql, want_rows = true } }).Append(new { type = "close" }) }
                : new { statements = statements.Select(sql => new { sql }) };
            using var response = await machine.Data.PostAsJsonAsync(ManagementApi.Uri(Path + (endpoint == "hrana" ? "/libsql/v2/pipeline" : "/sql/execute")), body);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(text, null, response.StatusCode);
            }

            var results = JsonElement.Parse(text).GetProperty("results");
            if (endpoint == "hrana")
            {
                if (results.EnumerateArray().Any(result => result.GetProperty("type").GetString() != "ok"))
                {
                    throw new HttpRequestException(text, null, HttpStatusCode.BadRequest);
                }
                return results[0].GetProperty("response").GetProperty("result");
            }

            return results[0];
        }

        public async Task<(bool Ready, string? Value)> ReadAsync(string endpoint, string table, string id)
        {
            if (endpoint == "wire")
            {
                var answer = await wire.GetAsync(table, id);
                return answer.Status switch
                {
                    200 => (true, JsonElement.Parse(answer.Json).GetProperty("value").GetString()),
                    404 => (true, null),
                    _ => throw new InvalidOperationException($"wire: {answer.Status} {answer.Code}"),
                };
            }

            if (endpoint == "documents")
            {
                using var response = await machine.Data.GetAsync(Document(table, id));
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (true, null);
                }

                response.EnsureSuccessStatusCode();
                return (true, JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("value").GetString());
            }

            var sql = $"SELECT value FROM {table} WHERE id = '{id}'";
            try
            {
                if (endpoint == "postgres")
                {
                    await using var connection = await postgres.OpenConnectionAsync();
                    await using var command = new NpgsqlCommand(sql, connection);
                    return (true, await command.ExecuteScalarAsync() as string);
                }

                var rows = (await HttpSqlAsync(endpoint, sql)).GetProperty("rows");
                return (true, rows.GetArrayLength() == 0 ? null : endpoint == "hrana" ? rows[0][0].GetProperty("value").GetString() : rows[0][0].GetString());
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return (false, null);
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.BadRequest && error.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
            {
                return (false, null);
            }
        }

        private Uri Document(string table, string id) => ManagementApi.Uri(ManagementApi.DocumentPath(machine.Cell.Managed, table, id));

        public async ValueTask DisposeAsync()
        {
            await wire.DisposeAsync();
            await postgres.DisposeAsync();
        }
    }
}

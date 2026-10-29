using System.Globalization;
using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Primitives;
using TerraBase.Testing;
using Cell = TerraBase.Fast.Replication.SqlWriteReplicationTests.Cell;
using Machine = TerraBase.Fast.Replication.SqlWriteReplicationTests.Machine;

namespace TerraBase.Fast.Replication;

/// <summary>Real HTTP and socket clients of three regions; every source is checked through every reader.</summary>
public sealed class EndpointReplicationTests
{
    private static readonly string[] Endpoints = ["documents", "sql", "postgres", "query"];

    [Test]
    [Arguments("documents")]
    [Arguments("sql")]
    [Arguments("postgres")]
    public async Task Every_region_originates_crud_read_through_all_readers(string endpoint)
    {
        await using var cell = await Cell.StartAsync();
        var readers = new List<Clients>();
        try
        {
            foreach (var machine in cell.Machines)
            {
                readers.Add(await Clients.OpenAsync(machine));
            }

            await Task.WhenAll(readers.Select(async source =>
            {
                var table = "rows_" + source.Machine.Region;
                await source.SqlAsync(endpoint == "postgres" ? endpoint : "sql",
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

                if (endpoint is "sql" or "postgres")
                {
                    var other = EntityIds.New().ToString();
                    await source.TransactionAsync(endpoint,
                        $"INSERT INTO {table} VALUES ('{id}', 'transaction')",
                        $"INSERT INTO {table} VALUES ('{other}', 'transaction')",
                        $"UPDATE {table} SET value = 'committed' WHERE id = '{id}'");
                    await EverywhereAsync(readers, table, id, "committed");
                    await EverywhereAsync(readers, table, other, "transaction");
                }
            }));
        }
        finally
        {
            foreach (var reader in readers)
            {
                await reader.DisposeAsync();
            }
        }
    }

    [Test]
    [Arguments("documents")]
    [Arguments("sql")]
    [Arguments("postgres")]
    public async Task Concurrent_writes_in_every_region_converge_through_all_readers_and_a_later_write_wins(string endpoint)
    {
        await using var cell = await Cell.StartAsync();
        var readers = new List<Clients>();
        try
        {
            foreach (var machine in cell.Machines)
            {
                readers.Add(await Clients.OpenAsync(machine));
            }

            const string table = "conflicts";
            await readers[0].SqlAsync("sql", $"CREATE TABLE {table} (id TEXT PRIMARY KEY, value TEXT)");
            await EverywhereAsync(readers, table, EntityIds.New().ToString(), null);
            for (var round = 0; round < 3; round++)
            {
                var id = EntityIds.New().ToString();
                await readers[0].WriteAsync(endpoint, table, id, "base", insert: true);
                await EverywhereAsync(readers, table, id, "base");
                var values = readers.Select(reader => $"{reader.Machine.Region}-{round}").ToArray();
                var feeds = new List<VersionFeed>();
                try
                {
                    foreach (var reader in readers)
                    {
                        feeds.Add(await VersionFeed.OpenAsync(reader.Machine, table));
                    }

                    await Task.WhenAll(readers.Select((reader, index) =>
                        reader.WriteAsync(endpoint, table, id, values[index], insert: false)));
                    var versions = await Task.WhenAll(feeds.Select(feed => feed.OwnVersionAsync(id)));
                    var winner = Enumerable.Range(0, versions.Length).MaxBy(index => versions[index]);
                    // Feed documents are current snapshots; the operation's stamp and origin identify the exact winner.
                    await EverywhereAsync(readers, table, id, values[winner]);
                }
                finally
                {
                    foreach (var feed in feeds)
                    {
                        feed.Dispose();
                    }
                }

                // This writer observed the converged version, so its causal overwrite must win everywhere.
                var later = $"later-{round}";
                await readers[(round + 1) % readers.Count].WriteAsync(endpoint, table, id, later, insert: false);
                await EverywhereAsync(readers, table, id, later);
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

    private sealed class VersionFeed(HttpResponseMessage response, StreamReader reader, ReplicaId local) : IDisposable
    {
        public static async Task<VersionFeed> OpenAsync(Machine machine, string table)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                ManagementApi.Uri($"{machine.Cell.Managed.Data}/changes?cursor=now&collections={table}"));
            request.Headers.Accept.ParseAdd("text/event-stream");
            var response = await machine.Data.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            try
            {
                response.EnsureSuccessStatusCode();
                var local = machine.Api.Server.Services.GetRequiredService<LocalReplica>().Id;
                return new VersionFeed(response, new StreamReader(await response.Content.ReadAsStreamAsync()), local);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        public async Task<EntryVersion> OwnVersionAsync(string id)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (await reader.ReadLineAsync(timeout.Token) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = JsonElement.Parse(line[6..]);
                if (!data.TryGetProperty("id", out var documentId) || documentId.GetString() != id)
                {
                    continue;
                }

                var node = new ReplicaId(ulong.Parse(data.GetProperty("origin").GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                // Actors use different epoch bits; the member and region still identify this writer.
                if (node.Region == local.Region && node.Member == local.Member)
                {
                    return new EntryVersion(new Hlc(ulong.Parse(data.GetProperty("version").GetString()!, CultureInfo.InvariantCulture)), node);
                }
            }

            throw new EndOfStreamException("the change feed ended before the local write");
        }

        public void Dispose()
        {
            reader.Dispose();
            response.Dispose();
        }
    }

    private sealed class Clients(Machine machine, NpgsqlDataSource postgres) : IAsyncDisposable
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
            return new Clients(machine, postgres);
        }

        public async Task WriteAsync(string endpoint, string table, string id, string value, bool insert)
        {
            if (endpoint == "documents")
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
            if (endpoint == "documents")
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
                _ = await HttpSqlAsync(statements);
            }
        }

        private async Task<JsonElement> HttpSqlAsync(params string[] statements)
        {
            var body = new { statements = statements.Select(sql => new { sql }) };
            using var response = await machine.Data.PostAsJsonAsync(ManagementApi.Uri(Path + "/sql/execute"), body);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(text, null, response.StatusCode);
            }

            var results = JsonElement.Parse(text).GetProperty("results");
            return results[0];
        }

        public async Task<(bool Ready, string? Value)> ReadAsync(string endpoint, string table, string id)
        {
            try
            {
                return await ReadOnceAsync(endpoint, table, id);
            }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.MisdirectedRequest)
            {
                return (false, null);
            }
            catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.CannotConnectNow)
            {
                return (false, null);
            }
            catch (NpgsqlException error) when (error.IsTransient)
            {
                return (false, null);
            }
        }

        private async Task<(bool Ready, string? Value)> ReadOnceAsync(string endpoint, string table, string id)
        {
            if (endpoint == "query")
            {
                // /sql is the read-only document query API; /sql/execute accepts relational writes.
                using var response = await machine.Api.Client.PostAsJsonAsync(
                    ManagementApi.Uri(machine.Cell.Managed.Path + "/sql"),
                    new { sql = $"SELECT value FROM {table} WHERE _id = '{id}'" });
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (false, null);
                }

                response.EnsureSuccessStatusCode();
                var rows = JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("rows");
                return (true, rows.GetArrayLength() == 0 ? null : rows[0].GetProperty("value").GetProperty("value").GetString());
            }

            if (endpoint == "documents")
            {
                using var response = await machine.Data.GetAsync(Document(table, id));
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return (true, null);
                }

                response.EnsureSuccessStatusCode();
                return (true, JsonElement.Parse(await response.Content.ReadAsStringAsync()).GetProperty("document").GetProperty("value").GetString());
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

                var rows = (await HttpSqlAsync(sql)).GetProperty("rows");
                return (true, rows.GetArrayLength() == 0 ? null : rows[0][0].GetString());
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
            await postgres.DisposeAsync();
        }
    }
}

using System.Net;
using System.Text.Json;
using Bogus;
using Npgsql;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Hrana;
using TerraBase.Testing;

namespace TerraBase.Fast.Sql;

/// <summary>SQL over HTTP, Postgres wire and libSQL bills each started 4 KiB read or written, as documents do.</summary>
public sealed class SqlMeteringTests
{
    private const string Notes = "CREATE TABLE notes (id INTEGER PRIMARY KEY, body TEXT)";

    private static readonly Faker Faker = new();

    // 10 KiB of ASCII: a stored or returned row of it is three units.
    private static string Large() => Faker.Random.String2(10 * 1024);

    [Test]
    public async Task Sql_http_bills_rows_by_started_4_KiB_and_a_failed_request_bills_nothing()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Notes);
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 0L)).Because("DDL bills nothing");

        await SendOkAsync(api, new { sql = "INSERT INTO notes VALUES (1, ?)", args = new object[] { Large() } });
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 3L));

        // One request is one transaction: a large row and a small one.
        await SendOkAsync(api, new { sql = "INSERT INTO notes VALUES (2, ?)", args = new object[] { Large() } }, new { sql = "INSERT INTO notes VALUES (3, 'small')" });
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 7L));

        // A request whose second statement fails rolls back the first: nothing is billed.
        var refused = await SqlApi.SendAsync(api, api.Client,
            new { sql = "INSERT INTO notes VALUES (4, ?)", args = new object[] { Large() } }, new { sql = "INSERT INTO notes VALUES (1, 'again')" });
        await Assert.That(SqlApi.Error(refused).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(SqlApi.Error(await SqlApi.ExecuteAsync(api, "SELECT * FROM nowhere")).Sqlite).IsEqualTo("SQLITE_ERROR");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 7L));

        // A returned 10 KiB row is three units, a small one one; constant reads and a delete's own read bill nothing.
        await SqlApi.OkAsync(api, "SELECT * FROM notes WHERE id IN (1, 3)", "SELECT 1", "DELETE FROM notes WHERE id = 3");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((4L, 8L));
    }

    [Test]
    public async Task Postgres_wire_bills_writes_once_at_commit_despite_replay_and_none_on_rollback()
    {
        await using var api = await DocumentApi.StartAsync();
        var key = await api.Server.IssueNarrowedAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, null, "documents:read", "documents:write");
        await using var source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = api.Server.PostgresPort,
            Username = "wire",
            Password = key,
            Database = DocumentApi.Database,
            SslMode = SslMode.Disable,
        }.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Notes);

        // Every statement is its own request, so each replays the ones before it; the read answers and bills once.
        await using (var rolledBack = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO notes VALUES (1, $1)", Large());
            await ExecuteAsync(connection, "SELECT body FROM notes WHERE id = 1");
            await rolledBack.RollbackAsync();
        }

        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 0L));

        await using (var committed = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO notes VALUES (1, $1)", Large());
            await ExecuteAsync(connection, "INSERT INTO notes VALUES (2, 'small')");
            await ExecuteAsync(connection, "UPDATE notes SET body = 'smaller' WHERE id = 2");
            await committed.CommitAsync();
        }

        // Inserted 3 + 1, updated 1: the replayed inserts are not billed again.
        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 5L));
    }

    [Test]
    public async Task Libsql_bills_the_same_units_as_the_other_protocols()
    {
        await using var api = await DocumentApi.StartAsync();
        var body = JsonSerializer.Serialize(Large());
        var result = await HranaPipelineTests.PostAsync(api, $$$"""
            {"requests": [
             {"type": "execute", "stmt": {"sql": "{{{Notes}}}", "args": [], "named_args": [], "want_rows": false}},
             {"type": "execute", "stmt": {"sql": "INSERT INTO notes VALUES (1, ?)", "args": [{"type": "text", "value": {{{body}}}}], "named_args": [], "want_rows": false}},
             {"type": "execute", "stmt": {"sql": "SELECT * FROM notes", "args": [], "named_args": [], "want_rows": true}},
             {"type": "close"}]}
            """);

        await Assert.That(HranaPipelineTests.Types(result.Json)).IsEqualTo("ok:execute,ok:execute,ok:execute,ok:close");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 3L));
    }

    private static async Task SendOkAsync(DocumentApi api, params object[] statements) =>
        (await SqlApi.SendAsync(api, api.Client, statements)).Status.ShouldBe(HttpStatusCode.OK);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params object[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
        }
    }

    // This month's read and write units of the project, as the usage API reports them.
    private static async Task<(long Read, long Write)> UnitsAsync(DocumentApi api)
    {
        using var owner = api.Management.As(api.Tenant.User);
        var month = (await owner.GetFromJsonAsync<JsonElement>(
            ManagementApi.Uri($"/v1/management/organizations/{api.Tenant.OrganizationId}/projects/{api.Tenant.ProjectId}/usage"))).GetProperty("month");
        return (month.GetProperty("readUnits").GetInt64(), month.GetProperty("writeUnits").GetInt64());
    }
}

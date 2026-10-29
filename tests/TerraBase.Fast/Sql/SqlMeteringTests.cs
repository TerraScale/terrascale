using System.Net;
using System.Text.Json;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Hrana;
using TerraBase.Fast.Management;
using TerraBase.Features.Management.Usage;
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
        await Assert.That(await OperationsAsync(api)).IsEqualTo((0L, 0L, 0L));

        await using (var committed = await connection.BeginTransactionAsync())
        {
            await ExecuteAsync(connection, "INSERT INTO notes VALUES (1, $1)", Large());
            await ExecuteAsync(connection, "INSERT INTO notes VALUES (2, 'small')");
            await ExecuteAsync(connection, "UPDATE notes SET body = 'smaller' WHERE id = 2");
            await committed.CommitAsync();
        }

        // Inserted 3 + 1, updated 1: the replayed inserts are not billed again.
        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 5L));
        await Assert.That(await OperationsAsync(api)).IsEqualTo((2L, 1L, 0L));
    }

    [Test]
    public async Task Postgres_wire_exchanges_each_record_one_request_with_its_latency_errors_and_bytes()
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
            IncludeErrorDetail = true,
        }.ConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        await ExecuteAsync(connection, Notes);
        var before = await RequestsAsync(api);
        var statements = await StatementsAsync(api, "postgres");

        // Parse, Bind, Describe, Execute and Sync are one exchange: one request, its latency and its bytes both ways.
        await ExecuteAsync(connection, "INSERT INTO notes VALUES (1, $1)", Faker.Lorem.Sentence());
        var inserted = await RequestsAsync(api);
        await Assert.That(inserted.Requests).IsEqualTo(before.Requests + 1);
        await Assert.That(inserted.Timed).IsEqualTo(before.Timed + 1);
        await Assert.That(inserted.Errors).IsEqualTo(before.Errors);
        await Assert.That(inserted.Received).IsGreaterThan(before.Received);
        await Assert.That(inserted.Sent).IsGreaterThan(before.Sent);
        // The statement's document operation is still counted once, by the SQL service.
        await Assert.That(await OperationsAsync(api)).IsEqualTo((1L, 0L, 0L));

        // A failed statement is one request and one error.
        await Assert.That(async () => await ExecuteAsync(connection, "SELECT * FROM nowhere")).Throws<PostgresException>();
        var failed = await RequestsAsync(api);
        await Assert.That(failed.Requests).IsEqualTo(inserted.Requests + 1);
        await Assert.That(failed.Errors).IsEqualTo(inserted.Errors + 1);
        var error = await Assert.That(async () => await ExecuteAsync(connection, "SELECT * FROM nowhere")).Throws<PostgresException>();
        await Assert.That(error!.Detail).StartsWith("request id ");

        // Statements are measured apart from exchanges, with their own errors and latency; each exchange is logged.
        await Assert.That(await StatementsAsync(api, "postgres")).IsEqualTo((statements.Statements + 3, statements.Errors + 2, statements.Timed + 3));
        var logs = (await LogsAsync(api)).Where(row => row.GetProperty("route").GetString() == "postgres").ToArray();
        await Assert.That(logs.Count(row => row.GetProperty("status").GetInt32() == 400 && row.GetProperty("statementErrors").GetInt64() == 1)).IsEqualTo(2);
        await Assert.That(logs.Any(row => row.GetProperty("requestId").GetString() == error.Detail!["request id ".Length..])).IsTrue();
        await Assert.That(logs.All(row => row.GetProperty("isQuery").GetBoolean())).IsTrue();
    }

    [Test]
    [Arguments("pipeline")]
    [Arguments("cursor")]
    public async Task Hrana_statement_errors_inside_a_200_are_statement_errors_not_request_errors(string endpoint)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Notes);
        const string steps = """[{"stmt":{"sql":"SELECT * FROM nowhere"}},{"stmt":{"sql":"SELECT id FROM notes"}}]""";
        var request = endpoint == "cursor" ? $$$"""{"batch":{"steps":{{{steps}}}}}"""
            : $$$"""{"requests":[{"type":"batch","batch":{"steps":{{{steps}}}}},{"type":"close"}]}""";
        var response = await HranaPipelineTests.PostAsync(api, request, version: "v3", endpoint: endpoint);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        if (endpoint == "cursor")
        {
            var baton = JsonElement.Parse(response.Raw.Split('\n')[0]).GetProperty("baton").GetString();
            await HranaPipelineTests.PostAsync(api, $$"""{"baton":"{{baton}}","requests":[{"type":"close"}]}""", version: "v3");
        }

        await Assert.That(await StatementsAsync(api, "hrana")).IsEqualTo((2L, 1L, 2L));
        var log = (await LogsAsync(api)).First(row => row.GetProperty("route").GetString()!.EndsWith("/" + endpoint, StringComparison.Ordinal));
        await Assert.That(log.GetProperty("status").GetInt32()).IsEqualTo(200);
        await Assert.That((log.GetProperty("statements").GetInt64(), log.GetProperty("statementErrors").GetInt64())).IsEqualTo((2L, 1L));
    }

    [Test]
    public async Task Atomic_http_discards_read_prefix_charges_on_statement_and_commit_refusal()
    {
        var hook = new UsageCommitHook(storageLimit: _ => 16 * 1024);
        await using var api = await DocumentApi.StartAsync(services: hook.InstallQuota);
        await SqlApi.OkAsync(api, Notes);
        await SendOkAsync(api, new { sql = "INSERT INTO notes VALUES (1, ?)", args = new object[] { Large() } });
        var missing = await SqlApi.ExecuteAsync(api, "SELECT body FROM notes", "SELECT * FROM missing");
        await Assert.That(SqlApi.Error(missing).Sqlite).IsEqualTo("SQLITE_ERROR");
        var failed = await SqlApi.SendAsync(api, api.Client,
            new { sql = "INSERT INTO notes VALUES (2, 'small')" },
            new { sql = "SELECT body FROM notes" }, new { sql = "SELECT * FROM missing" });
        await Assert.That(SqlApi.Error(failed).Sqlite).IsEqualTo("SQLITE_ERROR");
        var full = await SqlApi.SendAsync(api, api.Client, new { sql = "SELECT body FROM notes" },
            new { sql = "INSERT INTO notes VALUES (2, ?)", args = new object[] { Large() } });
        await Assert.That(SqlApi.Error(full).Sqlite).IsEqualTo("SQLITE_FULL");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 3L));
        await Assert.That(await OperationsAsync(api)).IsEqualTo((1L, 0L, 0L));
        await Assert.That((await SqlApi.OkAsync(api, "SELECT body FROM notes")).Single().GetProperty("rows").GetArrayLength()).IsEqualTo(1);
        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 3L));
    }

    [Test]
    public async Task Sql_committed_operation_kinds_reach_analytics_and_request_logs_but_failed_and_noop_writes_do_not()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Notes);
        await SqlApi.OkAsync(api, "INSERT INTO notes VALUES (1, 'first'), (2, 'second')");
        await SqlApi.OkAsync(api, "UPDATE notes SET body = 'edited' WHERE id = 1");
        await SqlApi.OkAsync(api, "DELETE FROM notes WHERE id = 2");
        await SqlApi.OkAsync(api, "INSERT OR IGNORE INTO notes VALUES (1, 'ignored')", "UPDATE notes SET body = 'absent' WHERE id = 99");
        var failed = await SqlApi.ExecuteAsync(api, "INSERT INTO notes VALUES (3, 'rollback')", "INSERT INTO notes VALUES (1, 'duplicate')");
        await Assert.That(SqlApi.Error(failed).Sqlite).IsEqualTo("SQLITE_CONSTRAINT_PRIMARYKEY");
        await Assert.That(await OperationsAsync(api)).IsEqualTo((2L, 1L, 1L));
        await api.Server.Services.GetRequiredService<UsageMeter>().FlushAsync(CancellationToken.None);
        await Assert.That(await OperationsAsync(api)).IsEqualTo((2L, 1L, 1L));

        using var owner = api.Management.As(api.Tenant.User);
        using var project = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "databases:read";
        }));
        using var response = await project.GetAsync(ManagementApi.Uri(api.DatabasePath() + "/logs"));
        response.EnsureSuccessStatusCode();
        var logs = await response.Content.ReadFromJsonAsync<JsonElement>();
        var operations = logs.GetProperty("items").EnumerateArray()
            .Where(row => row.GetProperty("route").GetString()!.EndsWith("/sql/execute", StringComparison.Ordinal))
            .Select(row => row.GetProperty("operations").GetInt64()).Order().ToArray();
        await Assert.That(operations).IsEquivalentTo([0L, 0L, 0L, 1L, 1L, 2L], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("v2", "pipeline")]
    [Arguments("v3", "pipeline")]
    [Arguments("v3", "cursor")]
    public async Task Hrana_bills_accepted_reads_and_commits_but_not_response_bound_rejected_reads(string version, string endpoint)
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Notes);
        await SendOkAsync(api, new { sql = "INSERT INTO notes VALUES (1, ?)", args = new object[] { new string('x', 16 * 1024) } });
        var (beforeRead, beforeWrite) = await UnitsAsync(api);
        var values = string.Join(',', Enumerable.Repeat("(1)", 400));
        var steps = $$$"""
            [{"stmt":{"sql":"SELECT body FROM notes"}},
             {"stmt":{"sql":"INSERT INTO notes VALUES (2, 'small')"}},
             {"stmt":{"sql":"SELECT body FROM notes CROSS JOIN (VALUES {{{values}}}) v WHERE notes.id = 1"}}]
            """;
        var request = endpoint == "cursor" ? $$$"""{"batch":{"steps":{{{steps}}}}}"""
            : $$$"""{"requests":[{"type":"batch","batch":{"steps":{{{steps}}}}},{"type":"close"}]}""";
        var response = await HranaPipelineTests.PostAsync(api, request, version: version, endpoint: endpoint);
        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Raw).Contains("SQLITE_TOOBIG");
        if (endpoint == "pipeline")
        {
            var batch = HranaPipelineTests.Response(response.Json, 0).GetProperty("result");
            await Assert.That(batch.GetProperty("step_results")[0].GetProperty("rows").GetArrayLength()).IsEqualTo(1);
            await Assert.That(batch.GetProperty("step_errors")[2].GetProperty("code").GetString()).IsEqualTo("SQLITE_TOOBIG");
        }
        else
        {
            var lines = response.Raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonElement.Parse(line)).ToArray();
            await Assert.That(lines.Count(line => line.TryGetProperty("type", out var type) && type.GetString() == "row")).IsEqualTo(1);
            await Assert.That(lines[^1].GetProperty("step").GetInt32()).IsEqualTo(2);
            await HranaPipelineTests.PostAsync(api, $$$"""{"baton":"{{{lines[0].GetProperty("baton").GetString()}}}","requests":[{"type":"close"}]}""", version: version);
        }

        await Assert.That(await UnitsAsync(api)).IsEqualTo((beforeRead + 4, beforeWrite + 1));
        await Assert.That((await SqlApi.OkAsync(api, "SELECT id FROM notes WHERE id = 2")).Single().GetProperty("rows").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Postgres_portal_describe_and_refused_execute_are_free_and_delivered_rows_bill_once()
    {
        await using var api = await DocumentApi.StartAsync();
        await SqlApi.OkAsync(api, Notes);
        await SendOkAsync(api, new { sql = "INSERT INTO notes VALUES (1, ?)", args = new object[] { Large() } });
        var key = await api.Server.IssueNarrowedAsync(api.Tenant.OrganizationId, api.Tenant.ProjectId, null, "documents:read");
        await using var wire = await PostgresWireClient.ConnectAsync(api.Server.PostgresPort, key, DocumentApi.Database);
        await Assert.That(string.Concat(await wire.PortalAsync("SELECT body FROM notes", null))).IsEqualTo("12T3Z");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 3L));
        await Assert.That(string.Concat(await wire.PortalAsync("SELECT body FROM notes", 1))).IsEqualTo("12TEZ");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((0L, 3L));
        await Assert.That(string.Concat(await wire.PortalAsync("SELECT body FROM notes", 0))).IsEqualTo("12TDCZ");
        await Assert.That(await UnitsAsync(api)).IsEqualTo((3L, 3L));
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

    // The database's `other` requests (the Postgres wire's), their errors, latency samples and client bytes.
    private static async Task<(long Requests, long Errors, long Timed, long Received, long Sent)> RequestsAsync(DocumentApi api)
    {
        var counts = await api.Server.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(await api.TenantOfAsync(),
            new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1), CancellationToken.None);
        var meters = (counts is UsageCounts usage ? usage.Lifetime : throw new InvalidOperationException("usage unavailable")).Meters?.Values.ToArray() ?? [];
        var requests = meters.Where(meter => meter.Dimension.Meter == "requests" && meter.Dimension.Kind == "other").ToArray();
        long Bytes(string kind)
        {
            return meters.Where(meter => meter.Dimension.Meter == "client_bytes" && meter.Dimension.Kind == kind).Sum(meter => meter.Count);
        }

        return (requests.Sum(meter => meter.Count), requests.Sum(meter => meter.Value), requests.Sum(meter => meter.Latency?.Sum() ?? 0), Bytes("received"), Bytes("sent"));
    }

    // A protocol's statements, failed statements and latency samples.
    private static async Task<(long Statements, long Errors, long Timed)> StatementsAsync(DocumentApi api, string protocol)
    {
        var counts = await api.Server.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(await api.TenantOfAsync(),
            new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1), CancellationToken.None);
        var meters = (counts is UsageCounts usage ? usage.Lifetime : throw new InvalidOperationException("usage unavailable")).Meters?.Values
            .Where(meter => meter.Dimension.Meter == "statements" && meter.Dimension.Kind == protocol).ToArray() ?? [];
        return (meters.Sum(meter => meter.Count), meters.Sum(meter => meter.Value), meters.Sum(meter => meter.Latency?.Sum() ?? 0));
    }

    private static async Task<JsonElement[]> LogsAsync(DocumentApi api)
    {
        using var owner = api.Management.As(api.Tenant.User);
        using var project = api.Management.WithToken(api.Management.Issuer.Mint(api.Tenant.User, token =>
        {
            token.Claims["project_id"] = api.Tenant.ProjectId;
            token.Claims["scope"] = "databases:read";
        }));
        using var response = await project.GetAsync(ManagementApi.Uri(api.DatabasePath() + "/logs"));
        response.EnsureSuccessStatusCode();
        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items").EnumerateArray()];
    }

    private static async Task<(long Put, long Patch, long Delete)> OperationsAsync(DocumentApi api)
    {
        var counts = await api.Server.Services.GetRequiredService<UsageMeter>().GetObservationsAsync(await api.TenantOfAsync(),
            new UsageWindow(UsageWindow.Hour, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 3600, 1), CancellationToken.None);
        var lifetime = counts is UsageCounts usage ? usage.Lifetime : throw new InvalidOperationException("usage unavailable");
        long Count(string kind)
        {
            return lifetime.Meters?.Values.Where(meter => meter.Dimension.Meter == "ops" && meter.Dimension.Kind == kind).Sum(meter => meter.Count) ?? 0;
        }

        return (Count("put"), Count("patch"), Count("delete"));
    }
}

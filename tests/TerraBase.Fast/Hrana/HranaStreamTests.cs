using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Documents;
using TerraBase.Features.Hrana;
using TerraBase.Features.Qos;
using TerraBase.Features.Sql;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

/// <summary>Streams across requests: batons, transactions, expiry and the per-database stream share.</summary>
public sealed class HranaStreamTests
{
    [Test]
    public async Task An_interactive_transaction_spans_requests_with_rotating_batons_and_stored_sql()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        var begin = await PostAsync(api, """
            {"requests": [{"type": "store_sql", "sql_id": 0, "sql": "INSERT INTO t VALUES (?)"}, {"type": "batch", "batch": {"steps": [{"stmt": {"sql": "BEGIN IMMEDIATE", "args": [], "named_args": [], "want_rows": false}}, {"condition": {"type": "ok", "step": 0}, "stmt": {"sql_id": 0, "args": [{"type": "integer", "value": "2"}], "named_args": [], "want_rows": true}}]}}]}
            """);
        var first = begin.Json.GetProperty("baton").GetString()!;
        var middle = await PostAsync(api, $$$"""
            {"baton": "{{{first}}}", "requests": [{"type": "store_sql", "sql_id": 1, "sql": "SELECT :a"}, {"type": "batch", "batch": {"steps": [{"stmt": {"sql_id": 1, "args": [], "named_args": [{"name": "a", "value": {"type": "float", "value": 2.5}}], "want_rows": true}}, {"stmt": {"sql_id": 0, "args": [{"type": "integer", "value": "3"}], "named_args": [], "want_rows": false}}]}}, {"type": "get_autocommit"}]}
            """);
        var second = middle.Json.GetProperty("baton").GetString()!;
        var outside = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"close"}]}""");
        var stale = await PostAsync(api, $$"""{"baton": "{{first}}", "requests": [{"type": "get_autocommit"}]}""");
        var commit = await PostAsync(api, $$$"""{"baton": "{{{second}}}", "requests": [{"type": "execute", "stmt": {"sql": "COMMIT", "args": [], "named_args": [], "want_rows": false}}, {"type": "close"}]}""");
        var closed = await PostAsync(api, $$"""{"baton": "{{second}}", "requests": [{"type": "get_autocommit"}]}""");
        var after = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t ORDER BY x"}},{"type":"close"}]}""");

        await Assert.That(Nulls(Response(begin.Json, 1).GetProperty("result").GetProperty("step_errors"))).IsEqualTo("null,null");
        await Assert.That((first.Length, first[^2..])).IsEqualTo((24, ".t"));
        await Assert.That(second).IsNotEqualTo(first);
        var steps = Response(middle.Json, 1).GetProperty("result");
        await Assert.That(steps.GetProperty("step_results")[0].GetProperty("rows").GetRawText()).IsEqualTo("""[[{"type":"float","value":2.5}]]""");
        await Assert.That(steps.GetProperty("step_results")[1].GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
        await Assert.That(steps.GetProperty("step_results")[1].GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await Assert.That(Response(middle.Json, 2).GetProperty("is_autocommit").GetBoolean()).IsFalse();
        await Assert.That(Response(outside.Json, 0).GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(0);
        await AssertExpiredAsync(stale);
        await Assert.That(Types(commit.Json)).IsEqualTo("ok:execute,ok:close");
        await Assert.That(commit.Json.GetProperty("baton").ValueKind).IsEqualTo(JsonValueKind.Null);
        await AssertExpiredAsync(closed);
        await Assert.That(Response(after.Json, 0).GetProperty("result").GetProperty("rows").GetRawText())
            .IsEqualTo("""[[{"type":"integer","value":"2"}],[{"type":"integer","value":"3"}]]""");
    }

    [Test]
    public async Task Idle_foreign_and_unknown_batons_are_refused_and_an_expired_transaction_rolls_back()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IHranaStreams>(provider => new HranaStreams(
                provider.GetRequiredService<ISqlService>(), time, provider.GetRequiredService<ILogger<HranaStreams>>())));
        using var other = await api.ClientWithScopesAsync("documents:read", "documents:write");
        await PostAsync(api, CreateT);
        var begin = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"BEGIN"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (4)"}}]}""");
        var foreign = await PostAsync(api, $$"""{"baton":"{{begin.Json.GetProperty("baton").GetString()}}","requests":[{"type":"get_autocommit"}]}""", client: other);
        var owner = await PostAsync(api, $$"""{"baton":"{{begin.Json.GetProperty("baton").GetString()}}","requests":[{"type":"get_autocommit"}]}""");
        time.Advance(HranaStreams.IdleTimeout + TimeSpan.FromSeconds(1));
        var idle = await PostAsync(api, $$"""{"baton":"{{owner.Json.GetProperty("baton").GetString()}}","requests":[{"type":"get_autocommit"}]}""");
        var unknown = await PostAsync(api, """{"baton":"AAAAAAAAAAAAAAAAAAAAAA","requests":[]}""");
        // A long-running app's connection: its stream held no state, so an idle baton opens a fresh stream.
        var stateless = await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""");
        time.Advance(HranaStreams.IdleTimeout + TimeSpan.FromSeconds(1));
        var resumed = await PostAsync(api, $$"""{"baton":"{{stateless.Json.GetProperty("baton").GetString()}}","requests":[{"type":"get_autocommit"},{"type":"close"}]}""");
        var after = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"close"}]}""");

        await AssertExpiredAsync(foreign);
        await Assert.That(Response(owner.Json, 0).GetProperty("is_autocommit").GetBoolean()).IsFalse();
        await AssertExpiredAsync(idle);
        await AssertExpiredAsync(unknown);
        await Assert.That(stateless.Json.GetProperty("baton").GetString()).EndsWith(".a");
        await Assert.That(Types(resumed.Json)).IsEqualTo("ok:get_autocommit,ok:close");
        await Assert.That(Response(resumed.Json, 0).GetProperty("is_autocommit").GetBoolean()).IsTrue();
        await Assert.That(Response(after.Json, 0).GetProperty("result").GetProperty("rows").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task A_database_holds_at_most_its_share_of_open_streams()
    {
        await using var api = await DocumentApi.StartAsync();
        var batons = new List<string>();
        for (var i = 0; i < HranaStreams.MaxDatabaseStreams; i++)
        {
            batons.Add((await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""")).Json.GetProperty("baton").GetString()!);
        }

        var refused = await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""");
        var closed = await PostAsync(api, $$"""{"baton":"{{batons[0]}}","requests":[{"type":"close"}]}""");
        var reopened = await PostAsync(api, """{"requests":[{"type":"get_autocommit"},{"type":"close"}]}""");

        await Assert.That((refused.Status, Code(refused))).IsEqualTo((HttpStatusCode.ServiceUnavailable, "DATABASE_STREAMS_EXHAUSTED"));
        await Assert.That(refused.RetryAfter).IsEqualTo("1");
        await Assert.That(closed.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(Types(reopened.Json)).IsEqualTo("ok:get_autocommit,ok:close");
    }

    [Test]
    public async Task A_forged_stateless_baton_opens_a_fresh_stream_for_the_caller_and_never_reaches_another()
    {
        await using var api = await DocumentApi.StartAsync();
        using var other = await api.ClientWithScopesAsync("documents:read", "documents:write");
        (await api.SendAsync(HttpMethod.Put, api.DatabasePath("other"))).Status.ShouldBe(HttpStatusCode.Created);
        var owner = await PostAsync(api, """{"requests":[{"type":"store_sql","sql_id":1,"sql":"SELECT 1"}]}""");
        var baton = owner.Json.GetProperty("baton").GetString()!;
        var forged = baton[..^2] + HranaStreams.StatelessSuffix;
        var otherKey = await PostAsync(api, UseStored(forged), client: other);
        var otherDatabase = await PostAsync(api, UseStored(forged), database: "other");
        var real = await PostAsync(api, UseStored(baton));

        await Assert.That(baton).EndsWith(".t");
        foreach (var fresh in new[] { otherKey, otherDatabase })
        {
            await Assert.That(Types(fresh.Json)).IsEqualTo("error,ok:close");
            await Assert.That(fresh.Json.GetProperty("results")[0].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQL_NOT_FOUND");
        }

        await Assert.That(Types(real.Json)).IsEqualTo("ok:execute,ok:close");
        await Assert.That(Response(real.Json, 0).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo("""[[{"type":"integer","value":"1"}]]""");
    }

    [Test]
    public async Task Stored_statements_hold_one_memory_reservation_per_stream()
    {
        await using var api = await DocumentApi.StartAsync();
        var store = $$$"""{"requests":[{{{string.Join(',', Enumerable.Range(0, HranaStream.MaxStoredSql).Select(id => $$"""{"type":"store_sql","sql_id":{{id}},"sql":"SELECT {{id}}"}"""))}}}]}""";
        // A Hobby organization holds 4,096 reservations: one per statement would exhaust it in the 17th stream.
        var streams = new List<ApiResult>();
        for (var i = 0; i < 17; i++)
        {
            streams.Add(await PostAsync(api, store));
        }

        var write = await api.PutAsync(DocumentApi.NewId(), new { still = "admitted" });

        foreach (var stream in streams)
        {
            await Assert.That(stream.Json.GetProperty("results").EnumerateArray().All(result => result.GetProperty("type").GetString() == "ok")).IsTrue();
        }

        await Assert.That(write.Status).IsEqualTo(HttpStatusCode.Created);
    }

    [Test]
    public async Task A_stream_belongs_to_one_credential_and_write_right_of_a_user()
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT);
        using var owner = api.Management.As(api.Tenant.User);
        string Token(string scope)
        {
            return api.Management.Issuer.Mint(api.Tenant.User, token =>
            {
                token.Claims["project_id"] = api.Tenant.ProjectId;
                token.Claims["scope"] = scope;
            });
        }

        using var writer = api.Management.WithToken(Token("documents:read documents:write"));
        using var reader = api.Management.WithToken(Token("documents:read"));
        var stored = await PostAsync(api, """{"requests":[{"type":"store_sql","sql_id":1,"sql":"INSERT INTO t VALUES (8)"}]}""", client: writer);
        var stateless = await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""", client: writer);
        var reuse = await PostAsync(api, $$$"""{"baton":"{{{stored.Json.GetProperty("baton").GetString()}}}","requests":[{"type":"execute","stmt":{"sql_id":1}}]}""", client: reader);
        var fresh = await PostAsync(api, $$$"""{"baton":"{{{stateless.Json.GetProperty("baton").GetString()}}}","requests":[{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (9)"}},{"type":"close"}]}""", client: reader);
        var own = await PostAsync(api, $$$"""{"baton":"{{{stored.Json.GetProperty("baton").GetString()}}}","requests":[{"type":"execute","stmt":{"sql_id":1}},{"type":"close"}]}""", client: writer);

        await AssertExpiredAsync(reuse);
        await Assert.That(fresh.Json.GetProperty("results")[0].GetProperty("error").GetProperty("code").GetString()).IsEqualTo("SQLITE_AUTH");
        await Assert.That(Response(own.Json, 0).GetProperty("result").GetProperty("affected_row_count").GetInt64()).IsEqualTo(1);
    }

    [Test]
    public async Task An_organization_holds_at_most_its_share_of_open_streams_across_databases()
    {
        // The request budget (50 per second) runs on its own clock, moved one second per request, so 257 opens are admitted.
        var budget = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await DocumentApi.StartAsync(services: services =>
            services.AddSingleton<IQosAdmission>(provider => new QosAdmission(budget, provider.GetRequiredService<IMeterFactory>())));
        var databases = Enumerable.Range(0, 5).Select(i => $"db{i}").ToList();
        foreach (var database in databases)
        {
            (await api.SendAsync(HttpMethod.Put, api.DatabasePath(database))).Status.ShouldBe(HttpStatusCode.Created);
        }

        var opened = 0;
        foreach (var database in databases.Take(4))
        {
            for (var i = 0; i < HranaStreams.MaxDatabaseStreams; i++)
            {
                budget.Advance(TimeSpan.FromSeconds(1));
                opened += (await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""", database: database)).Status == HttpStatusCode.OK ? 1 : 0;
            }
        }

        budget.Advance(TimeSpan.FromSeconds(1));
        var refused = await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""", database: databases[4]);

        await Assert.That(opened).IsEqualTo(HranaStreams.MaxOrganizationStreams);
        await Assert.That((refused.Status, Code(refused))).IsEqualTo((HttpStatusCode.ServiceUnavailable, "ORGANIZATION_STREAMS_EXHAUSTED"));
    }

    private static string UseStored(string baton) => $$$"""{"baton":"{{{baton}}}","requests":[{"type":"execute","stmt":{"sql_id":1}},{"type":"close"}]}""";
}

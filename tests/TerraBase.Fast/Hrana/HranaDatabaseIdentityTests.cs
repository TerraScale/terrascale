using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Documents;
using TerraBase.Features.Hrana;
using TerraBase.Features.Sql;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaDatabaseIdentityTests
{
    [Test]
    [Arguments("v2", "stateless", false)]
    [Arguments("v2", "stateless", true)]
    [Arguments("v2", "stored", false)]
    [Arguments("v2", "stored", true)]
    [Arguments("v2", "transaction", false)]
    [Arguments("v2", "transaction", true)]
    [Arguments("v3", "stateless", false)]
    [Arguments("v3", "stateless", true)]
    [Arguments("v3", "stored", false)]
    [Arguments("v3", "stored", true)]
    [Arguments("v3", "transaction", false)]
    [Arguments("v3", "transaction", true)]
    public async Task Recreated_database_rejects_old_inline_reads_and_writes(string version, string state, bool expired)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartAsync(time);
        var baton = await ReplaceAsync(api, version, state);
        if (expired)
        {
            time.Advance(HranaStreams.IdleTimeout + TimeSpan.FromSeconds(1));
        }

        var stale = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (98)"}},{"type":"close"}]}
            """, version);
        await AssertExpiredAsync(stale);
        await AssertRowsAsync(api, version, """[[{"type":"integer","value":"7"}]]""");
    }

    [Test]
    [Arguments("stateless", false)]
    [Arguments("stateless", true)]
    [Arguments("stored", false)]
    [Arguments("stored", true)]
    [Arguments("transaction", false)]
    [Arguments("transaction", true)]
    public async Task Recreated_database_refuses_a_cursor_before_batch_output_or_mutation(string state, bool expired)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartAsync(time);
        var baton = await ReplaceAsync(api, "v3", state);
        if (expired)
        {
            time.Advance(HranaStreams.IdleTimeout + TimeSpan.FromSeconds(1));
        }

        var stale = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","batch":{"steps":[{"stmt":{"sql":"INSERT INTO t VALUES (98)"}},{"stmt":{"sql":"SELECT x FROM t"}}]}}
            """, "v3", endpoint: "cursor");
        await AssertCursorExpiredAsync(stale);
        await AssertRowsAsync(api, "v3", """[[{"type":"integer","value":"7"}]]""");
    }

    [Test]
    [Arguments("v2", false)]
    [Arguments("v2", true)]
    [Arguments("v3", false)]
    [Arguments("v3", true)]
    public async Task Same_database_stateless_recovery_survives_expiry_and_missing_streams(string version, bool missing)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var api = await StartAsync(time);
        await PostAsync(api, CreateT, version);
        var opened = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (23)"}}]}""", version);
        var baton = opened.Json.GetProperty("baton").GetString()!;
        if (missing)
        {
            // A different random prefix cannot locate this stream; the original identity pin is unchanged.
            baton = (baton[0] == 'A' ? "B" : "A") + baton[1..];
        }
        else
        {
            time.Advance(HranaStreams.IdleTimeout + TimeSpan.FromSeconds(1));
        }

        var resumed = await PostAsync(api, $$$"""
            {"baton":"{{{baton}}}","requests":[{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (24)"}},{"type":"close"}]}
            """, version);
        await Assert.That(Types(resumed.Json)).IsEqualTo("ok:execute,ok:close");
        await AssertRowsAsync(api, version, """[[{"type":"integer","value":"23"}],[{"type":"integer","value":"24"}]]""");
    }

    [Test]
    [Arguments("v2", "pipeline")]
    [Arguments("v3", "pipeline")]
    [Arguments("v3", "cursor")]
    public async Task Unbound_malformed_and_foreign_identity_envelopes_cannot_execute(string version, string endpoint)
    {
        await using var api = await DocumentApi.StartAsync();
        await PostAsync(api, CreateT, version);
        var opened = await PostAsync(api, """{"requests":[{"type":"get_autocommit"}]}""", version);
        var baton = opened.Json.GetProperty("baton").GetString()!;
        var malformed = new[]
        {
            new string('A', 22) + ".a", baton[..^1], baton + "x", "!" + baton[1..],
            baton[..22] + "_" + baton[23..], baton[..^1] + "x",
            baton[..23] + DocumentApi.NewId() + ".a", baton[..23] + baton[23..49].ToLowerInvariant() + ".a",
        };
        foreach (var invalid in malformed)
        {
            var body = endpoint == "pipeline"
                ? $$$"""{"baton":"{{{invalid}}}","requests":[{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (99)"}},{"type":"close"}]}"""
                : $$$"""{"baton":"{{{invalid}}}","batch":{"steps":[{"stmt":{"sql":"INSERT INTO t VALUES (99)"}}]}}""";
            var refused = await PostAsync(api, body, version, endpoint: endpoint);
            if (endpoint == "pipeline")
            {
                await AssertExpiredAsync(refused);
            }
            else
            {
                await AssertCursorExpiredAsync(refused);
            }
        }

        var original = await PostAsync(api, $$$"""{"baton":"{{{baton}}}","requests":[{"type":"get_autocommit"},{"type":"close"}]}""", version);
        await Assert.That(Types(original.Json)).IsEqualTo("ok:get_autocommit,ok:close");
        await AssertRowsAsync(api, version, "[]");
    }

    private static Task<DocumentApi> StartAsync(FakeTimeProvider time) => DocumentApi.StartAsync(services: services =>
        services.AddSingleton<IHranaStreams>(provider => new HranaStreams(
            provider.GetRequiredService<ISqlService>(), time, provider.GetRequiredService<ILogger<HranaStreams>>())));

    private static async Task<string> ReplaceAsync(DocumentApi api, string version, string state)
    {
        await PostAsync(api, CreateT, version);
        var before = await api.TenantOfAsync();
        var request = state switch
        {
            "stored" => """{"requests":[{"type":"store_sql","sql_id":1,"sql":"SELECT x FROM t"}]}""",
            "transaction" => """{"requests":[{"type":"execute","stmt":{"sql":"BEGIN"}},{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (2)"}}]}""",
            _ => """{"requests":[{"type":"get_autocommit"}]}""",
        };
        var opened = await PostAsync(api, request, version);
        var baton = opened.Json.GetProperty("baton").GetString()!;
        await Assert.That(opened.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(baton.Split('.')[1]).IsEqualTo(before.DatabaseId);
        await Assert.That(baton.EndsWith(".a", StringComparison.Ordinal)).IsEqualTo(state == "stateless");
        await api.DeleteDatabaseAsync();
        var created = await api.SendAsync(HttpMethod.Put, api.DatabasePath());
        await Assert.That(created.Status).IsEqualTo(HttpStatusCode.Created);
        var after = await api.TenantOfAsync();
        await Assert.That(after.DatabaseId).IsNotEqualTo(before.DatabaseId);
        await PostAsync(api, CreateT, version);
        var seeded = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"INSERT INTO t VALUES (7)"}},{"type":"close"}]}""", version);
        await Assert.That(Types(seeded.Json)).IsEqualTo("ok:execute,ok:close");
        return baton;
    }

    private static async Task AssertRowsAsync(DocumentApi api, string version, string expected)
    {
        var read = await PostAsync(api, """{"requests":[{"type":"execute","stmt":{"sql":"SELECT x FROM t ORDER BY x"}},{"type":"close"}]}""", version);
        await Assert.That(Types(read.Json)).IsEqualTo("ok:execute,ok:close");
        await Assert.That(Response(read.Json, 0).GetProperty("result").GetProperty("rows").GetRawText()).IsEqualTo(expected);
    }

    private static async Task AssertCursorExpiredAsync(ApiResult refused)
    {
        using var body = JsonDocument.Parse(refused.Raw);
        await Assert.That(refused.Status).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("STREAM_EXPIRED");
        await Assert.That(refused.ContentType).IsEqualTo("application/json");
        await Assert.That(refused.Raw).DoesNotContain("step_begin");
    }
}

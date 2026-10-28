using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Fast.Documents;
using TerraBase.Fast.Sql;
using TerraBase.Features.Management.Usage;
using TerraBase.Tenancy;
using static TerraBase.Fast.Hrana.HranaPipelineTests;

namespace TerraBase.Fast.Hrana;

public sealed class HranaDeadlineTests
{
    [Test]
    [Arguments("execute")]
    [Arguments("batch")]
    [Arguments("cursor")]
    [Arguments("sequence")]
    [Arguments("sequence-control")]
    public async Task Autocommit_statements_and_scripts_share_a_deadline_and_the_next_request_recovers(string shape)
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var trigger = new ReadClock(time);
        await using var api = await DocumentApi.StartAsync(time: time, services: services =>
            services.AddSingleton<IUsageMeter>(provider => new ClockedMeter(provider.GetRequiredService<UsageMeter>(), trigger)));
        await SqlApi.OkAsync(api, "CREATE TABLE t (x INTEGER PRIMARY KEY)", "INSERT INTO t VALUES (1)");
        trigger.Arm();
        const string read = "SELECT x FROM t";
        const string write = "INSERT INTO t VALUES (2)";
        var steps = $$$"""[{"stmt":{"sql":"{{{read}}}"}},{"stmt":{"sql":"{{{write}}}"}}]""";
        var body = shape switch
        {
            "batch" => $$$"""{"requests":[{"type":"batch","batch":{"steps":{{{steps}}}}}]}""",
            "cursor" => $$$"""{"batch":{"steps":{{{steps}}}}}""",
            "sequence-control" => $$$"""{"requests":[{"type":"sequence","sql":"BEGIN; {{{write}}}; {{{read}}}; COMMIT"}]}""",
            "sequence" => $$$"""{"requests":[{"type":"sequence","sql":"{{{read}}}; {{{write}}}"}]}""",
            _ => $$$"""{"requests":[{"type":"execute","stmt":{"sql":"{{{read}}}"}}, {"type":"execute","stmt":{"sql":"{{{write}}}"}}, {"type":"describe","sql":"SELECT 1"}, {"type":"sequence","sql":"SELECT 1"}]}""",
        };

        var response = await PostAsync(api, body, version: "v3", endpoint: shape == "cursor" ? "cursor" : "pipeline");

        await Assert.That(response.Status).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(response.Raw).Contains("SQLITE_INTERRUPT");
        if (shape == "execute")
        {
            await Assert.That(Types(response.Json)).IsEqualTo("ok:execute,error,error,error");
        }
        else if (shape == "batch")
        {
            await Assert.That(Response(response.Json, 0).GetProperty("result").GetProperty("step_errors")[1].GetProperty("code").GetString())
                .IsEqualTo("SQLITE_INTERRUPT");
        }
        else if (shape == "cursor")
        {
            await Assert.That(response.Raw).Contains("\"type\":\"step_end\"").And.Contains("\"type\":\"step_error\"");
        }

        var committed = await SqlApi.OkAsync(api, "SELECT count(*) FROM t");
        await Assert.That(committed[0].GetProperty("rows").GetRawText()).IsEqualTo("[[1]]");

        // Reuse the stream: the elapsed time from the preceding request must not poison its new deadline.
        using var head = System.Text.Json.JsonDocument.Parse(shape == "cursor" ? response.Raw.Split('\n')[0] : response.Raw);
        var baton = head.RootElement.GetProperty("baton").GetString();
        var recovered = await PostAsync(api, $$$"""{"baton":"{{{baton}}}","requests":[{"type":"get_autocommit"},{"type":"execute","stmt":{"sql":"ROLLBACK"}},{"type":"execute","stmt":{"sql":"SELECT count(*) FROM t"}},{"type":"close"}]}""", version: "v3");
        await Assert.That(Types(recovered.Json)).IsEqualTo("ok:get_autocommit,ok:execute,ok:execute,ok:close");
        await Assert.That(Response(recovered.Json, 0).GetProperty("is_autocommit").GetBoolean()).IsEqualTo(shape != "sequence-control");
        await Assert.That(Response(recovered.Json, 2).GetProperty("result").GetProperty("rows")[0][0].GetProperty("value").GetString()).IsEqualTo("1");
    }

    // Advance after the first real table read. SQL, storage and metering all remain the production implementations.
    private sealed class ReadClock(FakeTimeProvider time)
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public void AfterRead()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
            {
                time.Advance(TimeSpan.FromSeconds(6));
            }
        }
    }

    private sealed class ClockedMeter(IUsageMeter inner, ReadClock clock) : IUsageMeter
    {
        public void RecordRead(TenantContext database, long units)
        {
            inner.RecordRead(database, units);
            clock.AfterRead();
        }

        public void RecordWrites(TenantContext database, int writes, long units) => inner.RecordWrites(database, writes, units);

        public void RecordOperation(TenantContext database, string kind, int count) => inner.RecordOperation(database, kind, count);

        public void RecordRequest(TenantContext database, string method, int status, TimeSpan elapsed, long received, long sent) =>
            inner.RecordRequest(database, method, status, elapsed, received, sent);

        public void RecordStatement(TenantContext database, string protocol, bool failed, TimeSpan elapsed) =>
            inner.RecordStatement(database, protocol, failed, elapsed);

        public void RecordClientBytes(TenantContext database, long received, long sent, DateTimeOffset? transferredAt = null) =>
            inner.RecordClientBytes(database, received, sent, transferredAt);

        public IDisposable TrackActive(Action report) => inner.TrackActive(report);

        public void RecordReplicationBytes(TenantContext database, string peerRegion, string kind, bool sent, long bytes) =>
            inner.RecordReplicationBytes(database, peerRegion, kind, sent, bytes);

        public void RecordReplicatedOperations(TenantContext database, string peerRegion, int operations) =>
            inner.RecordReplicatedOperations(database, peerRegion, operations);

        public void RecordReplicationState(TenantContext database, string peerRegion, long remaining, bool diverged, double? convergenceMs = null) =>
            inner.RecordReplicationState(database, peerRegion, remaining, diverged, convergenceMs);
    }
}

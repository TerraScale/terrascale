using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseExportScheduleTests
{
    private const string Unsaved = """{"cron":null,"updatedAt":null,"lastRun":null}""";

    [Test]
    public async Task Schedule_calls_match_manifest_and_send_explicit_null_when_removing()
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("export-schedules.json"));
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var route = manifest.RootElement.GetProperty("operations")[calls == 0 ? 0 : 1];
            await Assert.That(request.Method.Method).IsEqualTo(route.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(route.GetProperty("path").GetString()!
                .Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal).Replace("{database}", "app", StringComparison.Ordinal));
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            if (calls++ == 0) return TerraBaseIntegrationFixture.Json(Unsaved);
            await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
            using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
            var cron = body.RootElement.GetProperty("cron");
            if (calls == 2) await Assert.That(cron.GetString()).IsEqualTo("0 3 * * *");
            else await Assert.That(cron.ValueKind).IsEqualTo(JsonValueKind.Null);
            return TerraBaseIntegrationFixture.Json(Unsaved);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        await Assert.That((await db.GetExportScheduleAsync()).LastRun).IsNull();
        await db.SetExportScheduleAsync("0 3 * * *");
        await db.SetExportScheduleAsync(null);
        await Assert.That(calls).IsEqualTo(3);
    }

    [Test]
    [Arguments("succeeded", "terrabase-exports/project/db/time.ndjson", 1234L, null)]
    [Arguments("failed", null, 0L, "the export could not be written to the customer bucket")]
    public async Task Run_outcomes_preserve_times_key_counts_and_fixed_error(string status, string? key, long bytes, string? error)
    {
        var run = JsonSerializer.Serialize(new { startedAt = "2026-10-24T03:00:00Z", finishedAt = "2026-10-24T03:00:01Z", status, key, documents = 14, bytes, error });
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(
            "{\"cron\":\"0 3 * * *\",\"updatedAt\":\"2026-10-24T02:00:00Z\",\"lastRun\":" + run + "}")));
        using var http = new HttpClient(handler);
        var result = await TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app").GetExportScheduleAsync();
        await Assert.That(result.LastRun!.Status).IsEqualTo(status);
        await Assert.That(result.LastRun.Key).IsEqualTo(key);
        await Assert.That(result.LastRun.Documents).IsEqualTo(14);
        await Assert.That(result.LastRun.Bytes).IsEqualTo(bytes);
        await Assert.That(result.LastRun.Error).IsEqualTo(error);
        await Assert.That(result.LastRun.FinishedAt - result.LastRun.StartedAt).IsEqualTo(TimeSpan.FromSeconds(1));
    }

    [Test]
    [Arguments("{}")]
    [Arguments("{\"cron\":null,\"lastRun\":null}")]
    [Arguments("{\"cron\":null,\"updatedAt\":null,\"lastRun\":{}}")]
    [Arguments("null")]
    public async Task Missing_response_fields_are_not_silently_defaulted(string body)
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(body)));
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraBaseException>(() =>
            TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app").GetExportScheduleAsync());
        await Assert.That(failure!.ErrorCode).IsEqualTo("invalid_response");
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, "invalid_request")]
    [Arguments(HttpStatusCode.Unauthorized, "unauthorized")]
    [Arguments(HttpStatusCode.Forbidden, "forbidden")]
    [Arguments(HttpStatusCode.NotFound, "not_found")]
    [Arguments(HttpStatusCode.Conflict, "conflict")]
    [Arguments(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task Refusals_are_preserved_and_mutations_are_not_replayed(HttpStatusCode status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"refused\"}}", status));
        });
        using var http = new HttpClient(handler);
        var failure = await Assert.ThrowsAsync<TerraBaseException>(() =>
            TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app").SetExportScheduleAsync("invalid"));
        await Assert.That(failure!.StatusCode).IsEqualTo(status);
        await Assert.That(failure.ErrorCode).IsEqualTo(code);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_is_honored_and_caller_http_client_remains_usable()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, token) =>
        {
            calls++;
            return calls == 1 ? WaitForCancellation(token) : Task.FromResult(TerraBaseIntegrationFixture.Json(Unsaved));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.GetExportScheduleAsync(canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.SetExportScheduleAsync(null, canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
        using var inflight = new CancellationTokenSource();
        var pending = db.GetExportScheduleAsync(inflight.Token);
        inflight.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await db.GetExportScheduleAsync();
        await Assert.That(calls).IsEqualTo(2);
    }

    private static async Task<HttpResponseMessage> WaitForCancellation(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        throw new InvalidOperationException();
    }
}

using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseFunctionCatalogTests
{
    private const string Function = """{"name":"hello","source":"function handler(event) { return event; }","trigger":{"collection":"items","events":["upsert"]},"cron":"* * * * *","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T01:00:00Z"}""";

    [Test]
    public async Task Catalog_matches_pinned_routes_and_roundtrips_replacement_fields()
    {
        using var manifest = JsonDocument.Parse(TerraBaseIntegrationFixture.ReadContract("functions.json"));
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            var route = manifest.RootElement.GetProperty("operations")[calls++];
            await Assert.That(request.Method.Method).IsEqualTo(route.GetProperty("method").GetString());
            await Assert.That(request.RequestUri!.AbsolutePath).IsEqualTo(route.GetProperty("path").GetString()!
                .Replace("{project}", TerraBaseIntegrationFixture.ProjectId, StringComparison.Ordinal)
                .Replace("{database}", "app", StringComparison.Ordinal).Replace("{function}", "hello", StringComparison.Ordinal));
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            await Assert.That(route.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!).ToArray())
                .IsEquivalentTo(new[] { "documents:read", "documents:write" });
            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);
            if (request.Method == HttpMethod.Put)
            {
                await Assert.That(request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(token));
                await Assert.That(body.RootElement.GetProperty("source").GetString()).IsEqualTo("function handler(event) { return event; }");
                await Assert.That(body.RootElement.GetProperty("trigger").GetProperty("events")[0].GetString()).IsEqualTo("upsert");
                await Assert.That(body.RootElement.GetProperty("cron").GetString()).IsEqualTo("* * * * *");
            }
            return TerraBaseIntegrationFixture.Json(calls == 1
                ? """[{"name":"hello","createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T01:00:00Z"}]""" : Function);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var listed = await db.ListFunctionsAsync();
        await Assert.That(listed.Single().Source).IsNull();
        var deployed = await db.DeployFunctionAsync("hello", new("function handler(event) { return event; }", new("items", ["upsert"]), "* * * * *"));
        await Assert.That(deployed.Trigger!.Collection).IsEqualTo("items");
        await Assert.That(deployed.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-10-26T00:00:00Z"));
        var fetched = await db.GetFunctionAsync("hello");
        await Assert.That(fetched.Source).IsEqualTo(deployed.Source);
        await db.DeleteFunctionAsync("hello");
        await Assert.That(calls).IsEqualTo(4);
    }

    [Test]
    public async Task Omitted_trigger_and_schedule_replace_previous_configuration()
    {
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            await Assert.That(body.RootElement.TryGetProperty("trigger", out _)).IsFalse();
            await Assert.That(body.RootElement.TryGetProperty("cron", out _)).IsFalse();
            return TerraBaseIntegrationFixture.Json("""{"name":"hello","source":"","trigger":null,"cron":null,"createdAt":"2026-10-26T00:00:00Z","updatedAt":"2026-10-26T01:00:00Z"}""");
        });
        using var http = new HttpClient(handler);
        var result = await TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app")
            .DeployFunctionAsync("hello", new(""));
        await Assert.That(result.Trigger).IsNull();
        await Assert.That(result.Cron).IsNull();
    }

    [Test]
    [Arguments(HttpStatusCode.BadRequest, "invalid_request")]
    [Arguments(HttpStatusCode.Forbidden, "forbidden_scope")]
    [Arguments(HttpStatusCode.NotFound, "not_found")]
    [Arguments(HttpStatusCode.Conflict, "function_limit")]
    [Arguments(HttpStatusCode.ServiceUnavailable, "unavailable")]
    public async Task Failures_preserve_status_and_code_without_replaying_mutations(HttpStatusCode status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"refused\"}}", status));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var deploy = await Assert.ThrowsAsync<TerraBaseException>(() => db.DeployFunctionAsync("hello", new("source")));
        await Assert.That(deploy!.StatusCode).IsEqualTo(status);
        await Assert.That(deploy.ErrorCode).IsEqualTo(code);
        if (status != HttpStatusCode.Conflict)
        {
            var delete = await Assert.ThrowsAsync<TerraBaseException>(() => db.DeleteFunctionAsync("hello"));
            await Assert.That(delete!.ErrorCode).IsEqualTo(code);
        }
        await Assert.That(calls).IsEqualTo(status == HttpStatusCode.Conflict ? 1 : 2);
    }

    [Test]
    public async Task Cancellation_reaches_an_inflight_deployment_without_replay()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var handler = new TerraBaseTestHandler(async (_, token) =>
        {
            calls++;
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TerraBaseIntegrationFixture.Json(Function);
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var cancellation = new CancellationTokenSource();
        var deployment = db.DeployFunctionAsync("hello", new("source"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => deployment);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Catalog_honors_precancellation_and_keeps_the_caller_client_alive()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("[]"));
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ListFunctionsAsync(canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.GetFunctionAsync("hello", canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.DeployFunctionAsync("hello", new("source"), canceled.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.DeleteFunctionAsync("hello", canceled.Token));
        await Assert.That(calls).IsEqualTo(0);
        await db.ListFunctionsAsync();
        await Assert.That(calls).IsEqualTo(1);
    }
}

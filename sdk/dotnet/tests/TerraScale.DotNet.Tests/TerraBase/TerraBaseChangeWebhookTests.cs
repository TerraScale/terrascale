using System.Net;
using System.Text.Json;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseChangeWebhookTests
{
    private const string HookId = "01JA0000000000000000000003";
    private const string DeliveryId = "01JA0000000000000000000004";
    private const string Hook = """{"id":"01JA0000000000000000000003","url":"https://receiver.example/events","createdAt":"2026-10-23T00:00:00Z","status":"active"}""";
    private const string Secret = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static string BasePath => $"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/databases/app/collections/items/webhooks";

    [Test]
    public async Task Routes_fields_pagination_and_creation_only_secret_match_contract()
    {
        var calls = new List<(string Method, string Path, string Query, string? Body)>();
        using var handler = new TerraBaseTestHandler(async (request, token) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
            await Assert.That(request.Headers.Authorization!.ToString()).IsEqualTo("Bearer project-user-token");
            await Assert.That(request.Headers.Contains("Idempotency-Key")).IsFalse();
            if (request.Method == HttpMethod.Post)
                return TerraBaseIntegrationFixture.Json(Hook[..^1] + ",\"secret\":\"" + Secret + "\"}", HttpStatusCode.Created);
            if (request.Method == HttpMethod.Delete) return new(HttpStatusCode.NoContent);
            if (request.RequestUri.AbsolutePath.EndsWith("/deliveries", StringComparison.Ordinal))
                return TerraBaseIntegrationFixture.Json("""[{"id":"01JA0000000000000000000004","createdAt":"2026-10-23T00:00:01Z","status":"pending","attempts":2147483648,"lastAttemptAt":null,"nextAttemptAt":"2026-10-23T00:00:02Z","httpStatus":503,"errorCode":"http_error"},{"id":"01JA0000000000000000000005","createdAt":"2026-10-23T00:00:01Z","status":"delivered","attempts":1,"lastAttemptAt":"2026-10-23T00:00:02Z","nextAttemptAt":null,"httpStatus":null,"errorCode":null}]""");
            return TerraBaseIntegrationFixture.Json(request.RequestUri.Query.Length == 0 ? "[]" : "[" + Hook.Replace("active", "feed_gap", StringComparison.Ordinal) + "]");
        });
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var created = await db.CreateChangeWebhookAsync("items", "https://receiver.example/events");
        var hooks = await db.ListChangeWebhooksAsync("items", HookId, 2);
        var deliveries = await db.ListChangeWebhookDeliveriesAsync("items", HookId, DeliveryId, 100);
        await db.DeleteChangeWebhookAsync("items", HookId);
        await Assert.That(await db.ListChangeWebhooksAsync("items")).IsEmpty();
        await Assert.That(created.Secret).IsEqualTo(Secret);
        await Assert.That(created.ToString().Contains(Secret, StringComparison.Ordinal)).IsFalse();
        await Assert.That(JsonSerializer.Serialize(hooks, TerraBaseJsonContext.Wire.IReadOnlyListTerraBaseChangeWebhook).Contains("secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(hooks[0].Status).IsEqualTo("feed_gap");
        await Assert.That(created.CreatedAt).IsEqualTo(DateTimeOffset.Parse("2026-10-23T00:00:00Z"));
        await Assert.That(deliveries[0].Attempts).IsEqualTo(2147483648L);
        await Assert.That(deliveries[0].LastAttemptAt).IsNull();
        await Assert.That(deliveries[0].HttpStatus).IsEqualTo(503);
        await Assert.That(deliveries[0].ErrorCode).IsEqualTo("http_error");
        await Assert.That(deliveries[1].NextAttemptAt).IsNull();
        await Assert.That(deliveries[1].HttpStatus).IsNull();
        await Assert.That(deliveries[1].ErrorCode).IsNull();
        await Assert.That(calls[0]).IsEqualTo(("POST", BasePath, "", "{\"url\":\"https://receiver.example/events\"}"));
        await Assert.That(calls[1].Path).IsEqualTo(BasePath);
        await Assert.That(calls[1].Query).IsEqualTo($"?after={HookId}&limit=2");
        await Assert.That(calls[2].Path).IsEqualTo(BasePath + "/" + HookId + "/deliveries");
        await Assert.That(calls[2].Query).IsEqualTo($"?after={DeliveryId}&limit=100");
        await Assert.That(calls[3].Method).IsEqualTo("DELETE");
        await Assert.That(calls[3].Path).IsEqualTo(BasePath + "/" + HookId);
    }

    [Test]
    [Arguments("wh_01JA0000000000000000000003")]
    [Arguments("01ja0000000000000000000003")]
    [Arguments("81JA0000000000000000000003")]
    [Arguments("01JA000000000000000000000I")]
    public async Task Invalid_webhook_ids_and_cursors_fail_before_transport(string id)
    {
        using var handler = new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("Must not send"));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        await Assert.ThrowsAsync<ArgumentException>(() => db.DeleteChangeWebhookAsync("items", id));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ListChangeWebhookDeliveriesAsync("items", id));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ListChangeWebhookDeliveriesAsync("items", HookId, id));
        await Assert.ThrowsAsync<ArgumentException>(() => db.ListChangeWebhooksAsync("items", id));
    }

    [Test]
    public async Task Invalid_url_and_page_limits_fail_before_transport()
    {
        using var http = new HttpClient(new TerraBaseTestHandler((_, _) => throw new InvalidOperationException("Must not send")));
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        foreach (var url in new[] { "http://receiver.example", "https://user:pass@receiver.example", "https://receiver.example/#fragment", "https://receiver.example:444", new string('a', 2049) })
            await Assert.ThrowsAsync<ArgumentException>(() => db.CreateChangeWebhookAsync("items", url));
        foreach (var limit in new[] { 0, 101 })
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.ListChangeWebhooksAsync("items", limit: limit));
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => db.ListChangeWebhookDeliveriesAsync("items", HookId, limit: limit));
        }
    }

    [Test]
    [Arguments(400, "invalid_request")]
    [Arguments(401, "invalid_key")]
    [Arguments(403, "forbidden_scope")]
    [Arguments(404, "not_found")]
    [Arguments(409, "webhook_limit")]
    [Arguments(503, "storage_unavailable")]
    public async Task Errors_preserve_status_and_code_without_replay(int status, string code)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json($$$"""{"error":{"code":"{{{code}}}","message":"refused"}}""", (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => TerraBaseIntegrationFixture.Client(http)
            .Database(TerraBaseIntegrationFixture.ProjectId, "app").CreateChangeWebhookAsync("items", "https://receiver.example/events"));
        await Assert.That(error!.ErrorCode).IsEqualTo(code);
        await Assert.That(error.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    public async Task Cancellation_and_malformed_response_follow_transport_conventions()
    {
        using var handler = new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json("[" + Hook.Replace(HookId, "wh_" + HookId, StringComparison.Ordinal) + "]")));
        using var http = new HttpClient(handler);
        var db = TerraBaseIntegrationFixture.Client(http).Database(TerraBaseIntegrationFixture.ProjectId, "app");
        var error = await Assert.ThrowsAsync<TerraBaseException>(() => db.ListChangeWebhooksAsync("items"));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_response");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.ListChangeWebhooksAsync("items", cancellationToken: cancellation.Token));
    }
}

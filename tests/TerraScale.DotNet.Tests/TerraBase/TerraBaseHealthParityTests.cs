using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseHealthParityTests
{
    [Test]
    [Arguments("/healthz", 200, "ok")]
    [Arguments("/readyz", 200, "ready")]
    [Arguments("/backupz", 200, "shipping")]
    [Arguments("/healthz", 503, "unavailable")] // Generic decoder robustness; producer liveness declares 200.
    [Arguments("/readyz", 503, "unavailable")]
    [Arguments("/backupz", 503, "unavailable")]
    public async Task Anonymous_public_probes_require_only_public_fields_and_preserve_status(string path, int status, string state)
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls++;
            if (request.Method != HttpMethod.Get || request.RequestUri!.AbsolutePath != path || request.Content is not null
                || request.Headers.Contains("Authorization") || request.Headers.Contains("Cookie"))
                throw new InvalidOperationException("Unexpected public probe request.");
            return Task.FromResult(TerraBaseIntegrationFixture.Json(PublicBody(state), (HttpStatusCode)status));
        });
        using var http = new HttpClient(handler);
        var result = await Read(new TerraBaseHealthClient(http, new Uri("https://api.example.test")), path);
        await Assert.That(result.StatusCode).IsEqualTo((HttpStatusCode)status);
        await Assert.That(result.Body).IsEqualTo(new TerraBaseStatus(state, "0.0.1", "gru"));
        await Assert.That(calls).IsEqualTo(1);
    }

    [Test]
    [Arguments("/healthz", 200)]
    [Arguments("/readyz", 200)]
    [Arguments("/backupz", 200)]
    [Arguments("/healthz", 503)]
    [Arguments("/readyz", 503)]
    [Arguments("/backupz", 503)]
    public async Task Missing_null_wrong_type_and_malformed_public_fields_are_safe_errors(string path, int status)
    {
        var payloads = new List<string> { "{", "null", "[]", JsonSerializer.Serialize("synthetic-private") };
        foreach (var field in new[] { "status", "version", "region" })
            foreach (var invalid in new[] { "missing", "null", "number" })
            {
                var body = JsonNode.Parse(PublicBody("ok"))!.AsObject();
                body["unknown"] = "synthetic-private";
                if (invalid == "missing") body.Remove(field);
                else if (invalid == "null") body[field] = null;
                else body[field] = 123;
                payloads.Add(body.ToJsonString());
            }
        foreach (var payload in payloads)
        {
            var calls = 0;
            using var http = new HttpClient(new TerraBaseTestHandler((_, _) =>
            { calls++; return Task.FromResult(TerraBaseIntegrationFixture.Json(payload, (HttpStatusCode)status)); }));
            var error = await Assert.ThrowsAsync<TerraBaseException>(() => Read(new TerraBaseHealthClient(http, new Uri("https://api.example.test")), path));
            await Assert.That(error!.StatusCode).IsEqualTo((HttpStatusCode)status);
            await Assert.That(error.ErrorCode).IsEqualTo("invalid_response");
            await Assert.That(error.Message).IsEqualTo("TerraBase returned an invalid response body.");
            await Assert.That(error.ToString()).DoesNotContain("synthetic-private");
            await Assert.That(error.InnerException).IsNull();
            await Assert.That(calls).IsEqualTo(1);
        }
    }

    [Test]
    [Arguments("/healthz")]
    [Arguments("/readyz")]
    [Arguments("/backupz")]
    public async Task Unknown_internal_metadata_is_ignored_and_not_exposed(string path)
    {
        var state = path == "/healthz" ? "ok" : path == "/readyz" ? "ready" : "shipping";
        var body = JsonNode.Parse(PublicBody(state))!.AsObject();
        body["storageFormat"] = new JsonObject { ["private"] = "synthetic-private" };
        body["revision"] = false;
        body["machine"] = "synthetic-private";
        using var http = new HttpClient(new TerraBaseTestHandler((_, _) => Task.FromResult(TerraBaseIntegrationFixture.Json(body.ToJsonString()))));
        var result = await Read(new TerraBaseHealthClient(http, new Uri("https://api.example.test")), path);
        await Assert.That(result.Body).IsEqualTo(new TerraBaseStatus(state, "0.0.1", "gru"));
        var serialized = JsonSerializer.Serialize(result.Body, TerraBaseJsonContext.Wire.TerraBaseStatus);
        using var publicBody = JsonDocument.Parse(serialized);
        await Assert.That(publicBody.RootElement.EnumerateObject().Select(field => field.Name).ToArray()).IsEquivalentTo(["status", "version", "region"]);
        await Assert.That(serialized).DoesNotContain("synthetic-private");
        await Assert.That(result.Body.ToString()).DoesNotContain("synthetic-private");
    }

    private static string PublicBody(string status) => JsonSerializer.Serialize(new { status, version = "0.0.1", region = "gru" });
    private static Task<TerraBaseProbeResult> Read(TerraBaseHealthClient client, string path) => path switch
    {
        "/healthz" => client.GetHealthAsync(),
        "/readyz" => client.GetReadinessAsync(),
        "/backupz" => client.GetBackupHealthAsync(),
        _ => throw new ArgumentException("Unknown probe path.", nameof(path))
    };

    [Test]
    public async Task Replication_degraded_status_and_unknown_lag_are_not_replaced_with_success_or_zero()
    {
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath != "/health/replication") throw new InvalidOperationException("wrong route");
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"region":"gru","status":"degraded","maxLagSeconds":null,"peers":[{"region":"fra","status":"degraded","lagSeconds":null}]}"""));
        });
        using var http = new HttpClient(handler);
        var health = await new TerraBaseHealthClient(http, new Uri("https://api.example.test")).GetReplicationHealthAsync();
        await Assert.That(health.Status).IsEqualTo("degraded");
        await Assert.That(health.MaxLagSeconds).IsNull();
        await Assert.That(health.Peers.Single().LagSeconds).IsNull();
    }

    [Test]
    public async Task Unexpected_probe_error_still_surfaces_and_is_sent_once()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"error":{"code":"misdirected_request","message":"wrong region"}}""", HttpStatusCode.MisdirectedRequest));
        });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<TerraBaseWrongRegionException>(() => new TerraBaseHealthClient(http, new Uri("https://api.example.test")).GetReadinessAsync());
        await Assert.That(calls).IsEqualTo(1);
    }
}

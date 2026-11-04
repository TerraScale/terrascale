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

    private static string PublicBody(string status) => JsonSerializer.Serialize(new { status, version = "0.0.1", region = "gru" });
    private static Task<TerraBaseProbeResult> Read(TerraBaseHealthClient client, string path) => path switch
    {
        "/healthz" => client.GetHealthAsync(),
        "/readyz" => client.GetReadinessAsync(),
        "/backupz" => client.GetBackupHealthAsync(),
        _ => throw new ArgumentException("Unknown probe path.", nameof(path))
    };

}

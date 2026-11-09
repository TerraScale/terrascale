using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using TerraScale.Authentication.Features.Observability;
using TerraScale.Authentication.Storage;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Platform;

public sealed class HealthTests
{
    [Test]
    public async Task Liveness_reports_ok_and_the_service_version()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/healthz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(body.GetProperty("version").GetString()).StartsWith("0.1.0-alpha");
        // Anonymous health never names the build revision (ServiceVersion.Value carries it as +<commit> in CI builds).
        await Assert.That(body.GetProperty("version").GetString()).DoesNotContain("+");
    }

    [Test]
    public async Task Readiness_fails_closed_when_storage_is_not_reachable()
    {
        await using var server = await AuthTestServer.StartAsync();

        using var response = await server.Client.GetAsync("/readyz");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("not_ready");
        await Assert.That(body.TryGetProperty("region", out _)).IsFalse();
    }
}

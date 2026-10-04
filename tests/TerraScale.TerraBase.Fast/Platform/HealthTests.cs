using System.Net;
using System.Text.Json;
using TerraScale.TerraBase.Testing;

namespace TerraScale.TerraBase.Fast.Platform;

public sealed class HealthTests
{
    [Test]
    public async Task Healthz_reports_ok_and_the_running_version()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        using var response = await server.Client.GetAsync(new Uri("/healthz", UriKind.Relative));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
        await Assert.That(body.RootElement.GetProperty("version").GetString()).IsEqualTo("0.1.0");
    }

    [Test]
    public async Task Admin_listener_binds_loopback_only()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        await Assert.That(IPAddress.IsLoopback(IPAddress.Parse(server.BaseAddress.Host))).IsTrue();
    }
}

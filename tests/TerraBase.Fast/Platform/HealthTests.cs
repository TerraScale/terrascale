using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Turso;
using TerraBase.Hosting;
using TerraBase.Testing;
using TUnit.Core.Enums;

namespace TerraBase.Fast.Platform;

public sealed class HealthTests
{
    [Test]
    public async Task Healthz_reports_ok_and_the_running_version()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        await AssertProbeAsync(server.Client, "/healthz", HttpStatusCode.OK, "ok");
    }

    [Test]
    public async Task Admin_listener_binds_loopback_only()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        await Assert.That(IPAddress.IsLoopback(IPAddress.Parse(server.BaseAddress.Host))).IsTrue();
    }

    [Test]
    public async Task Probes_answer_on_the_client_listener_without_an_api_key()
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);

        await AssertProbeAsync(client, "/healthz", HttpStatusCode.OK, "ok");
        await AssertProbeAsync(client, "/readyz", HttpStatusCode.OK, "ready");
    }

    [Test]
    [Arguments("/healthz")]
    [Arguments("/readyz")]
    public async Task Client_probes_enforce_region_pins_without_an_api_key(string path)
    {
        using var tls = new ClientTls();
        await using var server = await TerraBaseTestServer.StartAsync(tls.Apply);
        using var client = tls.CreateClient(server.ClientAddress!);
        client.DefaultRequestHeaders.Add("X-TerraScale-Region-Pin", "test-region");
        await AssertProbeAsync(client, path, HttpStatusCode.OK, path == "/healthz" ? "ok" : "ready");

        client.DefaultRequestHeaders.Remove("X-TerraScale-Region-Pin");
        client.DefaultRequestHeaders.Add("X-TerraScale-Region-Pin", "other-region");
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.MisdirectedRequest);
        await Assert.That(response.Headers.GetValues("X-TerraBase-Region").Single()).IsEqualTo("test-region");
    }

    [Test]
    public async Task Readyz_is_ready_on_the_admin_listener()
    {
        await using var server = await TerraBaseTestServer.StartAsync();

        await AssertProbeAsync(server.Client, "/readyz", HttpStatusCode.OK, "ready");
    }

    private static async Task AssertProbeAsync(HttpClient client, string path, HttpStatusCode status, string state)
    {
        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var anonymous = response.Headers.Contains("X-TerraBase-Region");

        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(body.RootElement.GetProperty("status").GetString()).IsEqualTo(state);
        await Assert.That(body.RootElement.GetProperty("version").GetString()).IsEqualTo("0.1.0");
        await Assert.That(body.RootElement.GetProperty("region").GetString()).IsEqualTo("test-region");
        // The source commit the build was made from: the checkout's HEAD here, TERRABASE_REVISION in a CI image.
        await Assert.That(ServiceVersion.Revision).Matches("^[0-9a-f]{40}$");
        if (anonymous)
        {
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["status", "version", "region"]);
            await Assert.That(text.Contains(ServiceVersion.Revision!, StringComparison.Ordinal)).IsFalse();
        }
        else
        {
            await Assert.That(body.RootElement.EnumerateObject().Select(property => property.Name).ToList()).IsEquivalentTo(["status", "version", "region", "storageFormat", "revision", "machine"]);
            await Assert.That(body.RootElement.GetProperty("revision").GetString()).IsEqualTo(ServiceVersion.Revision);
            await Assert.That(body.RootElement.GetProperty("machine").GetString()).IsEqualTo("local");
            await Assert.That(body.RootElement.GetProperty("storageFormat").GetString()).IsEqualTo(StorageFormat.Id);
        }

        if (client.BaseAddress?.Scheme == Uri.UriSchemeHttps)
        {
            await Assert.That(response.Headers.GetValues("X-TerraBase-Region").Single()).IsEqualTo("test-region");
        }
    }
}

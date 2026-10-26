using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Platform;

// The built production program under ts-orch (ts-orchestrator M1): Production, TS_DASHBOARD_TERRABASE_URL for the state
// listener, ASPNETCORE_URLS for the listen address and a loopback TLS edge that forwards the scheme and client IP.
[ClassDataSource<DashboardRootFixture>]
public sealed class OciProductionJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    public async Task ProductionServesFromTheConfiguredTerraBaseOnTheConfiguredUrl()
    {
        var peerSecret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var credentialed = 0;
        var ids = new TerraScaleIdGenerator();
        var terraBasePort = FreePort();
        var builder = WebApplication.CreateBuilder();
        // The private listener speaks HTTP/2 with prior knowledge, like TerraBase's.
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, terraBasePort, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Logging.ClearProviders();
        await using var terraBase = builder.Build();
        // Test-only HTTP fake of TerraBase's private listener: every seeded collection exists.
        terraBase.MapGet("/v1/projects/{project}/databases/ts-dashboard/collections", (HttpContext http) =>
        {
            if (http.Request.Headers["X-TerraBase-Peer-Credential"] == peerSecret) Interlocked.Increment(ref credentialed);
            return Results.Json(new
            {
                items = DashboardStateSchema.Collections.Select(name => new
                {
                    id = ids.NewId(),
                    name,
                    created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                }),
                next_cursor = (string?)null
            });
        });
        terraBase.MapGet("/v1/projects/{project}/databases/ts-dashboard/collections/{name}", (HttpContext http, string name) =>
        {
            if (http.Request.Headers["X-TerraBase-Peer-Credential"] == peerSecret) Interlocked.Increment(ref credentialed);
            return Results.Json(new { id = ids.NewId(), name, created_at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) });
        });
        await terraBase.StartAsync();

        var port = FreePort();
        using var process = Start(port, $"http://127.0.0.1:{terraBasePort}", peerSecret);
        try
        {
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(10) };
            var log = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, line) => { lock (log) log.AppendLine(line.Data); };
            process.ErrorDataReceived += (_, line) => { lock (log) log.AppendLine(line.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // Readiness also probes the real ts-auth, which this hermetic journey does not require: it waits for the
            // configured TerraBase to read as ready in the body, whatever ts-auth answers.
            string? terraBaseReady = null, region = null;
            for (var attempt = 0; attempt < 120 && terraBaseReady != "ok"; attempt++)
            {
                if (process.HasExited) break;
                try
                {
                    using var response = await client.GetAsync("/health/ready");
                    using var health = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    terraBaseReady = health.RootElement.GetProperty("dependencies").GetProperty("terrabase").GetString();
                    region = health.RootElement.GetProperty("region").GetString();
                }
                catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException) { }
                if (terraBaseReady != "ok") await Task.Delay(500);
            }
            string output;
            lock (log) output = log.ToString();
            await Assert.That(terraBaseReady).IsEqualTo("ok").Because("Production under ts-orch reads its TerraBase state on ASPNETCORE_URLS: " + output);
            await Assert.That(credentialed).IsGreaterThan(0).Because("State requests reach the configured TerraBase with the peer credential.");
            await Assert.That(region).IsEqualTo("gru").Because("Readiness names the region ts-orch sets in TS_REGION.");

            // The loopback edge terminates TLS: its X-Forwarded-Proto makes cookies Secure.
            using var behindEdge = new HttpRequestMessage(HttpMethod.Get, "/login");
            behindEdge.Headers.Add("X-Forwarded-Proto", "https");
            behindEdge.Headers.Add("X-Forwarded-For", "203.0.113.7");
            using var login = await client.SendAsync(behindEdge);
            var cookies = login.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToArray() : [];
            await Assert.That(cookies).IsNotEmpty().Because("The sign-in page issues its CSRF cookie.");
            await Assert.That(cookies.All(cookie => cookie.Contains("secure", StringComparison.OrdinalIgnoreCase))).IsTrue()
                .Because("Behind the edge every cookie is Secure: " + string.Join(" | ", cookies));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
    }

    private Process Start(int port, string terraBase, string peerSecret)
    {
        var project = Path.Combine(fixture.Root, "src", "TerraScale.Dashboard");
        var application = Path.Combine(project, "bin", "Release", "net10.0", "TerraScale.Dashboard.dll");
        if (!File.Exists(application)) throw new FileNotFoundException("Build the Release dashboard with the E2E suite.", application);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = project, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(application);
        foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("TS_", StringComparison.Ordinal) ||
                     name.StartsWith("STRIPE_", StringComparison.Ordinal) ||
                     name.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || name.StartsWith("DOTNET_ENVIRONMENT", StringComparison.Ordinal) ||
                     name == "DEMO" || name == "TERRABASE_PEER_SECRET").ToArray())
            start.Environment.Remove(name);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        // The image sets ASPNETCORE_HTTP_PORTS=8080; ASPNETCORE_URLS takes precedence over it.
        start.Environment["ASPNETCORE_HTTP_PORTS"] = "8080";
        start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        start.Environment["TS_DASHBOARD_TERRABASE_URL"] = terraBase;
        start.Environment["TS_REGION"] = "gru";
        // Format-valid test-only identity; readiness contacts ts-auth's public /readyz only, never Stripe.
        start.Environment["TS_AUTH_DASHBOARD_CLIENT_SECRET"] = new string('a', 48);
        start.Environment["TS_DASHBOARD_SESSION_ENCRYPTION_KEY"] = Convert.ToBase64String(new byte[32]);
        start.Environment["STRIPE_SECRET_KEY"] = "sk_test_ocijourney";
        start.Environment["STRIPE_WEBHOOK_SECRET"] = "whsec_ocijourney";
        start.Environment["TERRABASE_PEER_SECRET"] = peerSecret;
        return Process.Start(start) ?? throw new IOException("Could not launch the dashboard program.");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

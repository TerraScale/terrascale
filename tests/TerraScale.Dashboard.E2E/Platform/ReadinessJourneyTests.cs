using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace TerraScale.Dashboard.E2E.Platform
{
    // ts-orch probes /readyz and ts-status probes /health/ready: both must fail, naming the dependency, when a signed-in
    // request would fail, and neither may publish the build revision.
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ReadinessJourneyTests(DashboardRootFixture fixture)
    {
        private static readonly string[] ReadyPaths = ["/readyz", "/health/ready"];

        [Test]
        public async Task TsAuthOutOfServiceFailsReadinessNamingTsAuth()
        {
            var environment = new DemoEnvironment();
            environment.ApiKeys.Ready = false;
            await using var app = await StartAsync(environment);
            using var client = Client(app);
            foreach (var path in ReadyPaths)
            {
                var (status, body) = await GetAsync(client, path);
                await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable).Because(path + " fails while ts-auth is not ready.");
                await Assert.That(body.GetProperty("status").GetString()).IsEqualTo("unavailable");
                await Assert.That(body.GetProperty("region").GetString()).IsEqualTo("gru");
                await Assert.That(body.GetProperty("dependencies").GetProperty("ts-auth").GetString()).IsEqualTo("unavailable");
                await Assert.That(body.GetProperty("dependencies").GetProperty("terrabase").GetString()).IsEqualTo("ok");
            }
        }

        [Test]
        public async Task TerraBaseOutOfServiceFailsReadinessNamingTerraBase()
        {
            var environment = new DemoEnvironment();
            environment.State.ReadyError = new IOException("Test-only TerraBase outage.");
            await using var app = await StartAsync(environment);
            using var client = Client(app);
            var (status, body) = await GetAsync(client, "/readyz");
            await Assert.That(status).IsEqualTo(HttpStatusCode.ServiceUnavailable);
            await Assert.That(body.GetProperty("dependencies").GetProperty("terrabase").GetString()).IsEqualTo("unavailable");
            await Assert.That(body.GetProperty("dependencies").GetProperty("ts-auth").GetString()).IsEqualTo("ok");
        }

        [Test]
        public async Task ReadyBodiesNameTheRegionAndNeverTheBuildRevision()
        {
            await using var app = await StartAsync(new DemoEnvironment());
            using var client = Client(app);
            foreach (var path in ReadyPaths.Append("/health/live"))
            {
                using var response = await client.GetAsync(path);
                var text = await response.Content.ReadAsStringAsync();
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path + ": " + text);
                await Assert.That(text).DoesNotMatch("[0-9a-f]{40}").Because(path + " must not publish the commit.");
                using var json = JsonDocument.Parse(text);
                await Assert.That(json.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
                await Assert.That(json.RootElement.GetProperty("region").GetString()).IsEqualTo("gru");
                await Assert.That(json.RootElement.TryGetProperty("version", out _)).IsFalse();
            }
        }

        private async Task<WebApplication> StartAsync(DemoEnvironment environment)
        {
            await environment.InitializeAsync();
            // ts-orch sets TS_REGION in the environment; per-host configuration keeps parallel hosts apart.
            var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder => builder.Configuration["TS_REGION"] = "gru");
            await app.StartAsync();
            return app;
        }

        private static HttpClient Client(WebApplication app)
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate }) { BaseAddress = new Uri(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(30) };
        }

        private static async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(HttpClient client, string path)
        {
            using var response = await client.GetAsync(path);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (response.StatusCode, json.RootElement.Clone());
        }
    }
}

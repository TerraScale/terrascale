using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;

namespace TerraScale.Dashboard.E2E.Platform
{
    // ts-orch probes /readyz and ts-status probes /health/ready: both must fail, naming the dependency, when a signed-in
    // request would fail, and each body carries the build revision it reports through X-TerraScale-Revision.
    [ClassDataSource<SharedDemoEnvironment>(Shared = SharedType.PerTestSession)]
    public sealed class ReadinessJourneyTests(SharedDemoEnvironment shared)
    {
        private static readonly string[] ReadyPaths = ["/readyz", "/health/ready"];

        [Test]
        public async Task TsAuthOutOfServiceFailsReadinessNamingTsAuth()
        {
            var environment = new DemoEnvironment();
            environment.ApiKeys.Ready = false;
            await environment.InitializeAsync();
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
        public async Task ReadyBodiesNameTheRegionAndTheBuildRevision()
        {
            await using var app = await StartAsync(shared.Environment);
            using var client = Client(app);
            string? revision = null;
            foreach (var path in ReadyPaths.Append("/health/live"))
            {
                using var response = await client.GetAsync(path);
                var text = await response.Content.ReadAsStringAsync();
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(path + ": " + text);
                using var json = JsonDocument.Parse(text);
                await Assert.That(json.RootElement.GetProperty("status").GetString()).IsEqualTo("ok");
                await Assert.That(json.RootElement.GetProperty("region").GetString()).IsEqualTo("gru");
                var version = json.RootElement.GetProperty("version").GetString();
                await Assert.That(version).IsNotNull().Because(path + " names its build revision.");
                await Assert.That(version).Matches("[0-9a-f]{40}|unknown").Because(path + " carries the commit sha or the local fallback.");
                revision ??= response.Headers.GetValues("X-TerraScale-Revision").Single();
                await Assert.That(version).IsEqualTo(revision).Because(path + " reports the same revision as the header.");
            }
        }

        private async Task<WebApplication> StartAsync(DemoEnvironment environment)
        {
            // ts-orch sets TS_REGION in the environment; per-host configuration keeps parallel hosts apart.
            var app = await environment.CreateTestHostAsync(shared.Root, 0, "http://127.0.0.1", builder => builder.Configuration["TS_REGION"] = "gru");
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

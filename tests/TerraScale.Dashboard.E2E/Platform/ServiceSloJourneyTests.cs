using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Home;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ServiceSloJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task PrivateCountersInitializePerHostAndCountTerminalOutcomesOnce()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var measurements = new FailingMeasurements();
            await using var first = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1", builder =>
                builder.Services.Replace(ServiceDescriptor.Singleton<IHomeMeasurements>(measurements)));
            await using var second = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
            first.MapGet("/api/slo/status/{status:int}", (int status) => Results.StatusCode(status));
            first.MapGet("/api/slo/exception", (Func<IResult>)(() => throw new InvalidOperationException("test failure")));
            first.MapGet("/api/slo/abort", (HttpContext http) => http.Abort());
            var callerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            first.MapGet("/api/slo/disconnect", async (HttpContext http) =>
            {
                callerEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite, http.RequestAborted);
            });
            first.MapGet("/api/slo/cancel", (HttpContext http) =>
            {
                http.RequestAborted = new CancellationToken(true);
                throw new OperationCanceledException(http.RequestAborted);
            });
            await first.StartAsync();
            await second.StartAsync();
            using var client = Client(first);
            using var replica = Client(second);
            await CountsAsync(client, 0, 0);
            await CountsAsync(replica, 0, 0);
            foreach (var path in new[] { "/healthz", "/readyz", "/health/ready", "/health/live", "/css/terrascale.css" })
            { using var probe = await client.GetAsync(path); }
            await CountsAsync(client, 0, 0);
            foreach (var header in new[] { "Host", "Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/metrics");
                request.Headers.Add(header, header == "Host" ? "dashboard.terrascale.tech" : header == "X-Forwarded-Proto" ? "https" : "127.0.0.1");
                using var response = await client.SendAsync(request);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("terrascale_slo_requests_total");
            }
            await CountsAsync(client, 0, 0);
            foreach (var status in new[] { 200, 400, 401, 403, 404, 429 })
            { using var response = await client.GetAsync("/api/slo/status/" + status); }
            await CountsAsync(client, 6, 0);
            foreach (var status in new[] { 500, 503, 408 })
            { using var response = await client.GetAsync("/api/slo/status/" + status); }
            using (var response = await client.GetAsync("/api/slo/exception"))
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            }

            try { using var response = await client.GetAsync("/api/slo/cancel"); }
            catch (HttpRequestException) { }
            await CountsAsync(client, 6, 5);
            try { using var response = await client.GetAsync("/api/slo/abort"); }
            catch (HttpRequestException) { }
            await CountsAsync(client, 6, 6);
            using (var caller = new CancellationTokenSource())
            {
                var pending = client.GetAsync("/api/slo/disconnect", caller.Token);
                await callerEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await caller.CancelAsync();
                try { using var response = await pending; }
                catch (OperationCanceledException) { }
            }
            // We know the cause from this client, but Kestrel exposes the same RequestAborted
            // token for a peer disconnect and service Abort(). Neither is excluded without proof.
            await CountsAsync(client, 6, 7);
            await CountsAsync(replica, 0, 0);

            // Only the test identity provider is used; no external identity or secret is needed.
            using var login = await HttpJourney.LoginAsync(client);
            await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            var token = await HttpJourney.CreatePatAsync(client);
            var before = await ReadAsync(client);
            measurements.Status = 403;
            var refused = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "get_overview", arguments = new { } });
            await Assert.That(refused.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await CountsAsync(client, before.Good + 1, before.Bad);
            measurements.Status = 503;
            var failed = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "get_overview", arguments = new { } });
            await Assert.That(failed.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await CountsAsync(client, before.Good + 1, before.Bad + 1);
            measurements.Status = 500;
            var unexpected = await HttpJourney.McpAsync(client, token, "tools/call", new { name = "get_overview", arguments = new { } });
            await Assert.That(unexpected.GetProperty("result").GetProperty("isError").GetBoolean()).IsTrue();
            await CountsAsync(client, before.Good + 1, before.Bad + 2);
            await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
            { using var response = await client.GetAsync("/api/slo/status/200"); }));
            await CountsAsync(client, before.Good + 33, before.Bad + 2);
            await CountsAsync(replica, 0, 0);
        }

        internal static async Task<(long Good, long Bad)> ReadAsync(HttpClient client)
        {
            var text = await client.GetStringAsync("/metrics");
            var samples = text.Split('\n').Where(line => line.StartsWith("terrascale_slo_requests_total{", StringComparison.Ordinal)).ToArray();
            await Assert.That(samples.Length).IsEqualTo(2);
            return (Value("good"), Value("bad"));
            long Value(string outcome) => long.Parse(samples.Single(line => line.StartsWith("terrascale_slo_requests_total{service=\"ts-dashboard\",outcome=\"" + outcome + "\"}", StringComparison.Ordinal)).Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static async Task CountsAsync(HttpClient client, long good, long bad)
        {
            var expected = (good, bad);
            var actual = await ReadAsync(client);
            for (var attempt = 0; actual != expected && attempt < 100; attempt++)
            { await Task.Delay(10); actual = await ReadAsync(client); }
            await Assert.That(actual).IsEqualTo(expected);
        }

        private static HttpClient Client(WebApplication app)
        {
            return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(10) };
        }

        private sealed class FailingMeasurements : IHomeMeasurements
        {
            public int Status;
            public Task<HomeMeasurement> ReadAsync(AccountContext context, string range, CancellationToken cancellationToken)
            {
                throw Status switch
                {
                    503 => new HttpRequestException("Test dependency outage", null, HttpStatusCode.ServiceUnavailable),
                    500 => new InvalidOperationException("Test unexpected failure"),
                    _ => (Exception)new AccountAccessException("Test dependency refusal", Status)
                };
            }
        }
    }
}

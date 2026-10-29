using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class ErrorResponseJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task InternalErrorsAndDependencyErrorsExplainDifferentCausesWithoutLeakingDetails()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
            app.MapGet("/__test/internal-error", (Func<IResult>)(() => throw new InvalidOperationException("private-internal-detail")));
            app.MapGet("/__test/dependency-error", (Func<IResult>)(() => throw new HttpRequestException("private-dependency-detail")));
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            foreach (var (path, status, copy) in new[]
            {
                ("/__test/internal-error", HttpStatusCode.InternalServerError, "Something went wrong on our side"),
                ("/__test/dependency-error", HttpStatusCode.ServiceUnavailable, "We couldn't reach the service")
            })
            {
                using var response = await client.GetAsync(path);
                await Assert.That(response.StatusCode).IsEqualTo(status);
                var body = await response.Content.ReadAsStringAsync();
                await Assert.That(body).Contains(copy);
                await Assert.That(body).Contains(response.Headers.GetValues("X-Request-ID").Single());
                await Assert.That(body).DoesNotContain("private-");
                if (status == HttpStatusCode.InternalServerError)
                {
                    await Assert.That(body).DoesNotContain("reach the service");
                }
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "/__test/internal-error");
            request.Headers.Accept.ParseAdd("application/json");
            using var jsonResponse = await client.SendAsync(request);
            await Problem(jsonResponse, HttpStatusCode.InternalServerError);
        }

        [Test]
        public async Task StaleFormsAndUnknownApiRoutesAlwaysHaveBodiesForBrowsersAndApiClients()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            foreach (var accept in new[] { "text/html", "application/json", "*/*", "text/event-stream" })
            {
                using var stale = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/logout")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = "expired" }) };
                stale.Headers.Accept.ParseAdd(accept);
                using var response = await client.SendAsync(stale);
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
                if (accept == "text/html")
                {
                    await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
                    var body = await response.Content.ReadAsStringAsync();
                    await Assert.That(body).Contains("reload and try again");
                    await Assert.That(body).Contains("Back to dashboard");
                    await Assert.That(body).Contains(response.Headers.GetValues("X-Request-ID").Single());
                }
                else
                {
                    await Problem(response, HttpStatusCode.Forbidden);
                }
            }
            foreach (var accept in new[] { "application/json", "*/*", "" })
            {
                using var missing = new HttpRequestMessage(HttpMethod.Get, "/api/v1/dashboard/__missing-route");
                if (accept.Length > 0)
                {
                    missing.Headers.Accept.ParseAdd(accept);
                }

                using var response = await client.SendAsync(missing);
                await Problem(response, HttpStatusCode.NotFound);
            }
        }

        private static async Task Problem(HttpResponseMessage response, HttpStatusCode status)
        {
            await Assert.That(response.StatusCode).IsEqualTo(status);
            await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("application/problem+json");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            await Assert.That(body.RootElement.GetProperty("detail").GetString()!.Length).IsGreaterThan(0);
            await Assert.That(body.RootElement.GetProperty("requestId").GetString()).IsEqualTo(response.Headers.GetValues("X-Request-ID").Single());
        }
    }
}

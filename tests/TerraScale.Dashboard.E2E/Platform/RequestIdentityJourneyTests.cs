using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using TerraScale.Dashboard.Platform.Http;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.E2E.Platform
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class RequestIdentityJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task SignedIngressSurvivesSuccessFailuresAndParallelRequestsWithoutExposingProof()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            await using var app = environment.CreateTestHost(fixture.Root, 0, "http://127.0.0.1");
            app.MapGet("/api/request-forward", async (HttpContext http) =>
            {
                if (http.Request.Headers.ContainsKey(RequestIdentity.ProofHeader))
                {
                    throw new InvalidOperationException("Ingress proof was not consumed.");
                }

                using var outbound = new HttpClient(new RequestIdentity.Handler(new Demo.TestIdentity()) { InnerHandler = new Echo() });
                using var untrusted = new HttpRequestMessage(HttpMethod.Get, "https://untrusted.example/");
                untrusted.Headers.Add(RequestIdentity.Header, "caller-supplied");
                untrusted.Headers.Add(RequestIdentity.ProofHeader, "caller-supplied");
                using var stripped = await outbound.SendAsync(untrusted);
                return Results.Text(await outbound.GetStringAsync("https://api.terrascale.tech/v1/probe"));
            });
            app.MapGet("/api/request-identity", () => Results.Problem("Safe validation failure.", statusCode: 422));
            app.MapGet("/api/request-timeout", (Func<IResult>)(() => throw new TimeoutException("https://private.test/?token=secret")));
            await app.StartAsync();
            using var client = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false })
            { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async index =>
            {
                var id = AccountService.NewId();
                using var request = new HttpRequestMessage(HttpMethod.Get, index % 2 == 0 ? "/api/request-forward" : "/api/request-identity");
                request.Headers.Add(RequestIdentity.Header, id);
                request.Headers.Add(RequestIdentity.ProofHeader, RequestIdentity.Sign(id, Demo.TestIdentity.PeerSecret));
                using var response = await client.SendAsync(request);
                await Assert.That(response.Headers.GetValues(RequestIdentity.Header).Single()).IsEqualTo(id);
                await Assert.That(response.Headers.Contains(RequestIdentity.ProofHeader)).IsFalse();
                if (index % 2 == 0)
                {
                    await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo(id);
                }

                if (index % 2 != 0)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    await Assert.That(body.RootElement.GetProperty("requestId").GetString()).IsEqualTo(id);
                }
            }));
            foreach (var path in new[] { "/healthz", "/api/request-identity", "/api/request-timeout?token=secret" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                var untrusted = AccountService.NewId();
                request.Headers.Add(RequestIdentity.Header, untrusted);
                request.Headers.Add(RequestIdentity.ProofHeader, new string('A', 64));
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request);
                var id = response.Headers.GetValues(RequestIdentity.Header).Single();
                await Assert.That(TerraScaleIds.IsValid(id)).IsTrue();
                await Assert.That(id).IsNotEqualTo(untrusted);
                await Assert.That(await response.Content.ReadAsStringAsync()).DoesNotContain("token=secret");
            }
        }
        private sealed class Echo : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri!.Host == "untrusted.example")
                {
                    if (request.Headers.Contains(RequestIdentity.Header) || request.Headers.Contains(RequestIdentity.ProofHeader))
                    {
                        throw new InvalidOperationException("Untrusted headers were not stripped.");
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
                }
                var id = request.Headers.GetValues(RequestIdentity.Header).Single();
                var proof = request.Headers.GetValues(RequestIdentity.ProofHeader).Single();
                if (!RequestIdentity.Verify(id, proof, Demo.TestIdentity.PeerSecret))
                {
                    throw new InvalidOperationException("Invalid outbound proof.");
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(id) });
            }
        }
    }
}

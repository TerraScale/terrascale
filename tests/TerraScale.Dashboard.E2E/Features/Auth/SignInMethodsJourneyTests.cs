using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class SignInMethodsJourneyTests
    {
        [Test]
        [Arguments("{\"providers\":[\"google\",\"github\"]}", "GitHub", true)]
        [Arguments("{\"providers\":[]}", "No linked providers", false)]
        [Arguments("{\"providers\":[\"unknown<script>\"]}", "Sign-in methods are unavailable", false)]
        [Arguments("not JSON", "Sign-in methods are unavailable", false)]
        public async Task ProfileDistinguishesLinkedEmptyAndUnavailable(string response, string expected, bool canUnlink)
        {
            await using var scenario = await Scenario.StartAsync();
            scenario.Wire.ListBody = response;
            using var client = scenario.Client();
            using var login = await HttpJourney.LoginAsync(client);
            var html = await client.GetStringAsync("/profile?unlink=github");
            await Assert.That(html).Contains("Sign-in methods");
            await Assert.That(html).Contains(expected);
            await Assert.That(html.Contains("Unlink GitHub and sign out", StringComparison.Ordinal)).IsEqualTo(canUnlink);
            await Assert.That(html.Contains("unknown&lt;script&gt;", StringComparison.Ordinal)).IsFalse();
            await Assert.That(scenario.Wire.Reads).IsGreaterThan(0);
        }

        [Test]
        public async Task UnlinkRequiresCsrfKnownProviderAndFreshCodeThenEndsAllLocalSessions()
        {
            await using var scenario = await Scenario.StartAsync();
            using var first = scenario.Client();
            using var second = scenario.Client();
            using var firstLogin = await HttpJourney.LoginAsync(first);
            using var secondLogin = await HttpJourney.LoginAsync(second);
            var csrf = await HttpJourney.CsrfAsync(first, "/profile");
            using (var rejected = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/providers/github/unlink", new() { ["code"] = "123456" }))
            {
                await Assert.That(rejected.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            }

            using (var unknown = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/providers/other/unlink", new() { ["_csrf"] = csrf, ["code"] = "123456" }))
            {
                await Assert.That(unknown.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
            }

            using (var malformed = await HttpJourney.PostFormAsync(first, "/api/v1/dashboard/profile/providers/github/unlink", new() { ["_csrf"] = csrf, ["code"] = "<bad>" }))
            {
                await Assert.That(malformed.StatusCode).IsEqualTo(HttpStatusCode.UnprocessableEntity);
            }

            await Assert.That(scenario.Wire.Deletes).IsEqualTo(0);

            scenario.Wire.DeleteStatus = HttpStatusCode.Unauthorized;
            using (var wrong = await Unlink(first, csrf))
            {
                await Assert.That(wrong.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                var html = await wrong.Content.ReadAsStringAsync();
                await Assert.That(html).Contains("code is wrong or expired");
                await Assert.That(html).Contains("Unlink GitHub and sign out");
                await Assert.That(html.Contains("value=\"123456\"", StringComparison.Ordinal)).IsFalse();
            }
            using (var stillSignedIn = await first.GetAsync("/api/v1/auth/me"))
            {
                await Assert.That(stillSignedIn.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }

            scenario.Wire.DeleteStatus = HttpStatusCode.NoContent;
            using (var unlinked = await Unlink(first, csrf))
            {
                await Assert.That(unlinked.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(unlinked.Headers.Location!.OriginalString).IsEqualTo("/login?returnTo=%2Fprofile");
            }
            await Assert.That(scenario.Wire.Deletes).IsEqualTo(2).Because("Only explicit valid confirmations reach the producer; no mutation is retried");
            foreach (var client in new[] { first, second })
            {
                using var signedOut = await client.GetAsync("/api/v1/auth/me");
                await Assert.That(signedOut.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
            }
        }

        [Test]
        [Arguments(HttpStatusCode.TooManyRequests)]
        [Arguments(HttpStatusCode.ServiceUnavailable)]
        [Arguments(HttpStatusCode.OK)]
        public async Task RateLimitsAndUnconfirmedMutationsNeverSignTheUserOut(HttpStatusCode status)
        {
            await using var scenario = await Scenario.StartAsync();
            scenario.Wire.DeleteStatus = status;
            using var client = scenario.Client();
            using var login = await HttpJourney.LoginAsync(client);
            var csrf = await HttpJourney.CsrfAsync(client, "/profile");
            using var response = await Unlink(client, csrf);
            await Assert.That(response.StatusCode).IsEqualTo(status == HttpStatusCode.TooManyRequests ? status : HttpStatusCode.ServiceUnavailable);
            if (status == HttpStatusCode.TooManyRequests)
            {
                await Assert.That(response.Headers.RetryAfter!.Delta).IsEqualTo(TimeSpan.FromSeconds(120));
                await Assert.That(await response.Content.ReadAsStringAsync()).Contains("Try again in 120 seconds");
            }
            using var me = await client.GetAsync("/api/v1/auth/me");
            await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(scenario.Wire.Deletes).IsEqualTo(1);
        }

        private static Task<HttpResponseMessage> Unlink(HttpClient client, string csrf)
        {
            return HttpJourney.PostFormAsync(client,
            "/api/v1/dashboard/profile/providers/github/unlink", new() { ["_csrf"] = csrf, ["code"] = "123456" });
        }

        private sealed class Scenario : IAsyncDisposable
        {
            private WebApplication app = null!;
            public ProviderWire Wire { get; private set; } = null!;
            public static async Task<Scenario> StartAsync()
            {
                var environment = new DemoEnvironment();
                await environment.InitializeAsync();
                var scenario = new Scenario();
                scenario.app = environment.CreateTestHost(DashboardE2EFixture.FindRoot(), 0, "http://127.0.0.1", builder =>
                {
                    var original = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                    scenario.Wire = new(original, environment.Auth.ResolveAccessToken);
                    builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(scenario.Wire));
                });
                await scenario.app.StartAsync();
                return scenario;
            }
            public HttpClient Client()
            {
                return new(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() })
                { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };
            }

            public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); Wire.Dispose(); }
        }

        private sealed class ProviderWire(IHttpClientFactory original, Func<string, (string UserId, string Email)?> resolve) : HttpMessageHandler, IHttpClientFactory
        {
            public string ListBody { get; set; } = "{\"providers\":[\"github\",\"google\"]}";
            public HttpStatusCode DeleteStatus { get; set; } = HttpStatusCode.NoContent;
            public int Reads { get; private set; }
            public int Deletes { get; private set; }
            public HttpClient CreateClient(string name)
            {
                return new(this, disposeHandler: false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (request.RequestUri?.AbsolutePath.StartsWith("/v1/terrascale/account/providers", StringComparison.Ordinal) != true)
                {
                    using var fallback = original.CreateClient("platform");
                    using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
                    foreach (var header in request.Headers)
                    {
                        forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (request.Content is not null)
                    {
                        forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                        foreach (var header in request.Content.Headers)
                        {
                            forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                        }
                    }
                    return await fallback.SendAsync(forwarded, cancellationToken);
                }
                if (request.RequestUri.GetLeftPart(UriPartial.Authority) != "https://auth.terrascale.tech" ||
                    request.Headers.Authorization is not { Scheme: "Bearer", Parameter: { } bearer } || resolve(bearer) is null || request.Headers.Contains("Cookie"))
                {
                    throw new InvalidOperationException("Provider requests require the platform origin and original sign-in bearer, never cookies or exchanged tokens.");
                }

                if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath == "/v1/terrascale/account/providers")
                {
                    Reads++;
                    return new(HttpStatusCode.OK) { Content = new StringContent(ListBody, Encoding.UTF8, "application/json") };
                }
                if (request.Method != HttpMethod.Delete || request.RequestUri.AbsolutePath != "/v1/terrascale/account/providers/github" || request.Content?.Headers.ContentType?.MediaType != "application/json")
                {
                    throw new InvalidOperationException("Unlink uses DELETE and JSON against a known provider route.");
                }

                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                if (body.RootElement.EnumerateObject().Count() != 1 || body.RootElement.GetProperty("code").GetString() != "123456")
                {
                    throw new InvalidOperationException("Only the entered fresh email code is sent to unlink.");
                }

                Deletes++;
                var response = new HttpResponseMessage(DeleteStatus) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
                if (DeleteStatus == HttpStatusCode.TooManyRequests)
                {
                    response.Headers.RetryAfter = new(TimeSpan.FromSeconds(120));
                }

                return response;
            }
        }
    }
}

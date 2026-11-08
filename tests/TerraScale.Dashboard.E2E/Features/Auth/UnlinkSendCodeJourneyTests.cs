using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class UnlinkSendCodeJourneyTests
    {
        [Test]
        public async Task SendCodeRedirectsWithNoticeAndDisablesResendWhileRateLimited()
        {
            await using var host = await UnlinkHost.StartAsync();
            using var client = host.Client();
            using (var signedIn = await HttpJourney.LoginAsync(client))
            {
                await Assert.That(signedIn.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
            }

            var csrf = await HttpJourney.CsrfAsync(client, "/profile?unlink=github");
            using (var sent = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/providers/github/code", new() { ["_csrf"] = csrf }))
            {
                await Assert.That(sent.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(sent.Headers.Location!.OriginalString)
                    .IsEqualTo("/profile?unlink=github&status=code-sent#profile-sign-in-methods");
            }

            using (var view = await client.GetAsync("/profile?unlink=github&status=code-sent"))
            {
                var body = await view.Content.ReadAsStringAsync();
                await Assert.That(body).Contains("We emailed a code to user@example.com. It expires in 30 minutes.");
                await Assert.That(body).Contains("Email me a code</button>");
            }

            await Assert.That(host.Providers.Sends).IsEqualTo(1);

            host.Providers.RetryAfterSeconds = 30;
            using (var throttled = await HttpJourney.PostFormAsync(client, "/api/v1/dashboard/profile/providers/github/code", new() { ["_csrf"] = csrf }))
            {
                await Assert.That(throttled.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(throttled.Headers.Location!.OriginalString)
                    .IsEqualTo("/profile?unlink=github&wait=30#profile-sign-in-methods");
            }

            using (var waiting = await client.GetAsync("/profile?unlink=github&wait=30"))
            {
                var body = await waiting.Content.ReadAsStringAsync();
                await Assert.That(body).Contains("Email me a code (wait 30 s)</button>");
                await Assert.That(body).Contains("disabled>Email me a code");
            }

            await Assert.That(host.Providers.Sends).IsEqualTo(2);
        }

        private sealed class UnlinkHost : IAsyncDisposable
        {
            private readonly DemoEnvironment environment = new();
            private WebApplication? app;
            public FakeProviders Providers { get; } = new();

            public static async Task<UnlinkHost> StartAsync()
            {
                var host = new UnlinkHost();
                await host.environment.InitializeAsync();
                host.app = await host.environment.CreateTestHostAsync(new DashboardRootFixture().Root, 0, "http://127.0.0.1", builder =>
                    builder.Services.Replace(ServiceDescriptor.Singleton<ITsAuthProviders>(host.Providers)));
                await host.app.StartAsync();
                await TestDeadline.StateInitializedAsync(host.app);
                return host;
            }

            public HttpClient Client()
            {
                return new(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = TestHttps.Validate,
                    AllowAutoRedirect = false,
                    CookieContainer = new CookieContainer()
                })
                { BaseAddress = new(app!.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(30) };
            }

            public async ValueTask DisposeAsync()
            {
                if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); app = null; }
            }
        }

        private sealed class FakeProviders : ITsAuthProviders
        {
            private int sends;
            public int Sends => Volatile.Read(ref sends);
            public int RetryAfterSeconds;

            public Task<IReadOnlyList<string>?> ListProvidersAsync(SessionRecord session, CancellationToken cancellationToken)
            {
                return Task.FromResult<IReadOnlyList<string>?>(["github"]);
            }

            public Task UnlinkProviderAsync(SessionRecord session, string provider, string code, CancellationToken cancellationToken)
            {
                return Task.CompletedTask;
            }

            public Task SendUnlinkCodeAsync(SessionRecord session, string provider, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref sends);
                if (RetryAfterSeconds > 0)
                {
                    throw new AccountAccessException("Too many attempts. Try again in 30 seconds.", 429,
                        RetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return Task.CompletedTask;
            }
        }
    }
}

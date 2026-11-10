using System.Net;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<SharedDemoEnvironment>(Shared = SharedType.PerTestSession)]
    public sealed class WebhookEndpointJourneyTests(SharedDemoEnvironment shared)
    {
        [Test]
        public async Task CreateAnswersTheSecretOnceAndListsTheEndpoint()
        {
            var app = await shared.Environment.CreateTestHostAsync(shared.Root, 0, "http://127.0.0.1");
            await app.StartAsync();
            try
            {
                using var client = new HttpClient(new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = TestHttps.Validate,
                    AllowAutoRedirect = false,
                    CookieContainer = new CookieContainer()
                })
                { BaseAddress = new(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)), Timeout = TimeSpan.FromSeconds(30) };
                using var login = await HttpJourney.LoginAsync(client);
                await Assert.That(login.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                var csrf = await HttpJourney.CsrfAsync(client, "/auth/webhooks");

                using (var created = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/auth-management/webhooks", new()
                { ["_csrf"] = csrf, ["url"] = "https://hooks.example.test/lifecycle", ["event_types"] = "user.signed_up" }))
                {
                    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    var html = await created.Content.ReadAsStringAsync();
                    var secret = Regex.Match(html, "id=\"auth-webhook-created-secret\"[^>]*>([^<]+)<").Groups[1].Value;
                    await Assert.That(secret.StartsWith("whsec_test_", StringComparison.Ordinal)).IsTrue();
                }

                using (var listed = await client.GetAsync("/auth/webhooks"))
                {
                    var html = await listed.Content.ReadAsStringAsync();
                    await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(html.Contains("https://hooks.example.test/lifecycle", StringComparison.Ordinal)).IsTrue();
                    await Assert.That(html.Contains("user.signed_up", StringComparison.Ordinal)).IsTrue();
                    await Assert.That(html.Contains("whsec_test_", StringComparison.Ordinal)).IsFalse();
                }
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}

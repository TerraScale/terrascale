using System.Net;
using System.Text.RegularExpressions;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    [ClassDataSource<DashboardRootFixture>]
    public sealed class OAuthClientSecretJourneyTests(DashboardRootFixture fixture)
    {
        [Test]
        public async Task RotateNeedsApprovalAndTheApprovedResumeAnswersTheNewSecretOnce()
        {
            var environment = new DemoEnvironment();
            await environment.InitializeAsync();
            var app = await environment.CreateTestHostAsync(fixture.Root, 0, "http://127.0.0.1");
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
                var csrf = await HttpJourney.CsrfAsync(client, "/auth/clients");
                string clientId;
                using (var created = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/auth-management/clients", new()
                { ["_csrf"] = csrf, ["name"] = "Rotation journey", ["type"] = "confidential", ["redirect_uris"] = "https://example.test/callback" }))
                {
                    await Assert.That(created.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    clientId = Regex.Match(await created.Content.ReadAsStringAsync(), "id=\"auth-created-client-id\"[^>]*>([^<]+)<").Groups[1].Value;
                }

                await Assert.That(clientId.Length).IsGreaterThan(0);
                using (var unapproved = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth-management/clients/" + clientId + "/rotate")
                { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = csrf }) })
                {
                    unapproved.Headers.Add("Origin", client.BaseAddress.GetLeftPart(UriPartial.Authority));
                    unapproved.Headers.Add("Sec-Fetch-Mode", "navigate");
                    unapproved.Headers.Accept.ParseAdd("text/html");
                    using var started = await client.SendAsync(unapproved);
                    await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
                    await Assert.That(started.Headers.Location!.AbsolutePath.StartsWith("/ui/terrascale/step-up/", StringComparison.Ordinal)).IsTrue();
                }

                await Assert.That(environment.Authentication.RotatedClients).IsEqualTo(0);
                using (var listed = await client.GetAsync("/auth/clients"))
                {
                    var html = await listed.Content.ReadAsStringAsync();
                    await Assert.That(listed.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    await Assert.That(html.Contains("Rotated secret", StringComparison.Ordinal)).IsFalse();
                }

                using (var rotated = await HttpJourney.PostConfirmedFormAsync(client, "/api/v1/auth-management/clients/" + clientId + "/rotate", new() { ["_csrf"] = csrf }))
                {
                    await Assert.That(rotated.StatusCode).IsEqualTo(HttpStatusCode.OK);
                    var html = await rotated.Content.ReadAsStringAsync();
                    var secret = Regex.Match(html, "id=\"auth-rotated-secret\"[^>]*>([^<]+)<").Groups[1].Value;
                    await Assert.That(secret.StartsWith("tsas_test_", StringComparison.Ordinal)).IsTrue();
                    await Assert.That(html.Contains("The previous secret works until", StringComparison.Ordinal)).IsTrue();
                }

                await Assert.That(environment.Authentication.RotatedClients).IsEqualTo(1);
            }
            finally
            {
                await app.DisposeAsync();
            }
        }
    }
}

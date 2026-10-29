using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.E2E.Features.Auth
{
    public sealed class EmailChangeJourneyTests
    {
        [Test]
        [Arguments(202, "", "Code sent")]
        [Arguments(409, "email_taken", "already used")]
        [Arguments(429, "rate_limited", "Too many")]
        [Arguments(403, "step_up_required", "Confirm this action")]
        public async Task HostedApprovalRequestsCodeThroughTheInstalledSdk(int status, string code, string message)
        {
            using var wire = new Wire { Status = (HttpStatusCode)status, Code = code };
            await using var scenario = await StartAsync(wire);
            using var invalid = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/email-change", new() { ["new_email"] = "new@example.test" });
            await Assert.That(invalid.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/dashboard/profile/email-change")
            { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Browser), ["new_email"] = "new@example.test" }) };
            request.Headers.Accept.ParseAdd("text/html");
            using var started = await scenario.Browser.SendAsync(request);
            await Assert.That(started.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
            await Assert.That(started.Headers.Location!.OriginalString).DoesNotContain("synthetic-backend-proof");
            await Assert.That(wire.Calls).IsEqualTo(0);
            using var approved = await scenario.ApproveAsync(started);
            using var resumed = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That((int)resumed.StatusCode).IsEqualTo(status == 202 ? 200 : status);
            var html = await resumed.Content.ReadAsStringAsync();
            await Assert.That(html).Contains(message);
            await Assert.That(html).Contains("email-change-code");
            await Assert.That(html).DoesNotContain("synthetic-backend-proof");
            await Assert.That(wire.Calls).IsEqualTo(1);
            using var replay = await scenario.Browser.GetAsync("/auth/step-up/callback");
            await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Forbidden);
            await Assert.That(wire.Calls).IsEqualTo(1);
        }

        [Test]
        [Arguments(204, "", "")]
        [Arguments(403, "email_change_denied", "code is incorrect")]
        [Arguments(409, "email_taken", "already used")]
        public async Task ConfirmationEndsLocalSessionsOnlyAfterSuccess(int status, string code, string message)
        {
            using var wire = new Wire { Status = (HttpStatusCode)status, Code = code };
            await using var scenario = await StartAsync(wire);
            using var second = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = TestHttps.Validate, AllowAutoRedirect = false, CookieContainer = new() }) { BaseAddress = scenario.Browser.BaseAddress };
            using var secondLogin = await HttpJourney.LoginAsync(second);
            using var missingCsrf = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/email-change/confirm", new() { ["code"] = "123456" });
            await Assert.That(missingCsrf.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
            using var response = await HttpJourney.PostFormAsync(scenario.Browser, "/api/v1/dashboard/profile/email-change/confirm", new()
            { ["_csrf"] = await HttpJourney.CsrfAsync(scenario.Browser), ["code"] = "123456" });
            await Assert.That(wire.Calls).IsEqualTo(1);
            using var me = await scenario.Browser.GetAsync("/api/v1/auth/me");
            using var other = await second.GetAsync("/api/v1/auth/me");
            if (status == 204)
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.SeeOther);
                await Assert.That(response.Headers.Location!.OriginalString).IsEqualTo("/login?emailChanged=1");
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
                await Assert.That(await scenario.Browser.GetStringAsync("/login?emailChanged=1")).Contains("Sign in again with your new email address");
            }
            else
            {
                await Assert.That((int)response.StatusCode).IsEqualTo(status);
                await Assert.That(await response.Content.ReadAsStringAsync()).Contains(message);
                await Assert.That(me.StatusCode).IsEqualTo(HttpStatusCode.OK);
                await Assert.That(other.StatusCode).IsEqualTo(HttpStatusCode.OK);
            }
        }

        private static async Task<StepUpScenario> StartAsync(Wire wire)
        {
            var scenario = await StepUpScenario.StartAsync(DashboardE2EFixture.FindRoot(), configure: builder =>
            {
                wire.Fallback = (IHttpClientFactory)builder.Services.Last(service => service.ServiceType == typeof(IHttpClientFactory)).ImplementationInstance!;
                builder.Services.Replace(ServiceDescriptor.Singleton<IHttpClientFactory>(wire));
            });
            wire.Scenario = scenario;
            return scenario;
        }

        private sealed class Wire : HttpMessageHandler, IHttpClientFactory
        {
            public IHttpClientFactory Fallback { get; set; } = null!;
            public StepUpScenario Scenario { get; set; } = null!;
            public HttpStatusCode Status { get; init; }
            public string Code { get; init; } = "";
            public int Calls { get; private set; }
            public HttpClient CreateClient(string name)
            {
                return new(this, false);
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!request.RequestUri!.AbsolutePath.StartsWith("/v1/terrascale/account/email-change", StringComparison.Ordinal))
                {
                    using var client = Fallback.CreateClient("platform");
                    using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri);
                    foreach (var header in request.Headers)
                    {
                        forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    if (request.Content is not null)
                    {
                        forwarded.Content = new ByteArrayContent(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                    }

                    return await client.SendAsync(forwarded, cancellationToken);
                }
                Calls++;
                var token = request.Headers.Authorization!.Parameter!;
                var actor = Scenario.Environment.Auth.ResolveAccessToken(token)!.Value;
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (request.RequestUri.AbsolutePath.EndsWith("/confirm", StringComparison.Ordinal))
                {
                    await Assert.That(body.RootElement.GetProperty("code").GetString()).IsEqualTo("123456");
                    await Assert.That(request.Headers.Contains("X-TerraScale-Step-Up-Proof")).IsFalse();
                }
                else
                {
                    await Assert.That(body.RootElement.GetProperty("new_email").GetString()).IsEqualTo("new@example.test");
                    await Scenario.Gateway.ConsumeAsync(new(actor.UserId, "Synthetic", actor.Email, token, null, DateTimeOffset.UtcNow.AddHours(1)),
                        request.Headers.GetValues("X-TerraScale-Step-Up-Proof").Single(), "change_email", "ts-auth", null, cancellationToken);
                }
                return new(Status)
                {
                    Content = new StringContent(Status == HttpStatusCode.Accepted
                    ? "{\"new_email\":\"new@example.test\",\"expires_at\":\"2026-10-28T12:10:00Z\"}"
                    : JsonSerializer.Serialize(new { error = new { code = Code } }), Encoding.UTF8, "application/json")
                };
            }
        }
    }
}

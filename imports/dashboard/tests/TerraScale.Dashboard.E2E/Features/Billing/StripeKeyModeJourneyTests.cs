using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.E2E.Features.Billing;

[ClassDataSource<DashboardRootFixture>]
public sealed class StripeKeyModeJourneyTests(DashboardRootFixture fixture)
{
    [Test]
    [Arguments("sk_test_fixture", false)]
    [Arguments("rk_test_fixture", false)]
    [Arguments("sk_live_fixture", true)]
    [Arguments("rk_live_fixture", true)]
    public async Task DeliveredKeyStartsHostAndRejectsSignedEventsFromOtherMode(string key, bool live)
    {
        var secrets = new Dictionary<string, string>
        {
            [DashboardIdentity.AuthClientSecretName] = "test-dashboard-client-secret-0123456789abcdef",
            [DashboardIdentity.SessionEncryptionKeyName] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [DashboardIdentity.StripeSecretKeyName] = key,
            [DashboardIdentity.StripeWebhookSecretName] = "whsec_fixture",
            [DashboardIdentity.TerraBasePeerSecretName] = TestIdentity.PeerSecret
        };
        var environment = new DemoEnvironment();
        await environment.InitializeAsync();
        await using var app = environment.CreateHost(fixture.Root, 0, "http://127.0.0.1", builder =>
            builder.Services.Replace(ServiceDescriptor.Singleton<IDashboardIdentity>(new DashboardIdentity(secrets.GetValueOrDefault))));
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single().Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal)) };

        foreach (var eventMode in new[] { live, !live })
        {
            var body = JsonSerializer.Serialize(new
            {
                id = "evt_key_mode",
                @object = "event",
                type = "invoice.paid",
                api_version = StripeConfiguration.ApiVersion,
                livemode = eventMode,
                data = new { @object = new { id = "in_unbound", @object = "invoice", customer = "cus_unbound" } }
            });
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/billing/stripe/webhook")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Stripe-Signature", EventUtility.GenerateSignatureHeader(body, "whsec_fixture"));
            using var response = await client.SendAsync(request);
            await Assert.That(response.StatusCode).IsEqualTo(eventMode == live ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
                .Because("Valid signatures authorize only events matching the delivered standard or restricted key's mode.");
        }
        await app.StopAsync();
    }
}

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TerraScale.Dashboard.Features.Payments.Models;
using TerraScale.Dashboard.Features.Payments.Services;

namespace TerraScale.Dashboard.FastTests;

/// <summary>The full HTTP surface of the sandbox integration: the hosted
/// Checkout redirect, the demo fallback, and the signed webhook route going
/// through the real middleware and endpoint pipeline.</summary>
public sealed class StripeBillingEndpointTests
{
    private static DashboardTestApp StripeApp(FakeStripeBillingClient fake) =>
        new(configureServices: services =>
        {
            services.RemoveAll<StripePaymentOptions>();
            services.AddSingleton(StripeTestSupport.EnabledOptions());
            services.RemoveAll<IStripeBillingClient>();
            services.AddSingleton<IStripeBillingClient>(fake);
        });

    [Test]
    public async Task CheckoutRedirectsToTheSandboxSession()
    {
        var fake = new FakeStripeBillingClient();
        using var app = StripeApp(fake);
        using var client = app.CreateDemoClient();

        using var response = await client.GetAsync("/billing/checkout?plan=pro&interval=monthly&return=%2Fusage");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo(fake.CheckoutUrl);
        await Assert.That(fake.CheckoutRequests.Count).IsEqualTo(1);
        await Assert.That(fake.CheckoutRequests[0].PriceId).IsEqualTo("price_pro_monthly");
        await Assert.That(fake.CheckoutRequests[0].OrganizationId.Length > 0).IsTrue();
    }

    [Test]
    public async Task PortalRedirectsToTheSandboxSession()
    {
        var fake = new FakeStripeBillingClient();
        using var app = StripeApp(fake);
        using var client = app.CreateDemoClient();

        using var response = await client.GetAsync("/billing/portal?return=%2Fusage");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).IsEqualTo(fake.PortalUrl);
    }

    [Test]
    public async Task CheckoutFallsBackToTheLocalDemoWithoutStripe()
    {
        using var app = new DashboardTestApp();
        using var client = app.CreateDemoClient();

        using var response = await client.GetAsync("/billing/checkout?return=%2Fusage");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Redirect);
        await Assert.That(response.Headers.Location!.ToString()).StartsWith("/checkout/cs_local_");
    }

    [Test]
    public async Task WebhookRejectsAnUnsignedDeliveryThroughTheMiddleware()
    {
        var fake = new FakeStripeBillingClient();
        using var app = StripeApp(fake);
        using var client = app.CreateClient();
        using var content = new StringContent(
            "{\"id\":\"evt_unsigned\",\"type\":\"invoice.paid\",\"data\":{\"object\":{}}}",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync("/api/auth/stripe/webhook", content);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task SignedWebhookActivatesTheOrganizationThroughTheEndpoint()
    {
        var fake = new FakeStripeBillingClient();
        using var app = StripeApp(fake);
        using var client = app.CreateClient();
        var store = app.Services.GetRequiredService<DashboardStore>();
        var organization = store.ActiveOrganization;

        var payload = JsonSerializer.Serialize(new
        {
            id = "evt_http_checkout_completed",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_http",
                    client_reference_id = organization.Id,
                    status = "complete",
                    payment_status = "paid",
                    customer = "cus_http",
                    subscription = "sub_http",
                    metadata = new { organization_id = organization.Id, plan_key = "pro" },
                },
            },
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/stripe/webhook")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Stripe-Signature", StripeTestSupport.Sign(payload));
        using var response = await client.SendAsync(request);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(store.FindOrganization(organization.Id)!.BillingStatus).IsEqualTo("active");
    }
}

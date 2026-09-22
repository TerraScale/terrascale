using System.Net;
using System.Text;
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
    public async Task NewOrganizationPaymentStepStartsTheSandboxCheckoutWithTheSelectedPlan()
    {
        var fake = new FakeStripeBillingClient();
        using var app = StripeApp(fake);
        using var client = app.CreateDemoClient();

        // Step 1 creates the pending Business tenant the wizard bills.
        using var organization = await DashboardTestApp.PostFormAsync(
            client,
            "/api/v1/dashboard/organization/new-organization",
            new Dictionary<string, string> { ["step"] = "organization", ["name"] = "Stripe Labs" });
        await Assert.That(organization.Headers.Location!.ToString()).IsEqualTo("/organization?newOrganization=payment");

        // Step 2 hands the chosen card and interval to the sandbox checkout and
        // returns to the modal's project step once billing is active.
        using var payment = await DashboardTestApp.PostFormAsync(
            client,
            "/api/v1/dashboard/organization/new-organization",
            new Dictionary<string, string> { ["step"] = "payment-method", ["plan"] = "business", ["interval"] = "annual" });
        await Assert.That(payment.Headers.Location!.ToString()).IsEqualTo(
            "/billing/checkout?plan=business&interval=annual&return=%2Forganization%3FnewOrganization%3Dproject");

        // The hand-off starts the hosted session for the posted plan and
        // interval, not for the seeded default.
        using var checkout = await client.GetAsync(payment.Headers.Location!.ToString());
        await Assert.That(checkout.Headers.Location!.ToString()).IsEqualTo(fake.CheckoutUrl);
        await Assert.That(fake.CheckoutRequests.Count).IsEqualTo(1);
        await Assert.That(fake.CheckoutRequests[0].PlanKey).IsEqualTo("business");
        await Assert.That(fake.CheckoutRequests[0].Interval).IsEqualTo("annual");
        await Assert.That(fake.CheckoutRequests[0].PriceId).IsEqualTo("price_business_annual");
        await Assert.That(fake.CheckoutRequests[0].SuccessUrl.ToString())
            .Contains("return=%2Forganization%3FnewOrganization%3Dproject");
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

        var payload = StripeTestSupport.CheckoutCompletedPayload(
            organization.Id,
            eventId: "evt_http_checkout_completed",
            customerId: "cus_http",
            subscriptionId: "sub_http");

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

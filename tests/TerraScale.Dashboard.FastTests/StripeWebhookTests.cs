using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Dashboard.Features.Payments.Services;

namespace TerraScale.Dashboard.FastTests;

/// <summary>Signature verification and webhook projection, exercised without a
/// socket so the Stripe contract stays regression-tested offline. Verification
/// and parsing go through the official SDK's EventUtility.</summary>
public sealed class StripeWebhookTests
{
    [Test]
    public async Task ReadsAFreshValidSignature()
    {
        var payload = StripeTestSupport.InvoicePayload("org_verify", eventId: "evt_verify");
        var read = StripeWebhookReader.TryRead(payload, StripeTestSupport.Sign(payload), StripeTestSupport.WebhookSecret, out var stripeEvent, out var error);

        await Assert.That(read).IsTrue();
        await Assert.That(error).IsNull();
        await Assert.That(stripeEvent!.Id).IsEqualTo("evt_verify");
        await Assert.That(stripeEvent.Type).IsEqualTo("invoice.paid");
    }

    [Test]
    public async Task RejectsAWrongSecret()
    {
        var payload = StripeTestSupport.InvoicePayload("org_verify");
        var read = StripeWebhookReader.TryRead(
            payload,
            StripeTestSupport.Sign(payload, "whsec_someone_else"),
            StripeTestSupport.WebhookSecret,
            out _,
            out var error);

        await Assert.That(read).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task RejectsAReplayedTimestamp()
    {
        var payload = StripeTestSupport.InvoicePayload("org_verify");
        var read = StripeWebhookReader.TryRead(
            payload,
            StripeTestSupport.Sign(payload, at: DateTimeOffset.UtcNow.AddMinutes(-30)),
            StripeTestSupport.WebhookSecret,
            out _,
            out var error);

        await Assert.That(read).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task RejectsAMalformedHeader()
    {
        var payload = StripeTestSupport.InvoicePayload("org_verify");
        var read = StripeWebhookReader.TryRead(payload, "not-a-signature", StripeTestSupport.WebhookSecret, out _, out var error);

        await Assert.That(read).IsFalse();
        await Assert.That(error).IsNotNull();
    }

    [Test]
    public async Task CheckoutWebhookActivatesTheOrganizationAndIsIdempotent()
    {
        var store = new DashboardStore();
        var error = store.CreateBusinessOrganization("Stripe Webhook Tests", "plan_scale", "pro");
        await Assert.That(error).IsNull();
        var organization = store.ActiveOrganization;
        var service = new StripePaymentsService(
            StripeTestSupport.EnabledOptions(),
            new FakeStripeBillingClient(),
            store,
            NullLogger<StripePaymentsService>.Instance);

        var payload = StripeTestSupport.CheckoutCompletedPayload(organization.Id);

        var first = await service.ApplyWebhookAsync(payload, StripeTestSupport.Sign(payload), CancellationToken.None);
        await Assert.That(first.StatusCode).IsEqualTo(200);
        await Assert.That(organization.BillingStatus).IsEqualTo("active");
        await Assert.That(organization.StripeSubscriptionId).IsEqualTo("sub_webhook");
        await Assert.That(organization.PaymentMethodValidated).IsTrue();
        await Assert.That(organization.PlanKey).IsEqualTo("pro");

        var second = await service.ApplyWebhookAsync(payload, StripeTestSupport.Sign(payload), CancellationToken.None);
        await Assert.That(second.StatusCode).IsEqualTo(200);
        await Assert.That(second.Message).IsEqualTo("duplicate");
    }

    [Test]
    public async Task SubscriptionWebhookProjectsStatusAndRenewal()
    {
        var store = new DashboardStore();
        await Assert.That(store.CreateBusinessOrganization("Stripe Subscription Tests", "plan_dedicated", "business")).IsNull();
        var organization = store.ActiveOrganization;
        organization.StripeSubscriptionId = "sub_existing";
        var service = new StripePaymentsService(
            StripeTestSupport.EnabledOptions(),
            new FakeStripeBillingClient(),
            store,
            NullLogger<StripePaymentsService>.Instance);

        var payload = StripeTestSupport.SubscriptionPayload(organization.Id);

        var result = await service.ApplyWebhookAsync(payload, StripeTestSupport.Sign(payload), CancellationToken.None);
        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(organization.BillingStatus).IsEqualTo("past_due");
        await Assert.That(organization.BillingRenewal).IsNotNull();
    }

    [Test]
    public async Task InvoiceWebhookResolvesTheOrganizationByCustomer()
    {
        var store = new DashboardStore();
        await Assert.That(store.CreateBusinessOrganization("Stripe Invoice Tests", "plan_scale", "pro")).IsNull();
        var organization = store.ActiveOrganization;
        organization.StripeCustomerId = "cus_existing";
        var service = new StripePaymentsService(
            StripeTestSupport.EnabledOptions(),
            new FakeStripeBillingClient(),
            store,
            NullLogger<StripePaymentsService>.Instance);

        var payload = StripeTestSupport.InvoicePayload(organization.Id, type: "invoice.payment_failed", eventId: "evt_invoice_failed");

        var result = await service.ApplyWebhookAsync(payload, StripeTestSupport.Sign(payload), CancellationToken.None);
        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(organization.BillingStatus).IsEqualTo("past_due");
    }

    [Test]
    public async Task RejectsAnUnsignedWebhookAtTheServiceBoundary()
    {
        var store = new DashboardStore();
        var service = new StripePaymentsService(
            StripeTestSupport.EnabledOptions(),
            new FakeStripeBillingClient(),
            store,
            NullLogger<StripePaymentsService>.Instance);

        var result = await service.ApplyWebhookAsync(StripeTestSupport.InvoicePayload("org_x"), "", CancellationToken.None);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }
}

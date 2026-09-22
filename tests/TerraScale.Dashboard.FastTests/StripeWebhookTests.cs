using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TerraScale.Dashboard.Features.Payments.Services;

namespace TerraScale.Dashboard.FastTests;

/// <summary>Signature verification and webhook projection, exercised without a
/// socket so the Stripe contract stays regression-tested offline.</summary>
public sealed class StripeWebhookTests
{
    [Test]
    public async Task AcceptsAFreshValidSignature()
    {
        const string payload = "{\"id\":\"evt_verify\",\"type\":\"invoice.paid\"}";
        var result = StripeWebhookVerifier.Verify(
            payload,
            StripeTestSupport.Sign(payload),
            StripeTestSupport.WebhookSecret,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        await Assert.That(result).IsEqualTo(StripeSignatureResult.Valid);
    }

    [Test]
    public async Task RejectsAWrongSecret()
    {
        const string payload = "{\"id\":\"evt_verify\"}";
        var result = StripeWebhookVerifier.Verify(
            payload,
            StripeTestSupport.Sign(payload, "whsec_someone_else"),
            StripeTestSupport.WebhookSecret,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        await Assert.That(result).IsEqualTo(StripeSignatureResult.SignatureMismatch);
    }

    [Test]
    public async Task RejectsAReplayedTimestamp()
    {
        const string payload = "{\"id\":\"evt_verify\"}";
        var result = StripeWebhookVerifier.Verify(
            payload,
            StripeTestSupport.Sign(payload, at: DateTimeOffset.UtcNow.AddMinutes(-30)),
            StripeTestSupport.WebhookSecret,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        await Assert.That(result).IsEqualTo(StripeSignatureResult.TimestampOutOfTolerance);
    }

    [Test]
    public async Task RejectsAMalformedHeader()
    {
        var result = StripeWebhookVerifier.Verify(
            "{}",
            "not-a-signature",
            StripeTestSupport.WebhookSecret,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
        await Assert.That(result).IsEqualTo(StripeSignatureResult.MalformedSignature);
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

        var payload = JsonSerializer.Serialize(new
        {
            id = "evt_checkout_completed",
            type = "checkout.session.completed",
            data = new
            {
                @object = new
                {
                    id = "cs_test_webhook",
                    client_reference_id = organization.Id,
                    status = "complete",
                    payment_status = "paid",
                    customer = "cus_webhook",
                    subscription = "sub_webhook",
                    metadata = new { organization_id = organization.Id, plan_key = "pro" },
                },
            },
        });

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
        var periodEnd = DateTimeOffset.UtcNow.AddDays(30);
        var service = new StripePaymentsService(
            StripeTestSupport.EnabledOptions(),
            new FakeStripeBillingClient(),
            store,
            NullLogger<StripePaymentsService>.Instance);

        var payload = JsonSerializer.Serialize(new
        {
            id = "evt_subscription_updated",
            type = "customer.subscription.updated",
            data = new
            {
                @object = new
                {
                    id = "sub_existing",
                    status = "past_due",
                    customer = "cus_existing",
                    current_period_end = periodEnd.ToUnixTimeSeconds(),
                    metadata = new { organization_id = organization.Id, plan_key = "business" },
                },
            },
        });

        var result = await service.ApplyWebhookAsync(payload, StripeTestSupport.Sign(payload), CancellationToken.None);
        await Assert.That(result.StatusCode).IsEqualTo(200);
        await Assert.That(organization.BillingStatus).IsEqualTo("past_due");
        await Assert.That(organization.BillingRenewal).IsNotNull();
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

        var result = await service.ApplyWebhookAsync("{\"id\":\"evt_x\",\"type\":\"invoice.paid\"}", "", CancellationToken.None);
        await Assert.That(result.StatusCode).IsEqualTo(400);
    }
}

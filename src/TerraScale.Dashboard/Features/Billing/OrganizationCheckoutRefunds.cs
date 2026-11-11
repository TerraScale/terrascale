using System.Globalization;
using System.Net;
using Stripe;
using CheckoutSession = Stripe.Checkout.Session;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    private async Task<bool> CancelDraftPaymentAsync(PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        CheckoutSession checkout;
        try
        {
            checkout = await client.V1.Checkout.Sessions.GetAsync(draft.CheckoutId, cancellationToken: cancellationToken);
        }
        catch (StripeException error) when (PermanentStripeFailure(error))
        {
            return false;
        }

        if (checkout.Status == "open")
        {
            await ExpireDraftCheckoutAsync(draft.CheckoutId, cancellationToken);
            checkout = await client.V1.Checkout.Sessions.GetAsync(draft.CheckoutId, cancellationToken: cancellationToken);
        }

        if (checkout.SubscriptionId is not { Length: > 0 } subscriptionId)
        {
            if (checkout.PaymentStatus == "paid")
            {
                throw new IOException("Paid organization checkout has no subscription to refund.");
            }
            return false;
        }

        if (!await CancelDraftSubscriptionAsync(client, subscriptionId, cancellationToken))
        {
            return false;
        }

        var refunded = false;
        await foreach (var invoice in client.V1.Invoices.ListAutoPagingAsync(
            new() { Customer = checkout.CustomerId, Subscription = subscriptionId, Status = "paid" }, cancellationToken: cancellationToken))
        {
            long paid = 0;
            await foreach (var payment in client.V1.InvoicePayments.ListAutoPagingAsync(
                new() { Invoice = invoice.Id, Status = "paid" }, cancellationToken: cancellationToken))
            {
                if (payment.AmountPaid is not > 0)
                {
                    continue;
                }

                await RefundDraftPaymentAsync(client, payment, cancellationToken);
                paid += payment.AmountPaid.Value;
            }

            if (paid < invoice.AmountPaid)
            {
                throw new IOException("Organization invoice could not be fully refunded.");
            }
            refunded |= paid > 0;
        }

        if (checkout.PaymentStatus == "paid" && !refunded)
        {
            throw new IOException("Paid organization checkout has no refundable invoice.");
        }

        return refunded;
    }

    private static async Task<bool> CancelDraftSubscriptionAsync(StripeClient client, string subscriptionId, CancellationToken cancellationToken)
    {
        try
        {
            var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId, cancellationToken: cancellationToken);
            if (subscription.Status is not ("canceled" or "incomplete_expired"))
            {
                try
                {
                    await client.V1.Subscriptions.CancelAsync(subscriptionId, new() { Prorate = false, InvoiceNow = false }, cancellationToken: cancellationToken);
                }
                catch (StripeException error) when (error.HttpStatusCode == HttpStatusCode.BadRequest)
                {
                    if ((await client.V1.Subscriptions.GetAsync(subscriptionId, cancellationToken: cancellationToken)).Status != "canceled")
                    {
                        throw;
                    }
                }
            }
        }
        catch (StripeException error) when (MissingStripeObject(error))
        {
            return false;
        }

        return true;
    }

    private static async Task RefundDraftPaymentAsync(StripeClient client, InvoicePayment payment, CancellationToken cancellationToken)
    {
        var options = payment.Payment switch
        {
            { Type: "payment_intent", PaymentIntentId.Length: > 0 } target => new RefundCreateOptions { PaymentIntent = target.PaymentIntentId },
            { Type: "charge", ChargeId.Length: > 0 } target => new RefundCreateOptions { Charge = target.ChargeId },
            _ => throw new IOException("Organization invoice payment cannot be refunded.")
        };
        long refunded = 0;
        var lastFailure = "";
        await foreach (var refund in client.V1.Refunds.ListAutoPagingAsync(
            new() { PaymentIntent = options.PaymentIntent, Charge = options.Charge }, cancellationToken: cancellationToken))
        {
            if (refund.Status is "pending" or "requires_action")
            {
                throw new IOException("Organization invoice refund is pending.");
            }
            if (refund.Status == "succeeded")
            {
                refunded += refund.Amount;
            }
            if (refund.Status is "failed" or "canceled" && string.CompareOrdinal(refund.Id, lastFailure) > 0)
            {
                lastFailure = refund.Id;
            }
        }

        if (refunded >= payment.AmountPaid)
        {
            return;
        }

        options.Amount = payment.AmountPaid - refunded;
        options.Reason = "requested_by_customer";
        var result = await client.V1.Refunds.CreateAsync(options,
            Idempotent("organization-refund-" + payment.Id + "-" + refunded.ToString(CultureInfo.InvariantCulture) + "-" + lastFailure), cancellationToken);
        if (result.Status != "succeeded")
        {
            throw new IOException("Organization invoice refund has not succeeded.");
        }
    }
}

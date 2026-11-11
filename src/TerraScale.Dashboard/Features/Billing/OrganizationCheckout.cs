using Microsoft.EntityFrameworkCore;
using Stripe;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Common.Validation;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    public async Task<string> StartOrganizationCheckoutAsync(SessionRecord session, string name, string planKey, string interval, CancellationToken cancellationToken)
    {
        name = AccountService.BusinessName(await accounts.GetContextAsync(session, cancellationToken), name);
        var plan = PlanCatalog.For(planKey);
        if (plan.PriceCents <= 0 || !plan.SupportsInterval(interval))
        {
            throw new OnboardingValidationException("Choose a paid plan and its published billing interval.");
        }

        var draft = await PendingOrganizationAsync(session.UserId, cancellationToken);
        if (draft is null)
        {
            var initial = new PendingOrganizationCheckout
            {
                UserId = session.UserId,
                DraftId = AccountService.NewId(),
                Name = name,
                Plan = plan.Key,
                Interval = interval,
                CreatedAt = time.GetUtcNow().UtcDateTime
            };
            draft = await storage.InsertAsync(initial, cancellationToken) ? initial
                : await PendingOrganizationAsync(session.UserId, cancellationToken) ?? throw new IOException("Checkout reservation is unavailable.");
        }

        if (draft.Name != name || draft.Plan != plan.Key || draft.Interval != interval)
        {
            throw new OnboardingValidationException("Finish or cancel the current organization checkout first.");
        }

        var checkout = await DraftCheckoutAsync(session, draft, cancellationToken);
        return checkout.Status == "complete" ? CheckoutReturnUrl(checkout.Id)
            : checkout.Status == "open" && checkout.Url is { Length: > 0 } url ? url
            : throw new OnboardingValidationException("This checkout expired. Start a new checkout.");
    }

    internal async Task<PendingOrganizationCheckout?> PendingOrganizationAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await db.PendingOrganizationCheckouts.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId, cancellationToken);
    }

    private async Task<Stripe.Checkout.Session> DraftCheckoutAsync(SessionRecord session, PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        Stripe.Checkout.Session checkout;
        if (draft.CheckoutId.Length > 0)
        {
            checkout = await client.V1.Checkout.Sessions.GetAsync(draft.CheckoutId, cancellationToken: cancellationToken);
        }
        else
        {
            var plan = PlanCatalog.For(draft.Plan);
            var price = await PriceAsync(client, plan, draft.Interval, cancellationToken);
            var customer = await client.V1.Customers.CreateAsync(new()
            {
                Name = draft.Name,
                Email = session.Email,
                Metadata = new() { ["draft_id"] = draft.DraftId, ["owner_user_id"] = session.UserId }
            }, Idempotent("organization-customer-" + draft.DraftId), cancellationToken);
            var metadata = new Dictionary<string, string>
            {
                ["draft_id"] = draft.DraftId,
                ["owner_user_id"] = session.UserId,
                ["name"] = draft.Name,
                ["plan"] = draft.Plan,
                ["interval"] = draft.Interval,
                ["request_id"] = draft.DraftId
            };
            checkout = await client.V1.Checkout.Sessions.CreateAsync(new()
            {
                Customer = customer.Id,
                ClientReferenceId = draft.DraftId,
                Metadata = metadata,
                Mode = "subscription",
                LineItems = [new() { Price = price.Id, Quantity = 1 }],
                PaymentMethodCollection = "always",
                BillingAddressCollection = "required",
                TaxIdCollection = new() { Enabled = true },
                CustomerUpdate = new() { Address = "auto", Name = "auto" },
                SubscriptionData = new() { Metadata = metadata },
                SuccessUrl = Origin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}",
                CancelUrl = Origin + "/organization?newOrganization=payment&payment=canceled",
                ExpiresAt = DateTime.SpecifyKind(draft.CreatedAt, DateTimeKind.Utc) + CheckoutLifetime
            }, Idempotent("organization-checkout-" + draft.DraftId), cancellationToken);
            draft.CheckoutId = checkout.Id;
            if (!await storage.ReplaceAsync(draft, cancellationToken))
            {
                var current = await PendingOrganizationAsync(session.UserId, cancellationToken);
                if (current?.DraftId != draft.DraftId || current.CheckoutId != checkout.Id)
                {
                    throw new AccountAccessException("Checkout changed concurrently. Reload organization setup.", 409);
                }

                draft.Version = current.Version;
            }
        }

        if (checkout.Id != draft.CheckoutId || checkout.ClientReferenceId != draft.DraftId ||
            checkout.Metadata.GetValueOrDefault("draft_id") != draft.DraftId ||
            checkout.Metadata.GetValueOrDefault("owner_user_id") != session.UserId ||
            checkout.Metadata.GetValueOrDefault("name") != draft.Name ||
            checkout.Metadata.GetValueOrDefault("plan") != draft.Plan ||
            checkout.Metadata.GetValueOrDefault("interval") != draft.Interval)
        {
            throw new IOException("Organization checkout does not match its signed-in owner and draft.");
        }

        return checkout;
    }

    internal async Task<string?> CompletePendingOrganizationAsync(SessionRecord session, HttpContext request, CancellationToken cancellationToken)
    {
        var draft = await PendingOrganizationAsync(session.UserId, cancellationToken);
        if (draft is null)
        {
            return null;
        }

        var checkout = await DraftCheckoutAsync(session, draft, cancellationToken);
        var client = await ClientAsync(cancellationToken);
        if (checkout.Status == "open" && request.Request.Query["payment"] == "canceled")
        {
            checkout = await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, cancellationToken: cancellationToken);
        }

        if (checkout.Status == "expired")
        {
            request.Items[nameof(PendingOrganizationCheckout)] = draft;

            await storage.RemoveAsync(draft, cancellationToken);
            return null;
        }

        if (checkout.Status != "complete" || checkout.PaymentStatus != "paid")
        {
            return null;
        }

        if (checkout.Mode != "subscription" || checkout.SubscriptionId is not { Length: > 0 } subscriptionId || checkout.CustomerId is not { Length: > 0 } customerId)
        {
            throw new IOException("Paid organization checkout has no subscription or customer.");
        }

        var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId,
            new() { Expand = ["items.data.price.product", "default_payment_method", "customer"] }, cancellationToken: cancellationToken);
        var item = subscription.Items.Data.Count == 1 ? subscription.Items.Data[0] : null;
        var effective = item is { Quantity: 1 } ? EffectivePlan(item.Price) : null;
        if (subscription.CustomerId != customerId || subscription.Metadata.GetValueOrDefault("draft_id") != draft.DraftId ||
            subscription.Metadata.GetValueOrDefault("owner_user_id") != session.UserId ||
            effective is not { } mapped || mapped.Plan.Key != draft.Plan || mapped.Interval != draft.Interval)
        {
            throw new IOException("Stripe has not confirmed the organization's subscription, plan and payment method.");
        }

        var paymentValid = subscription.Status is "active" or "trialing" &&
            (subscription.DefaultPaymentMethodId is { Length: > 0 } || subscription.Customer is { Deleted: not true, InvoiceSettings.DefaultPaymentMethodId.Length: > 0 });
        var organization = await accounts.CompleteBusinessAsync(session, draft.Name, checkout.Id, mapped.Plan, paymentValid, cancellationToken);
        var metadata = new Dictionary<string, string> { ["organization_id"] = organization.Id };
        if (subscription.Metadata.GetValueOrDefault("organization_id") != organization.Id)
        {
            await client.V1.Subscriptions.UpdateAsync(subscriptionId, new() { Metadata = metadata }, Idempotent("organization-subscription-" + draft.DraftId), cancellationToken);
        }

        await client.V1.Customers.UpdateAsync(customerId, new() { Metadata = metadata }, Idempotent("organization-owner-" + draft.DraftId), cancellationToken);
        var binding = new BillingBinding
        {
            OrganizationId = organization.Id,
            CustomerId = customerId,
            RequestId = draft.DraftId,
            CheckoutId = checkout.Id,
            Plan = draft.Plan,
            Interval = draft.Interval,
            StartedAt = new DateTimeOffset(DateTime.SpecifyKind(draft.CreatedAt, DateTimeKind.Utc)),
            SubscriptionId = subscriptionId,
            EffectivePlan = mapped.Plan.Key,
            EffectiveInterval = mapped.Interval,
            Status = subscription.Status,
            State = "active",
            RenewalAt = subscription.Status is "canceled" or "incomplete_expired" ? null : item!.CurrentPeriodEnd,
            ReturnPath = "/organization?newOrganization=project&organizationId=" + Uri.EscapeDataString(organization.Id)
        };
        if (!await storage.InsertAsync(binding, cancellationToken))
        {
            binding = await ReadAsync(organization.Id, cancellationToken) ?? throw new IOException("Organization billing did not persist.");
            if (binding.RequestId != draft.DraftId || binding.CheckoutId != checkout.Id || binding.SubscriptionId != subscriptionId || binding.CustomerId != customerId)
            {
                throw new IOException("Organization billing belongs to a different checkout.");
            }
        }

        if (!await storage.InsertAsync(new StripeCustomerRow { Id = AccountService.NewId(), OrganizationId = organization.Id, CustomerId = customerId, CreatedAt = time.GetUtcNow().UtcDateTime }, cancellationToken) &&
            (await CustomerAsync(customerId, cancellationToken))?.OrganizationId != organization.Id)
        {
            throw new IOException("Organization customer belongs to a different checkout.");
        }

        await IndexCheckoutAsync(binding, cancellationToken);
        await RefreshAsync(organization.Id, cancellationToken);
        await storage.RemoveAsync(draft, cancellationToken);
        return organization.Id;
    }
}

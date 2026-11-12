using System.Net;
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
        if (plan.Key.Length is 0 or > 128 || plan.PriceCents <= 0 || !plan.SupportsInterval(interval))
        {
            throw new OnboardingValidationException("Choose a paid plan and its published billing interval.");
        }

        var draft = await PendingOrganizationAsync(session.UserId, cancellationToken);
        if (draft is null)
        {
            var draftId = AccountService.NewId();
            var createdAt = time.GetUtcNow().UtcDateTime;
            var createdCheckout = await CreateDraftCheckoutAsync(session, draftId, name, plan, interval, createdAt, cancellationToken);
            var initial = new PendingOrganizationCheckout
            {
                UserId = session.UserId,
                DraftId = draftId,
                Name = name,
                Plan = plan.Key,
                Interval = interval,
                CheckoutId = createdCheckout.Id,
                CreatedAt = createdAt
            };
            if (await storage.InsertAsync(initial, cancellationToken))
            {
                draft = initial;
            }
            else
            {
                draft = await PendingOrganizationAsync(session.UserId, cancellationToken)
                    ?? throw new AccountAccessException("Checkout changed concurrently. Retry organization setup.", 409);
                if (draft.CheckoutId != createdCheckout.Id)
                {
                    await ExpireDraftCheckoutAsync(createdCheckout.Id, cancellationToken);
                }
            }
        }

        if (draft.Name != name || draft.Plan != plan.Key || draft.Interval != interval)
        {
            throw new OnboardingValidationException("Cancel the pending checkout in the organization setup wizard before starting another.");
        }

        if (draft.State == OrganizationCheckoutStates.Creating)
        {
            return CheckoutReturnUrl(draft.CheckoutId);
        }
        if (draft.State != OrganizationCheckoutStates.Pending)
        {
            throw new OnboardingValidationException("Checkout cancellation is being completed. Open the organization page to continue.");
        }

        var checkout = await DraftCheckoutAsync(session, draft, cancellationToken);
        return checkout.Status == "complete" ? CheckoutReturnUrl(checkout.Id)
            : checkout.Status == "open" && checkout.Url is { Length: > 0 } url ? url
            : throw new OnboardingValidationException("This checkout expired. Start a new checkout.");
    }

    internal async Task<PendingOrganizationCheckout?> PendingOrganizationAsync(string userId, CancellationToken cancellationToken)
    {
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        return await db.PendingOrganizationCheckouts.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == userId && row.State != OrganizationCheckoutStates.RefundPending, cancellationToken);
    }

    private async Task<Stripe.Checkout.Session> CreateDraftCheckoutAsync(SessionRecord session, string draftId, string name, BillingPlan plan, string interval, DateTime createdAt, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        var price = await PriceAsync(client, plan, interval, cancellationToken);
        var customer = await client.V1.Customers.CreateAsync(new()
        {
            Name = name,
            Email = session.Email,
            Metadata = new() { ["draft_id"] = draftId, ["owner_user_id"] = session.UserId }
        }, Idempotent("organization-customer-" + draftId), cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["draft_id"] = draftId,
            ["owner_user_id"] = session.UserId,
            ["name"] = name,
            ["plan"] = plan.Key,
            ["interval"] = interval,
            ["request_id"] = draftId
        };
        var options = CheckoutOptions(plan);
        options.Customer = customer.Id;
        options.ClientReferenceId = draftId;
        options.Metadata = metadata;
        options.LineItems = [new() { Price = price.Id, Quantity = 1 }];
        options.SubscriptionData = new() { Metadata = metadata };
        options.SuccessUrl = Origin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}";
        options.CancelUrl = Origin + "/organization?newOrganization=payment&payment=canceled&draft_id=" + Uri.EscapeDataString(draftId);
        options.ExpiresAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc) + CheckoutLifetime;
        try
        {
            return await client.V1.Checkout.Sessions.CreateAsync(options, Idempotent(draftId), cancellationToken);
        }
        catch (StripeException error) when (error.HttpStatusCode == HttpStatusCode.Conflict)
        {
            var sessions = await client.V1.Checkout.Sessions.ListAsync(new() { Customer = customer.Id }, cancellationToken: cancellationToken);
            return sessions.Data.SingleOrDefault(item => item.ClientReferenceId == draftId)
                ?? throw new AccountAccessException("Checkout has already started. Retry organization setup.", 409);
        }
    }

    private async Task<Stripe.Checkout.Session> DraftCheckoutAsync(SessionRecord session, PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        var client = await ClientAsync(cancellationToken);
        var checkout = await client.V1.Checkout.Sessions.GetAsync(draft.CheckoutId, cancellationToken: cancellationToken);
        if (checkout.Id != draft.CheckoutId || checkout.ClientReferenceId != draft.DraftId ||
            checkout.Metadata.GetValueOrDefault("draft_id") != draft.DraftId ||
            checkout.Metadata.GetValueOrDefault("owner_user_id") != session.UserId ||
            checkout.Metadata.GetValueOrDefault("name") != draft.Name ||
            checkout.Metadata.GetValueOrDefault("plan") != draft.Plan ||
            checkout.Metadata.GetValueOrDefault("interval") != draft.Interval)
        {
            throw new OrganizationCheckoutRefusedException("Stripe checkout does not match this organization draft. Start again.");
        }

        return checkout;
    }

    private async Task<string?> CompletePendingOrganizationAsync(SessionRecord session, HttpContext request, PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        if (draft.State == OrganizationCheckoutStates.Creating)
        {
            return await CompleteCreatingOrganizationAsync(session, draft, cancellationToken);
        }
        if (draft.State is OrganizationCheckoutStates.Canceling or OrganizationCheckoutStates.RefundPending)
        {
            throw new OrganizationCheckoutRefusedException(draft.RefusalReason);
        }

        var checkout = await DraftCheckoutAsync(session, draft, cancellationToken);
        var client = await ClientAsync(cancellationToken);
        var canceled = request.Request.Path == "/organization" && request.Request.Query["newOrganization"] == "payment"
            && request.Request.Query["payment"] == "canceled" && request.Request.Query["draft_id"] == draft.DraftId;
        if (checkout.Status == "open" && canceled)
        {
            request.Items[nameof(PendingOrganizationCheckout)] = draft;
            throw new OrganizationCheckoutRefusedException("Organization checkout canceled.");
        }

        if (checkout.Status == "expired")
        {
            if (canceled)
            {
                request.Items[nameof(PendingOrganizationCheckout)] = draft;
            }

            throw new OrganizationCheckoutRefusedException(canceled ? "Organization checkout canceled." : "Organization checkout expired. Start again.");
        }

        if (checkout.Status != "complete")
        {
            return null;
        }

        if (checkout.Mode != "subscription" || checkout.SubscriptionId is not { Length: > 0 } subscriptionId || checkout.CustomerId is not { Length: > 0 } customerId)
        {
            throw new OrganizationCheckoutRefusedException("Stripe checkout has no subscription or customer. Start again.");
        }

        if (checkout.PaymentStatus == "paid")
        {
            draft = await RecordPaidDraftAsync(draft, cancellationToken);
            if (draft.State != OrganizationCheckoutStates.Pending)
            {
                return await CompletePendingOrganizationAsync(session, request, draft, cancellationToken);
            }
        }

        var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId,
            new() { Expand = ["items.data.price.product", "default_payment_method", "customer"] }, cancellationToken: cancellationToken);
        if (checkout.PaymentStatus != "paid")
        {
            if (subscription.Status is "incomplete_expired" or "canceled")
            {
                throw new OrganizationCheckoutRefusedException("The payment didn't go through. Start organization setup again.");
            }

            return null;
        }

        var item = subscription.Items.Data.Count == 1 ? subscription.Items.Data[0] : null;
        var effective = item is { Quantity: 1 } ? EffectivePlan(item.Price) : null;
        if (subscription.CustomerId != customerId || subscription.Metadata.GetValueOrDefault("draft_id") != draft.DraftId ||
            subscription.Metadata.GetValueOrDefault("owner_user_id") != session.UserId ||
            effective is not { } mapped || mapped.Plan.Key != draft.Plan || mapped.Interval != draft.Interval)
        {
            throw new OrganizationCheckoutRefusedException("Stripe subscription does not match this organization draft. Start again.");
        }

        if (subscription.Status is not ("active" or "trialing"))
        {
            throw new OrganizationCheckoutRefusedException("The payment didn't go through. Start organization setup again.");
        }

        var paymentValid =
            (subscription.DefaultPaymentMethodId is { Length: > 0 } || subscription.Customer is { Deleted: not true, InvoiceSettings.DefaultPaymentMethodId.Length: > 0 });
        if (!paymentValid)
        {
            throw new OrganizationCheckoutRefusedException("The payment didn't go through. Start organization setup again.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await ReadPendingOrganizationAsync(draft.DraftId, cancellationToken);
            if (current is null)
            {
                return (await CheckoutAsync(draft.CheckoutId, cancellationToken))?.OrganizationId;
            }
            if (current.State != OrganizationCheckoutStates.Pending)
            {
                return await CompletePendingOrganizationAsync(session, request, current, cancellationToken);
            }

            current.State = OrganizationCheckoutStates.Creating;
            current.PaidAt = time.GetUtcNow().UtcDateTime;
            current.CustomerId = customerId;
            current.SubscriptionId = subscriptionId;
            current.SubscriptionStatus = subscription.Status;
            current.PlanId = mapped.Plan.Id;
            current.RenewalAt = item!.CurrentPeriodEnd;
            if (await storage.ReplaceAsync(current, cancellationToken))
            {
                return await CompleteCreatingOrganizationAsync(session, current, cancellationToken);
            }
        }
        throw new IOException("Organization creation claim changed concurrently. Retry organization setup.");
    }

    private async Task<string> CompleteCreatingOrganizationAsync(SessionRecord session, PendingOrganizationCheckout draft, CancellationToken cancellationToken)
    {
        try
        {
            var organization = await accounts.CompleteBusinessAsync(session, draft, cancellationToken);
            await plans.PublishAsync(organization, cancellationToken);
            return organization.Id;
        }
        catch (Exception error) when (error is AccountAccessException or HttpRequestException or IOException or StripeException or OperationCanceledException)
        {
            telemetry.ReportOrganizationCreationFailure();
            throw;
        }
    }
}

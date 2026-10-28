using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;
using AccountService = TerraScale.Dashboard.Shared.Accounts.AccountService;
using TerraScale.Dashboard.Shared.Validation;

namespace TerraScale.Dashboard.Features.Billing;

// Every entitlement is derived from a bound checkout and Stripe's current
// subscription. Browser return parameters and webhook snapshots grant nothing.
public sealed partial class StripeBilling(IDashboardIdentity identity, IDashboardState state, AccountService accounts,
    IHttpClientFactory http, TimeProvider time, TerraBasePlanSync plans, ILogger<StripeBilling> logger)
{
    public const string Collection = "dashboard_billing";
    public const string CustomersCollection = "dashboard_stripe_customers";
    internal const string Origin = "https://dash.terrascale.tech";
    internal static IReadOnlySet<string> WebhookEvents { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed",
        "checkout.session.expired", "customer.subscription.created", "customer.subscription.updated",
        "customer.subscription.deleted", "customer.subscription.paused", "customer.subscription.resumed",
        "customer.subscription.pending_update_applied", "customer.subscription.pending_update_expired",
        "customer.subscription.trial_will_end", "invoice.finalized", "invoice.paid", "invoice.payment_succeeded",
        "invoice.payment_failed", "invoice.payment_action_required", "invoice.marked_uncollectible", "invoice.voided"
    };

    public Task<IReadOnlyList<BillingPlan>> PlansAsync(CancellationToken cancellationToken) => Task.FromResult(PlanCatalog.Plans);

    public async Task<BillingBinding?> ReadBindingAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await MemberAsync(context, cancellationToken);
        return await ReadAsync(context.Organization!.Id, cancellationToken);
    }

    public async Task<string> StartCheckoutAsync(AccountContext context, string planKey, string interval, string returnPath, CancellationToken cancellationToken)
    {
        context = await OwnerAsync(context, cancellationToken);
        var plan = PlanCatalog.For(planKey);
        var org = context.Organization!;
        if (plan.PriceCents == 0)
            throw new OnboardingValidationException(plan.Name + " does not require Stripe checkout.");
        if (!plan.SupportsInterval(interval))
            throw new OnboardingValidationException("Choose the published billing interval for this plan.");
        if (!ValidCheckoutReturnPath(context, returnPath))
            throw new OnboardingValidationException("Choose a valid checkout return path.");
        var client = await ClientAsync(cancellationToken);
        var existing = await ReadAsync(org.Id, cancellationToken);
        if (existing is not null)
        {
            RequireStripe(existing);
            if (existing.State == "deleting") throw new IOException("Billing deletion is pending.");
        }
        if (existing?.SubscriptionId is { Length: > 0 })
        {
            await RefreshAsync(org.Id, cancellationToken);
            existing = await ReadAsync(org.Id, cancellationToken);
            if (existing is not null && existing.Status is not ("canceled" or "incomplete_expired"))
                throw new OnboardingValidationException("Manage your existing subscription from the billing portal.");
        }
        var binding = await ReserveAsync(org.Id, context.Project?.Id ?? "", plan.Key, interval, returnPath, cancellationToken);
        var metadata = new Dictionary<string, string>
        {
            ["organization_id"] = org.Id,
            ["owner_user_id"] = context.UserId,
            ["plan"] = plan.Key,
            ["interval"] = interval,
            ["request_id"] = binding.RequestId
        };
        if (binding.CustomerId.Length == 0)
        {
            var customer = await client.V1.Customers.CreateAsync(new()
            {
                Name = org.Name,
                Email = context.Account.Email,
                Metadata = new() { ["organization_id"] = org.Id }
            }, Idempotent("customer-" + org.Id), cancellationToken);
            binding = await UpdateAsync(org.Id, current =>
            {
                if (current.CustomerId.Length != 0 && current.CustomerId != customer.Id) throw new IOException("Billing customer changed.");
                current.CustomerId = customer.Id;
            }, cancellationToken);
        }
        // Resume the separate customer index write after an interrupted first
        // checkout. Its create-only ownership binding must never be reassigned.
        try
        {
            await state.PutAsync(CustomersCollection, binding.CustomerId, JsonSerializer.SerializeToElement(new CustomerBinding(org.Id)), TerraBasePrecondition.Absent, cancellationToken);
        }
        catch (TerraBasePreconditionFailedException)
        {
            var index = await state.GetAsync(CustomersCollection, binding.CustomerId, cancellationToken);
            if (index is null || AccountService.Read<CustomerBinding>(index).OrganizationId != org.Id) throw new IOException("Billing customer ownership changed.");
        }
        if (binding.CheckoutId.Length != 0)
        {
            var pending = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
            ValidateCheckout(binding, pending);
            await IndexCheckoutAsync(binding, cancellationToken);
            if (pending.Status == "open" && pending.Url is { Length: > 0 }) return pending.Url;
            if (pending.Status == "complete") return CheckoutReturnUrl(pending.Id);
            throw new OnboardingValidationException("This checkout expired. Start a new checkout.");
        }
        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Customer = binding.CustomerId,
            ClientReferenceId = org.Id,
            Metadata = metadata,
            SuccessUrl = Origin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}",
            CancelUrl = Origin + returnPath + (returnPath.Contains('?') ? "&" : "?") + "payment=canceled&plan=" + Uri.EscapeDataString(plan.Key),
            Mode = plan.PriceCents == 0 ? "setup" : "subscription",
            BillingAddressCollection = "required",
            ExpiresAt = binding.StartedAt.UtcDateTime.AddHours(1)
        };
        if (plan.PriceCents > 0)
        {
            var price = await PriceAsync(client, plan, interval, cancellationToken);
            options.LineItems = [new() { Price = price.Id, Quantity = 1 }];
            options.PaymentMethodCollection = "always";
            // Invoices carry the customer's name, address and tax IDs. Stripe Tax (automatic_tax) stays off: the account's
            // country is not eligible yet, and Stripe refuses every Checkout Session that asks for it (docs/billing/stripe-tax-handoff.md).
            options.TaxIdCollection = new() { Enabled = true };
            options.CustomerUpdate = new() { Address = "auto", Name = "auto" };
            options.SubscriptionData = new() { Metadata = metadata, TrialPeriodDays = binding.TrialUsed || plan.TrialDays == 0 ? null : plan.TrialDays };
        }
        var checkout = await client.V1.Checkout.Sessions.CreateAsync(options,
            Idempotent("checkout-" + binding.RequestId), cancellationToken);
        ValidateCheckout(binding, checkout);
        try
        {
            binding = await UpdateAsync(org.Id, current =>
            {
                if (current.RequestId != binding.RequestId || current.CustomerId != checkout.CustomerId) throw new IOException("Checkout reservation changed.");
                if (current.CheckoutId.Length != 0 && current.CheckoutId != checkout.Id) throw new IOException("Checkout identity changed.");
                current.CheckoutId = checkout.Id;
            }, cancellationToken);
        }
        catch
        {
            // A concurrent caller may already have persisted this same
            // idempotent session. Never expire a successfully bound checkout.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                var current = await ReadAsync(org.Id, cleanup.Token);
                if (current?.CheckoutId != checkout.Id)
                    await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, cancellationToken: cleanup.Token);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or StripeException or OperationCanceledException) { }
            throw;
        }
        await IndexCheckoutAsync(binding, cancellationToken);
        return checkout.Url is { Length: > 0 } url ? url : throw new HttpRequestException("Stripe returned no checkout URL.");
    }

    public async Task<bool> ConfirmAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await OwnerAsync(context, cancellationToken);
        return await RefreshAsync(context.Organization!.Id, cancellationToken);
    }

    // Reloading billing finishes a checkout whose browser return or webhook
    // never reconciled it. Only Stripe's canonical facts grant the plan.
    public async Task<bool> ReconcileCheckoutAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await MemberAsync(context, cancellationToken);
        if (context.Organization!.InternalBilling is not null ||
            await ReadAsync(context.Organization.Id, cancellationToken) is not { Kind: "stripe", CheckoutId.Length: > 0, SubscriptionId.Length: 0, Status: "pending" }) return false;
        await RefreshAsync(context.Organization.Id, cancellationToken);
        return true;
    }

    // PlanReconciler finishes a checkout paid in the last day whose webhook and browser return both went missing.
    internal async Task<bool> ReconcilePendingCheckoutAsync(BillingBinding binding, CancellationToken cancellationToken)
    {
        if (binding is not { Kind: "stripe", CheckoutId.Length: > 0, SubscriptionId.Length: 0, Status: "pending" } ||
            time.GetUtcNow() - binding.StartedAt > TimeSpan.FromDays(1)) return false;
        return await RefreshAsync(binding.OrganizationId, cancellationToken);
    }

    public async Task<string> PortalAsync(AccountContext context, CancellationToken cancellationToken)
    {
        context = await OwnerAsync(context, cancellationToken);
        var binding = await ReadAsync(context.Organization!.Id, cancellationToken);
        if (binding is null || binding.CustomerId.Length == 0) throw new AccountAccessException("Set up billing before opening the customer portal.", 422);
        var client = await ClientAsync(cancellationToken);
        var portal = await client.V1.BillingPortal.Sessions.CreateAsync(new()
        {
            Customer = binding.CustomerId,
            ReturnUrl = Origin + "/billing"
        }, cancellationToken: cancellationToken);
        return portal.Url;
    }

    public async Task HandleWebhookAsync(string body, string signature, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var webhookSecret = credentials.Stripe?.WebhookSecret ?? throw new IOException("Stripe identity is unavailable.");
        Event @event;
        try { @event = EventUtility.ConstructEvent(body, signature, webhookSecret); }
        catch (Exception error) when (error is StripeException or System.Text.Json.JsonException or Newtonsoft.Json.JsonException)
        { throw new StripeWebhookRejectedException(); }
        if (@event.Livemode != credentials.Stripe!.IsLive)
            throw new StripeWebhookRejectedException();
        if (!WebhookEvents.Contains(@event.Type)) return;
        var customerId = @event.Data.Object switch
        {
            Stripe.Checkout.Session checkout => checkout.CustomerId,
            Subscription subscription => subscription.CustomerId,
            Invoice invoice => invoice.CustomerId,
            _ => null
        };
        if (string.IsNullOrEmpty(customerId)) return;
        var customer = await state.GetAsync(CustomersCollection, customerId, cancellationToken);
        if (customer is null) return;
        var orgId = AccountService.Read<CustomerBinding>(customer).OrganizationId;
        var binding = await ReadAsync(orgId, cancellationToken);
        try
        {
            if (binding?.CustomerId != customerId) throw new IOException("Billing index does not match its customer.");
            await RefreshAsync(orgId, cancellationToken);
        }
        // Not applied: the endpoint answers 503 so Stripe redelivers it; a redelivery re-reads Stripe's current state.
        catch (Exception error) when (error is IOException or HttpRequestException or TimeoutException or StripeException ||
                                      error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        { throw new StripeWebhookDeferredException(@event.Id, @event.Type, error); }
        logger.LogInformation("Stripe event {EventId} ({EventType}) applied for organization {OrganizationId}", @event.Id, @event.Type, orgId);
    }

    // Organization deletion ends the organization's Stripe subscription now, without proration.
    // A live owner preflight or a completed auth deletion proof authorizes the scope.
    internal async Task<BillingBinding?> EndSubscriptionAsync(string orgId, CancellationToken cancellationToken, bool completedDeletion = false, bool authorizedDeletionPreflight = false)
    {
        var document = await state.GetAsync(Collection, orgId, cancellationToken);
        if (document is null || AccountService.IsDeletionFence(document)) return null;
        var binding = ReadBinding(document, orgId);
        if (binding.Kind != "stripe") return binding;
        var terminalDeletion = completedDeletion || authorizedDeletionPreflight;
        if (terminalDeletion)
            binding = await UpdateAsync(orgId, current => current.State = "deleting", cancellationToken, allowDeletion: true);
        // Creation may have reached Stripe before the Checkout ID's CAS write.
        // Its durable reservation cannot certify absence of a subscription.
        if (binding.CheckoutId.Length == 0 && binding.SubscriptionId.Length == 0 && !terminalDeletion)
            throw new IOException("Checkout creation has not been reconciled.");
        if (terminalDeletion) await EndCustomersForDeletionAsync(orgId, binding.CustomerId.Length == 0 ? [] : [binding.CustomerId], cancellationToken);
        var client = await ClientAsync(cancellationToken);
        var subscriptions = new HashSet<string>(StringComparer.Ordinal);
        if (binding.SubscriptionId.Length > 0) subscriptions.Add(binding.SubscriptionId);
        if (binding.CheckoutId.Length > 0)
        {
            // The webhook/browser return may not have persisted a completed
            // Checkout's subscription yet. Keep its binding until it is terminal.
            var checkout = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
            ValidateCheckout(binding, checkout);
            if (checkout.Status == "open")
            {
                checkout = await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, new(), Idempotent("delete-checkout-" + checkout.Id), cancellationToken);
                ValidateCheckout(binding, checkout);
            }
            if (checkout.Status == "complete" && checkout.Mode == "subscription")
            {
                if (checkout.SubscriptionId is not { Length: > 0 }) throw new IOException("Completed checkout subscription is unavailable.");
                subscriptions.Add(checkout.SubscriptionId);
            }
            else if (checkout.Status != "expired" && !(checkout.Status == "complete" && checkout.Mode == "setup"))
                throw new IOException("Checkout cancellation is incomplete.");
        }
        foreach (var subscriptionId in subscriptions)
        {
            var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId, cancellationToken: cancellationToken);
            if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId)
                throw new IOException("Subscription ownership does not match.");
            if (subscription.Status is not ("canceled" or "incomplete_expired"))
            {
                subscription = await client.V1.Subscriptions.CancelAsync(subscription.Id, new(), Idempotent("delete-organization-" + subscription.Id), cancellationToken);
                if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId || subscription.Status is not ("canceled" or "incomplete_expired"))
                    throw new IOException("Subscription cancellation is incomplete.");
            }
        }
        return await UpdateAsync(orgId, current =>
        {
            if (current.RequestId != binding.RequestId || current.CheckoutId != binding.CheckoutId || current.SubscriptionId != binding.SubscriptionId)
                throw new IOException("Billing deletion scope changed.");
            current.Status = "canceled"; current.PaymentValidated = false; current.RenewalAt = null;
        }, cancellationToken, allowDeletion: terminalDeletion);
    }

    // Stripe delivers a checkout's events together, and the return page and reconciler refresh the same organization;
    // one refresh per organization at a time keeps them from exhausting each other's conditional-write retries.
    private readonly SemaphoreSlim[] refreshing = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    private async Task<bool> RefreshAsync(string orgId, CancellationToken cancellationToken)
    {
        var turn = refreshing[(StringComparer.Ordinal.GetHashCode(orgId) & int.MaxValue) % refreshing.Length];
        await turn.WaitAsync(cancellationToken);
        try { return await RefreshOnceAsync(orgId, cancellationToken); }
        finally { turn.Release(); }
    }

    private async Task<bool> RefreshOnceAsync(string orgId, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var billingDocument = await state.GetAsync(Collection, orgId, cancellationToken);
            if (billingDocument is null) return false;
            var binding = ReadBinding(billingDocument, orgId);
            RequireStripe(binding);
            if (binding.State == "deleting") return false;
            if (binding.CheckoutId.Length == 0 && binding.SubscriptionId.Length == 0) return false;
            var client = await ClientAsync(cancellationToken);
            // Capture both versions before Stripe reads. A conflict in either
            // projection requires fresh canonical facts, never a stale replay.
            var organizationDocument = await state.GetAsync(AccountService.OrganizationsCollection, orgId, cancellationToken)
                ?? throw new IOException("Billing organization is unavailable.");
            var org = AccountService.Read<OrganizationRecord>(organizationDocument);
            if (org.Id != orgId) throw new IOException("Billing organization changed.");
            var previousPlanId = org.PlanId;
            Stripe.Checkout.Session? checkout = null;
            if (binding.SubscriptionId.Length == 0 && binding.CheckoutId.Length > 0)
            {
                checkout = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId,
                    new() { Expand = ["setup_intent"] }, cancellationToken: cancellationToken);
                ValidateCheckout(binding, checkout);
            }
            BillingPlan plan;
            string effectiveInterval;
            var paymentValid = false;
            var unmappedPrice = false;
            DateTimeOffset? renewalAt = null;
            string status;
            var subscriptionId = binding.SubscriptionId.Length > 0 ? binding.SubscriptionId :
                checkout is { Status: "complete", Mode: "subscription", SubscriptionId.Length: > 0 } ? checkout.SubscriptionId : "";
            if (subscriptionId.Length > 0)
            {
                // The persisted subscription is an independent binding. Portal
                // plan changes and cancellation remain effective even while a
                // later checkout is open or expired.
                var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId,
                    new() { Expand = ["items.data.price.product", "default_payment_method", "customer"] }, cancellationToken: cancellationToken);
                if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId)
                    throw new IOException("Subscription ownership does not match.");
                var item = subscription.Items.Data.Count == 1 ? subscription.Items.Data[0] : null;
                var mapped = item is { Quantity: 1 } ? EffectivePlan(item.Price) : null;
                unmappedPrice = mapped is null;
                (plan, effectiveInterval) = mapped ?? (PlanCatalog.For("free"), "");
                status = subscription.Status;
                // Invoices fall back to the customer's default, which the billing portal sets when a card is made default.
                paymentValid = !unmappedPrice && (status is "active" or "trialing") && (subscription.DefaultPaymentMethodId is { Length: > 0 } ||
                    subscription.Customer is { Deleted: not true, InvoiceSettings.DefaultPaymentMethodId.Length: > 0 });
                if (item is not null && status is not ("canceled" or "incomplete_expired")) renewalAt = item.CurrentPeriodEnd;
            }
            else
            {
                if (checkout?.Status != "complete") return false;
                plan = PlanCatalog.For(binding.Plan);
                if (plan.PriceCents != 0) throw new IOException("Checkout subscription is unavailable.");
                if (org.Kind != "personal") throw new IOException("Free setup is unavailable for this organization.");
                effectiveInterval = binding.Interval;
                paymentValid = checkout.Mode == "setup" && checkout.SetupIntent is { Status: "succeeded", PaymentMethodId.Length: > 0 };
                status = paymentValid ? "active" : "incomplete";
            }
            var previousEffectivePlan = binding.EffectivePlan;
            var previousEffectiveInterval = binding.EffectiveInterval;
            binding.SubscriptionId = subscriptionId;
            binding.EffectivePlan = plan.Key;
            binding.EffectiveInterval = effectiveInterval;
            binding.RenewalAt = renewalAt;
            binding.PaymentValidated = paymentValid;
            binding.Status = status;
            binding.TrialUsed |= status is "active" or "trialing";
            binding.Revision = checked(binding.Revision + 1);
            org.PaymentMethodValidated = paymentValid;
            if (paymentValid)
            {
                org.PlanId = plan.Id;
                org.BillingStatus = "active";
            }
            else if (unmappedPrice && (status is "active" or "trialing"))
            {
                // Keep Stripe's canonical subscription binding for portal and
                // replacement decisions, but revoke an unqualified plan even
                // when this organization previously held paid entitlements.
                if (org.Kind == "personal") org.PlanId = OrganizationRecord.FreePlanId;
                org.BillingStatus = "billing_required";
                binding.EffectivePlan = "";
                binding.EffectiveInterval = "";
            }
            else if (org.Kind == "personal" && status is not ("past_due" or "unpaid" or "paused"))
            {
                // Personal workspaces retain Free access when a subscription
                // ends or its first payment has not confirmed.
                org.PlanId = OrganizationRecord.FreePlanId;
                org.BillingStatus = "active";
                binding.EffectivePlan = "free";
                binding.EffectiveInterval = "";
                binding.RenewalAt = null;
            }
            else
            {
                // Dunning preserves the last granted plan while Stripe retries
                // payment. Business workspaces require billing attention.
                org.BillingStatus = "billing_required";
                binding.EffectivePlan = previousEffectivePlan;
                binding.EffectiveInterval = previousEffectiveInterval;
            }
            // Stripe reports the change; the plan it grants joins the organization's audit history.
            if (org.PlanId != previousPlanId)
                org.AuditEvents.Add(new(AccountService.NewId(), "billing service", "billing.plan_changed", PlanCatalog.Plans.FirstOrDefault(item => item.Id == org.PlanId)?.Name ?? org.PlanId, "plan", time.GetUtcNow()));
            try
            {
                await state.PutAsync(Collection, orgId, JsonSerializer.SerializeToElement(binding), TerraBasePrecondition.Matches(billingDocument.Version), cancellationToken);
                await state.PutAsync(AccountService.OrganizationsCollection, orgId, JsonSerializer.SerializeToElement(org), TerraBasePrecondition.Matches(organizationDocument.Version), cancellationToken);
                // The two records have separate CAS versions. If another
                // reconciliation advanced billing while we projected it,
                // reconcile again before reporting success.
                var current = await ReadAsync(orgId, cancellationToken);
                if (current?.RequestId == binding.RequestId && current.Revision == binding.Revision)
                {
                    // Webhooks change plans without a dashboard view; publish the stored projects' plan now.
                    await plans.PublishAsync(org, cancellationToken);
                    return paymentValid;
                }
            }
            // Another writer (an account view, audit or plan publication) moved a record: re-read after a jittered pause.
            catch (TerraBasePreconditionFailedException) when (attempt < 3)
            { await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 150) << attempt), time, cancellationToken); }
        }
        throw new IOException("Billing changed concurrently. Retry reconciliation.");
    }

    private async Task<BillingBinding> ReserveAsync(string orgId, string projectId, string plan, string interval, string returnPath, CancellationToken cancellationToken)
    {
        var initial = new BillingBinding
        {
            OrganizationId = orgId,
            CheckoutProjectId = projectId,
            Plan = plan,
            Interval = interval,
            ReturnPath = returnPath,
            RequestId = Guid.CreateVersion7().ToString(),
            StartedAt = time.GetUtcNow()
        };
        try { await state.PutAsync(Collection, orgId, JsonSerializer.SerializeToElement(initial), TerraBasePrecondition.Absent, cancellationToken); return initial; }
        catch (TerraBasePreconditionFailedException) { }
        var existing = await ReadAsync(orgId, cancellationToken) ?? throw new IOException("Billing reservation did not persist.");
        RequireStripe(existing);
        if (existing.State == "deleting") throw new IOException("Billing deletion is pending.");
        var endedSubscription = existing.SubscriptionId.Length > 0 && existing.Status is "canceled" or "incomplete_expired";
        if (!endedSubscription && existing.StartedAt.AddHours(1) > time.GetUtcNow())
        {
            if (existing.Plan != plan || existing.Interval != interval || existing.ReturnPath != returnPath)
                throw new OnboardingValidationException("Finish the current checkout before choosing another plan.");
            return existing;
        }
        // A pending checkout must expire before replacement. A completed
        // checkout whose subscription ended can be replaced immediately,
        // preserving the customer's binding.
        return await UpdateAsync(orgId, current =>
        {
            if (current.RequestId != existing.RequestId) throw new AccountAccessException("Another checkout is in progress.", 409);
            if (current.SubscriptionId.Length > 0 && current.Status is not ("canceled" or "incomplete_expired"))
                throw new OnboardingValidationException("Manage your existing subscription from the billing portal.");
            current.Plan = plan; current.Interval = interval; current.ReturnPath = returnPath;
            current.CheckoutProjectId = initial.CheckoutProjectId;
            current.RequestId = initial.RequestId; current.StartedAt = initial.StartedAt; current.CheckoutId = "";
            current.SubscriptionId = ""; current.EffectivePlan = ""; current.EffectiveInterval = "";
            current.RenewalAt = null;
            current.PaymentValidated = false; current.Status = "pending";
        }, cancellationToken);
    }

    private async Task<BillingBinding?> ReadAsync(string orgId, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(Collection, orgId, cancellationToken);
        if (document is null) return null;
        return ReadBinding(document, orgId);
    }

    internal static BillingBinding ReadBinding(TerraBaseDocument document, string orgId)
    {
        var value = AccountService.Read<BillingBinding>(document);
        if (document.Id != orgId || value.OrganizationId != orgId || value.Revision < 0 || string.IsNullOrEmpty(value.RequestId) ||
            value.CustomerId is null || value.CheckoutId is null || value.CheckoutProjectId is null || value.ReturnPath is null || value.SubscriptionId is null || value.Status is null || value.Plan is null || value.Interval is null || value.EffectivePlan is null || value.EffectiveInterval is null)
            throw new IOException("Billing scope is invalid.");
        if (value.Kind == "internal")
        {
            var reservation = value.InternalReservation;
            if (value.State is not ("reserved" or "active") || reservation is null || reservation.SpendCapCents <= 0 || reservation.PreviousConfigurationId is null ||
                reservation.Policy is null || reservation.Policy.ConfigurationId != value.RequestId ||
                value.CustomerId.Length != 0 || value.CheckoutId.Length != 0 || value.SubscriptionId.Length != 0 ||
                value.CheckoutProjectId.Length != 0 || value.ReturnPath.Length != 0 || value.Plan != "internal" || value.Interval != "monthly")
                throw new IOException("Internal billing reservation is invalid.");
            reservation.Policy.Validate(new() { Id = orgId, PlanId = reservation.Policy.PlanId });
        }
        else if (value.Kind != "stripe" || value.InternalReservation is not null) throw new IOException("Billing ownership kind is invalid.");
        return value;
    }

    internal static void RequireStripe(BillingBinding binding)
    {
        if (binding.Kind != "stripe") throw new AccountAccessException("Internal billing reports usage without Stripe charges.");
    }

    private async Task<BillingBinding> UpdateAsync(string orgId, Action<BillingBinding> update, CancellationToken cancellationToken, bool allowDeletion = false)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var document = await state.GetAsync(Collection, orgId, cancellationToken) ?? throw new IOException("Billing state is unavailable.");
            var binding = ReadBinding(document, orgId);
            RequireStripe(binding);
            if (binding.State == "deleting" && !allowDeletion) throw new IOException("Billing deletion is pending.");
            update(binding);
            try { await state.PutAsync(Collection, orgId, JsonSerializer.SerializeToElement(binding), TerraBasePrecondition.Matches(document.Version), cancellationToken); return binding; }
            catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
        }
        throw new IOException("Billing changed concurrently.");
    }

    private async Task<AccountContext> OwnerAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var current = await MemberAsync(context, cancellationToken);
        if (current.Role != "owner") throw new AccountAccessException("Only organization owners manage billing.");
        if (current.Organization!.DeletionRequested) throw new AccountAccessException("Organization deletion is pending. Billing changes are unavailable.");
        if (current.Organization.InternalBilling is not null) throw new AccountAccessException("Internal billing reports usage without Stripe charges.");
        var binding = await ReadAsync(current.Organization.Id, cancellationToken);
        if (binding is not null) RequireStripe(binding);
        return current;
    }

    private async Task<AccountContext> MemberAsync(AccountContext context, CancellationToken cancellationToken)
    {
        var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
        await InternalBillingService.ValidateClaimsAsync(state, fresh.Organization!, cancellationToken);
        return fresh;
    }

    private async Task<StripeClient> ClientAsync(CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var secret = credentials.Stripe?.SecretKey ?? throw new IOException("Stripe identity is unavailable.");
        return new StripeClient(secret, httpClient: new SystemNetHttpClient(http.CreateClient("platform"), enableTelemetry: false));
    }

    private static RequestOptions Idempotent(string key) => new() { IdempotencyKey = "TerraScale.Dashboard/" + key };

    private static void ValidateCheckout(BillingBinding binding, Stripe.Checkout.Session checkout)
    {
        if (binding.CheckoutId.Length > 0 && checkout.Id != binding.CheckoutId || checkout.CustomerId != binding.CustomerId || checkout.ClientReferenceId != binding.OrganizationId ||
            checkout.Metadata.GetValueOrDefault("organization_id") != binding.OrganizationId ||
            checkout.Metadata.GetValueOrDefault("plan") != binding.Plan || checkout.Metadata.GetValueOrDefault("interval") != binding.Interval ||
            checkout.Metadata.GetValueOrDefault("request_id") != binding.RequestId)
            throw new IOException("Stripe checkout does not match its persisted binding.");
    }

    internal static bool ValidCheckoutReturnPath(AccountContext context, string path) =>
        ValidCheckoutReturnPath(context.Organization?.Id, context.Project?.Id, path);

    private static bool ValidCheckoutReturnPath(string? organizationId, string? projectId, string path)
    {
        if (path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl)) return false;
        var queryAt = path.IndexOf('?');
        var basePath = queryAt < 0 ? path : path[..queryAt];
        if (basePath is not ("/onboarding" or "/billing" or "/organization")) return false;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryAt < 0 ? "" : path[queryAt..]);
        if (query.Any(pair => pair.Value.Count != 1)) return false;
        if (query.TryGetValue("organizationId", out var returnOrganizationId) && returnOrganizationId != organizationId) return false;
        if (query.TryGetValue("projectId", out var returnProjectId) && (string.IsNullOrEmpty(projectId) || returnProjectId != projectId)) return false;
        // The legacy wizard ID differs only in case from organizationId; ParseQuery treats them as one key.
        if (basePath == "/organization")
        {
            if (query.TryGetValue("tab", out var tab) && tab != "billing") return false;
            if (query.TryGetValue("newOrganization", out var step))
            {
                if (step != "project" || !query.TryGetValue("organizationId", out var wizardId) || wizardId != organizationId) return false;
            }
            else if (query.GetValueOrDefault("tab") != "billing") return false;
        }
        return query.Keys.All(key => key.Equals("organizationId", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("projectId", StringComparison.OrdinalIgnoreCase) || basePath == "/organization" &&
            (key.Equals("tab", StringComparison.OrdinalIgnoreCase) || key.Equals("newOrganization", StringComparison.OrdinalIgnoreCase)));
    }

    private static async Task<Price> PriceAsync(StripeClient client, BillingPlan plan, string interval, CancellationToken cancellationToken)
    {
        Price? result = null;
        await foreach (var price in client.V1.Prices.ListAutoPagingAsync(new() { Active = true, Currency = "usd", Type = "recurring", Limit = 100, Expand = ["data.product"] }, cancellationToken: cancellationToken))
        {
            if (!MatchesPrice(price, plan, interval, requirePublished: true)) continue;
            if (result is not null) throw new HttpRequestException("Stripe has ambiguous prices for this plan.");
            result = price;
        }
        return result ?? throw new HttpRequestException("Stripe has no published price for this plan.");
    }

    internal static bool MatchesPrice(Price price, BillingPlan plan, string interval, bool requirePublished = false) =>
        plan.SupportsInterval(interval) && price.Recurring?.UsageType != "metered" && (!requirePublished || price.Active) && price.Currency == "usd" && price.UnitAmount == (interval == "annual" ? plan.AnnualPriceCents : plan.PriceCents) &&
        price.Recurring?.Interval == (interval == "annual" ? "year" : "month") && price.Recurring.IntervalCount == 1 &&
        price.Product is { } product && (!requirePublished || product.Active) && (product.Metadata.GetValueOrDefault("plan") == plan.Key ||
            product.Name == plan.Name || product.Name == "TerraScale " + plan.Name);

    private static (BillingPlan Plan, string Interval)? EffectivePlan(Price? price)
    {
        if (price is null) return null;
        var matches = (from plan in PlanCatalog.Plans
                       where plan.PriceCents > 0
                       from interval in new[] { plan.CheckoutInterval }
                       where MatchesPrice(price, plan, interval)
                       select (plan, interval)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}

public sealed class BillingBinding
{
    public string Kind { get; set; } = "stripe";
    public string State { get; set; } = "reserved";
    public InternalBillingReservation? InternalReservation { get; set; }
    public string OrganizationId { get; set; } = "";
    public string CustomerId { get; set; } = "";
    public string Plan { get; set; } = "";
    public string Interval { get; set; } = "";
    public string ReturnPath { get; set; } = "";
    public string RequestId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public string CheckoutId { get; set; } = "";
    public string CheckoutProjectId { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string EffectivePlan { get; set; } = "";
    public string EffectiveInterval { get; set; } = "";
    public DateTimeOffset? RenewalAt { get; set; }
    public string Status { get; set; } = "pending";
    public bool PaymentValidated { get; set; }
    public bool TrialUsed { get; set; }
    public long Revision { get; set; }
}
public sealed record CustomerBinding(string OrganizationId);
public sealed class StripeWebhookRejectedException : Exception;

// A verified event this dashboard could not apply; Stripe redelivers it after a non-2xx answer.
public sealed class StripeWebhookDeferredException(string eventId, string eventType, Exception inner) : Exception("Stripe event was not applied.", inner)
{
    public string EventId { get; } = eventId;
    public string EventType { get; } = eventType;
}

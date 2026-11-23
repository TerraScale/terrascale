using Dapper;
using System.Data;
using System.Text.Json;
using Stripe;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.Dashboard.Platform.Observability;
using TerraScale.Dashboard.Platform.Clients;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;
using TerraScale.Dashboard.Common.Validation;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Billing
{
    public readonly union WebhookReceipt(Done, Rejected, Deferred);

    public sealed partial class StripeBilling(IDashboardIdentity identity, DashboardDb storage, OrganizationStore organizations, AccountService accounts,
        IHttpClientFactory http, TimeProvider time, TerraBasePlanSync plans, ILogger<StripeBilling> logger, ServiceSloTelemetry telemetry)
    {
        internal const string Origin = "https://dash.terrascale.tech";
        internal static readonly TimeSpan CheckoutLifetime = TimeSpan.FromHours(1);
        private static readonly TimeSpan CheckoutCreationWindow = TimeSpan.FromMinutes(15);
        internal static IReadOnlySet<string> WebhookEvents { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "checkout.session.completed", "checkout.session.async_payment_succeeded", "checkout.session.async_payment_failed",
            "checkout.session.expired", "customer.subscription.created", "customer.subscription.updated",
            "customer.subscription.deleted", "customer.subscription.paused", "customer.subscription.resumed",
            "customer.subscription.pending_update_applied", "customer.subscription.pending_update_expired",
            "customer.subscription.trial_will_end", "invoice.finalized", "invoice.paid", "invoice.payment_succeeded",
            "invoice.payment_failed", "invoice.payment_action_required", "invoice.marked_uncollectible", "invoice.voided"
        };

        public Task<IReadOnlyList<BillingPlan>> PlansAsync(CancellationToken _)
        {
            return Task.FromResult(PlanCatalog.Plans);
        }

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
            {
                throw new OnboardingValidationException(plan.Name + " does not require Stripe checkout.");
            }

            if (!plan.SupportsInterval(interval))
            {
                throw new OnboardingValidationException("Choose the published billing interval for this plan.");
            }

            if (!ValidCheckoutReturnPath(context, returnPath))
            {
                throw new OnboardingValidationException("Choose a valid checkout return path.");
            }

            var client = await ClientAsync(cancellationToken);
            var existing = await ReadAsync(org.Id, cancellationToken);
            if (existing is not null)
            {
                RequireStripe(existing);
                if (existing.State == "deleting")
                {
                    throw new IOException("Billing deletion is pending.");
                }
            }
            if (existing?.SubscriptionId is { Length: > 0 })
            {
                await RefreshAsync(org.Id, cancellationToken);
                existing = await ReadAsync(org.Id, cancellationToken);
                if (existing is not null && existing.Status is not ("canceled" or "incomplete_expired"))
                {
                    throw new OnboardingValidationException("Manage your existing subscription from the billing portal.");
                }
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
                }, Idempotent("customer-" + binding.RequestId), cancellationToken);
                binding = await UpdateAsync(org.Id, current =>
                {
                    if (current.RequestId != binding.RequestId)
                    {
                        throw new IOException("Checkout reservation changed.");
                    }

                    if (current.CustomerId.Length != 0 && current.CustomerId != customer.Id)
                    {
                        throw new IOException("Billing customer changed.");
                    }

                    current.CustomerId = customer.Id;
                }, cancellationToken, written: binding);
            }
            if (!await storage.InsertAsync(new StripeCustomerRow { Id = AccountService.NewId(), CustomerId = binding.CustomerId, OrganizationId = org.Id, CreatedAt = time.GetUtcNow().UtcDateTime }, cancellationToken))
            {
                var index = await CustomerAsync(binding.CustomerId, cancellationToken);
                if (index is null || index.OrganizationId != org.Id)
                {
                    throw new IOException("Billing customer ownership changed.");
                }
            }
            if (binding.CheckoutId.Length != 0)
            {
                var pending = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
                ValidateCheckout(binding, pending);
                await IndexCheckoutAsync(binding, cancellationToken);
                if (pending.Status == "open" && pending.Url is { Length: > 0 })
                {
                    return pending.Url;
                }

                if (pending.Status == "complete")
                {
                    return CheckoutReturnUrl(pending.Id);
                }

                throw new OnboardingValidationException("This checkout expired. Start a new checkout.");
            }
            var options = CheckoutOptions(plan);
            options.Customer = binding.CustomerId;
            options.ClientReferenceId = org.Id;
            options.Metadata = metadata;
            options.SuccessUrl = Origin + "/billing/checkout/return?session_id={CHECKOUT_SESSION_ID}";
            options.CancelUrl = Origin + returnPath + (returnPath.Contains('?') ? "&" : "?") + "payment=canceled&plan=" + Uri.EscapeDataString(plan.Key);
            options.ExpiresAt = (binding.StartedAt + CheckoutLifetime).UtcDateTime;
            if (plan.PriceCents > 0)
            {
                var price = await PriceAsync(client, plan, interval, cancellationToken);
                options.LineItems = [new() { Price = price.Id, Quantity = 1 }];
                options.SubscriptionData = new() { Metadata = new(metadata) { ["terrascale_tax"] = "automatic" }, TrialPeriodDays = binding.TrialUsed || plan.TrialDays == 0 ? null : plan.TrialDays };
            }
            var checkout = await client.V1.Checkout.Sessions.CreateAsync(options,
                Idempotent("checkout-" + binding.RequestId), cancellationToken);
            try
            {
                ValidateCheckout(binding, checkout);
                binding = await UpdateAsync(org.Id, current =>
                {
                    if (current.RequestId != binding.RequestId || current.CustomerId != checkout.CustomerId)
                    {
                        throw new IOException("Checkout reservation changed.");
                    }

                    if (current.CheckoutId.Length != 0 && current.CheckoutId != checkout.Id)
                    {
                        throw new IOException("Checkout identity changed.");
                    }

                    current.CheckoutId = checkout.Id;
                }, cancellationToken, written: binding);
            }
            catch
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    var current = await ReadAsync(org.Id, cleanup.Token);
                    if (current?.CheckoutId != checkout.Id)
                    {
                        await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, cancellationToken: cleanup.Token);
                    }
                }
                catch (Exception error) when (error is IOException or TerraBaseException or HttpRequestException or StripeException or OperationCanceledException) { }
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

        public async Task<bool> ReconcileCheckoutAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await MemberAsync(context, cancellationToken);
            if (context.Organization!.InternalBilling is not null ||
                await ReadAsync(context.Organization.Id, cancellationToken) is not { Kind: "stripe", CheckoutId.Length: > 0, SubscriptionId.Length: 0, Status: "pending" })
            {
                return false;
            }

            await RefreshAsync(context.Organization.Id, cancellationToken);
            return true;
        }

        internal async Task<bool> ReconcilePendingCheckoutAsync(BillingBinding binding, CancellationToken cancellationToken)
        {
            if (binding is not { Kind: "stripe", CheckoutId.Length: > 0, SubscriptionId.Length: 0, Status: "pending" } ||
                time.GetUtcNow() - binding.StartedAt > TimeSpan.FromDays(1))
            {
                return false;
            }

            return await RefreshAsync(binding.OrganizationId, cancellationToken);
        }

        public async Task<PortalSession> PortalAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await OwnerAsync(context, cancellationToken);
            var binding = await ReadAsync(context.Organization!.Id, cancellationToken);
            if (binding is null || binding.CustomerId.Length == 0)
            {
                return new DashboardValidation("Set up billing before opening the customer portal.");
            }

            var client = await ClientAsync(cancellationToken);
            var portal = await client.V1.BillingPortal.Sessions.CreateAsync(new()
            {
                Customer = binding.CustomerId,
                ReturnUrl = Origin + "/billing"
            }, cancellationToken: cancellationToken);
            return new PortalSessionUrl(portal.Url);
        }

        public async Task<WebhookReceipt> HandleWebhookAsync(string body, string signature, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var webhookSecret = credentials.Stripe?.WebhookSecret ?? throw new IOException("Stripe identity is unavailable.");
            Event @event;
            try { @event = EventUtility.ConstructEvent(body, signature, webhookSecret); }
            catch (Exception)
            { return new Rejected(); }
            if (@event.Livemode != credentials.Stripe!.IsLive)
            {
                return new Rejected();
            }

            if (!WebhookEvents.Contains(@event.Type))
            {
                return new Done();
            }

            var customerId = @event.Data.Object switch
            {
                Stripe.Checkout.Session checkout => checkout.CustomerId,
                Subscription subscription => subscription.CustomerId,
                Invoice invoice => invoice.CustomerId,
                _ => null
            };
            if (string.IsNullOrEmpty(customerId))
            {
                return new Done();
            }

            var customer = await CustomerAsync(customerId, cancellationToken);
            if (customer is null)
            {
                return new Done();
            }

            var orgId = customer.OrganizationId;
            var binding = await ReadAsync(orgId, cancellationToken);
            if (@event.Created < time.GetUtcNow().UtcDateTime.AddDays(-30)) { return new Done(); }
            var receipt = new StripeEventReceipt
            {
                Id = @event.Id,
                OrganizationId = orgId,
                SubscriptionId = binding?.SubscriptionId ?? "",
                InvoiceId = @event.Data.Object is Invoice paymentInvoice && paymentInvoice.Parent?.SubscriptionDetails?.SubscriptionId == binding?.SubscriptionId ? paymentInvoice.Id : "",
                EventType = @event.Type,
                EventCreated = new(DateTime.SpecifyKind(@event.Created, DateTimeKind.Utc)),
                ReceivedAt = time.GetUtcNow(),
                ProcessingUntil = time.GetUtcNow().AddMinutes(5)
            };
            if (!await storage.InsertAsync(receipt, cancellationToken)) { return new Done(); }
            try
            {
                if (binding?.CustomerId != customerId)
                {
                    throw new IOException("Billing index does not match its customer.");
                }

                await ApplyEventReceiptAsync(receipt, cancellationToken);
            }
            catch (Exception error) when (error is IOException or TerraBaseException or HttpRequestException or TimeoutException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                receipt.ProcessingUntil = time.GetUtcNow();
                await storage.ReplaceAsync(receipt, cancellationToken);
                return new Deferred(@event.Id, @event.Type, error);
            }
            logger.StripeEventAppliedForOrganization(@event.Id, @event.Type, orgId);
            return new Done();
        }

        internal async Task<BillingBinding?> EndSubscriptionAsync(string orgId, CancellationToken cancellationToken, bool completedDeletion = false)
        {
            var row = await storage.BindingRowAsync(orgId, cancellationToken);
            if (row is null || row.ErasureId is not null)
            {
                return null;
            }

            var binding = ReadBinding(row, orgId);
            if (binding.Kind != "stripe")
            {
                return binding;
            }

            var terminalDeletion = completedDeletion;
            if (terminalDeletion)
            {
                binding = await UpdateAsync(orgId, current => current.State = "deleting", cancellationToken, allowDeletion: true);
            }
            if (binding.CheckoutId.Length == 0 && binding.SubscriptionId.Length == 0 && !terminalDeletion)
            {
                throw new IOException("Checkout creation has not been reconciled.");
            }

            if (terminalDeletion)
            {
                await EndCustomersForDeletionAsync(orgId, binding.CustomerId.Length == 0 ? [] : [binding.CustomerId], cancellationToken);
            }

            var client = await ClientAsync(cancellationToken);
            var subscriptions = new HashSet<string>(StringComparer.Ordinal);
            if (binding.SubscriptionId.Length > 0)
            {
                subscriptions.Add(binding.SubscriptionId);
            }

            if (binding.CheckoutId.Length > 0)
            {
                var checkout = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
                ValidateCheckout(binding, checkout);
                if (checkout.Status == "open")
                {
                    checkout = await client.V1.Checkout.Sessions.ExpireAsync(checkout.Id, new(), Idempotent("delete-checkout-" + checkout.Id), cancellationToken);
                    ValidateCheckout(binding, checkout);
                }
                if (checkout.Status == "complete" && checkout.Mode == "subscription")
                {
                    if (checkout.SubscriptionId is not { Length: > 0 })
                    {
                        throw new IOException("Completed checkout subscription is unavailable.");
                    }

                    subscriptions.Add(checkout.SubscriptionId);
                }
                else if (checkout.Status != "expired" && !(checkout.Status == "complete" && checkout.Mode == "setup"))
                {
                    throw new IOException("Checkout cancellation is incomplete.");
                }
            }
            foreach (var subscriptionId in subscriptions)
            {
                var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId, cancellationToken: cancellationToken);
                if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId)
                {
                    throw new IOException("Subscription ownership does not match.");
                }

                if (subscription.Status is not ("canceled" or "incomplete_expired"))
                {
                    subscription = await client.V1.Subscriptions.CancelAsync(subscription.Id, cancellationToken: cancellationToken);
                    if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId || subscription.Status is not ("canceled" or "incomplete_expired"))
                    {
                        throw new IOException("Subscription cancellation is incomplete.");
                    }
                }
            }
            return await UpdateAsync(orgId, current =>
            {
                if (current.RequestId != binding.RequestId || current.CheckoutId != binding.CheckoutId || current.SubscriptionId != binding.SubscriptionId)
                {
                    throw new IOException("Billing deletion scope changed.");
                }

                current.Status = "canceled"; current.RenewalAt = null;
            }, cancellationToken, allowDeletion: terminalDeletion);
        }

        private readonly SemaphoreSlim[] refreshing = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

        private async Task ApplyPaymentAsync(BillingBinding binding, string invoiceId, string eventType, DateTime created, CancellationToken cancellationToken)
        {
            if (eventType is not ("invoice.payment_failed" or "invoice.paid" or "invoice.payment_succeeded") || binding.SubscriptionId.Length == 0)
            {
                return;
            }

            var client = await ClientAsync(cancellationToken);
            var subscription = await client.V1.Subscriptions.GetAsync(binding.SubscriptionId, new() { Expand = ["latest_invoice"] }, cancellationToken: cancellationToken);
            if (subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != binding.OrganizationId ||
                subscription.LatestInvoiceId != invoiceId || subscription.LatestInvoice is not { Status: "paid" or "open" } invoice)
            {
                return;
            }

            if (invoice.Status != "paid" && eventType != "invoice.payment_failed")
            {
                return;
            }

            while (true)
            {
                var stored = await organizations.FindAsync(binding.OrganizationId, cancellationToken) ?? throw new IOException("Billing organization is unavailable.");
                var org = stored.Erased ? throw new IOException("Dashboard record was erased.") : stored.Organization;
                var deadline = invoice.Status != "paid"
                    ? org.PaymentGraceUntil ?? GraceDeadline(new DateTimeOffset(DateTime.SpecifyKind(created, DateTimeKind.Utc)))
                    : (DateTimeOffset?)null;
                if (org.PaymentGraceUntil == deadline)
                {
                    return;
                }

                org.PaymentGraceUntil = deadline;
                try
                {
                    await organizations.ReplaceAsync(stored, cancellationToken);
                    return;
                }
                catch (DBConcurrencyException)
                {
                    continue;
                }
            }
        }

        internal async Task<bool> RefreshAsync(string orgId, CancellationToken cancellationToken)
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
                var billingRow = await storage.BindingRowAsync(orgId, cancellationToken);
                if (billingRow is null)
                {
                    return false;
                }

                var binding = ReadBinding(billingRow, orgId);
                RequireStripe(binding);
                if (binding.State == "deleting")
                {
                    return false;
                }

                if (binding.CheckoutId.Length == 0 && binding.SubscriptionId.Length == 0)
                {
                    return false;
                }

                var client = await ClientAsync(cancellationToken);
                var organizationRow = await organizations.FindAsync(orgId, cancellationToken)
                    ?? throw new IOException("Billing organization is unavailable.");
                var org = organizationRow.Erased ? throw new IOException("Dashboard record was erased.") : organizationRow.Organization;

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
                var taxRepair = false;
                var unmappedPrice = false;
                DateTimeOffset? renewalAt = null;
                string status;
                Subscription? observedSubscription = null;
                var subscriptionId = binding.SubscriptionId.Length > 0 ? binding.SubscriptionId :
                    checkout is { Status: "complete", Mode: "subscription", SubscriptionId.Length: > 0 } ? checkout.SubscriptionId : "";
                if (subscriptionId.Length > 0)
                {
                    var subscription = await client.V1.Subscriptions.GetAsync(subscriptionId,
                        new() { Expand = ["items.data.price.product", "default_payment_method", "customer.tax", "latest_invoice"] }, cancellationToken: cancellationToken);
                    if (subscription.Id != subscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != orgId)
                    {
                        throw new IOException("Subscription ownership does not match.");
                    }

                    observedSubscription = subscription;

                    binding.TaxLocationStatus = subscription.Customer?.Tax?.AutomaticTax ?? "unrecognized_location";
                    taxRepair = subscription.Metadata.GetValueOrDefault("terrascale_tax") == "automatic" && binding.TaxLocationStatus is "supported" or "not_collecting" && subscription.AutomaticTax?.Enabled != true && subscription.Status is not ("canceled" or "incomplete_expired");

                    var item = subscription.Items.Data.Count == 1 ? subscription.Items.Data[0] : null;
                    var mapped = item is { Quantity: 1 } ? EffectivePlan(item.Price) : null;
                    unmappedPrice = mapped is null;
                    (plan, effectiveInterval) = mapped ?? (PlanCatalog.For("free"), "");
                    status = subscription.Status;
                    var canonicalPaid = status is "active" or "trialing" || subscription.LatestInvoice is { Status: "paid" } && status is "past_due" or "unpaid";
                    if (unmappedPrice && canonicalPaid && PlanCatalog.For(org) is { PriceCents: > 0 } retained)
                    {
                        plan = retained;
                        effectiveInterval = binding.EffectiveInterval;
                    }
                    paymentValid = canonicalPaid && (!unmappedPrice || plan.PriceCents > 0);
                    if (item is not null && status is not ("canceled" or "incomplete_expired"))
                    {
                        renewalAt = item.CurrentPeriodEnd;
                    }
                }
                else
                {
                    if (checkout?.Status != "complete")
                    {
                        return false;
                    }

                    plan = PlanCatalog.For(binding.Plan);
                    if (plan.PriceCents != 0)
                    {
                        throw new IOException("Checkout subscription is unavailable.");
                    }

                    if (org.Kind != "personal")
                    {
                        throw new IOException("Free setup is unavailable for this organization.");
                    }

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
                binding.Status = status;
                binding.TrialUsed |= status is "active" or "trialing";
                binding.Revision = checked(binding.Revision + 1);
                if (paymentValid || status is "active" or "trialing" or "canceled" or "incomplete_expired")
                {
                    org.PaymentGraceUntil = null;
                }
                else if (status is "past_due" or "unpaid" && PlanCatalog.For(org) is { PriceCents: > 0 })
                {
                    org.PaymentGraceUntil ??= GraceDeadline(time.GetUtcNow());
                }
                paymentValid &= org.PaymentGraceUntil is null;
                org.PaymentMethodValidated = paymentValid;
                if (org.PaymentGraceUntil is { } deadline && status is not ("canceled" or "incomplete_expired"))
                {
                    org.BillingStatus = deadline > time.GetUtcNow() ? "active" : "billing_required";
                    binding.EffectivePlan = previousEffectivePlan;
                    binding.EffectiveInterval = previousEffectiveInterval;
                }
                else if (paymentValid)
                {
                    org.PlanId = plan.Id;
                    org.BillingStatus = "active";
                }
                else if (unmappedPrice && (status is "active" or "trialing"))
                {
                    if (org.Kind == "personal")
                    {
                        org.PlanId = OrganizationRecord.FreePlanId;
                    }

                    org.BillingStatus = "billing_required";
                    binding.EffectivePlan = "";
                    binding.EffectiveInterval = "";
                }
                else if (org.Kind == "personal" && status is not ("past_due" or "unpaid" or "paused"))
                {
                    org.PlanId = OrganizationRecord.FreePlanId;
                    org.BillingStatus = "active";
                    binding.EffectivePlan = "free";
                    binding.EffectiveInterval = "";
                    binding.RenewalAt = null;
                }
                else
                {
                    org.BillingStatus = "billing_required";
                    binding.EffectivePlan = previousEffectivePlan;
                    binding.EffectiveInterval = previousEffectiveInterval;
                }
                if (binding.OverageStartsAt == default)
                {
                    var now = time.GetUtcNow();
                    binding.OverageStartsAt = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
                }
                binding.ExpiryUnavailableSince = null;
                binding.ExpiryLastLoggedAt = null;
                if (org.PlanId != previousPlanId)
                {
                    org.AuditEvents.Add(new(AccountService.NewId(), "billing service", "billing.plan_changed", PlanCatalog.Plans.FirstOrDefault(item => item.Id == org.PlanId)?.Name ?? org.PlanId, "plan", time.GetUtcNow()));
                }

                var conflicted = !await storage.ReplaceAsync(binding, cancellationToken);
                if (!conflicted)
                {
                    try { await organizations.ReplaceAsync(organizationRow, cancellationToken); }
                    catch (DBConcurrencyException) when (attempt < 3) { conflicted = true; }
                }

                if (!conflicted)
                {
                    var current = await ReadAsync(orgId, cancellationToken);
                    if (current?.RequestId == binding.RequestId && current.Revision == binding.Revision)
                    {
                        await plans.PublishAsync(org, cancellationToken);
                        if (taxRepair) { await RepairTaxAsync(client, subscriptionId, cancellationToken); }
                        if (observedSubscription is not null) { await RecordTermsAsync(client, orgId, observedSubscription, cancellationToken); }
                        return paymentValid;
                    }
                }
                else if (attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(50, 150) << attempt), time, cancellationToken);
                }
            }
            throw new IOException("Billing changed concurrently. Retry reconciliation.");
        }

        private static DateTimeOffset GraceDeadline(DateTimeOffset failedAt) => new(failedAt.UtcDateTime.Date.AddDays(8).AddMilliseconds(-1), TimeSpan.Zero);

        private async Task<BillingBinding> ReserveAsync(string orgId, string projectId, string plan, string interval, string returnPath, CancellationToken cancellationToken)
        {
            var now = time.GetUtcNow();
            var initial = new BillingBinding
            {
                OrganizationId = orgId,
                CheckoutProjectId = projectId,
                Plan = plan,
                Interval = interval,
                ReturnPath = returnPath,
                RequestId = Guid.CreateVersion7().ToString(),
                StartedAt = now,
                OverageStartsAt = new(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero)
            };
            if (await storage.InsertAsync(initial, cancellationToken))
            {
                return initial;
            }

            var existing = await ReadAsync(orgId, cancellationToken) ?? throw new IOException("Billing reservation did not persist.");
            RequireStripe(existing);
            if (existing.State == "deleting")
            {
                throw new IOException("Billing deletion is pending.");
            }

            var endedSubscription = existing.SubscriptionId.Length > 0 && existing.Status is "canceled" or "incomplete_expired";
            if (!endedSubscription && existing.StartedAt + (existing.CheckoutId.Length > 0 ? CheckoutLifetime : CheckoutCreationWindow) > time.GetUtcNow())
            {
                if (existing.Plan != plan || existing.Interval != interval || existing.ReturnPath != returnPath)
                {
                    throw new OnboardingValidationException("Finish the current checkout before choosing another plan.");
                }

                return existing;
            }
            return await UpdateAsync(orgId, current =>
            {
                if (current.RequestId != existing.RequestId || current.CheckoutId != existing.CheckoutId)
                {
                    throw new AccountAccessException("Another checkout is in progress.", 409);
                }

                if (current.SubscriptionId.Length > 0 && current.Status is not ("canceled" or "incomplete_expired"))
                {
                    throw new OnboardingValidationException("Manage your existing subscription from the billing portal.");
                }

                current.Plan = plan; current.Interval = interval; current.ReturnPath = returnPath;
                current.CheckoutProjectId = initial.CheckoutProjectId;
                current.RequestId = initial.RequestId; current.StartedAt = initial.StartedAt; current.CheckoutId = "";
                current.SubscriptionId = ""; current.EffectivePlan = ""; current.EffectiveInterval = "";
                current.RenewalAt = null;
                current.Status = "pending";
            }, cancellationToken);
        }

        private async Task<BillingBinding?> ReadAsync(string orgId, CancellationToken cancellationToken)
        {
            var row = await storage.BindingRowAsync(orgId, cancellationToken);
            return row is null ? null : ReadBinding(row, orgId);
        }

        private Task<StripeCustomerRow?> CustomerAsync(string customerId, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((storage, nameof(StripeCustomerRow), customerId), async () =>
            {
                await using var connection = await storage.OpenAsync(cancellationToken);
                return await connection.QuerySingleOrDefaultAsync<StripeCustomerRow>(DashboardSql.StripeCustomers.Select + " WHERE customer_id = @CustomerId",
                    new { CustomerId = customerId });
            });
        }

        internal static BillingBinding ReadBinding(BillingBinding value, string orgId)
        {
            if (value.ErasureId is not null || value.OrganizationId != orgId || value.Revision < 0 || string.IsNullOrEmpty(value.RequestId) ||
                value.CustomerId is null || value.CheckoutId is null || value.CheckoutProjectId is null || value.ReturnPath is null || value.SubscriptionId is null || value.Status is null || value.Plan is null || value.Interval is null || value.EffectivePlan is null || value.EffectiveInterval is null)
            {
                throw new IOException("Billing scope is invalid.");
            }

            if (value.Kind == "internal")
            {
                var reservation = value.Reservation;
                if (value.State is not ("reserved" or "active") || reservation is null || reservation.SpendCapCents <= 0 || reservation.PreviousConfigurationId is null ||
                    reservation.Policy is null || reservation.Policy.ConfigurationId != value.RequestId ||
                    value.CustomerId.Length != 0 || value.CheckoutId.Length != 0 || value.SubscriptionId.Length != 0 ||
                    value.CheckoutProjectId.Length != 0 || value.ReturnPath.Length != 0 || value.Plan != "internal" || value.Interval != "monthly")
                {
                    throw new IOException("Internal billing reservation is invalid.");
                }

                reservation.Policy.Validate(new() { Id = orgId, PlanId = reservation.Policy.PlanId });
            }
            else if (value.Kind != "stripe" || value.Reservation is not null)
            {
                throw new IOException("Billing ownership kind is invalid.");
            }

            return value;
        }

        internal static void RequireStripe(BillingBinding binding)
        {
            if (binding.Kind != "stripe")
            {
                throw new AccountAccessException("Internal billing reports usage without Stripe charges.");
            }
        }

        private async Task<BillingBinding> UpdateAsync(string orgId, Action<BillingBinding> update, CancellationToken cancellationToken, bool allowDeletion = false,
            BillingBinding? written = null)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var binding = ReadBinding(attempt == 0 && written is not null ? written.Copy()
                    : await storage.BindingRowAsync(orgId, cancellationToken) ?? throw new IOException("Billing state is unavailable."), orgId);
                RequireStripe(binding);
                if (binding.State == "deleting" && !allowDeletion)
                {
                    throw new IOException("Billing deletion is pending.");
                }

                update(binding);
                if (await storage.ReplaceAsync(binding, cancellationToken))
                {
                    return binding;
                }
            }
            throw new IOException("Billing changed concurrently.");
        }

        private async Task<AccountContext> OwnerAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var current = await MemberAsync(context, cancellationToken);
            if (current.Role != "owner")
            {
                throw new AccountAccessException("Only organization owners manage billing.");
            }

            if (current.Organization!.DeletionRequested)
            {
                throw new AccountAccessException("Organization deletion is pending. Billing changes are unavailable.");
            }

            if (current.Organization.InternalBilling is not null)
            {
                throw new AccountAccessException("Internal billing reports usage without Stripe charges.");
            }

            var binding = await ReadAsync(current.Organization.Id, cancellationToken);
            if (binding is not null)
            {
                RequireStripe(binding);
            }

            return current;
        }

        private async Task<AccountContext> MemberAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var fresh = await AccountScope.RefreshAsync(accounts, context, cancellationToken);
            await InternalBillingService.ValidateClaimsAsync(storage, fresh.Organization!, cancellationToken);
            return fresh;
        }

        private async Task<StripeClient> ClientAsync(CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            var secret = credentials.Stripe?.SecretKey ?? throw new IOException("Stripe identity is unavailable.");
            return new StripeClient(secret, httpClient: new SystemNetHttpClient(http.CreateClient("platform"), enableTelemetry: false));
        }

        private static RequestOptions Idempotent(string key)
        {
            return new() { IdempotencyKey = "TerraScale.Dashboard/" + key };
        }

        private static Stripe.Checkout.SessionCreateOptions CheckoutOptions(BillingPlan plan)
        {
            var options = new Stripe.Checkout.SessionCreateOptions
            {
                Mode = plan.PriceCents == 0 ? "setup" : "subscription",
                BillingAddressCollection = "required"
            };
            if (plan.PriceCents > 0)
            {
                options.AllowedPaymentMethodTypes = ["card", "link"];
                options.PaymentMethodCollection = "always";
                options.AutomaticTax = new() { Enabled = true };
                options.TaxIdCollection = new() { Enabled = true };
                options.CustomerUpdate = new() { Address = "auto", Name = "auto" };
            }

            return options;
        }

        private static void ValidateCheckout(BillingBinding binding, Stripe.Checkout.Session checkout)
        {
            if (binding.CheckoutId.Length > 0 && checkout.Id != binding.CheckoutId || checkout.CustomerId != binding.CustomerId || !(checkout.ClientReferenceId == binding.OrganizationId && checkout.Metadata.GetValueOrDefault("organization_id") == binding.OrganizationId ||
                  checkout.ClientReferenceId == binding.RequestId && checkout.Metadata.GetValueOrDefault("draft_id") == binding.RequestId) ||
                checkout.Metadata.GetValueOrDefault("plan") != binding.Plan || checkout.Metadata.GetValueOrDefault("interval") != binding.Interval ||
                checkout.Metadata.GetValueOrDefault("request_id") != binding.RequestId)
            {
                throw new IOException("Stripe checkout does not match its persisted binding.");
            }
        }

        internal static bool ValidCheckoutReturnPath(AccountContext context, string path)
        {
            return ValidCheckoutReturnPath(context.Organization?.Id, context.Project?.Id, path);
        }

        private static bool ValidCheckoutReturnPath(string? organizationId, string? projectId, string path)
        {
            if (path.Contains('#') || path.Contains('\\') || path.Any(char.IsControl))
            {
                return false;
            }

            var queryAt = path.IndexOf('?');
            var basePath = queryAt < 0 ? path : path[..queryAt];
            if (basePath is not ("/onboarding" or "/billing" or "/organization"))
            {
                return false;
            }

            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(queryAt < 0 ? "" : path[queryAt..]);
            if (query.Any(pair => pair.Value.Count != 1))
            {
                return false;
            }

            if (query.TryGetValue("organizationId", out var returnOrganizationId) && returnOrganizationId != organizationId)
            {
                return false;
            }

            if (query.TryGetValue("projectId", out var returnProjectId) && (string.IsNullOrEmpty(projectId) || returnProjectId != projectId))
            {
                return false;
            }
            if (basePath == "/organization")
            {
                if (query.TryGetValue("tab", out var tab) && tab != "billing")
                {
                    return false;
                }

                if (query.TryGetValue("newOrganization", out var step))
                {
                    if (step != "project" || !query.TryGetValue("organizationId", out var wizardId) || wizardId != organizationId)
                    {
                        return false;
                    }
                }
                else if (query.GetValueOrDefault("tab") != "billing")
                {
                    return false;
                }
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
                if (!MatchesPrice(price, plan, interval, requirePublished: true))
                {
                    continue;
                }

                if (result is not null)
                {
                    throw new HttpRequestException("Stripe has ambiguous prices for this plan.");
                }

                result = price;
            }
            return result ?? throw new HttpRequestException("Stripe has no published price for this plan.");
        }

        internal static bool MatchesPrice(Price price, BillingPlan plan, string interval, bool requirePublished = false)
        {
            return plan.SupportsInterval(interval) && price.Recurring?.UsageType != "metered" && (!requirePublished || price.Active) && price.Currency == "usd" && price.UnitAmount == plan.IntervalPriceCents(interval) &&
            price.Recurring?.Interval == (interval == "annual" ? "year" : "month") && price.Recurring.IntervalCount == 1 &&
            price.Product is { } product && (!requirePublished || product.Active) && (product.Metadata.GetValueOrDefault("plan") == plan.Key ||
                product.Name == plan.Name || product.Name == "TerraScale " + plan.Name);
        }

        private static (BillingPlan Plan, string Interval)? EffectivePlan(Price? price)
        {
            if (price is null)
            {
                return null;
            }

            var matches = (from plan in PlanCatalog.Plans
                           where plan.PriceCents > 0
                           from interval in plan.CheckoutIntervals
                           where MatchesPrice(price, plan, interval)
                           select (plan, interval)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
    }
}

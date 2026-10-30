using System.Text.Json;
using System.Text.Json.Serialization;
using System.ComponentModel;
using Stripe;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing
{
    public sealed record BillingPlanState(string PlanKey, string Interval, DateTimeOffset RenewalAt, string ScheduledPlanKey,
        string ScheduledInterval, DateTimeOffset? ScheduledAt, string PendingPlanKey, string PendingInterval, bool CancelAtPeriodEnd, bool Entitled, string PaymentUrl = "")
    {
        [JsonIgnore] public DateTimeOffset? PeriodStart { get; init; }
    }
    public sealed record BillingPlanPreview(string PlanKey, string Interval,
        [property: Description("Whether the change applies now (upgrade) instead of at renewal (downgrade).")] bool Immediate, DateTimeOffset EffectiveAt,
        [property: Description("Estimated charge today in the smallest currency unit.")] long AmountDueCents, long ProrationCents, string Currency, string Note);

    // Stripe owns all current, pending and scheduled plan facts. The separate CAS
    // receipt only serializes changes and preserves their provider idempotency key.
    public sealed partial class StripeBilling
    {
        public const string ChangesCollection = "dashboard_billing_changes";

        public async Task<BillingPlanState?> PlanStateAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await MemberAsync(context, cancellationToken);
            return (await PlanSnapshotAsync(context, cancellationToken))?.State;
        }

        public async Task<BillingPlanPreview> PreviewPlanChangeAsync(AccountContext context, string key, string interval, CancellationToken cancellationToken)
        {
            context = await OwnerAsync(context, cancellationToken);
            (key, interval) = NormalizeChange(context, key, interval);
            var snapshot = await PlanSnapshotAsync(context, cancellationToken) ?? throw Conflict("Choose a paid plan through checkout first.");
            ValidateChange(snapshot.State);
            if (key == snapshot.State.PlanKey && interval == snapshot.State.Interval)
            {
                throw Conflict("This is already the current plan.");
            }

            if (Rank(key) <= Rank(snapshot.State.PlanKey))
            {
                return new(key, interval, false, snapshot.State.RenewalAt, 0, 0, "usd", "No charge today; the change applies at renewal.");
            }

            var price = await PriceAsync(snapshot.Client, PlanCatalog.For(key), interval, cancellationToken);
            var invoice = await snapshot.Client.V1.Invoices.CreatePreviewAsync(new()
            {
                Subscription = snapshot.Subscription.Id,
                SubscriptionDetails = new() { Items = [new() { Id = snapshot.Item.Id, Price = price.Id, Quantity = 1 }], ProrationBehavior = "always_invoice" }
            }, cancellationToken: cancellationToken);
            if (invoice.CustomerId != snapshot.Binding.CustomerId || invoice.Currency != "usd")
            {
                throw new IOException("Stripe returned a preview for a different billing account.");
            }

            long prorations = 0;
            foreach (var line in invoice.Lines?.Data ?? [])
            {
                if (line.Parent?.SubscriptionItemDetails?.Proration == true || line.Parent?.InvoiceItemDetails?.Proration == true)
                {
                    prorations = checked(prorations + line.Amount);
                }
            }

            return new(key, interval, true, time.GetUtcNow(), invoice.AmountDue, prorations, invoice.Currency,
                "Estimated by Stripe; the change applies after Stripe confirms payment.");
        }

        public async Task<string> ChangePlanAsync(AccountContext context, string key, string interval, CancellationToken cancellationToken)
        {
            context = await OwnerAsync(context, cancellationToken);
            (key, interval) = NormalizeChange(context, key, interval);
            var snapshot = await PlanSnapshotAsync(context, cancellationToken) ?? throw Conflict("Choose a paid plan through checkout first.");
            if (snapshot.State.ScheduledPlanKey == key && snapshot.State.ScheduledInterval == interval)
            {
                return "scheduled";
            }

            if (snapshot.State.PendingPlanKey == key && snapshot.State.PendingInterval == interval)
            {
                return "pending";
            }

            ValidateChange(snapshot.State);
            if (snapshot.State.PlanKey == key && snapshot.State.Interval == interval)
            {
                return "current";
            }

            var receipt = await ReserveChangeAsync(context, snapshot, "change", key, interval, cancellationToken);
            // Recheck the live role and subscription after the persistent reservation.
            context = await OwnerAsync(context, cancellationToken);
            snapshot = await PlanSnapshotAsync(context, cancellationToken) ?? throw Conflict("The subscription changed. Reload billing.");
            if (Realized(receipt, snapshot.State)) { await FinishChangeAsync(context.Organization!.Id, receipt, cancellationToken); return Rank(key) > Rank(receipt.SourcePlanKey) ? "pending" : "scheduled"; }
            await ValidateSourceAsync(context.Organization!.Id, receipt, snapshot);
            if (receipt.Status == "reserved")
            {
                try
                {
                    ValidateChange(snapshot.State);
                    if (snapshot.Subscription.ScheduleId is { Length: > 0 })
                    {
                        throw Conflict("The subscription changed in Stripe. Reload billing before retrying.");
                    }
                }
                catch (AccountAccessException) { await AbandonReservationAsync(context.Organization.Id, receipt); throw; }
            }
            var nextPrice = key == "free" ? null : await TargetPriceAsync(context, receipt, snapshot.Client, cancellationToken);
            await BeginChangeAsync(context.Organization.Id, receipt, cancellationToken);
            var request = Idempotent("plan-" + receipt.RequestId);
            if (key == "free")
            {
                await snapshot.Client.V1.Subscriptions.UpdateAsync(snapshot.Subscription.Id, new() { CancelAtPeriodEnd = true }, request, cancellationToken);
            }
            else if (Rank(key) > Rank(snapshot.State.PlanKey))
            {
                if (snapshot.Subscription.ScheduleId is { Length: > 0 })
                {
                    throw Conflict("Undo the scheduled change first.");
                }

                await snapshot.Client.V1.Subscriptions.UpdateAsync(snapshot.Subscription.Id, new()
                {
                    Items = [new() { Id = snapshot.Item.Id, Price = nextPrice!.Id, Quantity = 1 }],
                    ProrationBehavior = "always_invoice",
                    PaymentBehavior = "pending_if_incomplete"
                }, request, cancellationToken);
            }
            else
            {
                var schedules = snapshot.Client.V1.SubscriptionSchedules;
                var schedule = snapshot.Subscription.ScheduleId is { Length: > 0 } scheduleId ? await schedules.GetAsync(scheduleId, cancellationToken: cancellationToken) : null;
                if (schedule?.Metadata.GetValueOrDefault("dashboard_change_key") != receipt.RequestId)
                {
                    // Stripe rejects every other parameter with from_subscription, so the update below binds the change key.
                    // A schedule without the key is ours only when this receipt's idempotent create replays its ID.
                    SubscriptionSchedule created;
                    try { created = await schedules.CreateAsync(new() { FromSubscription = snapshot.Subscription.Id }, Idempotent("plan-" + receipt.RequestId + "-schedule"), cancellationToken); }
                    catch (StripeException) when (schedule is not null) { throw Conflict("The subscription schedule does not belong to this change."); }
                    if (schedule is not null && created.Id != schedule.Id)
                    {
                        throw Conflict("The subscription schedule does not belong to this change.");
                    }

                    schedule ??= created;
                }
                if (schedule.SubscriptionId != snapshot.Subscription.Id || schedule.CustomerId != snapshot.Binding.CustomerId || schedule.CurrentPhase is null ||
                    schedule.CurrentPhase.StartDate >= snapshot.Item.CurrentPeriodEnd)
                {
                    throw Conflict("The subscription schedule does not belong to this change.");
                }

                await schedules.UpdateAsync(schedule.Id, new()
                {
                    EndBehavior = "release",
                    ProrationBehavior = "none",
                    Metadata = new() { ["dashboard_change_key"] = receipt.RequestId },
                    Phases = [
                        new() { StartDate = schedule.CurrentPhase.StartDate, EndDate = snapshot.Item.CurrentPeriodEnd, Items = [new() { Price = snapshot.Item.Price.Id, Quantity = 1 }],
                            TrialEnd = snapshot.Subscription.Status == "trialing" ? snapshot.Subscription.TrialEnd : null },
                        // Stripe starts each later phase where the previous one ends.
                        new() { Duration = new() { Interval = interval == "annual" ? "year" : "month", IntervalCount = 1 },
                            Items = [new() { Price = nextPrice!.Id, Quantity = 1 }], Metadata = new() { ["organization_id"] = context.Organization!.Id, ["plan"] = key, ["interval"] = interval } }
                    ]
                }, Idempotent("plan-" + receipt.RequestId + "-update"), cancellationToken);
            }
            await SettleChangeAsync(context, receipt);
            return Rank(key) > Rank(receipt.SourcePlanKey) ? "pending" : "scheduled";
        }

        public async Task<string> UndoPlanChangeAsync(AccountContext context, CancellationToken cancellationToken)
        {
            context = await OwnerAsync(context, cancellationToken);
            var snapshot = await PlanSnapshotAsync(context, cancellationToken);
            if (snapshot is null || snapshot.State.ScheduledPlanKey.Length == 0)
            {
                return "current";
            }

            var receipt = await ReserveChangeAsync(context, snapshot, "undo", snapshot.State.ScheduledPlanKey, snapshot.State.ScheduledInterval, cancellationToken);
            context = await OwnerAsync(context, cancellationToken);
            snapshot = await PlanSnapshotAsync(context, cancellationToken) ?? throw Conflict("The subscription changed. Reload billing.");
            if (Realized(receipt, snapshot.State)) { await FinishChangeAsync(context.Organization!.Id, receipt, cancellationToken); return "undone"; }
            await ValidateSourceAsync(context.Organization!.Id, receipt, snapshot);
            if (snapshot.State.ScheduledPlanKey != receipt.Plan || snapshot.State.ScheduledInterval != receipt.Interval)
            {
                await AbandonReservationAsync(context.Organization.Id, receipt);
                throw Conflict("The scheduled change changed in Stripe.");
            }
            await BeginChangeAsync(context.Organization.Id, receipt, cancellationToken);
            if (receipt.Plan == "free")
            {
                await snapshot.Client.V1.Subscriptions.UpdateAsync(snapshot.Subscription.Id, new() { CancelAtPeriodEnd = false }, Idempotent("plan-" + receipt.RequestId), cancellationToken);
            }
            else if (snapshot.Subscription.ScheduleId is { Length: > 0 } scheduleId)
            {
                await snapshot.Client.V1.SubscriptionSchedules.ReleaseAsync(scheduleId, new(), Idempotent("plan-" + receipt.RequestId), cancellationToken);
            }
            else
            {
                throw Conflict("The scheduled change changed in Stripe.");
            }

            await SettleChangeAsync(context, receipt);
            return "undone";
        }

        public async Task<string> CancelCheckoutAsync(AccountContext context, string checkoutId, CancellationToken cancellationToken)
        {
            context = checkoutId.Length == 0 ? await OwnerAsync(context, cancellationToken) :
                (await BoundCheckoutAsync(context, checkoutId, cancellationToken)).Context;
            var orgId = context.Organization!.Id;
            var binding = await ReadAsync(orgId, cancellationToken) ?? throw new AccountAccessException("No checkout is in progress.", 404);
            if (binding.CheckoutId.Length == 0 || checkoutId.Length > 0 && binding.CheckoutId != checkoutId)
            {
                throw new AccountAccessException("Checkout is unavailable.", 404);
            }

            var client = await ClientAsync(cancellationToken);
            var checkout = await client.V1.Checkout.Sessions.GetAsync(binding.CheckoutId, cancellationToken: cancellationToken);
            ValidateCheckout(binding, checkout);
            // A paid checkout cannot be canceled; reconcile it instead of dead-ending.
            if (checkout.Status == "complete")
            {
                return PaymentOutcome(binding.ReturnPath, await RefreshAsync(orgId, cancellationToken) ? "success" : "pending");
            }

            if (checkout.Status == "open")
            {
                checkout = await client.V1.Checkout.Sessions.ExpireAsync(binding.CheckoutId, requestOptions: Idempotent("expire-" + binding.RequestId), cancellationToken: cancellationToken);
                ValidateCheckout(binding, checkout);
            }
            if (checkout.Status != "expired")
            {
                throw new IOException("Stripe did not confirm the checkout cancellation.");
            }

            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await UpdateAsync(orgId, current =>
            {
                if (current.RequestId != binding.RequestId || current.CheckoutId != binding.CheckoutId)
                {
                    throw Conflict("Checkout changed concurrently. Reload billing.");
                }

                current.StartedAt = time.GetUtcNow().AddHours(-1);
                if (current.SubscriptionId.Length == 0)
                {
                    current.Status = "canceled";
                }
            }, cleanup.Token);
            return PaymentOutcome(binding.ReturnPath, "canceled");
        }

        public async Task<string> CheckoutReturnAsync(AccountContext context, string checkoutId, CancellationToken cancellationToken)
        {
            var bound = await BoundCheckoutAsync(context, checkoutId, cancellationToken);
            context = bound.Context;
            var binding = bound.Binding;
            var client = await ClientAsync(cancellationToken);
            var checkout = await client.V1.Checkout.Sessions.GetAsync(checkoutId, cancellationToken: cancellationToken);
            ValidateCheckout(binding, checkout);
            context = await OwnerAsync(context, cancellationToken);
            var confirmed = await RefreshAsync(context.Organization!.Id, cancellationToken);
            await RestoreCheckoutScopeAsync(context, binding, cancellationToken);
            return PaymentOutcome(binding.ReturnPath, confirmed ? "success" : checkout.Status == "expired" ? "canceled" : "pending");
        }

        private static string PaymentOutcome(string path, string outcome)
        {
            var uri = new Uri(Origin + path);
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            query["payment"] = outcome;
            return Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(uri.AbsolutePath, query);
        }

        private async Task<PlanSnapshot?> PlanSnapshotAsync(AccountContext context, CancellationToken cancellationToken)
        {
            var binding = await ReadAsync(context.Organization!.Id, cancellationToken);
            if (binding?.SubscriptionId is not { Length: > 0 })
            {
                return null;
            }

            var client = await ClientAsync(cancellationToken);
            var subscription = await client.V1.Subscriptions.GetAsync(binding.SubscriptionId,
                new() { Expand = ["items.data.price.product", "pending_update.subscription_items.price.product", "latest_invoice"] }, cancellationToken: cancellationToken);
            if (subscription.Id != binding.SubscriptionId || subscription.CustomerId != binding.CustomerId || subscription.Metadata.GetValueOrDefault("organization_id") != context.Organization.Id)
            {
                throw new IOException("Subscription ownership does not match.");
            }

            var item = subscription.Items?.Data is { Count: 1 } items ? items[0] : null;
            var mapped = item is { Quantity: 1 } ? EffectivePlan(item.Price) : null;
            if (item is null || mapped is null)
            {
                throw Conflict("The current subscription needs billing attention.");
            }

            if (subscription.Status is "active" or "trialing" && item.CurrentPeriodEnd <= time.GetUtcNow())
            {
                throw new IOException("Stripe returned an expired subscription period.");
            }

            var (plan, interval) = mapped.Value;
            var scheduledKey = ""; var scheduledInterval = ""; DateTimeOffset? scheduledAt = null;
            if (subscription.CancelAtPeriodEnd && context.Organization.Kind == "personal") { scheduledKey = "free"; scheduledAt = item.CurrentPeriodEnd; }
            if (subscription.ScheduleId is { Length: > 0 } scheduleId)
            {
                var schedule = await client.V1.SubscriptionSchedules.GetAsync(scheduleId, new() { Expand = ["phases.items.price.product"] }, cancellationToken: cancellationToken);
                if (schedule.Id != scheduleId || schedule.SubscriptionId != subscription.Id || schedule.CustomerId != binding.CustomerId || schedule.CurrentPhase is null)
                {
                    throw new IOException("Subscription schedule ownership does not match.");
                }

                var next = schedule.Phases?.Where(phase => phase.StartDate >= schedule.CurrentPhase.EndDate).OrderBy(phase => phase.StartDate).FirstOrDefault();
                if (next is not null)
                {
                    var nextMapped = (next.Items is { Count: 1 } phaseItems && phaseItems[0].Quantity == 1 ? EffectivePlan(phaseItems[0].Price) : null) ?? throw Conflict("The scheduled plan needs billing attention.");
                    (scheduledKey, scheduledInterval) = (nextMapped.Plan.Key, nextMapped.Interval); scheduledAt = next.StartDate;
                }
            }
            var pendingKey = ""; var pendingInterval = ""; var paymentUrl = "";
            if (subscription.PendingUpdate is { } pending)
            {
                var pendingMapped = (pending.SubscriptionItems is { Count: 1 } pendingItems && pendingItems[0].Quantity == 1 ? EffectivePlan(pendingItems[0].Price) : null) ?? throw Conflict("The pending plan needs billing attention.");
                (pendingKey, pendingInterval) = (pendingMapped.Plan.Key, pendingMapped.Interval);
                // The update's open invoice is where the customer authenticates (3D Secure) or retries its payment.
                if (subscription.LatestInvoice is { Status: "open" } invoice && invoice.CustomerId == binding.CustomerId)
                {
                    paymentUrl = Invoices.InvoiceHistory.SafeUrl(invoice.HostedInvoiceUrl);
                }
            }
            return new(client, binding, subscription, item, new(plan.Key, interval, item.CurrentPeriodEnd, scheduledKey, scheduledInterval, scheduledAt,
                pendingKey, pendingInterval, subscription.CancelAtPeriodEnd, subscription.Status is "active" or "trialing", paymentUrl)
            { PeriodStart = item.CurrentPeriodStart > DateTime.UnixEpoch && item.CurrentPeriodStart < item.CurrentPeriodEnd ? item.CurrentPeriodStart : null });
        }

        private async Task<BillingChangeReceipt> ReserveChangeAsync(AccountContext context, PlanSnapshot snapshot, string action, string key, string interval, CancellationToken cancellationToken)
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                context = await OwnerAsync(context, cancellationToken);
                var document = await state.GetAsync(ChangesCollection, context.Organization!.Id, cancellationToken);
                var previous = document is null ? null : AccountService.Read<BillingChangeReceipt>(document);
                if (previous is not null && (previous.OrganizationId != context.Organization.Id || previous.Revision < 1 || previous.RequestId.Length == 0 || previous.Status is not ("reserved" or "applying" or "applied" or "abandoned")))
                {
                    throw new IOException("Billing change scope is invalid.");
                }

                if (previous is { Status: "reserved" or "applying" } && !Realized(previous, snapshot.State) &&
                    (previous.Status == "applying" || SourceMatches(previous, snapshot)))
                {
                    if (previous.SubscriptionId != snapshot.Subscription.Id || previous.Action != action || previous.Plan != key || previous.Interval != interval)
                    {
                        throw Conflict("Another billing change is in progress. Retry that change before choosing another plan.");
                    }

                    return previous;
                }
                var receipt = new BillingChangeReceipt
                {
                    OrganizationId = context.Organization.Id,
                    SubscriptionId = snapshot.Subscription.Id,
                    RequestId = Guid.CreateVersion7().ToString(),
                    Action = action,
                    Plan = key,
                    Interval = interval,
                    SourcePlanKey = snapshot.State.PlanKey,
                    SourceInterval = snapshot.State.Interval,
                    SourcePriceId = snapshot.Item.Price.Id,
                    PeriodEnd = snapshot.Item.CurrentPeriodEnd,
                    Revision = checked((previous?.Revision ?? 0) + 1)
                };
                try { await state.PutAsync(ChangesCollection, context.Organization.Id, JsonSerializer.SerializeToElement(receipt), document is null ? TerraBasePrecondition.Absent : TerraBasePrecondition.Matches(document.Version), cancellationToken); return receipt; }
                catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
            }
            throw Conflict("Billing changed concurrently. Retry the change.");
        }
        private async Task BeginChangeAsync(string orgId, BillingChangeReceipt receipt, CancellationToken cancellationToken)
        {
            var document = await state.GetAsync(ChangesCollection, orgId, cancellationToken) ?? throw Conflict("Billing changed concurrently. Reload billing.");
            var current = AccountService.Read<BillingChangeReceipt>(document);
            if (current.OrganizationId != orgId || current.RequestId != receipt.RequestId)
            {
                throw Conflict("Billing changed concurrently. Reload billing.");
            }

            if (current.Action == "change" && current.Plan != "free" && (current.TargetPriceId.Length == 0 || current.TargetPriceId != receipt.TargetPriceId))
            {
                throw new IOException("Billing change price binding is unavailable.");
            }

            if (current.Status == "applying") { receipt.Status = "applying"; return; }
            if (current.Status != "reserved")
            {
                throw Conflict("Billing changed concurrently. Reload billing.");
            }

            current.Status = "applying";
            try { await state.PutAsync(ChangesCollection, orgId, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cancellationToken); }
            catch (TerraBasePreconditionFailedException)
            {
                var latest = await state.GetAsync(ChangesCollection, orgId, cancellationToken);
                if (latest is null || AccountService.Read<BillingChangeReceipt>(latest) is not { Status: "applying" } applying || applying.RequestId != receipt.RequestId)
                {
                    throw Conflict("Billing changed concurrently. Reload billing.");
                }
            }
            receipt.Status = "applying";
        }
        private async Task<Price> TargetPriceAsync(AccountContext context, BillingChangeReceipt receipt, StripeClient client, CancellationToken cancellationToken)
        {
            Price? candidate = null;
            for (var attempt = 0; attempt < 4; attempt++)
            {
                context = await OwnerAsync(context, cancellationToken);
                var document = await state.GetAsync(ChangesCollection, context.Organization!.Id, cancellationToken) ?? throw Conflict("Billing changed concurrently. Reload billing.");
                var current = AccountService.Read<BillingChangeReceipt>(document);
                if (current.OrganizationId != context.Organization.Id || current.RequestId != receipt.RequestId)
                {
                    throw Conflict("Billing changed concurrently. Reload billing.");
                }

                if (current.TargetPriceId.Length > 0)
                {
                    receipt.TargetPriceId = current.TargetPriceId;
                    var price = candidate?.Id == current.TargetPriceId ? candidate : await client.V1.Prices.GetAsync(current.TargetPriceId,
                        new() { Expand = ["product"] }, cancellationToken: cancellationToken);
                    if (price.Id != current.TargetPriceId || !MatchesPrice(price, PlanCatalog.For(receipt.Plan), receipt.Interval))
                    {
                        throw new IOException("Stripe returned a different plan for the bound change price.");
                    }

                    return price;
                }
                // Once a mutation can have reached Stripe its original price and
                // idempotency key are inseparable, even if the price is archived.
                if (current.Status != "reserved")
                {
                    throw new IOException("Billing change price binding is unavailable.");
                }

                candidate ??= await PriceAsync(client, PlanCatalog.For(receipt.Plan), receipt.Interval, cancellationToken);
                current.TargetPriceId = candidate.Id;
                try
                {
                    await state.PutAsync(ChangesCollection, context.Organization.Id, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                    receipt.TargetPriceId = candidate.Id;
                    return candidate;
                }
                catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
            }
            throw Conflict("Billing changed concurrently. Retry the change.");
        }
        private async Task AbandonReservationAsync(string orgId, BillingChangeReceipt receipt)
        {
            // A reserved receipt has never admitted a vendor mutation. An applying
            // receipt can have an unknown provider outcome and must keep its key.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var document = await state.GetAsync(ChangesCollection, orgId, cleanup.Token);
            if (document is null)
            {
                return;
            }

            var current = AccountService.Read<BillingChangeReceipt>(document);
            if (current.OrganizationId != orgId || current.RequestId != receipt.RequestId || current.Status != "reserved")
            {
                return;
            }

            current.Status = "abandoned";
            try { await state.PutAsync(ChangesCollection, orgId, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cleanup.Token); }
            catch (TerraBasePreconditionFailedException) { }
        }
        private async Task SettleChangeAsync(AccountContext context, BillingChangeReceipt receipt)
        {
            // Stripe accepted the operation, so it is the outcome even when the local projection loses a race with the
            // webhooks reconciling the same change; they and the next billing view finish it from Stripe's facts.
            try { await ReconcileChangeAsync(context, receipt); }
            catch (Exception error) when (error is IOException or TerraBaseException or HttpRequestException or StripeException or AccountAccessException or OperationCanceledException)
            { logger.BillingAppliedInStripeReconciliationDeferred(receipt.Action, receipt.RequestId, error.GetType().Name, BillingPlanEndpoints.Describe(error)); }
        }
        private async Task ReconcileChangeAsync(AccountContext context, BillingChangeReceipt receipt)
        {
            // Stripe accepted the operation. Reconcile even if the browser closed.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await RefreshAsync(context.Organization!.Id, cleanup.Token);
            var snapshot = await PlanSnapshotAsync(context, cleanup.Token);
            if (snapshot is null || !Realized(receipt, snapshot.State))
            {
                throw new IOException("Stripe accepted the change, but its updated state is not available yet. Reload billing.");
            }

            await FinishChangeAsync(context.Organization.Id, receipt, cleanup.Token);
        }
        private async Task FinishChangeAsync(string orgId, BillingChangeReceipt receipt, CancellationToken cancellationToken)
        {
            var document = await state.GetAsync(ChangesCollection, orgId, cancellationToken) ?? throw new IOException("Billing change receipt is unavailable.");
            var current = AccountService.Read<BillingChangeReceipt>(document);
            if (current.OrganizationId != orgId || current.RequestId != receipt.RequestId)
            {
                throw Conflict("Billing changed concurrently. Reload billing.");
            }

            if (current.Status == "applied")
            {
                return;
            }

            current.Status = "applied";
            try { await state.PutAsync(ChangesCollection, orgId, JsonSerializer.SerializeToElement(current), TerraBasePrecondition.Matches(document.Version), cancellationToken); }
            catch (TerraBasePreconditionFailedException)
            {
                var latest = await state.GetAsync(ChangesCollection, orgId, cancellationToken); if (latest is null || AccountService.Read<BillingChangeReceipt>(latest) is not { Status: "applied" } applied || applied.RequestId != receipt.RequestId)
                {
                    throw;
                }
            }
        }
        private static bool Realized(BillingChangeReceipt receipt, BillingPlanState state)
        {
            return receipt.Action == "undo"
            ? state.ScheduledPlanKey.Length == 0 && !state.CancelAtPeriodEnd && state.PlanKey == receipt.SourcePlanKey && state.Interval == receipt.SourceInterval
            : state.PlanKey == receipt.Plan && state.Interval == receipt.Interval || state.PendingPlanKey == receipt.Plan && state.PendingInterval == receipt.Interval || state.ScheduledPlanKey == receipt.Plan && state.ScheduledInterval == receipt.Interval;
        }

        private static bool SourceMatches(BillingChangeReceipt receipt, PlanSnapshot snapshot)
        {
            return receipt.SubscriptionId == snapshot.Subscription.Id && receipt.SourcePriceId == snapshot.Item.Price.Id && receipt.PeriodEnd == snapshot.Item.CurrentPeriodEnd;
        }

        private async Task ValidateSourceAsync(string orgId, BillingChangeReceipt receipt, PlanSnapshot snapshot)
        {
            if (!SourceMatches(receipt, snapshot))
            {
                await AbandonReservationAsync(orgId, receipt);
                throw Conflict("The subscription changed in Stripe. Reload billing before retrying.");
            }
        }
        private static void ValidateChange(BillingPlanState state)
        {
            if (!state.Entitled)
            {
                throw Conflict("The current subscription needs billing attention.");
            }

            if (state.PendingPlanKey.Length > 0)
            {
                throw Conflict("Wait for Stripe to confirm the pending payment before changing plans again.");
            }

            if (state.ScheduledPlanKey.Length > 0)
            {
                throw Conflict("Undo the scheduled change before choosing another plan.");
            }

            if (state.CancelAtPeriodEnd)
            {
                throw Conflict("Resume the subscription in Manage payment before changing plans.");
            }
        }
        private static (string Key, string Interval) NormalizeChange(AccountContext context, string key, string interval)
        {
            key = key.Trim().ToLowerInvariant(); interval = interval.Trim().ToLowerInvariant();
            var plan = PlanCatalog.For(key);
            if (plan.Key == "free") { if (context.Organization?.Kind != "personal") { throw new AccountAccessException(plan.Name + " is available only for Personal workspaces.", 400); } return (key, ""); }
            if (interval.Length == 0)
            {
                interval = plan.CheckoutInterval;
            }

            if (!plan.SupportsInterval(interval))
            {
                throw new AccountAccessException("Choose the published billing interval for this plan.", 400);
            }

            return (key, interval);
        }
        private static long Rank(string key)
        {
            return PlanCatalog.For(key).PriceCents;
        }

        private static AccountAccessException Conflict(string message)
        {
            return new(message, 409);
        }

        private sealed record PlanSnapshot(StripeClient Client, BillingBinding Binding, Subscription Subscription, SubscriptionItem Item, BillingPlanState State);
    }

    public sealed class BillingChangeReceipt
    {
        public string OrganizationId { get; set; } = "";
        public string SubscriptionId { get; set; } = "";
        public string RequestId { get; set; } = "";
        public string Action { get; set; } = "";
        public string Plan { get; set; } = "";
        public string Interval { get; set; } = "";
        public string SourcePlanKey { get; set; } = "";
        public string SourceInterval { get; set; } = "";
        public string SourcePriceId { get; set; } = "";
        public string TargetPriceId { get; set; } = "";
        public DateTimeOffset PeriodEnd { get; set; }
        public long Revision { get; set; }
        public string Status { get; set; } = "reserved";
    }
}

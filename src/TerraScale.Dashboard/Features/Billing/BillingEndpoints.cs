using TerraScale.Dashboard.Platform.Clients;
using Stripe;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Features.Billing.Invoices;
using TerraScale.Dashboard.Features.Billing.Plans;
using TerraScale.Dashboard.Features.Billing.Webhooks;
using TerraScale.Dashboard.Common.Validation;
using TerraScale.Dashboard.Errors;

namespace TerraScale.Dashboard.Features.Billing
{
    public static class BillingEndpoints
    {
        public static IServiceCollection AddBillingFeatures(this IServiceCollection services)
        {
            return services.AddSingleton<BillingSchemaGate>().AddSingleton<StripeBilling>().AddSingleton<InternalBillingService>().AddHostedService<PlanReconciler>()
                .AddSingleton<StripeBindingRefresh>()
                .AddHostedService(services => services.GetRequiredService<StripeBindingRefresh>())
                .AddSingleton<UsageOverageReporter>().AddHostedService<UsageOverageWorker>();
        }

        public static async Task<BillingPage> LoadAsync(ShellModel shell, StripeBilling billing, SpendingService spending,
            IQueryCollection query, DateTimeOffset now, InternalBillingService internalBilling, UsageOverageReporter reporter, CancellationToken cancellationToken)
        {
            var page = new BillingPage(shell, PlanCatalog.Plans)
            {
                Binding = await billing.ReadBindingAsync(shell.Context, cancellationToken)
            };
            if (shell.Context.Organization!.InternalBilling is not null)
            {
                var internalBill = await internalBilling.ReadAsync(shell.Context, cancellationToken);
                var internalControls = await spending.ReadAsync(shell.Context, cancellationToken);
                return page with
                {
                    InternalBill = internalBill,
                    PlanChanges = new(page.Binding, null, null, ""),
                    History = new([], InvoiceHistory.ForBrowser([], query, now), InvoiceHistory.Summarize([], now), now, "", internalControls.OrganizationCapCents)
                };
            }
            var changesRead = BillingPlanPresentation.ForAsync(page, billing, query, now, cancellationToken);
            var paymentMethodRead = billing.PaymentMethodAsync(shell.Context, cancellationToken);
            var invoicesRead = billing.InvoicesAsync(shell.Context, cancellationToken);
            var detailsRead = billing.DetailsAsync(shell.Context, cancellationToken);
            var overageRead = page.Binding is { Kind: "stripe", SubscriptionId.Length: > 0 } subscribed && PlanCatalog.For(shell.Context.Organization) is { PriceCents: > 0 }
                ? reporter.OperationUnitOverageAsync(subscribed, cancellationToken) : Task.FromResult<long?>(0);
            BillingCard? paymentMethod = null;
            var paymentMethodUnavailable = false;
            try
            {
                switch (await paymentMethodRead)
                {
                    case BillingCard card:
                        paymentMethod = card;
                        break;
                    case Unavailable:
                        paymentMethodUnavailable = true;
                        break;
                    case NoCard:
                        break;
                }
            }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { paymentMethodUnavailable = true; }
            IReadOnlyList<BillingInvoice> invoices = [];
            var errorMessage = "";
            try { invoices = await invoicesRead; }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { errorMessage = "Payment history could not be loaded. Reload the page to retry."; }
            long? overage = null;
            try { overage = await overageRead; }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or StripeException ||
                                          error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { }
            var changes = await changesRead;
            BillingDetails? details = null;
            var detailsUnavailable = false;
            try { details = await detailsRead; }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or IOException or StripeException ||
                error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            { detailsUnavailable = true; }
            return page with
            {
                Details = details,
                DetailsUnavailable = detailsUnavailable,
                OverageUnits = overage,
                PlanChanges = changes,
                PaymentMethod = paymentMethod,
                PaymentMethodUnavailable = paymentMethodUnavailable,
                History = new(invoices, InvoiceHistory.ForBrowser(invoices, query, now), InvoiceHistory.Summarize(invoices, now, Renewal(page.Binding, changes.State)), now, errorMessage, 0)
            };
        }

        private static InvoiceRenewal? Renewal(BillingBinding? binding, BillingPlanState? state)
        {
            static long Price(string key, string interval)
            {
                return PlanCatalog.Plans.FirstOrDefault(plan => plan.Key == key) is { } plan
                ? plan.IntervalPriceCents(interval) : 0;
            }

            if (state is not null)
            {
                var scheduled = state.ScheduledPlanKey.Length > 0 && (state.ScheduledAt is null || state.ScheduledAt <= state.RenewalAt);
                var interval = scheduled && state.ScheduledInterval.Length > 0 ? state.ScheduledInterval : state.Interval;
                return new(state.RenewalAt, Price(scheduled ? state.ScheduledPlanKey : state.PlanKey, interval), "usd", state.Interval, state.CancelAtPeriodEnd, state.PeriodStart);
            }
            if (binding is not { SubscriptionId.Length: > 0, RenewalAt: { } renews } || binding.Status is "canceled" or "incomplete_expired")
            {
                return null;
            }

            var current = binding.EffectiveInterval.Length > 0 ? binding.EffectiveInterval : binding.Interval;
            return new(renews, Price(binding.EffectivePlan, current), "usd", current);
        }
    }

    public sealed record CheckoutRequest([property: System.Text.Json.Serialization.JsonPropertyName("_csrf")] string Csrf,
        string Plan, string Interval, [property: System.Text.Json.Serialization.JsonPropertyName("return")] string Return);
}

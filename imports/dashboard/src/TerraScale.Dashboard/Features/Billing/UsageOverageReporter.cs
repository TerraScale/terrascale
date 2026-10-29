using System.Text.Json;
using StripeException = Stripe.StripeException;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;

namespace TerraScale.Dashboard.Features.Billing;

// Hourly, every paid Stripe subscription's operation units over its plan allowance this UTC month
// become invoice items on the customer's next subscription invoice. Stripe holds the billed record.
public sealed class UsageOverageReporter(IDashboardState state, IAllowanceNotificationBackend usage, StripeBilling billing, TimeProvider clock, ILogger<UsageOverageReporter> logger)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        string? after = null;
        do
        {
            var page = await usage.ReadAsync(after, cancellationToken);
            var month = AllowanceNotifications.ValidatedMonth(page, after, clock.GetUtcNow());
            foreach (var item in page.Items)
            {
                if (item.Operations is not { } units) continue;
                try { await ReportAsync(item.OrganizationId, units, month, cancellationToken); }
                catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Usage overage could not be reported for one organization ({ErrorType}); other organizations continue.", error.GetType().Name);
                }
            }
            after = page.Next;
        } while (after is not null);
    }

    public async Task ReportAsync(string organizationId, long units, DateTimeOffset month, CancellationToken cancellationToken)
    {
        var document = await state.GetAsync(AccountService.OrganizationsCollection, organizationId, cancellationToken);
        if (document is null) return;
        var organization = AccountService.Read<OrganizationRecord>(document);
        if (document.Id != organizationId || organization.Id != organizationId) throw new IOException("Overage organization identity is invalid.");
        // Internal billing reports usage without Stripe charges.
        if (organization.InternalBilling is not null || PlanCatalog.For(organization) is not { PriceCents: > 0 } plan) return;
        var bindingDocument = await state.GetAsync(StripeBilling.Collection, organizationId, cancellationToken);
        if (bindingDocument is null) return;
        var binding = StripeBilling.ReadBinding(bindingDocument, organizationId);
        if (binding.Kind != "stripe" || binding.CustomerId.Length == 0 || binding.SubscriptionId.Length == 0 || binding.Status is not ("active" or "trialing" or "past_due")) return;
        await billing.ReportOperationUnitOverageAsync(binding, plan, OperationUnitOverage.Units(plan, units), month, cancellationToken);
    }
}

public sealed class UsageOverageWorker(UsageOverageReporter reporter, TimeProvider clock, ILogger<UsageOverageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try { await reporter.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or InvalidOperationException or StripeException or OperationCanceledException)
            {
                logger.LogWarning("Usage overage reporting is unavailable ({ErrorType}); retrying next hour.", error.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

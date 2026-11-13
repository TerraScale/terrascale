using Microsoft.EntityFrameworkCore;
using Stripe;
using TerraScale.TerraBase.Contracts;
using TerraScale.Dashboard.Platform.Persistence;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    private static readonly TimeSpan EventRetention = TimeSpan.FromDays(29);

    private async Task RecordTermsAsync(StripeClient client, string orgId, Subscription subscription, CancellationToken cancellationToken)
    {
        try { await RecordTermsOnceAsync(client, orgId, subscription, cancellationToken); }
        catch (Exception error) when (error is DbUpdateException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            telemetry.ReportUnrecordedTerms();
            BillingLog.TermsNotRecorded(logger, error.GetType().Name);
        }
    }

    private async Task RecordTermsOnceAsync(StripeClient client, string orgId, Subscription subscription, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var db = await storage.CreateDbContextAsync(cancellationToken);
        var row = await db.BillingSubscriptions.SingleOrDefaultAsync(item => item.OrganizationId == orgId && item.SubscriptionId == subscription.Id, cancellationToken);
        var latest = await db.BillingTerms.AsNoTracking().Where(item => item.OrganizationId == orgId && item.SubscriptionId == subscription.Id)
            .OrderByDescending(item => item.EffectiveFrom).FirstOrDefaultAsync(cancellationToken);
        var startedAt = Utc(subscription.StartDate);
        var from = row?.CheckedAt ?? startedAt;
        var horizon = now - EventRetention;
        var windowStart = from < horizon ? horizon : from;
        var events = new List<(DateTimeOffset At, string Type, Subscription Snapshot)>();
        await foreach (var item in client.V1.Events.ListAutoPagingAsync(new() { Types = ["customer.subscription.created", "customer.subscription.updated"], Created = new DateRangeOptions { GreaterThanOrEqual = windowStart.UtcDateTime, LessThanOrEqual = now.UtcDateTime }, Limit = 100 }, cancellationToken: cancellationToken))
        {
            if (item.Data.Object is Subscription snapshot && snapshot.Id == subscription.Id)
            {
                events.Add((Utc(item.Created), item.Type, snapshot));
            }
        }
        events.Sort((left, right) => left.At.CompareTo(right.At));
        var prices = new Dictionary<string, Price>(StringComparer.Ordinal);
        if (subscription.Items.Data is [{ Price: { } polledPrice }]) { prices[polledPrice.Id] = polledPrice; }
        var polled = await TermsAtAsync(client, prices, orgId, subscription, now, BillingTerms.FirstCheck, now, cancellationToken);
        var added = new List<BillingTerms>();
        var current = latest;
        if (from < horizon)
        {
            current = Retyped(polled, from, BillingTerms.Unestablished);
            added.Add(current);
            if (events.Count == 0)
            {
                current = Retyped(polled, horizon, BillingTerms.FirstCheck);
                added.Add(current);
            }
        }
        else if (latest is null)
        {
            var created = events.FindIndex(item => item.Type == "customer.subscription.created");
            current = created >= 0 ? await TermsAtAsync(client, prices, orgId, events[created].Snapshot, startedAt, BillingTerms.StartDate, now, cancellationToken)
                : Retyped(polled, startedAt, events.Count == 0 ? BillingTerms.StartDate : BillingTerms.Unestablished);
            added.Add(current);
        }
        foreach (var (at, type, snapshot) in events)
        {
            if (type != "customer.subscription.updated") { continue; }
            var terms = await TermsAtAsync(client, prices, orgId, snapshot, at, BillingTerms.ProviderEvent, now, cancellationToken);
            if (current is null || current.Basis == BillingTerms.Unestablished || terms.PricingVersion != current.PricingVersion)
            {
                added.Add(terms);
                current = terms;
            }
        }
        if (current is null || polled.PricingVersion != current.PricingVersion)
        {
            var since = events.Count > 0 && events[^1].At > windowStart ? events[^1].At : windowStart;
            added.Add(Retyped(polled, since, BillingTerms.Unestablished));
        }
        foreach (var terms in added.DistinctBy(item => (item.EffectiveFrom, item.PricingVersion)))
        {
            db.BillingTerms.Add(terms);
        }
        if (row is null)
        {
            db.BillingSubscriptions.Add(new()
            {
                Version = AccountService.NewId(),
                OrganizationId = orgId,
                SubscriptionId = subscription.Id,
                CustomerId = subscription.CustomerId,
                StartedAt = startedAt,
                EndedAt = subscription.EndedAt is { } ended ? Utc(ended) : null,
                Status = subscription.Status,
                CheckedAt = now
            });
        }
        else
        {
            row.Status = subscription.Status;
            row.EndedAt = subscription.EndedAt is { } ended ? Utc(ended) : null;
            row.CheckedAt = now > row.CheckedAt ? now : row.CheckedAt;
            row.Version = AccountService.NewId();
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<BillingTerms> TermsAtAsync(StripeClient client, Dictionary<string, Price> prices, string orgId, Subscription snapshot, DateTimeOffset at, string basis, DateTimeOffset now, CancellationToken cancellationToken)
    {
        BillingPlan? plan = null;
        if (snapshot.Items?.Data is [{ Quantity: 1, Price.Id.Length: > 0 } item])
        {
            if (!prices.TryGetValue(item.Price.Id, out var price))
            {
                price = item.Price.Product is not null ? item.Price : await client.V1.Prices.GetAsync(item.Price.Id, new() { Expand = ["product"] }, cancellationToken: cancellationToken);
                prices[item.Price.Id] = price;
            }
            plan = EffectivePlan(price)?.Plan;
        }
        return BillingTerms.For(orgId, snapshot.Id, at, basis, now, plan);
    }

    private static BillingTerms Retyped(BillingTerms terms, DateTimeOffset from, string basis) =>
        BillingTerms.For(terms.OrganizationId, terms.SubscriptionId, from, basis, terms.LearnedAt, terms.Priced ? PlanCatalog.Plans.First(plan => plan.Key == terms.Plan) : null);

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

using Dapper;
using System.Data;
using Stripe;
using TerraScale.Dashboard.Platform.Clients;
using TerraScale.Dashboard.Platform.Persistence;
using AccountService = TerraScale.Dashboard.Common.Accounts.AccountService;

namespace TerraScale.Dashboard.Features.Billing;

public sealed partial class StripeBilling
{
    private static readonly TimeSpan EventRetention = TimeSpan.FromDays(29);

    private async Task RecordTermsAsync(StripeClient client, string orgId, Subscription subscription, CancellationToken cancellationToken)
    {
        try { await RecordTermsOnceAsync(client, orgId, subscription, cancellationToken); }
        catch (Exception error) when (error is DBConcurrencyException or StripeException or HttpRequestException or IOException or TerraBaseException or TimeoutException || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            telemetry.ReportUnrecordedTerms();
            BillingLog.TermsNotRecorded(logger, error.GetType().Name);
        }
    }

    private const string SubscriptionOf = DashboardSql.BillingSubscriptions.Select + " WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId";
    private const string LatestTerms = DashboardSql.BillingTerms.Select
        + " WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId ORDER BY effective_from DESC LIMIT 1";
    private const string RecordedTerms = DashboardSql.BillingTerms.Select
        + " WHERE organization_id = @OrganizationId AND subscription_id = @SubscriptionId AND effective_from >= @Earliest";

    private async Task RecordTermsOnceAsync(StripeClient client, string orgId, Subscription subscription, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var connection = await storage.OpenAsync(cancellationToken);
        var key = new { OrganizationId = orgId, SubscriptionId = subscription.Id };
        var row = await connection.QuerySingleOrDefaultAsync<BillingSubscription>(SubscriptionOf, key);
        var latest = await connection.QueryFirstOrDefaultAsync<BillingTerms>(LatestTerms, key);
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
        var earliest = added.Count > 0 ? added.Min(item => item.EffectiveFrom) : now;
        var recorded = (await connection.QueryAsync<BillingTerms>(RecordedTerms, new { OrganizationId = orgId, SubscriptionId = subscription.Id, Earliest = earliest }))
            .Select(item => (item.EffectiveFrom, item.PricingVersion, item.Basis)).ToHashSet();
        var inserts = added.DistinctBy(item => (item.EffectiveFrom, item.PricingVersion, item.Basis))
            .Where(item => !recorded.Contains((item.EffectiveFrom, item.PricingVersion, item.Basis))).ToArray();
        BillingSubscription next;
        var create = row is null;
        if (row is null)
        {
            next = new()
            {
                Version = AccountService.NewId(),
                OrganizationId = orgId,
                SubscriptionId = subscription.Id,
                CustomerId = subscription.CustomerId,
                StartedAt = startedAt,
                EndedAt = subscription.EndedAt is { } ended ? Utc(ended) : null,
                Status = subscription.Status,
                CheckedAt = now
            };
        }
        else
        {
            next = row with
            {
                Status = subscription.Status,
                EndedAt = subscription.EndedAt is { } ended ? Utc(ended) : null,
                CheckedAt = now > row.CheckedAt ? now : row.CheckedAt,
                OriginalVersion = row.Version,
                Version = AccountService.NewId(),
            };
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var terms in inserts)
        {
            await connection.ExecuteAsync(DashboardSql.BillingTerms.Insert, terms, transaction);
        }

        DashboardDb.Expect(create
            ? await connection.ExecuteAsync(DashboardSql.BillingSubscriptions.Insert, next, transaction)
            : await connection.ExecuteAsync(DashboardSql.BillingSubscriptions.Update, next, transaction));
        await transaction.CommitAsync(cancellationToken);
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

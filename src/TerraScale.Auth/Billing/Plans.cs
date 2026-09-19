namespace TerraScale.Auth.Billing;

using TerraScale.Auth.Configuration;
using TerraScale.Auth.Store;

/// Plan names, price-id mapping, and the per-plan user quota.
public static class Plans
{
    public const string Free = "free";
    public const string Pro = "pro";
    public const string Enterprise = "enterprise";

    /// The user quota of a plan; unknown plans fall back to the free quota.
    public static int Quota(string plan) => plan switch
    {
        Pro => 10_000,
        Enterprise => 1_000_000,
        _ => 100,
    };

    /// Maps a Stripe price id to its plan; null when the price belongs to no known plan.
    public static string? FromPriceId(string? priceId, AuthOptions options) =>
        priceId is null ? null
        : priceId == options.PricePro ? Pro
        : priceId == options.PriceEnterprise ? Enterprise
        : null;
}

/// The effective billing state of an instance.
public sealed record BillingState(string Plan, string Status, int Quota, Subscription? Subscription)
{
    /// The live subscription wins — active, trialing, or past_due (past_due keeps the paid
    /// plan but flags it); anything else falls back to the free plan.
    public static async Task<BillingState> CurrentAsync(AuthStore store, string instanceId, CancellationToken ct = default)
    {
        var live = (await store.ListSubscriptionsAsync(instanceId, ct))
            .Where(s => s.Status is "active" or "trialing" or "past_due")
            .OrderByDescending(s => s.UpdatedAt)
            .ThenByDescending(s => s.CreatedAt)
            .FirstOrDefault();
        return live is null
            ? new(Plans.Free, Plans.Free, Plans.Quota(Plans.Free), null)
            : new(live.Plan, live.Status, Plans.Quota(live.Plan), live);
    }

    /// <summary>Live (non-deleted) users of the instance, stopping once <paramref name="cap"/>
    /// is reached — callers only need "at quota?" and a display number, not exact totals.
    /// Walks the shared users collection by the raw page cursor, same as ManagementEndpoints,
    /// so foreign-user interleave cannot end the scan early.</summary>
    public static async Task<int> CountUsersAsync(AuthStore store, string instanceId, int cap, CancellationToken ct = default)
    {
        var count = 0;
        string? after = null;
        while (count < cap)
        {
            var (page, next) = await store.ListUsersAsync(instanceId, 1000, after, ct);
            count += page.Count(u => !u.Deleted);
            if (next is null)
                return count;
            after = next;
        }
        return count;
    }
}

/// The sign-up quota gate.
public static class Quota
{
    /// True when the instance's effective plan still has room for one more user.
    public static async Task<bool> AllowsSignUpAsync(AuthStore store, string instanceId, CancellationToken ct = default)
    {
        var state = await BillingState.CurrentAsync(store, instanceId, ct);
        return await BillingState.CountUsersAsync(store, instanceId, state.Quota, ct) < state.Quota;
    }
}

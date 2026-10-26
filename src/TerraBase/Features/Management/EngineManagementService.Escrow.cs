using System.Diagnostics.Metrics;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>Grants other regions units of an organization's database allowance (docs/terrabase/decisions/0016).</summary>
internal interface IDatabaseEscrow
{
    /// <summary>
    /// Grants <paramref name="region"/> up to <paramref name="want"/> units this region holds, unless a grant above
    /// <paramref name="received"/> is still unconfirmed, and returns the cumulative grant; null while the catalog cannot be used.
    /// </summary>
    ValueTask<long?> GrantAsync(string organizationId, string region, long received, int want, CancellationToken cancellationToken);
}

/// <summary>The other regions, as holders of allowance units (decision 0016).</summary>
internal interface IEscrowPeers
{
    /// <summary>The other serving regions, ordered.</summary>
    ValueTask<IReadOnlyList<string>> RegionsAsync(CancellationToken cancellationToken);

    /// <summary>Asks <paramref name="region"/> for units: its cumulative grant to this region, or null when it did not answer.</summary>
    ValueTask<long?> TransferAsync(string region, string organizationId, long received, int want, CancellationToken cancellationToken);
}

/// <summary>A region's registration as an allowance holder; the earliest registration is the genesis region's.</summary>
internal sealed record QuotaRegion(string Registration);

/// <summary>One region's escrow of an organization's database allowance: cumulative units granted to, and confirmed from, each region.</summary>
internal sealed record DatabaseEscrowShare(Dictionary<string, long> Granted, Dictionary<string, long> Received);

/// <summary>The region whose allowance unit a database consumed.</summary>
internal sealed record DatabaseUnit(string Region);

internal sealed partial class EngineManagementService : IDatabaseEscrow
{
    private const string QuotaRegions = "quota_regions";

    private static readonly JsonTypeInfo<DatabaseEscrowShare> EscrowShare = TerraBaseJsonContext.Default.DatabaseEscrowShare;

    private readonly Counter<long> _escrowGranted = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.quota.escrow_granted", "{unit}",
        "Database allowance units this region granted to other regions.");

    // The plan allowance a registration spends, as this region reads it. Registering: the time to register this region at
    // when it has no registration yet; null in a read transaction.
    private sealed record AllowanceCheck(Func<PlanTier, long> Limit, string Region, IReadOnlyList<string> Serving, DateTimeOffset? Registering = null);

    // An organization's allowance in one region: its rights, the live databases, the genesis region (null while a serving
    // region is unregistered), its own escrow, and the cumulative grants it counts from each other region.
    private sealed record EscrowView(long Rights, long Live, string? Genesis, DatabaseEscrowShare Own, Dictionary<string, long> Known);

    private static string DatabaseEscrow(string organizationId) => $"organizations/{organizationId}/database_escrow";

    private static string DatabaseUnits(string organizationId) => $"organizations/{organizationId}/database_units";

    // A database's unit record: by project and id, as its record is, so a database id two projects reuse spends two units.
    private static string UnitId(string projectId, string databaseId) => $"{projectId}:{databaseId}";

    // A registration after every one seen, even when this clock is behind theirs: a region registering later is never genesis.
    private static QuotaRegion Registration(DateTimeOffset now, IEnumerable<QuotaRegion> seen) =>
        new(Ulid.NewUlid(seen.Select(region => Ulid.TryParse(region.Registration, out var id) ? id.Time.AddMilliseconds(1) : now).Append(now).Max()).ToString());

    private async ValueTask<IReadOnlyList<string>> ServingAsync(CancellationToken cancellationToken) =>
        [options.Region, .. await escrowPeers.Value.RegionsAsync(cancellationToken).ConfigureAwait(false)];

    // rights = genesis units + units counted from others - units granted to others - live databases on this region's units.
    private static async ValueTask<EscrowView> EscrowAsync(
        IStorageTransaction transaction, string organizationId, long allowed, AllowanceCheck allowance, CancellationToken cancellationToken)
    {
        var regions = new Dictionary<string, QuotaRegion>(StringComparer.Ordinal);
        await foreach (var (region, registration) in KeyedAsync(transaction, QuotaRegions, TerraBaseJsonContext.Default.QuotaRegion, cancellationToken).ConfigureAwait(false))
        {
            regions[region] = registration;
        }

        // Unregistered in a read: as the registering write will find it, after every registration ("~" sorts after ULIDs).
        if (!regions.ContainsKey(allowance.Region))
        {
            regions[allowance.Region] = allowance.Registering is { } now ? Registration(now, regions.Values) : new QuotaRegion("~");
            if (allowance.Registering is not null)
            {
                await PutAsync(transaction, QuotaRegions, allowance.Region, regions[allowance.Region], TerraBaseJsonContext.Default.QuotaRegion, cancellationToken).ConfigureAwait(false);
            }
        }

        var genesis = allowance.Serving.All(regions.ContainsKey)
            ? regions.OrderBy(pair => pair.Value.Registration, StringComparer.Ordinal).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key
            : null;
        DatabaseEscrowShare own = new([], []);
        var known = new Dictionary<string, long>(StringComparer.Ordinal);
        await foreach (var (region, share) in KeyedAsync(transaction, DatabaseEscrow(organizationId), EscrowShare, cancellationToken).ConfigureAwait(false))
        {
            if (region == allowance.Region)
            {
                own = share;
            }
            else
            {
                known[region] = share.Granted.GetValueOrDefault(allowance.Region);
            }
        }

        foreach (var (region, total) in own.Received)
        {
            known[region] = Math.Max(known.GetValueOrDefault(region), total);
        }

        // A database registered without a unit record (before escrow) spends a genesis unit.
        long live = 0, mine = 0;
        foreach (var project in await ListAllAsync(transaction, Projects(organizationId), TerraBaseJsonContext.Default.Project, cancellationToken).ConfigureAwait(false))
        {
            await foreach (var (database, _) in KeyedAsync(transaction, Databases(project.Id), TerraBaseJsonContext.Default.ManagedDatabase, cancellationToken).ConfigureAwait(false))
            {
                live++;
                var unit = await GetAsync(transaction, DatabaseUnits(organizationId), UnitId(project.Id, database), TerraBaseJsonContext.Default.DatabaseUnit, cancellationToken).ConfigureAwait(false);
                mine += (unit?.Region ?? genesis) == allowance.Region ? 1 : 0;
            }
        }

        var rights = (genesis == allowance.Region ? allowed : 0) + known.Values.Sum() - own.Granted.Values.Sum() - mine;
        return new EscrowView(rights, live, genesis, own, known);
    }

    private static async IAsyncEnumerable<(string Id, T Value)> KeyedAsync<T>(
        IStorageTransaction transaction, string collection, JsonTypeInfo<T> type, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? after = null;
        DocumentPage page;
        do
        {
            page = await transaction.ListAsync(collection, after, limit: 100, MaxPageBytes, cancellationToken).ConfigureAwait(false);
            foreach (var document in page.Documents)
            {
                yield return (document.Id, EntityPayload.Read(document, type)!);
            }

            after = page.Documents.Count > 0 ? page.Documents[^1].Id : after;
        }
        while (page.HasMore);
    }

    public async ValueTask<long?> GrantAsync(string organizationId, string region, long received, int want, CancellationToken cancellationToken)
    {
        var allowance = new AllowanceCheck(databaseAllowance.Limit, options.Region, await ServingAsync(cancellationToken).ConfigureAwait(false), time.GetUtcNow());
        return await WriteAsync<long?>(async transaction =>
        {
            // An organization not seen here yet (created by the requester's own commit) is on Hobby, the default.
            var organization = await GetAsync(transaction, Organizations, organizationId, TerraBaseJsonContext.Default.Organization, cancellationToken).ConfigureAwait(false);
            var allowed = databaseAllowance.Limit(organization?.Plan ?? PlanTier.Hobby);
            var view = await EscrowAsync(transaction, organizationId, allowed == long.MaxValue ? 0 : allowed, allowance, cancellationToken).ConfigureAwait(false);
            var granted = view.Own.Granted.GetValueOrDefault(region);
            // An earlier grant the requester has not confirmed yet answers again unchanged: a retry never grants twice.
            var give = region == options.Region || granted > received ? 0 : Math.Min(want, view.Rights);
            if (give > 0)
            {
                view.Own.Granted[region] = granted + give;
                await PutAsync(transaction, DatabaseEscrow(organizationId), options.Region, view.Own, EscrowShare, cancellationToken).ConfigureAwait(false);
                _escrowGranted.Add(give);
            }

            return granted + Math.Max(give, 0);
        }, static _ => null, cancellationToken).ConfigureAwait(false);
    }

    // Whether this region is registered as an allowance holder; convergence registers it once its catalog gate opened.
    private bool _quotaRegistered;

    // Registers this region as an allowance holder, so the genesis region settles; false while the catalog is unavailable.
    private ValueTask<bool> RegisterQuotaRegionAsync(CancellationToken cancellationToken) =>
        WriteAsync<bool>(async transaction =>
        {
            if (await transaction.GetVersionAsync(QuotaRegions, options.Region, cancellationToken).ConfigureAwait(false) is not { Deleted: false })
            {
                List<QuotaRegion> seen = [];
                await foreach (var (_, registration) in KeyedAsync(transaction, QuotaRegions, TerraBaseJsonContext.Default.QuotaRegion, cancellationToken).ConfigureAwait(false))
                {
                    seen.Add(registration);
                }

                await PutAsync(transaction, QuotaRegions, options.Region, Registration(time.GetUtcNow(), seen), TerraBaseJsonContext.Default.QuotaRegion, cancellationToken)
                    .ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken);
}

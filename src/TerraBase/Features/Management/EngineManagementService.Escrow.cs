using System.Diagnostics.Metrics;
using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Errors;
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

    // Registration attempts that ask peers for a unit: a concurrent create here can spend a unit just received.
    private const int MaxUnitAcquisitions = 3;

    private static readonly JsonTypeInfo<DatabaseEscrowShare> EscrowShare = TerraBaseJsonContext.Default.DatabaseEscrowShare;

    private readonly Counter<long> _escrowGranted = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.quota.escrow_granted", "{unit}",
        "Database allowance units this region granted to other regions.");

    private readonly Counter<long> _escrowAcquisitions = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.quota.escrow_acquisitions", "{acquisition}",
        "Requests for database allowance units from other regions, by result (granted, exhausted, unreachable).");

    // The plan allowance a registration spends, as this region reads it. Registering: the time to register this region at
    // when it has no registration yet; null in a read transaction.
    private sealed record AllowanceCheck(Func<PlanTier, long> Limit, string Region, IReadOnlyList<string> Serving, DateTimeOffset? Registering = null);

    // An organization's allowance in one region: its rights, the live databases, the genesis region (null while a serving
    // region is unregistered), its own escrow, the cumulative grants it counts from each other region, and every
    // registered region.
    private sealed record EscrowView(
        long Rights, long Live, string? Genesis, DatabaseEscrowShare Own, Dictionary<string, long> Known, IReadOnlyCollection<string> Registered);

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
        return new EscrowView(rights, live, genesis, own, known, regions.Keys);
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

    // Asks the other serving regions, the genesis region first, until this region holds need units of the tenant's
    // organization. Null then, else the refusal: unreachable regions may hold units, otherwise the organization is at its allowance.
    private async ValueTask<Conflict?> AcquireUnitsAsync(TenantContext tenant, long allowed, long need, CancellationToken cancellationToken)
    {
        var allowance = new AllowanceCheck(databaseAllowance.Limit, options.Region, await ServingAsync(cancellationToken).ConfigureAwait(false));
        if (await ReadAsync<EscrowView?>(async transaction => await EscrowAsync(transaction, tenant.OrganizationId, allowed, allowance, cancellationToken).ConfigureAwait(false),
            static _ => null, cancellationToken).ConfigureAwait(false) is not { } view)
        {
            return ManagementErrors.DatabaseAllowanceElsewhere;
        }

        // A grant whose reply was lost may have replicated here meanwhile.
        var lacking = need - view.Rights;
        // A registered region that is not serving now (its node is down) may hold units too.
        var unreachable = view.Registered.Any(region => !allowance.Serving.Contains(region));
        foreach (var region in allowance.Serving.Skip(1).OrderBy(region => region == view.Genesis ? 0 : 1).ThenBy(region => region, StringComparer.Ordinal))
        {
            var known = view.Known.GetValueOrDefault(region);
            if (lacking <= 0)
            {
                break;
            }

            var total = await escrowPeers.Value.TransferAsync(region, tenant.OrganizationId, known, (int)Math.Clamp(lacking, 1, 1000), cancellationToken).ConfigureAwait(false);
            unreachable |= total is null;
            if (total > known && await RecordReceivedAsync(tenant.OrganizationId, region, total.Value, cancellationToken).ConfigureAwait(false))
            {
                lacking -= total.Value - known;
            }
        }

        if (lacking <= 0)
        {
            _escrowAcquisitions.Add(1, new KeyValuePair<string, object?>("result", "granted"));
            return null;
        }

        _escrowAcquisitions.Add(1, new KeyValuePair<string, object?>("result", unreachable ? "unreachable" : "exhausted"));
        return unreachable ? ManagementErrors.DatabaseAllowanceElsewhere : ManagementErrors.DatabaseLimit(PlanName(tenant.Plan), allowed);
    }

    // Confirms a peer's cumulative grant in this region's escrow, in its own durable catalog commit.
    private ValueTask<bool> RecordReceivedAsync(string organizationId, string region, long total, CancellationToken cancellationToken) =>
        WriteAsync<bool>(async transaction =>
        {
            var own = await GetAsync(transaction, DatabaseEscrow(organizationId), options.Region, EscrowShare, cancellationToken).ConfigureAwait(false) ?? new([], []);
            if (own.Received.GetValueOrDefault(region) < total)
            {
                own.Received[region] = total;
                await PutAsync(transaction, DatabaseEscrow(organizationId), options.Region, own, EscrowShare, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }, static _ => false, cancellationToken);

    // A registration's unit, when the check found this region holding none: null once a peer granted one, else the refusal.
    private async ValueTask<Conflict?> AcquireUnitAsync(Func<IStorageTransaction, ValueTask<ProjectTenant>> project, AllowanceCheck allowance, CancellationToken cancellationToken) =>
        await ReadAsync<TenantContext?>(async transaction => await project(transaction).ConfigureAwait(false) is TenantContext tenant ? tenant : null, static _ => null, cancellationToken)
            .ConfigureAwait(false) is { } tenant
            ? await AcquireUnitsAsync(tenant, allowance.Limit(tenant.Plan), 1, cancellationToken).ConfigureAwait(false)
            : ManagementErrors.DatabaseAllowanceElsewhere;

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

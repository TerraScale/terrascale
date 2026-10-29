using System.Text.Json.Serialization.Metadata;
using TerraBase.Engine;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed record QuotaRegion(string Registration);

internal sealed record DatabaseEscrowShare(Dictionary<string, long> Granted, Dictionary<string, long> Received);

internal sealed record DatabaseUnit(string Region);

internal sealed partial class EngineManagementService
{
    private const string QuotaRegions = "quota_regions";

    private static readonly JsonTypeInfo<DatabaseEscrowShare> EscrowShare = TerraBaseJsonContext.Default.DatabaseEscrowShare;

    private sealed record AllowanceCheck(Func<PlanTier, long> Limit, string Region, IReadOnlyList<string> Serving, DateTimeOffset? Registering = null);

    private sealed record EscrowView(long Rights, long Live);

    private static string DatabaseEscrow(string organizationId) => $"organizations/{organizationId}/database_escrow";

    private static string DatabaseUnits(string organizationId) => $"organizations/{organizationId}/database_units";

    private static string UnitId(string projectId, string databaseId) => $"{projectId}:{databaseId}";

    private static QuotaRegion Registration(DateTimeOffset now, IEnumerable<QuotaRegion> seen) =>
        new(Ulid.NewUlid(seen.Select(region => Ulid.TryParse(region.Registration, out var id) ? id.Time.AddMilliseconds(1) : now).Append(now).Max()).ToString());

    private static async ValueTask<EscrowView> EscrowAsync(
        IStorageTransaction transaction, string organizationId, long allowed, AllowanceCheck allowance, CancellationToken cancellationToken)
    {
        var regions = new Dictionary<string, QuotaRegion>(StringComparer.Ordinal);
        await foreach (var (region, registration) in KeyedAsync(transaction, QuotaRegions, TerraBaseJsonContext.Default.QuotaRegion, cancellationToken).ConfigureAwait(false))
        {
            regions[region] = registration;
        }

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
        return new EscrowView(rights, live);
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

    private bool _quotaRegistered;

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

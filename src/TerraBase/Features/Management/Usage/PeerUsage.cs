using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Management.Usage;

/// <summary>One organization's units this node served in a month.</summary>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Reads">SQL read units.</param>
/// <param name="Writes">Billed write units.</param>
/// <param name="StorageByteHours">Stored bytes times hours.</param>
public sealed record NodeUsageMonth(string OrganizationId, long Reads, long Writes, long StorageByteHours);

/// <summary>The units this node served in a month, per organization asked for.</summary>
/// <param name="Items">One item per organization, in the order asked.</param>
public sealed record NodeUsageMonths(IReadOnlyList<NodeUsageMonth> Items);

/// <summary>
/// Sums usage over every region. Each node meters only the work it serves, once: a write at the database's home, a
/// replica read in the replica region, a forwarded request only where it runs. Its units live in its own system
/// database, which no other region holds, so every total a client or billing reads is this node's units plus every
/// other region's own, asked over the internal listener with the peer credential.
/// </summary>
internal sealed class PeerUsage(IRegionPeers peers, IHttpClientFactory http, InternalCredential credential)
{
    /// <summary>Marks a peer's request for this node's own units only, so the fan-out never recurses.</summary>
    internal const string ScopeHeader = "X-TerraBase-Usage-Scope";

    private const string NodeScope = "node";

    internal const string MonthsPath = "/v1/internal/usage/months";

    /// <summary>Organizations one months request names at most.</summary>
    internal const int MaxOrganizations = 100;

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(3);

    /// <summary>Whether a peer asked for this node's own units: honored only behind the peer credential.</summary>
    internal static bool NodeOnly(HttpContext context) =>
        (Listeners.IsForwarded(context) || Listeners.ArrivedOn(context) == TerraBaseListener.Internal)
        && context.Request.Headers[ScopeHeader] is [NodeScope];

    /// <summary>
    /// Every other region's share of the project usage the client asked for, sent as the same client request so each
    /// region checks the same token. A region that does not hold the project adds nothing; null when one cannot answer.
    /// </summary>
    public async Task<List<ProjectUsage>?> ProjectAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var path = context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        var answers = await AskAsync(peer =>
        {
            var request = Request(peer, path);
            HomeForwarding.Sign(context, request, peer.Region);
            return request;
        }, HttpStatusCode.NotFound, TerraBaseJsonContext.Default.ProjectUsage, cancellationToken).ConfigureAwait(false);
        return answers?.Where(answer => answer is not null).Select(answer => answer!).ToList();
    }

    /// <summary>Every other region's units of <paramref name="organizations"/> in <paramref name="month"/> (<c>yyyy-MM</c>); null when one cannot answer.</summary>
    public async Task<Dictionary<string, UsageTotals>?> MonthsAsync(IReadOnlyCollection<string> organizations, string month, CancellationToken cancellationToken)
    {
        var sums = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        foreach (var organization in organizations)
        {
            sums[organization] = new UsageTotals(0, 0);
        }

        foreach (var batch in organizations.Chunk(MaxOrganizations))
        {
            var query = new StringBuilder(MonthsPath).Append("?month=").Append(month);
            foreach (var organization in batch)
            {
                _ = query.Append("&organization=").Append(Uri.EscapeDataString(organization));
            }

            var path = query.ToString();
            var answers = await AskAsync(peer => Signed(Request(peer, path)), status: null, TerraBaseJsonContext.Default.NodeUsageMonths, cancellationToken)
                .ConfigureAwait(false);
            if (answers is null)
            {
                return null;
            }

            foreach (var answer in answers)
            {
                foreach (var item in answer!.Items)
                {
                    if (sums.TryGetValue(item.OrganizationId, out var sum))
                    {
                        sums[item.OrganizationId] = Add(sum, item);
                    }
                }
            }
        }

        return sums;
    }

    /// <summary>Every other region's own page of allowance usage after <paramref name="after"/>; null when one cannot answer.</summary>
    public async Task<List<AllowanceUsagePage>?> AllowancesAsync(string? after, string? month, CancellationToken cancellationToken)
    {
        var query = new StringBuilder("/v1/usage/allowances?");
        if (after is not null)
        {
            _ = query.Append("after=").Append(Uri.EscapeDataString(after)).Append('&');
        }

        if (month is not null)
        {
            _ = query.Append("month=").Append(Uri.EscapeDataString(month));
        }

        var path = query.ToString().TrimEnd('&', '?');
        var answers = await AskAsync(peer => Signed(Request(peer, path)), status: null, TerraBaseJsonContext.Default.AllowanceUsagePage, cancellationToken)
            .ConfigureAwait(false);
        return answers?.Select(answer => answer!).ToList();
    }

    /// <summary>
    /// Merges every node's own allowance page into one page of at most <paramref name="size"/> organizations. Every node
    /// pages its organizations in id order, so up to the smallest last id of a node with more to page, every page is
    /// complete: the merged page ends there, summing each organization's units over the nodes that hold it.
    /// </summary>
    internal static AllowanceUsagePage MergeAllowances(AllowanceUsagePage local, IReadOnlyList<AllowanceUsagePage> others, int size)
    {
        var pages = others.Prepend(local).ToList();
        var cutoff = pages.Where(page => page.Next is not null).Select(page => page.Next!).Min(StringComparer.Ordinal);
        var sums = new SortedDictionary<string, AllowanceUsage>(StringComparer.Ordinal);
        foreach (var item in pages.SelectMany(page => page.Items))
        {
            if (cutoff is not null && string.CompareOrdinal(item.OrganizationId, cutoff) > 0)
            {
                continue;
            }

            sums[item.OrganizationId] = sums.TryGetValue(item.OrganizationId, out var sum)
                ? sum with
                {
                    Operations = checked(sum.Operations + item.Operations),
                    SqlReads = checked(sum.SqlReads + item.SqlReads),
                    StorageByteHours = checked(sum.StorageByteHours + item.StorageByteHours),
                }
                : item;
        }

        var items = sums.Values.Take(size).ToList();
        var more = sums.Count > size || cutoff is not null;
        return local with { Items = items, Next = more && items.Count > 0 ? items[^1].OrganizationId : null };
    }

    /// <summary>The client's project usage: this node's share plus every other region's, with the organization's units of every region.</summary>
    internal static ProjectUsage MergeProject(ProjectUsage local, IReadOnlyList<ProjectUsage> others, UsageTotals organization)
    {
        var all = others.Prepend(local).ToList();
        // A replica region lists the database too, with the reads it served: its counts add to the home's, its copy adds no storage.
        var items = all.SelectMany(usage => usage.Items).GroupBy(item => item.DatabaseId, StringComparer.Ordinal)
            .Select(copies => copies.Aggregate((sum, item) => sum with
            {
                StorageBytes = Math.Max(sum.StorageBytes, item.StorageBytes),
                Reads = sum.Reads + item.Reads,
                Writes = sum.Writes + item.Writes,
                Period = sum.Period with { Reads = sum.Period.Reads + item.Period.Reads, Writes = sum.Period.Writes + item.Period.Writes },
            })).ToList();
        var series = local.Series.Select((point, index) => point with
        {
            Reads = all.Sum(usage => usage.Series.Count > index ? usage.Series[index].Reads : 0),
            Writes = all.Sum(usage => usage.Series.Count > index ? usage.Series[index].Writes : 0),
        }).ToArray();
        var fileBytes = all.Sum(usage => usage.FileBytes);
        var month = local.Month with
        {
            ReadUnits = all.Sum(usage => usage.Month.ReadUnits),
            WriteUnits = all.Sum(usage => usage.Month.WriteUnits),
            Units = all.Sum(usage => usage.Month.Units),
            OrganizationUnits = local.Month.OrganizationUnits + organization.Writes,
            OrganizationReadUnits = local.Month.OrganizationReadUnits + organization.Reads,
        };
        return local with
        {
            Databases = items.Count,
            StorageBytes = checked(items.Sum(item => item.StorageBytes) + fileBytes),
            Reads = items.Sum(item => item.Reads),
            Writes = items.Sum(item => item.Writes),
            Period = local.Period with { Reads = items.Sum(item => item.Period.Reads), Writes = items.Sum(item => item.Period.Writes) },
            Series = series,
            Items = items,
            Month = month,
            FileBytes = fileBytes,
        };
    }

    private static UsageTotals Add(UsageTotals sum, NodeUsageMonth item) =>
        new(checked(sum.Reads + item.Reads), checked(sum.Writes + item.Writes)) { StorageByteHours = checked(sum.StorageByteHours + item.StorageByteHours) };

    private static HttpRequestMessage Request(RegionPeer peer, string path) => new(HttpMethod.Get, new Uri(peer.Address, path))
    {
        Version = HttpVersion.Version20,
        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
        Headers = { { ScopeHeader, NodeScope } },
    };

    private HttpRequestMessage Signed(HttpRequestMessage request)
    {
        credential.Sign(request.Headers);
        return request;
    }

    // Asks every other region's node at once. An answer of `status` counts as nothing; any other failure fails the whole.
    private async Task<T?[]?> AskAsync<T>(
        Func<RegionPeer, HttpRequestMessage> build, HttpStatusCode? status, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
        where T : class
    {
        var others = await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        if (others.Count == 0)
        {
            return [];
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PeerTimeout);
        var answers = await Task.WhenAll(others.Select(peer => AskOneAsync(peer, build, status, type, timeout.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return answers.Any(answer => !answer.Answered) ? null : [.. answers.Select(answer => answer.Value)];
    }

    private async Task<(bool Answered, T? Value)> AskOneAsync<T>(
        RegionPeer peer, Func<RegionPeer, HttpRequestMessage> build, HttpStatusCode? status, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type,
        CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var request = build(peer);
            using var client = http.CreateClient(HomeLocator.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == status)
            {
                return (true, null);
            }

            return response.StatusCode == HttpStatusCode.OK
                && await response.Content.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false) is { } value
                ? (true, value)
                : (false, null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
        {
            return (false, null);
        }
    }

    internal static string MonthOf(DateTimeOffset start) => start.ToString("yyyy-MM", CultureInfo.InvariantCulture);
}

using System.Net;
using TerraBase.Errors;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

/// <summary>
/// Every region admits an organization's requests against its own catalog, which no other region holds, so a plan published
/// in one region is published in every other one too (<c>PUT /admin/organizations/{organization}/plan</c> over the internal
/// listener with the peer credential). A region that cannot take it fails the publication with 503, so its publisher
/// retries until every region enforces the plan; nothing falls back to Hobby unseen.
/// </summary>
internal sealed class PlanFanout(IRegionPeers peers, IHttpClientFactory http, InternalCredential credential)
{
    /// <summary>Marks a peer's publication for its receiving region only, so the fan-out never recurses.</summary>
    internal const string ScopeHeader = "X-TerraBase-Plan-Scope";

    private const string RegionScope = "region";

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    private static readonly Unavailable Unpublished = new("the plan is not published in every region yet; retry", "plan_unpublished");

    /// <summary>Publishes <paramref name="plan"/> for <paramref name="organizationId"/> in every other region; null once each took it.</summary>
    public async Task<Unavailable?> PublishAsync(HttpContext context, string organizationId, PlanTier plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if ((Listeners.IsForwarded(context) || Listeners.ArrivedOn(context) == TerraBaseListener.Internal) && context.Request.Headers[ScopeHeader] is [RegionScope])
        {
            return null;
        }

        var others = await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PeerTimeout);
        var path = "/admin/organizations/" + Uri.EscapeDataString(organizationId) + "/plan";
        var name = plan.ToString().ToLowerInvariant();
        var taken = await Task.WhenAll(others.Select(peer => PublishOneAsync(peer, path, name, timeout.Token))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return taken.All(ok => ok) ? null : Unpublished;
    }

    // The route's 404 leaves nothing to set there (Hobby on no record, or a deleted organization); a bare 404 is a misrouted request.
    private async Task<bool> PublishOneAsync(RegionPeer peer, string path, string plan, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(peer.Address, path))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Headers = { { ScopeHeader, RegionScope } },
                Content = JsonContent.Create(new PlanRequest(plan), TerraBaseJsonContext.Default.PlanRequest),
            };
            credential.Sign(request.Headers);
            using var client = http.CreateClient(HomeLocator.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                || response.StatusCode == HttpStatusCode.NotFound && response.Content.Headers.ContentType?.MediaType == "application/json";
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

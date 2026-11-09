using System.Net;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed class PlanFanout(IRegionPeers peers, IHttpClientFactory http, InternalCredential credential)
{
    internal const string ScopeHeader = "X-TerraBase-Plan-Scope";

    private const string RegionScope = "region";

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    private static readonly Unavailable Unpublished = new("the plan is not published in every region yet; retry", "plan_unpublished");

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

    private async Task<bool> PublishOneAsync(RegionPeer peer, string path, string plan, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(peer.Address, path))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Headers = { { ScopeHeader, RegionScope } },
                Content = JsonContent.Create(new PlanRequest(plan), ManagementJsonContext.Default.PlanRequest),
            };
            credential.Sign(request.Headers);
            using var client = http.CreateClient(HomeLocator.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException)
        {
            return false;
        }
    }
}

using System.Net;
using System.Text.Json;
using TerraBase.Features.Regions;
using TerraBase.Hosting;
using ZLogger;

namespace TerraBase.Features.Management;

internal sealed class EnrollmentFanout(IRegionPeers peers, IHttpClientFactory http, InternalCredential credential, ILogger<EnrollmentFanout> logger)
{
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    public async Task EnrollAsync(string project, string organization, CancellationToken cancellationToken)
    {
        try
        {
            var others = await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false);
            if (others.Count == 0)
            {
                return;
            }

            var path = "/admin/projects/" + Uri.EscapeDataString(project) + "/enrollment";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            var sent = await Task.WhenAll(others.Select(peer => SendAsync(peer, path, organization, timeout.Token))).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (sent.Any(accepted => !accepted))
            {
                logger.ZLogWarning($"A region did not accept the enrollment of a new project; it resolves through home forwarding until it does");
            }
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.ZLogWarning($"A new project's enrollment could not be sent to every region; they resolve through home forwarding until a retry: {error.GetType().Name:@Error}");
        }
    }

    private async Task<bool> SendAsync(RegionPeer peer, string path, string organization, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(peer.Address, path))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = JsonContent.Create(new EnrollmentRequest(organization), TerraBaseJsonContext.Default.EnrollmentRequest),
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

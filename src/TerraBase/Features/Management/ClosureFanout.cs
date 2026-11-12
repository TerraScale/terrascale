using System.Net;
using TerraBase.Configuration;
using TerraBase.Features.FileStorage;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal enum RegionClosure
{
    Acknowledged,
    Pending,
    Unanswered,
}

internal sealed class ClosureFanout(
    IRegionPeers peers, IRegionCatalog regions, TerraBaseOptions options, IHttpClientFactory http, InternalCredential credential, FileCleanupFaults faults)
{
    internal const string ScopeHeader = "X-TerraBase-Closure-Scope";

    internal const string RegionScope = "region";

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) =>
        [.. (await regions.ServingAsync(cancellationToken).ConfigureAwait(false)).Where(region => region != options.Region)];

    public void Failed(string? project, Exception error)
    {
        if (project is null)
        {
            faults.Failed(FileCleanupFaults.ClosureRegions, null, null, error);
        }
        else
        {
            faults.Unacknowledged(project, null, error);
        }
    }

    public async Task<RegionClosure> SendAsync(string region, ProjectClosureRecord closure, CancellationToken cancellationToken)
    {
        try
        {
            if ((await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(peer => peer.Region == region) is not { } peer)
            {
                faults.Unacknowledged(closure.ProjectId, region, new InvalidOperationException("a project closure waits for a region that the peers file does not list"));
                return RegionClosure.Unanswered;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(peer.Address, "/admin/projects/" + Uri.EscapeDataString(closure.ProjectId) + "/closure"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Headers = { { ScopeHeader, RegionScope } },
                Content = JsonContent.Create(new ProjectClosureRequest(closure.FormatVersion, closure.ClosureId, closure.AuthorityOrganizationId),
                    ManagementJsonContext.Default.ProjectClosureRequest),
            };
            credential.Sign(request.Headers);
            using var client = http.CreateClient(HomeLocator.HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Accepted)
            {
                return RegionClosure.Pending;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                faults.Unacknowledged(closure.ProjectId, region, new HttpRequestException("a region refused a project closure", null, response.StatusCode));
                return RegionClosure.Pending;
            }

            var answer = await response.Content.ReadFromJsonAsync(ManagementJsonContext.Default.RegionClosureStatus, timeout.Token).ConfigureAwait(false);
            if (answer is { Status: ProjectClosure.Accepted, EvidenceVersion: 2 } && answer.ClosureId == closure.ClosureId)
            {
                return RegionClosure.Acknowledged;
            }

            faults.Unacknowledged(closure.ProjectId, region, new InvalidDataException("a region accepted a project closure without version-2 evidence of this closure"));
            return RegionClosure.Pending;
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            faults.Unacknowledged(closure.ProjectId, region, error);
            return RegionClosure.Unanswered;
        }
    }
}

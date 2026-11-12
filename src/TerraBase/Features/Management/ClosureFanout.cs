using System.Net;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Features.Regions;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal enum RegionAnswer
{
    Accepted,
    Pending,
    Empty,
    NotEmpty,
    ClosureConflict,
    BindingConflict,
    Refused,
    Invalid,
    NotListed,
    TimedOut,
    Unreachable,
}

internal sealed record RegionReply(RegionAnswer Answer, Exception? Cause = null);

internal sealed class ClosureFanout(
    IRegionPeers peers, IRegionCatalog regions, TerraBaseOptions options, IHttpClientFactory http, InternalCredential credential, ClosureFaults faults)
{
    internal const string ScopeHeader = "X-TerraBase-Closure-Scope";

    internal const string RegionScope = "region";

    internal const string CheckScope = "check";

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    public async ValueTask<IReadOnlyList<string>> OtherRegionsAsync(CancellationToken cancellationToken) =>
        [.. (await regions.ServingAsync(cancellationToken).ConfigureAwait(false)).Where(region => region != options.Region)];

    public void Failed(string? project, Exception error) => faults.Failed(project, error);

    public void Refused(string project, string region) => faults.Unacknowledged(project, new(RegionAnswer.NotEmpty), region);

    public async Task<RegionAnswer> CheckAsync(string region, string project, ProjectClosureRequest closure, CancellationToken cancellationToken) =>
        (await AskAsync(region, CheckScope, project, closure, cancellationToken).ConfigureAwait(false)).Answer;

    public async Task<RegionAnswer> SendAsync(string region, ProjectClosureRecord closure, CancellationToken cancellationToken)
    {
        var reply = await AskAsync(region, RegionScope, closure.ProjectId,
            new ProjectClosureRequest(closure.FormatVersion, closure.ClosureId, closure.AuthorityOrganizationId), cancellationToken).ConfigureAwait(false);
        if (reply.Answer == RegionAnswer.Empty)
        {
            reply = new(RegionAnswer.Invalid);
        }

        if (reply.Answer is not (RegionAnswer.Accepted or RegionAnswer.Pending or RegionAnswer.NotEmpty))
        {
            faults.Unacknowledged(closure.ProjectId, reply, region);
        }

        return reply.Answer;
    }

    private async Task<RegionReply> AskAsync(string region, string scope, string project, ProjectClosureRequest closure, CancellationToken cancellationToken)
    {
        try
        {
            if ((await peers.OtherRegionsAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(peer => peer.Region == region) is not { } peer)
            {
                return new(RegionAnswer.NotListed);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(peer.Address, "/admin/projects/" + Uri.EscapeDataString(project) + "/closure"))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Headers = { { ScopeHeader, scope } },
                Content = JsonContent.Create(closure, ManagementJsonContext.Default.ProjectClosureRequest),
            };
            credential.Sign(request.Headers);
            using var client = http.CreateClient(HomeLocator.HttpClientName);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.Accepted => new(RegionAnswer.Pending),
                HttpStatusCode.OK => new(Answered(
                    await response.Content.ReadFromJsonAsync(ManagementJsonContext.Default.RegionClosureStatus, timeout.Token).ConfigureAwait(false), closure)),
                HttpStatusCode.Conflict => new(Refusal(
                    await response.Content.ReadFromJsonAsync(ManagementJsonContext.Default.RegionRefusal, timeout.Token).ConfigureAwait(false))),
                _ => new(RegionAnswer.Refused, new HttpRequestException("a region refused a project closure", null, response.StatusCode)),
            };
        }
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new(error switch
            {
                OperationCanceledException => RegionAnswer.TimedOut,
                JsonException => RegionAnswer.Invalid,
                _ => RegionAnswer.Unreachable,
            }, error);
        }
    }

    private static RegionAnswer Answered(RegionClosureStatus? answer, ProjectClosureRequest closure) => answer switch
    {
        { Status: ProjectClosure.Accepted, EvidenceVersion: 2 } when answer.ClosureId == closure.ClosureId => RegionAnswer.Accepted,
        { Status: ProjectClosure.Empty } when answer.ClosureId == closure.ClosureId => RegionAnswer.Empty,
        _ => RegionAnswer.Invalid,
    };

    private static RegionAnswer Refusal(RegionRefusal? refusal) => refusal?.Error?.Code switch
    {
        ManagementErrors.NotEmptyCode => RegionAnswer.NotEmpty,
        ProjectClosure.ClosureConflictCode => RegionAnswer.ClosureConflict,
        ProjectClosure.BindingConflictCode => RegionAnswer.BindingConflict,
        _ => RegionAnswer.Refused,
    };
}

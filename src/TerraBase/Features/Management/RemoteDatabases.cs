using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

/// <summary>
/// The local database page of one peer region, fetched from its internal listener behind the shared peer credential.
/// An unreachable, slow or unreadable peer answers null; the caller serves what the cluster still lists.
/// </summary>
internal sealed class RemoteDatabases(IHttpClientFactory http, InternalCredential credential)
{
    public const string HttpClientName = "terrabase-regions";

    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The peer's page for <paramref name="project"/>: its databases after <paramref name="after"/> in id order.</summary>
    public async Task<ManagementPage<ManagedDatabaseBody>?> PageAsync(
        Uri peer, string project, string? after, int limit, CancellationToken cancellationToken)
    {
        var secret = credential.CorrelationKey
            ?? throw new InvalidOperationException("the cluster peer credential is required to list peer regions");
        var query = new StringBuilder("admin/projects/").Append(Uri.EscapeDataString(project)).Append("/databases?limit=")
            .Append(limit.ToString(CultureInfo.InvariantCulture));
        if (after is not null)
        {
            _ = query.Append("&after=").Append(Uri.EscapeDataString(after));
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(peer, query.ToString()));
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? await response.Content.ReadFromJsonAsync(TerraBaseJsonContext.Default.ManagementPageManagedDatabaseBody, timeout.Token).ConfigureAwait(false)
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Tells <paramref name="peer"/> its record of a database this region deleted at its home. Best effort before the
    /// delete answers: a peer that cannot be reached keeps its record until its own sweep forgets it, and the merged
    /// listing still drops the database because its home no longer lists it.
    /// </summary>
    public Task ForgetAsync(Uri peer, string organization, string project, string database, CancellationToken cancellationToken)
    {
        var secret = credential.CorrelationKey
            ?? throw new InvalidOperationException("the cluster peer credential is required to reach peer regions");
        var address = new Uri(peer, $"v1/sync/organizations/{Uri.EscapeDataString(organization)}/projects/{Uri.EscapeDataString(project)}/databases/{Uri.EscapeDataString(database)}");
        return SendForgetAsync(address, secret, cancellationToken);
    }

    private async Task SendForgetAsync(Uri address, string secret, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Delete, address);
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, secret);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            _ = response.EnsureSuccessStatusCode();
        }
        catch (Exception error) when (error is HttpRequestException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
        }
    }
}

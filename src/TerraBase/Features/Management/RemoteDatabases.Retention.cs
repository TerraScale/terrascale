using System.Net;
using System.Text.Json;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;
using TerraBase.Tenancy;

namespace TerraBase.Features.Management;

internal sealed partial class RemoteDatabases
{
    public async Task<bool?> RetentionAsync(Uri peer, TenantContext database, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(peer, ReplicaRoutes.DatabasePath(database) + "/retention"));
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, credential.CorrelationKey
                ?? throw new InvalidOperationException("the cluster peer credential is required"));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK
                ? (await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RetentionStatus, timeout.Token).ConfigureAwait(false))?.Retained
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}

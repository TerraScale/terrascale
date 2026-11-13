using System.Net;
using System.Text.Json;
using TerraBase.Features.Replicas;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal sealed partial class RemoteDatabases
{
    internal async Task<bool?> ConfirmExpiryAsync(Uri peer, DateTimeOffset deletedAt, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(peer, "/v1/sync/retention/expiry"));
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, credential.CorrelationKey
                ?? throw new InvalidOperationException("the cluster peer credential is required"));
            request.Content = JsonContent.Create(new RetentionExpiryRequest(deletedAt), ReplicasJsonContext.Default.RetentionExpiryRequest);
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode
                ? (await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RetentionExpiryAnswer, timeout.Token).ConfigureAwait(false))?.Confirmed
                : null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return null;
        }
    }
}

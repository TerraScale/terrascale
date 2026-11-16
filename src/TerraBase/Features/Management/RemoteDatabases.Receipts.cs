using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal closed record class HolderReceipt;

internal sealed record class HeldReceipt(DatabasePurgeReceipt Receipt) : HolderReceipt;

internal sealed record class NoReceipt : HolderReceipt;

internal sealed record class HolderUnavailable : HolderReceipt;

internal sealed partial class RemoteDatabases
{
    public const string PurgeReceiptsPath = "v1/sync/purge-receipts";

    public async Task<HolderReceipt> FetchPurgeReceiptAsync(Uri holder, string databaseId, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(holder, $"{PurgeReceiptsPath}/{databaseId}/receipt"));
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, credential.CorrelationKey
                ?? throw new InvalidOperationException("the cluster peer credential is required"));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => await response.Content.ReadFromJsonAsync(ManagementJsonContext.Default.DatabasePurgeReceipt, timeout.Token).ConfigureAwait(false) is { } receipt
                    ? new HeldReceipt(receipt)
                    : new HolderUnavailable(),
                HttpStatusCode.NotFound => new NoReceipt(),
                _ => new HolderUnavailable(),
            };
        }
        catch (Exception error) when (error is HttpRequestException or JsonException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new HolderUnavailable();
        }
    }
}

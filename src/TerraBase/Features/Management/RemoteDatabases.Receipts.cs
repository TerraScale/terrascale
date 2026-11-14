using System.Net;
using System.Net.Http.Json;
using TerraBase.Hosting;

namespace TerraBase.Features.Management;

internal enum ReceiptDelivery
{
    Recorded,
    Refused,
    Unavailable,
}

internal sealed partial class RemoteDatabases
{
    public const string PurgeReceiptsPath = "v1/sync/purge-receipts";

    public async Task<ReceiptDelivery> DeliverPurgeReceiptAsync(Uri writer, DatabasePurgeReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PeerTimeout);
            using var client = http.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(writer, PurgeReceiptsPath))
            {
                Content = JsonContent.Create(receipt, ManagementJsonContext.Default.DatabasePurgeReceipt),
            };
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            request.Headers.Add(InternalCredential.HeaderName, credential.CorrelationKey
                ?? throw new InvalidOperationException("the cluster peer credential is required"));
            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.NoContent => ReceiptDelivery.Recorded,
                HttpStatusCode.Conflict => ReceiptDelivery.Refused,
                _ => ReceiptDelivery.Unavailable,
            };
        }
        catch (Exception error) when (error is HttpRequestException
            || error is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ReceiptDelivery.Unavailable;
        }
    }
}

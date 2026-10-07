namespace TerraBase.Features.Backup;

// AWS materializes S3 listing/error XML before returning to the object-store adapter. Bound those bodies before
// the SDK parses them, including responses without Content-Length. Backup objects themselves remain streamed.
internal sealed class CustomerBackupResponseLimits : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode || request.RequestUri!.Query.Contains("list-type=2", StringComparison.Ordinal))
            {
                await response.Content.LoadIntoBufferAsync(response.IsSuccessStatusCode ? 2 * 1024 * 1024 : 64 * 1024, cancellationToken).ConfigureAwait(false);
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}

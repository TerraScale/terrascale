using System.Globalization;
using System.Net;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

/// <summary>Counts request and response body bytes of one transport.</summary>
internal sealed class CountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private long bytesOut;
    private long bytesIn;

    internal long BytesOut => Interlocked.Read(ref bytesOut);

    internal long BytesIn => Interlocked.Read(ref bytesIn);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Add(ref bytesOut, request.Content?.Headers.ContentLength ?? 0);
        var response = await base.SendAsync(request, cancellationToken);
        var counted = new CountedContent(response.Content, this);
        foreach (var header in response.Content.Headers) counted.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = counted;
        return response;
    }

    // Copies the body in the same chunks the SDK reads it, so the counter adds no buffering of its own.
    private sealed class CountedContent(HttpContent inner, CountingHandler owner) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => CopyAsync(target, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken cancellationToken) => CopyAsync(target, cancellationToken);

        private async Task CopyAsync(Stream target, CancellationToken cancellationToken)
        {
            using var source = await inner.ReadAsStreamAsync(cancellationToken);
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    Interlocked.Add(ref owner.bytesIn, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            if (inner.Headers.ContentLength is { } known)
            {
                length = known;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

internal static class Http
{
    /// <summary>Maps a failed operation to its status key. Only the exception type and status are read, never a message.</summary>
    internal static string StatusOf(Exception error) => error switch
    {
        HttpRequestException { StatusCode: { } code } => ((int)code).ToString(CultureInfo.InvariantCulture),
        TimeoutException or OperationCanceledException { InnerException: TimeoutException } => "timeout",
        OperationCanceledException => "canceled",
        _ => "transport",
    };

    /// <summary>A missing document is a 404 result, not a success.</summary>
    internal static string ReadStatus(TerraBaseDocument? document) => document is null ? "404" : "ok";

    internal static string CodeOf(HttpResponseMessage response) =>
        response.IsSuccessStatusCode ? "ok" : ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);

    internal static Uri DocumentsUri(Uri endpoint, string project, string database, string collection) =>
        new(endpoint, $"{Database(project, database)}/collections/{Uri.EscapeDataString(collection)}/documents");

    /// <summary>Point read by key, the same <c>/documents/by-key</c> route the SDK's <c>GetDocumentAsync</c> uses.</summary>
    internal static Uri ReadUri(Uri endpoint, string project, string database, string collection, string id) =>
        new(endpoint, $"{Database(project, database)}/collections/{Uri.EscapeDataString(collection)}/documents/by-key?id={Uri.EscapeDataString(id)}");

    private static string Database(string project, string database) =>
        $"/v1/projects/{Uri.EscapeDataString(project)}/databases/{Uri.EscapeDataString(database)}";
}

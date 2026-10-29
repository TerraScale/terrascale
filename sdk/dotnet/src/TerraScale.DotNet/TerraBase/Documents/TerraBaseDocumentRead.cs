using System.Net;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A document read, including conditional and deleted-document responses.</summary>
/// <param name="Status">200, 304, or 404 for an absent document.</param>
/// <param name="Document">Document on 200; otherwise null.</param>
/// <param name="ETag">Quoted HTTP validator, including a deleted document's tombstone validator when supplied.</param>
public sealed record TerraBaseDocumentRead(HttpStatusCode Status, TerraBaseDocument? Document, string? ETag);

public sealed partial class TerraBaseDatabase
{
    /// <summary>
    /// Reads a document with optional RFC 9110 entity-tag lists or wildcard conditions. Copy quoted ETags verbatim;
    /// If-Match compares strongly, If-None-Match weakly. Returns 304 without a body, or 404 for an absent document.
    /// Other failures, including an unknown database and failed If-Match (412), throw the normal SDK exceptions.
    /// </summary>
    public Task<TerraBaseDocumentRead> ReadDocumentAsync(string collection, string id, string? ifMatch = null,
        string? ifNoneMatch = null, CancellationToken cancellationToken = default) =>
        connection.ExchangeAsync(DocumentPath(collection, id), null, headers =>
        {
            if (ifMatch is not null) headers.IfMatch.ParseAdd(ifMatch);
            if (ifNoneMatch is not null) headers.IfNoneMatch.ParseAdd(ifNoneMatch);
        }, async opened =>
        {
            var response = opened.Response;
            var status = response.StatusCode;
            var etag = response.Headers.ETag?.ToString();
            if (status == HttpStatusCode.NotModified) return new TerraBaseDocumentRead(status, null, etag);
            if (status == HttpStatusCode.OK)
            {
                var document = TerraBaseConnection.ReadResponse(await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false),
                    Json.TerraBaseDocument, status);
                return new TerraBaseDocumentRead(status, document, etag);
            }
            var error = await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false);
            if (error is TerraBaseNotFoundException { ErrorCode: "not_found" }) return new TerraBaseDocumentRead(status, null, etag);
            throw error;
        }, cancellationToken);
}

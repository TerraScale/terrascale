using System.Text.Json;
using Microsoft.Net.Http.Headers;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>PATCH …/documents/by-key?id=</c>: patches an existing JSON document.</summary>
public sealed class PatchDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(DocumentRoute.Document);
        Name("patchDocument");
        Summary("Patches a JSON document");
        Description("application/merge-patch+json applies an RFC 7396 merge patch (null removes members). application/vnd.terrabase.ops+json applies {\"ops\":[…]} in order: set (creates missing parents), unset, inc (missing is 0, exact decimal arithmetic) and append (creates a missing array); paths are JSON Pointers to object members, at most 256 ops. A target of the wrong type fails with 409 patch_conflict. Optional If-Match. The document must exist (404 not_found).");
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentPatch.MergeMediaType));
        Metadata(new RequestBodyMetadata(typeof(PatchOpsRequest), DocumentPatch.OpsMediaType));
        Produces<WriteBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckKeyAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            DocumentKey key => (DocumentRoute.CheckParameters(HttpContext, "id"), DocumentRoute.CheckIdempotencyKey(HttpContext), ETags.CheckPrecondition(HttpContext)) switch
            {
                (Validation error, _, _) => ErrorResponse.From(error),
                (_, Validation error, _) => ErrorResponse.From(error),
                (_, _, Validation error) => ErrorResponse.From(error),
                (_, _, Precondition precondition) => await DocumentRoute.CheckBodyAsync(
                    HttpContext, DocumentRoute.MaxRequestBytes, "invalid_patch", cancellationToken, DocumentPatch.MergeMediaType, DocumentPatch.OpsMediaType) switch
                {
                    Validation error => ErrorResponse.From(error),
                    PayloadTooLarge error => ErrorResponse.From(error),
                    UnsupportedMediaType error => ErrorResponse.From(error),
                    byte[] body => Patch(body) switch
                    {
                        Validation error => ErrorResponse.From(error),
                        DocumentPatch patch => await DocumentRoute.WriteAsync(
                            HttpContext, documents, key, new PatchOperation(key.Collection, key.Id, patch, precondition), (written, _) => TypedResults.Ok(written), cancellationToken),
                    },
                },
            },
        };

    private Valid<DocumentPatch> Patch(byte[] body)
    {
        // CheckBodyAsync accepted one of the two media types; compare the parsed type, not a prefix of the header.
        var mediaType = MediaTypeHeaderValue.Parse(HttpContext.Request.ContentType!).MediaType.Value;
        return string.Equals(mediaType, DocumentPatch.OpsMediaType, StringComparison.OrdinalIgnoreCase)
            ? RequestJson.Read<PatchOpsRequest>(body, "invalid_patch") switch
            {
                PatchOpsRequest request => DocumentPatch.CheckOps(request.Ops),
                Validation error => error,
            }
            : RequestJson.Read<JsonElement>(body, "invalid_patch") switch
            {
                JsonElement merge => DocumentPatch.CheckMerge(merge),
                Validation error => error,
            };
    }
}

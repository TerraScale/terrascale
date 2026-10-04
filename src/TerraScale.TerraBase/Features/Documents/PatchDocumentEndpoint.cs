using System.Text.Json;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary><c>PATCH …/documents/{id}</c>: patches an existing JSON document.</summary>
public sealed class PatchDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(DocumentRoute.Collection + "/{id}");
        Name("patchDocument");
        Summary("Patches a JSON document");
        Description("application/merge-patch+json applies an RFC 7396 merge patch (null removes members). application/vnd.terrabase.ops+json applies {\"ops\":[…]} in order: set (creates missing parents), unset, inc (missing is 0, exact decimal arithmetic) and append (creates a missing array); paths are JSON Pointers to object members, at most 256 ops. A target of the wrong type fails with 409 patch_conflict. Optional If-Match. The document must exist (404 not_found).");
        Tags("Documents");
        Metadata(new RequestBodyMetadata(typeof(JsonElement), DocumentPatch.MergeMediaType));
        Metadata(new RequestBodyMetadata(typeof(PatchOpsRequest), DocumentPatch.OpsMediaType));
        Produces<WriteBody>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var key = await DocumentRoute.KeyAsync(HttpContext, tenants, cancellationToken);
        DocumentRoute.Parameters(HttpContext);
        DocumentRoute.RejectIdempotencyKey(HttpContext);
        var precondition = ETags.Precondition(HttpContext);
        var body = await DocumentRoute.ReadBodyAsync(
            HttpContext, DocumentRoute.MaxRequestBytes, "invalid_patch", cancellationToken, DocumentPatch.MergeMediaType, DocumentPatch.OpsMediaType);
        var patch = HttpContext.Request.ContentType!.StartsWith(DocumentPatch.OpsMediaType, StringComparison.OrdinalIgnoreCase)
            ? DocumentPatch.Ops(RequestJson.Deserialize<PatchOpsRequest>(body, "invalid_patch").Ops)
            : DocumentPatch.Merge(RequestJson.Deserialize<JsonElement>(body, "invalid_patch"));

        var (written, _) = await DocumentRoute.WriteAsync(
            HttpContext, documents, key, new PatchOperation(key.Collection, key.Id, patch, precondition), cancellationToken);
        return TypedResults.Ok(written);
    }
}

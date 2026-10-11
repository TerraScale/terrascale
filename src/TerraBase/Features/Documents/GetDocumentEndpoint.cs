using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary><c>GET …/documents/by-key?id=</c>: reads one JSON document.</summary>
public sealed class GetDocumentEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(DocumentRoute.Document);
        Name("getDocument");
        Summary("Reads a JSON document");
        Description("Sends the document state as a strong ETag, including the database identity (the same on every member), merge version, JSON and deletion state. Equal-version replicated body changes get distinct validators. If-None-Match with the current version returns 304. A deleted document returns 404 not_found with its tombstone's ETag, so it can be recreated with If-Match. A retained loser of a replicated uniqueness conflict returns 409 unique_violation with its source state's ETag.");
        Tags("Documents");
        Produces<DocumentBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await DocumentRoute.CheckKeyAsync(HttpContext, tenants, cancellationToken) switch
        {
            Validation error => ErrorResponse.From(error),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            DocumentKey key => DocumentRoute.CheckParameters(HttpContext, "id") switch
            {
                Validation error => ErrorResponse.From(error),
                IQueryCollection => await ReadAsync(key, cancellationToken),
            },
        };

    private async ValueTask<IResult> ReadAsync(DocumentKey key, CancellationToken cancellationToken) =>
        await documents.GetAsync(key, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            StoredDocument document => Conditional(key, document, cancellationToken),
            NeverWritten => Conditional(key, null, cancellationToken),
        };

    private IResult Conditional(DocumentKey key, StoredDocument? document, CancellationToken cancellationToken)
    {
        var version = document is null ? null : ETags.Version(document.Scope, key.Collection, document.Id, document.Version, document.Json, document.Deleted);
        var condition = ETags.CheckNotModified(HttpContext, document is { Deleted: false }, version);
        if (document is { UniqueConflict: true } && condition is not Validation)
        {
            ETags.Set(HttpContext, version!);
            return ErrorResponse.From(new Conflict("the document lost a replicated uniqueness conflict", "unique_violation"));
        }

        return condition switch
        {
            Validation error => ErrorResponse.From(error),
            PreconditionFailed error => ErrorResponse.From(error),
            bool notModified => Answer(key, document, version, notModified, cancellationToken),
        };
    }

    private IResult Answer(DocumentKey key, StoredDocument? document, string? version, bool notModified, CancellationToken cancellationToken)
    {
        if (version is not null)
        {
            // A deleted document's 404 carries its tombstone's version too.
            ETags.Set(HttpContext, version);
        }

        return notModified ? TypedResults.StatusCode(StatusCodes.Status304NotModified)
            : document is { Deleted: false } ? TypedResults.Ok(Body(key.Collection, document, cancellationToken: cancellationToken))
            : ErrorResponse.From(Missing);
    }

    private static readonly NotFound Missing = new("the document does not exist", "not_found");

    internal static DocumentBody Body(
        string collection, StoredDocument document, IReadOnlyList<DocumentPath>? project = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(document.Json, RequestJson.StoredDocumentOptions);
        var body = project is null ? json.RootElement.Clone() : Projection.Apply(json.RootElement, project, cancellationToken);
        return new DocumentBody(document.Id, ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json), "value", body);
    }
}

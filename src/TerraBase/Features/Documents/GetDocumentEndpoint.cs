using System.Text.Json;
using Microsoft.OpenApi;
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
        Description("Sends the document state as a strong ETag, including the database identity (the same on every member), merge version, JSON and deletion state. Equal-version replicated body changes get distinct validators. Optional If-Match is checked first using strong comparison; a failed condition returns 412 precondition_failed, including If-Match: * on an absent or deleted document. If-None-Match uses weak comparison against a live document; a match returns 304 with ETag and no response body. Malformed conditional headers return 400. A deleted document returns 404 not_found with its tombstone's ETag, so it can be recreated with If-Match. A retained loser of a replicated uniqueness conflict returns 409 unique_violation with its source state's ETag.");
        Tags("Documents");
        Metadata(new OpenApiParameter
        {
            Name = "If-Match",
            In = ParameterLocation.Header,
            Description = "Optional * or list of entity tags, compared strongly before If-None-Match; a failed condition returns 412 precondition_failed.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        Metadata(new OpenApiParameter
        {
            Name = "If-None-Match",
            In = ParameterLocation.Header,
            Description = "Optional * or list of entity tags, compared weakly against a live document; a match returns 304 with ETag and no body.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        Produces<DocumentBody>();
        Produces(StatusCodes.Status304NotModified);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Produces<ErrorBody>(StatusCodes.Status412PreconditionFailed);
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
            Forbidden error => ErrorResponse.From(error),
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

        // A 304 returns no logical object; errors and tombstones likewise accept no read charge.
        if (notModified)
        {
            return TypedResults.StatusCode(StatusCodes.Status304NotModified);
        }

        if (document is not { Deleted: false })
        {
            return ErrorResponse.From(Missing);
        }

        var body = Body(key.Collection, document, cancellationToken: cancellationToken, version: version);
        documents.AcceptRead(key, document);
        return TypedResults.Ok(body);
    }

    private static readonly NotFound Missing = new("the document does not exist", "not_found");

    internal static DocumentBody Body(
        string collection, StoredDocument document, IReadOnlyList<DocumentPath>? project = null, CancellationToken cancellationToken = default, string? version = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var json = JsonDocument.Parse(document.Json, RequestJson.StoredDocumentOptions);
        var body = project is null ? json.RootElement.Clone() : Projection.Apply(json.RootElement, project, cancellationToken);
        return new DocumentBody(document.Id, version ?? ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json), "value", body);
    }
}

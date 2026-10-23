using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Declares a collection's expiry member: documents past its time are deleted by the home region's sweeper.</summary>
public sealed class PutCollectionTtlEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(CollectionTtlRoute.Route);
        Name("putCollectionTtl");
        Summary("Declares a collection's TTL");
        Description("Body {\"path\":\"/expiresAt\"}, a JSON Pointer of 1 to 16 object members. A document whose member holds an RFC 3339 time in UTC (for example \"2026-10-22T12:00:00Z\") is deleted within about an hour after that time, by its database's home region, as an ordinary replicated delete that appears in the changes feed. Documents without the member, or with another value, never expire. Until the sweep, expired documents stay readable. Index the member for large collections.");
        Tags("Collections");
        Metadata(new RequestBodyMetadata(typeof(CollectionTtlBody), DocumentRoute.JsonMediaType));
        Produces<CollectionTtlBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants, (database, name) => PutAsync(database, name, cancellationToken), cancellationToken);

    private async ValueTask<IResult> PutAsync(TenantContext database, string name, CancellationToken cancellationToken) =>
        DocumentRoute.CheckIdempotencyKey(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            Done => await DocumentRoute.CheckBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_ttl", cancellationToken, DocumentRoute.JsonMediaType) switch
            {
                Validation error => ErrorResponse.From(error),
                PayloadTooLarge error => ErrorResponse.From(error),
                UnsupportedMediaType error => ErrorResponse.From(error),
                byte[] body => RequestJson.Read<CollectionTtlBody>(body, "invalid_ttl") switch
                {
                    Validation error => ErrorResponse.From(error),
                    CollectionTtlBody request => QueryParser.CheckPointer(request.Path, "invalid_ttl", "path is a JSON Pointer of 1 to 16 object members") switch
                    {
                        Validation error => ErrorResponse.From(error),
                        IReadOnlyList<string> => CollectionTtlRoute.Answer(await documents.SetCollectionTtlAsync(database, name, request.Path, cancellationToken)),
                    },
                },
            },
        };
}

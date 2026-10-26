using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Validates and persists a collection policy as replicated collection metadata.</summary>
public sealed class PutCollectionRulesEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(CollectionRulesRoute.Route);
        Name("putCollectionRules");
        Summary("Replaces collection access rules");
        Description("Project credentials only. Body {owner:\"/userId\",read:{owner:true,public:false,roles:[]},write:{owner:true,public:false,roles:[]}}. An owner is an immutable signed subject at a JSON Pointer; public and trusted case-sensitive roles grant collection-wide access. Omit owner or set it to null for policies without ownership. Both read and write policies and their roles arrays are required. Use all-false grants to deny access. End-user writes cannot transfer ownership or recreate another user's deleted id.");
        Tags("Collections");
        Metadata(new RequestBodyMetadata(typeof(CollectionAccessRules), DocumentRoute.JsonMediaType));
        Produces<CollectionRulesBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        RequireAuthorization(ApiKeyScopes.DocumentsWrite);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        HttpContext.Features.Get<ProjectAccess>() is { IsEndUser: true }
            ? ValueTask.FromResult(ErrorResponse.From(new Forbidden("project credentials are required")))
            : CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants, (database, name) => PutAsync(database, name, cancellationToken), cancellationToken);

    private async ValueTask<IResult> PutAsync(TenantContext database, string name, CancellationToken cancellationToken)
    {
        if (DocumentRoute.CheckIdempotencyKey(HttpContext) is Validation invalid)
        {
            return ErrorResponse.From(invalid);
        }

        var body = await DocumentRoute.CheckBodyAsync(HttpContext, 16 * 1024, "invalid_rules", cancellationToken, DocumentRoute.JsonMediaType);
        if (body is not byte[] json)
        {
            return body switch
            {
                Validation error => ErrorResponse.From(error),
                PayloadTooLarge error => ErrorResponse.From(error),
                UnsupportedMediaType error => ErrorResponse.From(error),
                _ => throw new InvalidOperationException("unknown body result"),
            };
        }

        try
        {
            var rules = JsonSerializer.Deserialize(json, CollectionRulesJsonContext.Default.CollectionAccessRules);
            return rules is null ? ErrorResponse.From(new Validation("rules must be an object", "invalid_rules"))
                : CollectionRulesRoute.Answer(await documents.SetCollectionRulesAsync(database, name, rules, cancellationToken));
        }
        catch (JsonException)
        {
            return ErrorResponse.From(new Validation("rules must be a valid declarative policy", "invalid_rules"));
        }
    }
}

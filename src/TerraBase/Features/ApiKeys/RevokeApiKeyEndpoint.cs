using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.ApiKeys;
public sealed class RevokeApiKeyEndpoint(IApiKeyStore keys) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Delete("/admin/api-keys/{key}");
        Metadata(AnchoredRevocation.Instance);
        Name("revokeApiKey");
        Summary("Revokes an operator-issued API key");
        Description("The key stops authenticating immediately. Served only on the internal administrative listener.");
        Tags("Administration");
        Produces(StatusCodes.Status204NoContent);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        AllowAnonymous();
    }
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var id = HttpContext.Request.RouteValues["key"] as string;
        if (!EntityIds.IsValid(id))
        {
            return ErrorResponse.From(new Validation("key must be a canonical uppercase ULID"));
        }

        return await keys.RevokeAsync(id!, cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

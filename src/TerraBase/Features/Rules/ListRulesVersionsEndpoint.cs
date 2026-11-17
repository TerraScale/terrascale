using Microsoft.OpenApi;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class ListRulesVersionsEndpoint(IManagementService management, IRules rules) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get(RulesRoute.Versions);
        Name("listDatabaseRulesVersions");
        Summary("Lists a database's rules versions");
        Description("Newest first, up to limit (1 to 100, default 20) versions older than after; next names the after of the following page, null on the last. "
            + "The last 100 versions are kept. " + RulesRoute.Description);
        Tags("Rules");
        Produces<RulesVersionsBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new OpenApiParameter { Name = "after", In = ParameterLocation.Query, Description = "Return versions older than this version.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } });
        Metadata(new OpenApiParameter { Name = "limit", In = ParameterLocation.Query, Description = "1 to 100; 20 when omitted.", Schema = new OpenApiSchema { Type = JsonSchemaType.Integer } });
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (RulesRoute.Page(HttpContext.Request) is not { } page)
        {
            return ErrorResponse.From(RulesRoute.BadPage);
        }

        var resolved = await RulesRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        return await rules.VersionsAsync(database, page.After, page.Limit, cancellationToken) switch
        {
            RulesVersionPage versions => TypedResults.Json(new RulesVersionsBody(versions.Items, versions.Next), RulesJsonContext.Default.RulesVersionsBody),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}

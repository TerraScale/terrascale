using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.HomeApply;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.HomeApply;

internal sealed class ApplyStateEndpoint(IHomeApply apply, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/apply/{transaction}");
        Name("getRegionTransactionState");
        Summary("Reads the state of a region transaction at the database's home");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. confirmed or reverted with what the home recorded, "
            + "or provisional when the home has not applied it.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HomeApplyResult>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var id = HttpContext.RouteValue("transaction");
        if (!EntityIds.IsValid(id))
        {
            return ErrorResponse.From(new NotFound("the transaction does not exist"));
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            return await tenants.FindDatabaseAsync(HttpContext.RouteValue("organization"), HttpContext.RouteValue("project"), HttpContext.RouteValue("database"), cancellationToken).ConfigureAwait(false) switch
            {
                TenantContext database when await apply.StateAsync(database, id, cancellationToken).ConfigureAwait(false) is { } state =>
                    TypedResults.Json(state, HomeApplyJsonContext.Default.HomeApplyResult),
                Unavailable error => ErrorResponse.From(error),
                _ => ErrorResponse.From(new NotFound("the database does not exist")),
            };
        }
        catch (Exception error) when (error is StorageBusyException or SqlStatementException)
        {
            return ErrorResponse.From(new Unavailable("the home cannot read the transaction now; retry"));
        }
    }

}

using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Replicas;

internal sealed class HeldRevisionEndpoint(TerraBaseOptions options, IDatabaseReplicas replicas) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/held");
        Name("getReplicaHeldRevision");
        Summary("Reads the revision this node's replica of a database holds");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. The replicas of a database whose home lease "
            + "expired compare these revisions, and the most caught-up one takes the lease. 404 when this node holds no pulled replica.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
        Produces<HeldRevisionAnswer>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
    }

    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (organization, project, id) = (Value("organization"), Value("project"), Value("database"));
        HttpContext.Response.Headers.CacheControl = "no-store";
        return ValueTask.FromResult(EntityIds.IsValid(organization) && EntityIds.IsValid(project) && EntityIds.IsValid(id)
            && replicas.HeldRevision(new TenantContext(organization, PlanTier.Hobby, project, id)) is { } held
            ? TypedResults.Json(new HeldRevisionAnswer(options.Region, held.ToString()), TerraBaseJsonContext.Default.HeldRevisionAnswer)
            : ErrorResponse.From(new NotFound("this node holds no replica of the database")));
    }

    private string Value(string name) => HttpContext.Request.RouteValues[name] as string ?? string.Empty;
}

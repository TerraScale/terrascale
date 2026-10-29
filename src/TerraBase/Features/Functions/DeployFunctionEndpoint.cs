using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Functions;

/// <summary>Deploys or replaces a function.</summary>
internal sealed class DeployFunctionEndpoint(IManagementService management, IFunctions functions) : Endpoint<DeployFunctionRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(FunctionRoute.Item);
        Name("deployFunction");
        Summary("Deploys or replaces a function");
        Description(FunctionRoute.Description + " Body {source, trigger?: {collection, events?: [upsert|delete]}, cron?}: at most 256 KiB of JavaScript that must parse; a trigger runs the function on the collection's changes (at least once, with retries); cron is five-field UTC. At most 32 functions per database.");
        Tags("Functions");
        Metadata(new RequestBodyMetadata(typeof(DeployFunctionRequest), "application/json"));
        Produces<FunctionBody>();
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(DeployFunctionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return FunctionRoute.RunAsync(HttpContext, management, (tenant, name) => functions.DeployAsync(tenant, HttpContext.Request.RouteValues["database"]!.ToString()!, name, request, cancellationToken), cancellationToken);
    }
}

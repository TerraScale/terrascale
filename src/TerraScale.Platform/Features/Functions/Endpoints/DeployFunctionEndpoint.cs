namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class DeployFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<DeployFunctionRequest, Result<FunctionBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
{
    public override void Configure()
    {
        Put(FunctionNames.RouteItem);
        Name("deployFunction");
        Summary("Deploys or replaces a function");
        Description(FunctionNames.Description + " Body {source, cron?}: at most 256 KiB of JavaScript that must parse; cron is five-field UTC. At most 32 functions per database.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(
        DeployFunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.DeployAsync(target, request.Function, request.Source, request.Cron, cancellationToken) switch
            {
                FunctionBody body => body,
                Validation invalid => invalid,
                Conflict conflict => conflict,
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }
}

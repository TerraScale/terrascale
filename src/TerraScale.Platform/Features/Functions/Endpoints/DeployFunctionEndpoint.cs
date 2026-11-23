namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union DeployFunctionReply(
    FunctionBody,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class DeployFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<DeployFunctionRequest, DeployFunctionReply>
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

    public override async ValueTask<DeployFunctionReply> HandleAsync(
        DeployFunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken);
        return outcome switch
        {
            FunctionTarget target => await HandleAuthorizedAsync(target),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<DeployFunctionReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.DeployAsync(target, request.Function, request.Source, request.Cron, cancellationToken);
            return operation switch
            {
                FunctionBody body => body,
                PlatformInvalidRequest invalid => invalid,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

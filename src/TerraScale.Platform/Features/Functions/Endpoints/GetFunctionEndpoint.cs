namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union GetFunctionReply(
    FunctionBody,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class GetFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, GetFunctionReply>
{
    public override void Configure()
    {
        Get(FunctionNames.RouteItem);
        Name("getFunction");
        Summary("Reads a function with its source");
        Description(FunctionNames.Description);
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<GetFunctionReply> HandleAsync(
        FunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.Read, cancellationToken);
        return outcome switch
        {
            FunctionTarget target => await HandleAuthorizedAsync(target),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<GetFunctionReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.GetAsync(target, request.Function, cancellationToken);
            return operation switch
            {
                FunctionBody body => body,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

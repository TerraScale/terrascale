namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union DeleteFunctionReply(
    FunctionDeleted,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class DeleteFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, DeleteFunctionReply>
{
    public override void Configure()
    {
        Delete(FunctionNames.RouteItem);
        Name("deleteFunction");
        Summary("Deletes a function with its secrets, logs and metrics");
        Description(FunctionNames.Description);
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<DeleteFunctionReply> HandleAsync(
        FunctionRequest request,
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

        async ValueTask<DeleteFunctionReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.DeleteAsync(target, request.Function, cancellationToken);
            return operation switch
            {
                FunctionDeleted deleted => deleted,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

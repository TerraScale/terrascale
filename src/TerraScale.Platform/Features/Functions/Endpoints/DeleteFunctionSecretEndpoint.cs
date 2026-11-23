namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union DeleteFunctionSecretReply(
    SecretDeleted,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class DeleteFunctionSecretEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionSecretRequest, DeleteFunctionSecretReply>
{
    public override void Configure()
    {
        Delete(FunctionNames.RouteSecret);
        Name("deleteFunctionSecret");
        Summary("Deletes a function secret");
        Description(FunctionNames.Description);
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<DeleteFunctionSecretReply> HandleAsync(
        FunctionSecretRequest request,
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

        async ValueTask<DeleteFunctionSecretReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.DeleteSecretAsync(target, request.Function, request.Secret, cancellationToken);
            return operation switch
            {
                SecretDeleted deleted => deleted,
                PlatformInvalidRequest invalid => invalid,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

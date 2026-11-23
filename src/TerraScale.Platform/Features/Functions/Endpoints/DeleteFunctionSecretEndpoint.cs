namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class DeleteFunctionSecretEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionSecretRequest, Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionSecretRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.DeleteSecretAsync(target, request.Function, request.Secret, cancellationToken) switch
            {
                Done done => done,
                Validation invalid => invalid,
                NotFound missing => missing,
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

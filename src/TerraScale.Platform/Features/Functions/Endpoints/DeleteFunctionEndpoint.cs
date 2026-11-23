namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class DeleteFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<Done, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.DeleteAsync(target, request.Function, cancellationToken) switch
            {
                Done done => done,
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

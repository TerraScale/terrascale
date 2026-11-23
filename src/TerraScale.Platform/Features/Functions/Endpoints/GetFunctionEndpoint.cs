namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class GetFunctionEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, Result<FunctionBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<FunctionBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.Read, cancellationToken) switch
        {
            FunctionTarget target => await functions.GetAsync(target, request.Function, cancellationToken) switch
            {
                FunctionBody body => body,
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

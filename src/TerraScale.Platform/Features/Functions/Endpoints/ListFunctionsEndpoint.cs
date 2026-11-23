namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class ListFunctionsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionDatabaseRequest, Result<FunctionList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(FunctionNames.RouteList);
        Name("listFunctions");
        Summary("Lists a database's functions");
        Description(FunctionNames.Description + " Sources are omitted.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, null, Scopes.Read, cancellationToken) switch
        {
            FunctionTarget target => await functions.ListAsync(target, cancellationToken) switch
            {
                FunctionList list => list,
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

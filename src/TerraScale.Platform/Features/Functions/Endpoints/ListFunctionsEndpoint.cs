namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union ListFunctionsReply(
    FunctionList,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ListFunctionsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionDatabaseRequest, ListFunctionsReply>
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

    public override async ValueTask<ListFunctionsReply> HandleAsync(
        FunctionDatabaseRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, null, Scopes.Read, cancellationToken);
        return outcome switch
        {
            FunctionTarget target => await HandleAuthorizedAsync(target),
            PlatformInvalidRequest invalid => invalid,
            PlatformUnauthorized unauthorized => unauthorized,
            PlatformForbidden forbidden => forbidden,
            PlatformNotFound missing => missing,
            PlatformUnavailable unavailable => unavailable,
        };

        async ValueTask<ListFunctionsReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.ListAsync(target, cancellationToken);
            return operation switch
            {
                FunctionList list => list,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

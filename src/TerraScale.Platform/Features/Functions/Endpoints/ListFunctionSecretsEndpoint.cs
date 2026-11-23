namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class ListFunctionSecretsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, Result<FunctionSecretList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Get(FunctionNames.RouteSecrets);
        Name("listFunctionSecrets");
        Summary("Lists a function's secret names");
        Description(FunctionNames.Description + " Values are write-only and never returned.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<Result<FunctionSecretList, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        FunctionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.ListSecretsAsync(target, request.Function, cancellationToken) switch
            {
                FunctionSecretList secrets => secrets,
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

namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union ListFunctionSecretsReply(
    FunctionSecretList,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformUnavailable);

public sealed class ListFunctionSecretsEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<FunctionRequest, ListFunctionSecretsReply>
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

    public override async ValueTask<ListFunctionSecretsReply> HandleAsync(
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

        async ValueTask<ListFunctionSecretsReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.ListSecretsAsync(target, request.Function, cancellationToken);
            return operation switch
            {
                FunctionSecretList secrets => secrets,
                PlatformNotFound missing => missing,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

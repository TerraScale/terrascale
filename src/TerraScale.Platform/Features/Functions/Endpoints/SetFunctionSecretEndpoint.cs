namespace TerraScale.Platform.Features.Functions.Endpoints;

public readonly union SetFunctionSecretReply(
    FunctionSecretBody,
    PlatformInvalidRequest,
    PlatformUnauthorized,
    PlatformForbidden,
    PlatformNotFound,
    PlatformConflict,
    PlatformUnavailable);

public sealed class SetFunctionSecretEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<SetFunctionSecretRequest, SetFunctionSecretReply>
{
    public override void Configure()
    {
        Put(FunctionNames.RouteSecret);
        Name("setFunctionSecret");
        Summary("Sets or replaces a function secret");
        Description(FunctionNames.Description + " Body {value}: at most 4 KiB of UTF-8, sealed with AES-GCM under a key derived from the master key and readable only by "
            + "the handler as event.secrets from the next invocation. Names match ^[A-Z_][A-Z0-9_]{0,63}$; at most 50 secrets per function.");
        Tags("Functions");
        AllowAnonymous();
    }

    public override async ValueTask<SetFunctionSecretReply> HandleAsync(
        SetFunctionSecretRequest request,
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

        async ValueTask<SetFunctionSecretReply> HandleAuthorizedAsync(FunctionTarget target)
        {
            var operation = await functions.SetSecretAsync(target, request.Function, request.Secret, request.Value, cancellationToken);
            return operation switch
            {
                FunctionSecretBody secret => secret,
                PlatformInvalidRequest invalid => invalid,
                PlatformNotFound missing => missing,
                PlatformConflict conflict => conflict,
                PlatformUnavailable unavailable => unavailable,
            };
        }
    }
}

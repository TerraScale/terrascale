namespace TerraScale.Platform.Features.Functions.Endpoints;

public sealed class SetFunctionSecretEndpoint(PlatformAccess access, FunctionService functions)
    : Endpoint<SetFunctionSecretRequest, Result<FunctionSecretBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>>
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

    public override async ValueTask<Result<FunctionSecretBody, Validation, Unauthorized, Forbidden, NotFound, Conflict, Unavailable>> HandleAsync(
        SetFunctionSecretRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await FunctionTargets.ResolveAsync(access, HttpContext, request.Project, request.Database, request.Function, Scopes.ReadWrite, cancellationToken) switch
        {
            FunctionTarget target => await functions.SetSecretAsync(target, request.Function, request.Secret, request.Value, cancellationToken) switch
            {
                FunctionSecretBody secret => secret,
                Validation invalid => invalid,
                NotFound missing => missing,
                Conflict conflict => conflict,
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

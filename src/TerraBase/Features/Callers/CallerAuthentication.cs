using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal sealed class CallerAuthentication(ApiKeyStore keys, TokenVerifier tokens)
{
    public const string UserHeader = "X-TerraBase-User-Id";

    public async ValueTask<Result<Caller, Unauthorized, Unavailable>> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admitted = context.Features.Get<AdmittedCaller>();
        if (admitted is not null && context.Request.Headers.TryGetValue(UserHeader, out var acting))
        {
            if (acting is not [{ Length: 26 } user] || !Ulid.TryParse(user, out _))
            {
                return new Unauthorized(UserHeader + " must be one canonical uppercase ULID", "invalid_user");
            }

            return admitted.Identity is { } identity && !identity.Grants.Contains("act-as")
                ? new Unauthorized("acting as a user needs the act-as grant", "forbidden_caller")
                : new UserCaller(user, ViaOperator: true);
        }

        if (Bearer(context.Request) is { } token)
        {
            return await AuthenticateBearerAsync(token, cancellationToken);
        }

        return admitted switch
        {
            { Identity: { } identity } => new ServiceCaller(identity.Key.Caller, identity.Grants),
            not null => new PeerCaller(),
            null => AnonymousCaller.Instance,
        };
    }

    public async ValueTask<Result<Caller, Unauthorized, Unavailable>> AuthenticateBearerAsync(string token, CancellationToken cancellationToken)
    {
        if (!token.StartsWith(ApiKeyStore.SecretPrefix, StringComparison.Ordinal))
        {
            return await tokens.VerifyAsync(token, cancellationToken);
        }

        return await keys.FindAsync(token, cancellationToken) switch
        {
            ApiKeyRecord key => new ApiKeyCaller(key),
            NotFound missing => new Unauthorized(missing.Message, "invalid_key"),
            Unavailable unavailable => unavailable,
        };
    }

    public static string? Bearer(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && header["Bearer ".Length..].Trim() is { Length: > 0 } token ? token : null;
    }
}

internal static class CallerAuthenticationExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseCallers()
        {
            app.Use(async (context, next) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>() is { } served
                    && (ReferenceEquals(served, ListenerMetadata.Probe) || ReferenceEquals(served, ListenerMetadata.Metrics)))
                {
                    await next(context);
                    return;
                }

                var authentication = context.RequestServices.GetRequiredService<CallerAuthentication>();
                var outcome = await authentication.AuthenticateAsync(context, context.RequestAborted);
                var refusal = outcome switch
                {
                    Caller caller => Accept(context, caller),
                    Unauthorized error => ErrorResults.Json(StatusCodes.Status401Unauthorized, error.Code, error.Message),
                    Unavailable error => ErrorResults.Json(StatusCodes.Status503ServiceUnavailable, error.Code, error.Message, retryAfter: TimeSpan.FromSeconds(1)),
                };
                if (refusal is not null)
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await refusal.ExecuteAsync(context);
                    return;
                }

                await next(context);
            });
            return app;
        }
    }

    private static IResult? Accept(HttpContext context, Caller caller)
    {
        context.SetCaller(caller);
        return null;
    }
}

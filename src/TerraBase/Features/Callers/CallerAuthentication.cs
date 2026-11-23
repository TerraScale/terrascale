using TerraBase.Features.ApiKeys;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal readonly union CallerAuthenticationOutcome(Caller, CredentialRejected, BackendUnavailable);

internal sealed class CallerAuthentication(ApiKeyStore keys, TokenVerifier tokens, ApiKeyExchange exchange)
{
    public const string UserHeader = "X-TerraBase-User-Id";

    public async ValueTask<CallerAuthenticationOutcome> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admitted = context.Features.Get<AdmittedCaller>();
        if (admitted is not null && context.Request.Headers.TryGetValue(UserHeader, out var acting))
        {
            if (acting is not [{ Length: 26 } user] || !Ulid.TryParse(user, out _))
            {
                return new CredentialRejected(UserHeader + " must be one canonical uppercase ULID", "invalid_user");
            }

            return admitted.Identity is { } identity && !identity.Grants.Contains("act-as")
                ? new CredentialRejected("acting as a user needs the act-as grant", "forbidden_caller")
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

    public async ValueTask<CallerAuthenticationOutcome> AuthenticateBearerAsync(string token, CancellationToken cancellationToken)
    {
        if (!token.StartsWith(ApiKeyStore.SecretPrefix, StringComparison.Ordinal))
        {
            return await tokens.VerifyAsync(token, cancellationToken);
        }

        var outcome = await keys.FindAsync(token, cancellationToken);
        return outcome switch
        {
            ApiKeyRecord key => new ApiKeyCaller(key),
            ResourceNotFound missing => new CredentialRejected(missing.Message, "invalid_key"),
            BackendUnavailable unavailable => unavailable,
        };
    }

    public async ValueTask<CallerAuthenticationOutcome> AuthenticatePasswordAsync(string secret, string project, CancellationToken cancellationToken)
    {
        if (!ApiKeyExchange.IsKey(secret))
        {
            return await AuthenticateBearerAsync(secret, cancellationToken);
        }

        var exchanged = await exchange.ExchangeAsync(secret, project, cancellationToken);
        return exchanged switch
        {
            string token => await tokens.VerifyAsync(token, cancellationToken),
            CredentialRejected unauthorized => unauthorized,
            BackendUnavailable unavailable => unavailable,
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
                    CredentialRejected error => ErrorResults.Json(StatusCodes.Status401Unauthorized, error.Code, error.Message),
                    BackendUnavailable error => ErrorResults.Json(StatusCodes.Status503ServiceUnavailable, error.Code, error.Message, retryAfter: TimeSpan.FromSeconds(1)),
                };
                if (refusal is not null)
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    await refusal.ExecuteAsync(context);
                    return;
                }

                if (context.Caller is ServiceCaller service && context.Request.Path.Value is { } path && !ServicePathAllowed(service, path))
                {
                    await ErrorResults.Json(StatusCodes.Status403Forbidden, "forbidden_caller", "the service caller does not grant access to this route").ExecuteAsync(context);
                    return;
                }

                await next(context);
            });
            return app;
        }
    }

    internal static bool ServicePathAllowed(ServiceCaller service, string path)
    {
        if (service.Grants.Contains("admin") || path is "/v1/regions" or "/v1/health" or "/healthz" or "/readyz" or "/backupz")
        {
            return true;
        }

        if (path.StartsWith("/admin/", StringComparison.Ordinal))
        {
            return service.Grants.Contains("plans") || service.Grants.Contains("closure") || service.Grants.Contains("platform") || service.Grants.Contains("platform.write");
        }

        if (path.StartsWith("/v1/sync/", StringComparison.Ordinal))
        {
            return service.Grants.Contains("catalog.read") || service.Grants.Contains("platform.read") || service.Grants.Contains("platform.write");
        }

        if (path.StartsWith("/v1/platform/", StringComparison.Ordinal))
        {
            return service.Grants.Contains("platform.read") || service.Grants.Contains("platform.write");
        }

        const string projectsPrefix = "/v1/projects/";
        if (path.StartsWith(projectsPrefix, StringComparison.Ordinal))
        {
            var rest = path.AsSpan(projectsPrefix.Length);
            var slash = rest.IndexOf('/');
            var segment = slash < 0 ? rest : rest[..slash];
            if (segment.Length == 26 && Ulid.TryParse(segment, out var projectId))
            {
                return service.Grants.Contains(string.Concat("data:", projectId.ToString()))
                    || service.Grants.Contains("catalog.read")
                    || (PlatformCatalog.IsPlatformProject(projectId)
                        && (service.Grants.Contains("platform.read") || service.Grants.Contains("platform.write")));
            }
        }

        return true;
    }

    private static IResult? Accept(HttpContext context, Caller caller)
    {
        context.SetCaller(caller);
        return null;
    }
}

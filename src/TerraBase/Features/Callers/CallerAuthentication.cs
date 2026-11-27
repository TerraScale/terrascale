using TerraBase.Features.ApiKeys;
using TerraBase.Features.Platform;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;

namespace TerraBase.Features.Callers;

internal readonly union CallerAuthenticationOutcome(Caller, CredentialRejected, BackendUnavailable);

internal sealed class CallerAuthentication(ApiKeyStore keys, TokenVerifier tokens, ApiKeyExchange exchange, AuthenticationGate gate)
{
    public const string UserHeader = "X-TerraBase-User-Id";

    private const string BearerPrefix = "Bearer ";

    public ValueTask<CallerAuthenticationOutcome> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var admitted = context.Features.Get<AdmittedCaller>();
        if (admitted is not null && context.Request.Headers.TryGetValue(UserHeader, out var acting))
        {
            if (acting is not [{ Length: 26 } user] || !Ulid.TryParse(user, out _))
            {
                return ValueTask.FromResult<CallerAuthenticationOutcome>(new CredentialRejected(UserHeader + " must be one canonical uppercase ULID", "invalid_user"));
            }

            CallerAuthenticationOutcome acted = admitted.Identity is { } identity && !identity.Grants.Contains("act-as")
                ? new CredentialRejected("acting as a user needs the act-as grant", "forbidden_caller")
                : new UserCaller(user, ViaOperator: true);
            return ValueTask.FromResult(acted);
        }

        if (TryReadBearer(context.Request, out var source, out var start, out var length))
        {
            var tokenSpan = source.AsSpan(start, length);
            if (tokenSpan.StartsWith(ApiKeyStore.SecretPrefix, StringComparison.Ordinal))
            {
                if (keys.TryFindCached(tokenSpan, out var lookup))
                {
                    return ValueTask.FromResult(MapApiKeyOutcome(lookup));
                }

                return AuthenticateApiKeyAsync(new string(tokenSpan), cancellationToken);
            }

            if (tokens.TryVerifyCached(tokenSpan, out var verified))
            {
                return ValueTask.FromResult<CallerAuthenticationOutcome>(verified);
            }

            return tokens.VerifyAsync(new string(tokenSpan), cancellationToken);
        }

        CallerAuthenticationOutcome fallback = admitted switch
        {
            { Identity: { } identity } => new ServiceCaller(identity.Key.Caller, identity.Grants),
            not null => new PeerCaller(),
            null => AnonymousCaller.Instance,
        };
        return ValueTask.FromResult(fallback);
    }

    public ValueTask<CallerAuthenticationOutcome> AuthenticateBearerAsync(string token, CancellationToken cancellationToken)
    {
        if (!token.StartsWith(ApiKeyStore.SecretPrefix, StringComparison.Ordinal))
        {
            return tokens.VerifyAsync(token, cancellationToken);
        }

        if (keys.TryFindCached(token, out var lookup))
        {
            return ValueTask.FromResult(MapApiKeyOutcome(lookup));
        }

        return AuthenticateApiKeyAsync(token, cancellationToken);
    }

    private async ValueTask<CallerAuthenticationOutcome> AuthenticateApiKeyAsync(string token, CancellationToken cancellationToken)
    {
        using var permit = await gate.EnterAsync(cancellationToken);
        if (!permit.IsAcquired)
        {
            return AuthenticationGate.Busy;
        }

        var outcome = await keys.FindAsync(token, cancellationToken);
        return MapApiKeyOutcome(outcome);
    }

    private static CallerAuthenticationOutcome MapApiKeyOutcome(ApiKeyLookup outcome) => outcome switch
    {
        ApiKeyRecord key => new ApiKeyCaller(key),
        ResourceNotFound missing => new CredentialRejected(missing.Message, "invalid_key"),
        BackendUnavailable unavailable => unavailable,
    };

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

    public static string? Bearer(HttpRequest request) =>
        TryReadBearer(request, out var source, out var start, out var length)
            ? new string(source.AsSpan(start, length))
            : null;

    private static bool TryReadBearer(HttpRequest request, out string source, out int start, out int length)
    {
        var values = request.Headers.Authorization;
        if (values.Count == 0)
        {
            source = string.Empty;
            start = 0;
            length = 0;
            return false;
        }

        var raw = values.Count == 1 ? values[0] : values.ToString();
        if (raw is null)
        {
            source = string.Empty;
            start = 0;
            length = 0;
            return false;
        }

        var span = raw.AsSpan();
        if (!span.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            source = string.Empty;
            start = 0;
            length = 0;
            return false;
        }

        var sliceStart = BearerPrefix.Length;
        var sliceEnd = span.Length;
        while (sliceStart < sliceEnd && char.IsWhiteSpace(span[sliceStart]))
        {
            sliceStart++;
        }

        while (sliceEnd > sliceStart && char.IsWhiteSpace(span[sliceEnd - 1]))
        {
            sliceEnd--;
        }

        if (sliceEnd <= sliceStart)
        {
            source = string.Empty;
            start = 0;
            length = 0;
            return false;
        }

        source = raw;
        start = sliceStart;
        length = sliceEnd - sliceStart;
        return true;
    }
}

internal static class CallerAuthenticationExtensions
{
    extension(WebApplication app)
    {
        public WebApplication UseTerraBaseCallers()
        {
            var authentication = app.Services.GetRequiredService<CallerAuthentication>();
            app.Use(async (context, next) =>
            {
                if (context.GetEndpoint()?.Metadata.GetMetadata<ListenerMetadata>() is { } served
                    && (ReferenceEquals(served, ListenerMetadata.Probe) || ReferenceEquals(served, ListenerMetadata.Metrics)))
                {
                    await next(context);
                    return;
                }

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

        return false;
    }

    public static bool ActingPathAllowed(string path) =>
        path.StartsWith("/v1/management/", StringComparison.Ordinal) || path.StartsWith("/v1/projects/", StringComparison.Ordinal);

    public static bool OperatesNode(Caller caller) => caller switch
    {
        PeerCaller => true,
        ServiceCaller service => service.Grants.Contains("admin"),
        AnonymousCaller or ApiKeyCaller or UserCaller or ProjectCaller or EndUserCaller => false,
    };

    private static IResult? Accept(HttpContext context, Caller caller)
    {
        context.SetCaller(caller);
        return null;
    }
}

using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraScale.Endpoints;

namespace TerraBase.Features.UserAuth;

internal static class UserAuthExtensions
{
    public const string PolicyName = "user";

    public const string TokenScheme = "TsAuth";

    public const string OperatorScheme = "Operator";

    public static IServiceCollection AddTerraBaseUserAuth(this IServiceCollection services)
    {
        services.AddHttpClient(TsAuthKeys.HttpClientName, client =>
        {
            client.Timeout = TsAuthKeys.FetchTimeout;
            client.MaxResponseContentBufferSize = TsAuthKeys.MaxResponseBytes;
        })
            .ConfigurePrimaryHttpMessageHandler((handler, _) =>
            {
                // Other regions reach ts-auth across continents: keep one pool, warm between key checks, so a check does not
                // pay a new TLS handshake whenever the factory would rotate handlers. Connections still renew for DNS changes.
                var sockets = (SocketsHttpHandler)handler;
                sockets.AllowAutoRedirect = false;
                sockets.PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10);
                sockets.PooledConnectionLifetime = TimeSpan.FromMinutes(10);
            })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            .AddHttpMessageHandler(provider => RequestIdentity.CreatePropagationHandler(
                provider.GetRequiredService<InternalCredential>().CorrelationKey,
                new Uri(provider.GetRequiredService<TerraBaseOptions>().AuthIssuer ?? "about:blank")))
            .RemoveAllLoggers();
        services.AddSingleton<TsAuthKeys>();
        services.AddSingleton<ValidatedTokens>();
        services.AddSingleton<IIssuerKeys>(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddSingleton<IProjectAuthorityVerifier, TsAuthProjectAuthorityVerifier>();
        services.AddSingleton<TsAuthKeyExchange>();
        services.AddSingleton<TsAuthKeyAuthority>();
        services.AddSingleton<IKeyAuthority>(provider => provider.GetRequiredService<TsAuthKeyAuthority>());
        services.AddHostedService(provider => provider.GetRequiredService<TsAuthKeyAuthority>());
        services.AddHostedService(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UserAuthenticationHandler>(UserAuthenticationHandler.SchemeName, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, OperatorHandler>(OperatorHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthenticationHandler.SchemeName, OperatorHandler.SchemeName)
            .RequireAuthenticatedUser());
        return services;
    }

    public static string UserId(this ClaimsPrincipal user) =>
        (Identity(user, UserAuthenticationHandler.SchemeName) ?? Identity(user, OperatorHandler.SchemeName))
            ?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("the request has no authenticated ts-auth user");

    internal static bool IsOperator(this ClaimsPrincipal user) => Identity(user, OperatorHandler.SchemeName) is not null;

    private static ClaimsIdentity? Identity(ClaimsPrincipal user, string scheme) =>
        user?.Identities.FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == scheme);

    internal sealed record SocketRefusal(string Code, string Message);

    internal static ValueTask<SocketRefusal?> AdmitSocketAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep) =>
        AdmitSocketCoreAsync(context, revoked, keep, shape: false);

    internal static ValueTask<SocketRefusal?> AdmitShapeSocketAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep) =>
        AdmitSocketCoreAsync(context, revoked, keep, shape: true);

    private static async ValueTask<SocketRefusal?> AdmitSocketCoreAsync(HttpContext context, CancellationTokenSource revoked, Action<IDisposable> keep, bool shape)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(revoked);
        ArgumentNullException.ThrowIfNull(keep);
        if (Identity(context.User, UserAuthenticationHandler.SchemeName) is not { } jwt)
        {
            return null;
        }

        if (!shape && jwt.HasClaim("token_kind", "end_user"))
        {
            return new SocketRefusal("forbidden", "end-user tokens cannot run SQL");
        }

        if (jwt.FindFirst("project_id")?.Value is not { } project)
        {
            return new SocketRefusal("invalid_key", "this token does not authorize project routes; use an API key or its project token");
        }

        if (context.Request.RouteValues["project"] as string != project)
        {
            return new SocketRefusal("unknown_database", "the database does not exist");
        }

        var token = context.Features.Get<ProjectToken>();
        if (token is { Allowlists.Count: > 0 } && IpAllowlists.ClientOf(context) is var client && !IpAllowlists.Admits(token.Allowlists, client))
        {
            return new SocketRefusal("ip_not_allowed", IpAllowlists.Refusal(client));
        }

        switch (await GrantAsync(context, jwt, project, bucketCreation: false).ConfigureAwait(false))
        {
            case ProjectAccess granted:
                context.Features.Set(granted);
                break;
            case NotFound:
                return new SocketRefusal("unknown_database", "the database does not exist");
            case Unavailable unavailable:
                return new SocketRefusal("unavailable", unavailable.Message);
        }

        if (token?.Key is not { } key)
        {
            return null;
        }

        var authority = context.RequestServices.GetRequiredService<IKeyAuthority>();
        switch ((await authority.AdmitAsync(key, context.RequestAborted).ConfigureAwait(false)).Value)
        {
            case KeyActive active:
                if (KeyWatch.Start(authority, watched: true, active.Until, _ => ValueTask.FromResult<KeyToken?>(key), revoked,
                    context.RequestServices.GetRequiredService<TimeProvider>()) is { } watch)
                {
                    keep(watch);
                }

                return null;
            case KeyRefused refused:
                return new SocketRefusal(refused.Code, KeyAuthorityMessages.Refused(refused.Code));
            default:
                return new SocketRefusal("auth_unavailable", KeyAuthorityMessages.Unavailable);
        }
    }

    private const int Serving = 0;
    private const int Started = 1;
    private const int CutOff = 2;
    private const int Refusing = 3;

    private static Task RefuseKeyAsync(HttpContext context, KeyRefused? refused)
    {
        if (refused is not null)
        {
            context.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{refused.Code}\"";
            return BadRequests.WriteAsync(context, StatusCodes.Status401Unauthorized, refused.Code, KeyAuthorityMessages.Refused(refused.Code), op: null);
        }

        context.Response.Headers.RetryAfter = "1";
        return BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "auth_unavailable", KeyAuthorityMessages.Unavailable, op: null);
    }

    // RFC 6750 §3: every 401 on the public listener carries a Bearer challenge.
    public static WebApplication UseTerraBaseBearerChallenge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var presented = context.Request.Headers.Authorization.ToString();
            context.Response.OnStarting(() =>
            {
                // The public listener only: internal traffic authenticates differently, and a refused API key
                // already carries a richer challenge with the refusal code.
                if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                    && context.Response.StatusCode == StatusCodes.Status401Unauthorized
                    && context.Response.Headers.WWWAuthenticate.Count == 0)
                {
                    // A presented-but-rejected credential is an invalid_token; a missing one names only the realm.
                    context.Response.Headers.WWWAuthenticate = context.Request.Headers.Authorization.Count > 0
                        ? "Bearer realm=\"terrascale\", error=\"invalid_token\""
                        : "Bearer realm=\"terrascale\"";
                }

                return Task.CompletedTask;
            });
            await next(context).ConfigureAwait(false);
            if (Listeners.ArrivedOn(context) == TerraBaseListener.Client
                && context.Response.StatusCode == StatusCodes.Status401Unauthorized
                && !context.Response.HasStarted
                && presented.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && TsAuthKeyExchange.IsKey(presented["Bearer ".Length..].Trim()))
            {
                await BadRequests.WriteAsync(
                    context, StatusCodes.Status401Unauthorized, "invalid_token", KeyAuthorityMessages.KeyAsBearer, op: null).ConfigureAwait(false);
            }
        });
        return app;
    }

    public static WebApplication UseTerraBaseProjectCheck(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            if (Identity(context.User, UserAuthenticationHandler.SchemeName) is null || context.Features.Get<ProjectToken>()?.Key is not { } key)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var authority = context.RequestServices.GetRequiredService<IKeyAuthority>();
            var answer = (await authority.AdmitAsync(key, context.RequestAborted).ConfigureAwait(false)).Value;
            if (answer is not KeyActive active)
            {
                await RefuseKeyAsync(context, answer as KeyRefused).ConfigureAwait(false);
                return;
            }

            var aborted = context.RequestAborted;
            using var refusal = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            var state = Serving;
            context.Response.OnStarting(() =>
            {
                if (refusal.IsCancellationRequested && !aborted.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref state, CutOff, Serving);
                }

                if (Interlocked.CompareExchange(ref state, Started, Serving) == CutOff)
                {
                    context.Abort();
                }

                return Task.CompletedTask;
            });
            using var cut = refusal.Token.Register(() =>
            {
                if (!aborted.IsCancellationRequested && Interlocked.CompareExchange(ref state, CutOff, Serving) == Started)
                {
                    context.Abort();
                }
            });
            using var watch = KeyWatch.Start(authority, watched: true, active.Until, _ => ValueTask.FromResult<KeyToken?>(key), refusal, context.RequestServices.GetRequiredService<TimeProvider>());
            context.RequestAborted = refusal.Token;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (refusal.IsCancellationRequested && !aborted.IsCancellationRequested && !context.Response.HasStarted)
            {
                Interlocked.CompareExchange(ref state, CutOff, Serving);
            }
            finally
            {
                context.RequestAborted = aborted;
            }

            if (refusal.IsCancellationRequested && !aborted.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref state, CutOff, Serving);
            }

            if (Interlocked.CompareExchange(ref state, Refusing, CutOff) == CutOff && !context.Response.HasStarted)
            {
                context.Response.Clear();
                await RefuseKeyAsync(context, watch!.Refusal).ConfigureAwait(false);
            }
        });
        app.Use(async (context, next) =>
        {
            if (Identity(context.User, UserAuthenticationHandler.SchemeName) is not null
                && context.Features.Get<ProjectToken>() is { Allowlists.Count: > 0 } restricted
                && IpAllowlists.ClientOf(context) is var client && !IpAllowlists.Admits(restricted.Allowlists, client))
            {
                await BadRequests.WriteAsync(context, StatusCodes.Status403Forbidden, "ip_not_allowed", IpAllowlists.Refusal(client), op: null).ConfigureAwait(false);
                return;
            }

            if (Identity(context.User, UserAuthenticationHandler.SchemeName)?.HasClaim("token_kind", "end_user") == true
                && context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not
                    ("getShape" or "getShapeKey" or "getShapeManifest" or "connectShapeHints"
                    or "createFileUpload" or "completeFileUpload" or "listBucketFiles" or "createFileDownload" or "deleteBucketFile"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            if (Identity(context.User, UserAuthenticationHandler.SchemeName)?.FindFirst("project_id") is not null
                && (context.Features.Get<LegacyManagementRoute>() is not null
                    || context.Request.Path.StartsWithSegments(ManagementRoute.Prefix, StringComparison.OrdinalIgnoreCase)
                    || context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "legacyCreateDatabase"))
            {
                await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                return;
            }

            if (context.Features.Get<LegacyManagementRoute>() is not null && context.User.Identity?.IsAuthenticated == true
                && Identity(context.User, UserAuthenticationHandler.SchemeName) is null && !context.User.IsOperator())
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            if (context.GetEndpoint() is not null && context.Features.Get<LegacyManagementRoute>() is null
                && context.Request.RouteValues["project"] is string project
                && !context.Request.RouteValues.ContainsKey("organization") && context.User.Identity?.IsAuthenticated == true)
            {
                var jwt = Identity(context.User, UserAuthenticationHandler.SchemeName);
                if (jwt is not null)
                {
                    if (jwt.FindFirst("project_id") is null)
                    {
                        await BadRequests.WriteAsync(context, StatusCodes.Status401Unauthorized, "invalid_key",
                            "this token does not authorize project routes; use an API key or its project token", op: null).ConfigureAwait(false);
                        return;
                    }

                    if (jwt.FindFirst("project_id")?.Value != project)
                    {
                        await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                        return;
                    }
                }
                else if (context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }

    public static WebApplication UseTerraBaseProjectCatalog(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            var managementRoute = context.GetEndpoint()?.Metadata.GetMetadata<ProjectManagementRoute>() is not null;
            if (context.Features.Get<LegacyManagementRoute>() is { } legacy)
            {
                context.Request.RouteValues["organization"] = legacy.OrganizationId;
                await next(context).ConfigureAwait(false);
                return;
            }

            if (context.Request.RouteValues["project"] is not string project || context.Request.RouteValues.ContainsKey("organization")
                || Identity(context.User, UserAuthenticationHandler.SchemeName) is not { } jwt)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
                is CreateManagedDatabaseEndpoint.OperationName or ListManagedDatabasesEndpoint.ListOperationName)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var bucketCreation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "createFileBucket"
                && !jwt.HasClaim("token_kind", "end_user") && jwt.FindFirst("database") is null;
            var granted = await GrantAsync(context, jwt, project, bucketCreation).ConfigureAwait(false);
            if (granted is not ProjectAccess grant)
            {
                await (granted switch
                {
                    NotFound refused => ErrorResponse.From(refused),
                    Unavailable unavailable => ErrorResponse.From(unavailable),
                    ProjectAccess => throw new UnreachableException(),
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            context.Features.Set(grant);
            var management = context.RequestServices.GetRequiredService<IManagementService>();
            var (organization, restricted, restrictedId) = (grant.OrganizationId, grant.DatabaseName, grant.DatabaseId);

            if (managementRoute)
            {
                context.Request.RouteValues["organization"] = organization;
            }

            if (managementRoute || restricted is not null)
            {
                if (context.Request.RouteValues["database"] is string name)
                {
                    if (!EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _))
                    {
                        await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                        return;
                    }

                    var found = await management.FindDatabaseRecordAsync(organization, project, name, context.RequestAborted).ConfigureAwait(false);
                    if (found is not ManagedDatabase record)
                    {
                        await (found switch
                        {
                            NotFound => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
                            Unavailable error => ErrorResponse.From(error),
                            ManagedDatabase => throw new UnreachableException(),
                        }).ExecuteAsync(context).ConfigureAwait(false);
                        return;
                    }

                    if (restricted is not null && restrictedId != record.Id)
                    {
                        await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                        return;
                    }

                    if (managementRoute)
                    {
                        context.Request.RouteValues["databaseName"] = record.Name;
                    }
                    context.Request.RouteValues["database"] = record.Id;
                }
            }

            await ProjectAccess.RunAsync(grant, () => next(context)).ConfigureAwait(false);
        });
        return app;
    }

    internal static async ValueTask<ProjectGrant> GrantAsync(HttpContext context, ClaimsIdentity jwt, string project, bool bucketCreation)
    {
        var actor = jwt.FindFirst(ClaimTypes.NameIdentifier)!.Value;
        var catalog = context.RequestServices.GetRequiredService<IProjectCatalog>();
        var resolved = await catalog.ResolveProjectAsync(project, actor,
            createIn: bucketCreation ? jwt.FindFirst("org_id")?.Value : null, context.RequestAborted).ConfigureAwait(false);
        if (resolved is not string organization)
        {
            return resolved switch
            {
                NotFound => new NotFound("the database does not exist", "unknown_database"),
                Unavailable error => error,
                string => throw new UnreachableException(),
            };
        }

        var restricted = jwt.FindFirst("database")?.Value;
        string? restrictedId = null;
        if (restricted is not null)
        {
            var restriction = await context.RequestServices.GetRequiredService<IManagementService>()
                .FindDatabaseRecordAsync(organization, project, restricted, context.RequestAborted).ConfigureAwait(false);
            switch (restriction)
            {
                case Unavailable unavailable:
                    return unavailable;
                case ManagedDatabase record:
                    restrictedId = record.Id;
                    break;
            }
        }

        return new ProjectAccess(actor, project, organization, restricted, restrictedId)
        {
            IsEndUser = jwt.HasClaim("token_kind", "end_user"),
            Roles = jwt.FindAll("roles").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
            Tables = jwt.FindAll("tables").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
        };
    }
}

using System.Collections.Frozen;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.UserAuth;

/// <summary>Registers ts-auth user authentication and reads the authenticated user.</summary>
public static class UserAuthExtensions
{
    /// <summary>Authorization policy of endpoints that act as a ts-auth user.</summary>
    public const string PolicyName = "user";

    /// <summary>The scheme that validates ts-auth access tokens.</summary>
    public const string TokenScheme = "TsAuth";

    /// <summary>The platform operator's scheme: the internal listener, acting as a named user.</summary>
    public const string OperatorScheme = "Operator";

    /// <summary>
    /// Registers <see cref="IIssuerKeys"/> with its background fetch, the <c>TsAuth</c> scheme (client listener), the
    /// <c>Operator</c> scheme (internal listener) and the <see cref="PolicyName"/> policy that accepts
    /// either. Without <c>auth-issuer</c> the token scheme authenticates no one.
    /// </summary>
    public static IServiceCollection AddTerraBaseUserAuth(this IServiceCollection services)
    {
        // No request logging: it would write auth-issuer's URL, and settings are never logged by value.
        services.AddHttpClient(TsAuthKeys.HttpClientName, client =>
        {
            client.Timeout = TsAuthKeys.FetchTimeout;
            client.MaxResponseContentBufferSize = TsAuthKeys.MaxDocumentBytes;
        })
            // Discovery and JWKS are fetched where auth-issuer and jwks_uri say, never where a redirect points.
            .ConfigurePrimaryHttpMessageHandler((handler, _) => ((SocketsHttpHandler)handler).AllowAutoRedirect = false)
            .AddHttpMessageHandler(provider => RequestIdentity.CreatePropagationHandler(
                provider.GetRequiredService<PeerCredential>().CorrelationKey,
                new Uri(provider.GetRequiredService<TerraBaseOptions>().AuthIssuer ?? "about:blank")))
            .RemoveAllLoggers();
        services.AddSingleton<TsAuthKeys>();
        services.AddSingleton<ValidatedTokens>();
        services.AddSingleton<IIssuerKeys>(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddSingleton<IProjectAuthorityVerifier, TsAuthProjectAuthorityVerifier>();
        services.AddSingleton<TsAuthKeyExchange>();
        services.AddSingleton<IKeyAuthority, TsAuthKeyAuthority>();
        services.AddHostedService(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UserAuthenticationHandler>(UserAuthenticationHandler.SchemeName, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, OperatorHandler>(OperatorHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthenticationHandler.SchemeName, OperatorHandler.SchemeName)
            .RequireAuthenticatedUser());
        return services;
    }

    /// <summary>
    /// The ts-auth user id of a request authorized by <see cref="PolicyName"/>: the token's <c>sub</c>, or the user the
    /// platform operator names.
    /// </summary>
    public static string UserId(this ClaimsPrincipal user) =>
        (Identity(user, UserAuthenticationHandler.SchemeName) ?? Identity(user, OperatorHandler.SchemeName))
            ?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("the request has no authenticated ts-auth user");

    /// <summary>
    /// Whether the request came from the platform operator (internal listener), which
    /// the dashboard document space of Management commits needs: a ts-auth user token never reaches it.
    /// </summary>
    public static bool IsOperator(this ClaimsPrincipal user) => Identity(user, OperatorHandler.SchemeName) is not null;

    /// <summary>The user's email when its ts-auth token marks it verified; otherwise <see langword="null"/>.</summary>
    public static string? VerifiedEmail(this ClaimsPrincipal user) =>
        Identity(user, UserAuthenticationHandler.SchemeName)?.FindFirst(ClaimTypes.Email)?.Value;

    // Only an identity these schemes authenticated, never one another scheme (an API key) added to the principal.
    private static ClaimsIdentity? Identity(ClaimsPrincipal user, string scheme) =>
        user?.Identities.FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == scheme);

    /// <summary>Checks signed project/database claims before scope authorization so cross-project calls answer 404.</summary>
    public static WebApplication UseTerraBaseProjectCheck(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use(async (context, next) =>
        {
            // An API key's token acts only while ts-auth says its key still does, and only as long as it does: a request
            // still running (a slow body, a changes stream) is aborted once the key is refused (docs/terrabase/key-revocation.md).
            if (Identity(context.User, UserAuthenticationHandler.SchemeName) is null || context.Features.Get<ProjectToken>()?.Key is not { } key)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var authority = context.RequestServices.GetRequiredService<IKeyAuthority>();
            var answer = (await authority.CheckAsync(key, context.RequestAborted).ConfigureAwait(false)).Value;
            switch (answer)
            {
                case KeyRefused refused:
                    context.Response.Headers.WWWAuthenticate = $"Bearer error=\"invalid_token\", error_description=\"{refused.Code}\"";
                    await BadRequests.WriteAsync(context, StatusCodes.Status401Unauthorized, refused.Code, KeyAuthorityMessages.Refused(refused.Code), op: null).ConfigureAwait(false);
                    return;
                case Unavailable:
                    context.Response.Headers.RetryAfter = "1";
                    await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "auth_unavailable", KeyAuthorityMessages.Unavailable, op: null).ConfigureAwait(false);
                    return;
            }

            using var refusal = new CancellationTokenSource();
            using var abort = refusal.Token.Register(context.Abort);
            using var watch = KeyWatch.Start(authority, watched: true, (answer as KeyActive)?.Until, _ => ValueTask.FromResult<KeyToken?>(key), refusal, context.RequestServices.GetRequiredService<TimeProvider>());
            await next(context).ConfigureAwait(false);
        });
        app.Use(async (context, next) =>
        {
            // An API key's token is used only from its key's and project's allowlists, on every route.
            if (Identity(context.User, UserAuthenticationHandler.SchemeName) is not null
                && context.Features.Get<ProjectToken>() is { Allowlists.Count: > 0 } restricted
                && IpAllowlists.ClientOf(context) is var client && !IpAllowlists.Admits(restricted.Allowlists, client))
            {
                await BadRequests.WriteAsync(context, StatusCodes.Status403Forbidden, "ip_not_allowed", IpAllowlists.Refusal(client), op: null).ConfigureAwait(false);
                return;
            }

            if (Identity(context.User, UserAuthenticationHandler.SchemeName)?.HasClaim("token_kind", "end_user") == true
                && context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName is not
                    ("getDocument" or "listDocuments" or "queryDocuments" or "createDocument" or "putDocument"
                    or "patchDocument" or "deleteDocument" or "runTransaction" or "streamDatabaseChanges"
                    or "getShape" or "getShapeKey" or "getShapeManifest"
                    or "createFileUpload" or "completeFileUpload" or "listBucketFiles" or "createFileDownload" or "deleteBucketFile"))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            // Project tokens never enter the membership-based compatibility API: that would discard their narrowing.
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

    /// <summary>Resolves authorized project tokens without moving production files or changing replication formats.</summary>
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

            // Creation validates its bound body and signed database restriction before it can enroll a project.
            if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == CreateManagedDatabaseEndpoint.OperationName)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            var actor = jwt.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var catalog = context.RequestServices.GetRequiredService<IProjectCatalog>();
            var bucketCreation = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "createFileBucket"
                && !jwt.HasClaim("token_kind", "end_user") && jwt.FindFirst("database") is null;
            var resolved = await catalog.ResolveProjectAsync(project, actor,
                createIn: bucketCreation ? jwt.FindFirst("org_id")?.Value : null, context.RequestAborted).ConfigureAwait(false);
            if (resolved is not string organization)
            {
                await (resolved switch
                {
                    NotFound => ErrorResponse.From(new NotFound("the database does not exist", "unknown_database")),
                    Unavailable error => ErrorResponse.From(error),
                    string => throw new UnreachableException(),
                }).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            var management = context.RequestServices.GetRequiredService<IManagementService>();
            var restricted = jwt.FindFirst("database")?.Value;
            string? restrictedId = null;
            if (restricted is not null)
            {
                var restriction = await management.FindDatabaseRecordAsync(organization, project, restricted, context.RequestAborted).ConfigureAwait(false);
                if (restriction is Unavailable unavailable)
                {
                    await ErrorResponse.From(unavailable).ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }

                restrictedId = restriction switch
                {
                    ManagedDatabase record => record.Id,
                    NotFound => null,
                    Unavailable => throw new UnreachableException(),
                };
            }
            var grant = new ProjectAccess(actor, project, organization, restricted, restrictedId)
            {
                IsEndUser = jwt.HasClaim("token_kind", "end_user"),
                Roles = jwt.FindAll("roles").Select(claim => claim.Value).ToFrozenSet(StringComparer.Ordinal),
            };
            context.Features.Set(grant);

            if (managementRoute)
            {
                context.Request.RouteValues["organization"] = organization;
            }

            // Check immutable narrowing before placement can disclose another database's serving regions.
            if (managementRoute || restricted is not null)
            {
                if (context.Request.RouteValues["database"] is string name)
                {
                    if (!Primitives.EntityIds.IsValid(name) && !DatabaseName.TryParse(name, out _))
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
                    // Keep this admitted request on its authorized identity if the name is reused before placement.
                    context.Request.RouteValues["database"] = record.Id;
                }
            }

            await ProjectAccess.RunAsync(grant, () => next(context)).ConfigureAwait(false);
        });
        return app;
    }
}

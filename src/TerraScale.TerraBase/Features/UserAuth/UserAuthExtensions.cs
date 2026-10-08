using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using TerraBase.Engine;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

/// <summary>Registers ts-auth user authentication and reads the authenticated user.</summary>
public static class UserAuthExtensions
{
    /// <summary>Authorization policy of endpoints that act as a ts-auth user.</summary>
    public const string PolicyName = "user";

    /// <summary>Client subscriptions accept only signed ts-auth access tokens.</summary>
    public const string SubscriptionPolicy = "subscription";

    /// <summary>The scheme that validates ts-auth access tokens.</summary>
    public const string TokenScheme = "TsAuth";

    /// <summary>The hidden, loopback-only compatibility scheme.</summary>
    public const string LegacyServiceScheme = "ManagementCredential";

    /// <summary>
    /// Registers <see cref="IIssuerKeys"/> with its background fetch, the <c>TsAuth</c> scheme (client listener), the
    /// <c>ManagementCredential</c> scheme (administrative listener) and the <see cref="PolicyName"/> policy that accepts
    /// either. Without <c>auth-issuer</c> or <c>management-credential-file</c> the matching scheme authenticates no one.
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
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        services.AddSingleton<TsAuthKeys>();
        services.AddSingleton<IIssuerKeys>(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddSingleton<IProjectAuthorityVerifier, TsAuthProjectAuthorityVerifier>();
        services.AddHostedService(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UserAuthenticationHandler>(UserAuthenticationHandler.SchemeName, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, ManagementCredentialHandler>(ManagementCredentialHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthenticationHandler.SchemeName, ManagementCredentialHandler.SchemeName)
            .RequireAuthenticatedUser());
        services.AddAuthorizationBuilder().AddPolicy(SubscriptionPolicy, policy => policy
            .AddAuthenticationSchemes(UserAuthenticationHandler.SchemeName).RequireAuthenticatedUser()
            .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, ApiKeyScopes.ShapesRead));
        return services;
    }

    /// <summary>
    /// The ts-auth user id of a request authorized by <see cref="PolicyName"/>: the token's <c>sub</c>, or the user the
    /// management service credential names.
    /// </summary>
    public static string UserId(this ClaimsPrincipal user) =>
        (Identity(user, UserAuthenticationHandler.SchemeName) ?? Identity(user, ManagementCredentialHandler.SchemeName))
            ?.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new InvalidOperationException("the request has no authenticated ts-auth user");

    /// <summary>
    /// Whether the request authenticated with the management service credential (loopback administrative listener), which
    /// the dashboard document space of Management commits needs: a ts-auth user token never reaches it.
    /// </summary>
    public static bool IsManagementService(this ClaimsPrincipal user) => Identity(user, ManagementCredentialHandler.SchemeName) is not null;

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
            // Project tokens never enter the membership-based compatibility API: that would discard their narrowing.
            if (Identity(context.User, UserAuthenticationHandler.SchemeName)?.FindFirst("project_id") is not null
                && (context.Features.Get<LegacyManagementRoute>() is not null
                    || context.Request.Path.StartsWithSegments(ManagementRoute.Prefix, StringComparison.OrdinalIgnoreCase)))
            {
                await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                return;
            }

            if (context.Features.Get<LegacyManagementRoute>() is not null && context.User.Identity?.IsAuthenticated == true
                && Identity(context.User, UserAuthenticationHandler.SchemeName) is null && !context.User.IsManagementService())
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
                    var database = jwt.FindFirst("database")?.Value;
                    if (jwt.FindFirst("project_id")?.Value != project
                        || database is not null && context.Request.RouteValues["database"] is string name && database != name)
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
            if (await catalog.ResolveProjectAsync(project, actor, create: false, context.RequestAborted).ConfigureAwait(false) is not { } organization)
            {
                await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                return;
            }

            var management = context.RequestServices.GetRequiredService<IManagementService>();
            var restricted = jwt.FindFirst("database")?.Value;
            var restrictedId = restricted is null ? null
                : (await management.FindDatabaseRecordAsync(organization, project, restricted, context.RequestAborted).ConfigureAwait(false))?.Id;
            var grant = new ProjectAccess(actor, project, organization, restricted, restrictedId);
            context.Features.Set(grant);

            if (managementRoute)
            {
                context.Request.RouteValues["organization"] = organization;
                if (context.Request.RouteValues["database"] is string name)
                {
                    if (!DatabaseName.TryParse(name, out _) || await management.FindDatabaseRecordAsync(organization, project, name, context.RequestAborted)
                        .ConfigureAwait(false) is not { } record)
                    {
                        await BadRequests.WriteAsync(context, 404, "unknown_database", "the database does not exist", op: null).ConfigureAwait(false);
                        return;
                    }

                    context.Request.RouteValues["databaseName"] = name;
                    context.Request.RouteValues["database"] = record.Id;
                }
            }

            await ProjectAccess.RunAsync(grant, () => next(context)).ConfigureAwait(false);
        });
        return app;
    }
}

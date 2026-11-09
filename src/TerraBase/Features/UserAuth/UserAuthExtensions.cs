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

    internal static ClaimsIdentity? Identity(ClaimsPrincipal user) =>
        Identity(user, UserAuthenticationHandler.SchemeName);

    private static ClaimsIdentity? Identity(ClaimsPrincipal user, string scheme) =>
        user?.Identities.FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == scheme);

    public static WebApplication UseTerraBaseBearerChallenge(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) => BearerChallenge.InvokeAsync(context, next));
        return app;
    }

    public static WebApplication UseTerraBaseProjectCheck(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) => ProjectCheck.WatchKeyAsync(context, next));
        app.Use((context, next) => ProjectCheck.GateClaimsAsync(context, next));
        return app;
    }

    public static WebApplication UseTerraBaseProjectCatalog(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.Use((context, next) => ProjectCatalog.InvokeAsync(context, next));
        return app;
    }
}

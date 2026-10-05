using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace TerraScale.TerraBase.Features.UserAuth;

/// <summary>Registers ts-auth user authentication and reads the authenticated user.</summary>
public static class UserAuthExtensions
{
    /// <summary>Authorization policy of endpoints that act as a ts-auth user.</summary>
    public const string PolicyName = "user";

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
        services.AddHostedService(provider => provider.GetRequiredService<TsAuthKeys>());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, UserAuthenticationHandler>(UserAuthenticationHandler.SchemeName, configureOptions: null)
            .AddScheme<AuthenticationSchemeOptions, ManagementCredentialHandler>(ManagementCredentialHandler.SchemeName, configureOptions: null);
        services.AddAuthorizationBuilder().AddPolicy(PolicyName, policy => policy
            .AddAuthenticationSchemes(UserAuthenticationHandler.SchemeName, ManagementCredentialHandler.SchemeName)
            .RequireAuthenticatedUser());
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

    /// <summary>The user's email when its ts-auth token marks it verified; otherwise <see langword="null"/>.</summary>
    public static string? VerifiedEmail(this ClaimsPrincipal user) =>
        Identity(user, UserAuthenticationHandler.SchemeName)?.FindFirst(ClaimTypes.Email)?.Value;

    // Only an identity these schemes authenticated, never one another scheme (an API key) added to the principal.
    private static ClaimsIdentity? Identity(ClaimsPrincipal user, string scheme) =>
        user?.Identities.FirstOrDefault(identity => identity.IsAuthenticated && identity.AuthenticationType == scheme);
}

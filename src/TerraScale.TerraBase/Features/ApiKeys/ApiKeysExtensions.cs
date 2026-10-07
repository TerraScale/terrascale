namespace TerraBase.Features.ApiKeys;

/// <summary>Registers API-key authentication and one authorization policy per scope.</summary>
public static class ApiKeysExtensions
{
    /// <summary>Registers <see cref="IApiKeyStore"/>, the <c>ApiKey</c> scheme and the scope policies.</summary>
    public static IServiceCollection AddTerraBaseApiKeys(this IServiceCollection services)
    {
        services.AddSingleton<IApiKeyStore, EngineApiKeyStore>();
        services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        var authorization = services.AddAuthorizationBuilder();
        foreach (var scope in ApiKeyScopes.All)
        {
            authorization.AddPolicy(scope, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName)
                .RequireClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope));
        }

        return services;
    }
}

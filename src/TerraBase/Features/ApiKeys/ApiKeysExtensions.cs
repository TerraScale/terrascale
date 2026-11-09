using Microsoft.AspNetCore.Authentication;
using TerraBase.Features.Management;
using TerraBase.Features.UserAuth;

namespace TerraBase.Features.ApiKeys;

public static class ApiKeysExtensions
{
    public static IServiceCollection AddTerraBaseApiKeys(this IServiceCollection services)
    {
        services.AddSingleton<IApiKeyStore, EngineApiKeyStore>();
        services.AddAuthentication("TerraBaseBearer")
            .AddPolicyScheme("TerraBaseBearer", "TerraBase bearer token", options => options.ForwardDefaultSelector = BearerScheme)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, configureOptions: null);
        var authorization = services.AddAuthorizationBuilder();
        foreach (var scope in ApiKeyScopes.All.Concat(ApiKeyScopes.ProjectTokenScopes))
        {
            authorization.AddPolicy(scope, policy => policy
                .AddAuthenticationSchemes("TerraBaseBearer", UserAuthExtensions.OperatorScheme)
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.HasClaim(ApiKeyAuthenticationHandler.ScopeClaim, scope)
                    || context.Resource is HttpContext http && http.GetEndpoint()?.Metadata.GetMetadata<ManagementUserRoute>() is not null
                        && context.User.Identities.Any(identity => identity.IsAuthenticated
                            && identity.AuthenticationType is UserAuthExtensions.TokenScheme or UserAuthExtensions.OperatorScheme
                            && identity.FindFirst("project_id") is null)));
        }

        return services;
    }

    internal static string BearerScheme(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            && header.AsSpan("Bearer ".Length).Trim().StartsWith("tb_", StringComparison.Ordinal)
                ? ApiKeyAuthenticationHandler.SchemeName
                : UserAuthExtensions.TokenScheme;
    }

    internal static async Task<AuthenticateResult> AuthenticateAgainAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var scope = context.RequestServices.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var handler = await scope.ServiceProvider.GetRequiredService<IAuthenticationHandlerProvider>()
                .GetHandlerAsync(context, BearerScheme(context)).ConfigureAwait(false);
            return handler is null ? AuthenticateResult.NoResult() : await handler.AuthenticateAsync().ConfigureAwait(false);
        }
    }
}

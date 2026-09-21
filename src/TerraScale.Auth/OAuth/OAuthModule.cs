namespace TerraScale.Auth.OAuth;

internal static class OAuthModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetOpenIdConfigurationEndpoint),
        typeof(GetJwksEndpoint),
        typeof(AuthorizeEndpoint),
        typeof(ExchangeTokenEndpoint),
        typeof(GetUserInfoEndpoint),
        typeof(RevokeTokenEndpoint),
        typeof(IntrospectTokenEndpoint),
        typeof(LogoutEndpoint),
        typeof(CreateOAuthClientEndpoint),
        typeof(ListOAuthClientsEndpoint),
        typeof(DeleteOAuthClientEndpoint),
    ];
}

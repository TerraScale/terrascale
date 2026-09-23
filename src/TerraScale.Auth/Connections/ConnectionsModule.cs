namespace TerraScale.Auth.Connections;

internal static class ConnectionsModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateConnectionEndpoint),
        typeof(ListConnectionsEndpoint),
        typeof(UpdateConnectionEndpoint),
        typeof(DeleteConnectionEndpoint),
        typeof(ListPublicConnectionsEndpoint),
        typeof(GetConnectionsPageEndpoint),
        typeof(AuthorizeOidcEndpoint),
        typeof(CompleteOidcEndpoint),
        typeof(AuthorizeGithubEndpoint),
        typeof(CompleteGithubEndpoint),
        typeof(SignInSamlEndpoint),
        typeof(CompleteSamlEndpoint),
    ];
}

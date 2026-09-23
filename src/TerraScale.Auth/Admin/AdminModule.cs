namespace TerraScale.Auth.Admin;

internal static class AdminModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetAdminPortalEndpoint),
        typeof(GetAdminAssetEndpoint),
        typeof(GetAdminProviderSetupEndpoint),
        typeof(CreateAdminConnectionEndpoint),
        typeof(UpdateAdminConnectionEndpoint),
    ];
}

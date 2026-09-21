namespace TerraScale.Auth.Management;

internal static class ManagementModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(UpdateInstanceSettingsEndpoint),
        typeof(GetInstanceSettingsEndpoint),
        typeof(ListUsersEndpoint),
        typeof(GetUserEndpoint),
        typeof(CreateUserEndpoint),
        typeof(UpdateUserEndpoint),
        typeof(DeleteUserEndpoint),
        typeof(ImpersonateUserEndpoint),
        typeof(ListUserSessionsEndpoint),
        typeof(RevokeSessionEndpoint),
        typeof(RevokeAllUserSessionsEndpoint),
        typeof(CreateApiKeyEndpoint),
        typeof(ListApiKeysEndpoint),
        typeof(DeleteApiKeyEndpoint),
    ];
}

namespace TerraScale.Auth.Scim;

internal static class ScimModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(CreateDirectoryEndpoint),
        typeof(ListDirectoriesEndpoint),
        typeof(DeleteDirectoryEndpoint),
        typeof(RotateDirectoryTokenEndpoint),
        typeof(ListUsersEndpoint),
        typeof(GetUserEndpoint),
        typeof(CreateUserEndpoint),
        typeof(ReplaceUserEndpoint),
        typeof(PatchUserEndpoint),
        typeof(DeleteUserEndpoint),
        typeof(ListGroupsEndpoint),
        typeof(GetGroupEndpoint),
        typeof(CreateGroupEndpoint),
        typeof(ReplaceGroupEndpoint),
        typeof(PatchGroupEndpoint),
        typeof(DeleteGroupEndpoint),
        typeof(GetServiceProviderConfigEndpoint),
        typeof(GetResourceTypesEndpoint),
        typeof(GetSchemasEndpoint),
    ];
}

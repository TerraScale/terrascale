namespace TerraScale.Auth.Passcodes;

internal static class PasscodesModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetPasscodePageEndpoint),
        typeof(RequestPasscodeEndpoint),
        typeof(VerifyPasscodeEndpoint),
        typeof(GetMfaPageEndpoint),
        typeof(VerifyMfaEndpoint),
    ];
}

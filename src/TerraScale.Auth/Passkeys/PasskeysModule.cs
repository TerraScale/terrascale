namespace TerraScale.Auth.Passkeys;

internal static class PasskeysModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetRegistrationOptionsEndpoint),
        typeof(RegisterPasskeyEndpoint),
        typeof(GetAssertionPageEndpoint),
        typeof(GetAssertionOptionsEndpoint),
        typeof(VerifyAssertionEndpoint),
        typeof(GetPasskeysPageEndpoint),
        typeof(DeleteHostedPasskeyEndpoint),
        typeof(ListUserPasskeysEndpoint),
        typeof(DeletePasskeyEndpoint),
    ];
}

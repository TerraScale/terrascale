namespace TerraScale.Auth.Users;

internal static class UsersModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(SignUpEndpoint),
        typeof(SignInEndpoint),
        typeof(SignOutEndpoint),
        typeof(GetAccountEndpoint),
        typeof(ChangePasswordEndpoint),
        typeof(RevokeAllSessionsEndpoint),
        typeof(GetSessionsPageEndpoint),
        typeof(RevokeSessionEndpoint),
        typeof(RequestMagicLinkEndpoint),
        typeof(ConsumeMagicLinkEndpoint),
        typeof(ForgotPasswordEndpoint),
        typeof(ResetPasswordEndpoint),
        typeof(VerifyEmailEndpoint),
    ];
}

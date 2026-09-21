namespace TerraScale.Auth.Mfa;

internal static class MfaModule
{
    internal static IEnumerable<Type> Endpoints =>
    [
        typeof(GetEnrollmentPageEndpoint),
        typeof(StartEnrollmentEndpoint),
        typeof(ConfirmEnrollmentEndpoint),
        typeof(GetVerificationPageEndpoint),
        typeof(VerifyMfaEndpoint),
        typeof(GetRecoveryPageEndpoint),
        typeof(ConsumeRecoveryCodeEndpoint),
        typeof(ListMfaFactorsEndpoint),
        typeof(DeleteMfaFactorEndpoint),
        typeof(RegenerateRecoveryCodesEndpoint),
    ];
}

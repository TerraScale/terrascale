
namespace TerraScale.Dashboard.Features.Auth;

public sealed record AuthFlowRow
{
    public const string OidcPurpose = "ts-dashboard-oidc";
    public const string StepUpPurpose = "ts-dashboard-step-up";
    public const string AlertSecretPurpose = "ts-dashboard-alert-secret";

    public required string Id { get; init; }

    public required string TokenHash { get; init; }

    public required string Purpose { get; init; }

    public required byte[] Nonce { get; init; }

    public required byte[] Ciphertext { get; init; }

    public required byte[] Tag { get; init; }

    public required DateTime CreatedAt { get; init; }

    public required DateTime ExpiresAt { get; init; }

    public string NonceHex => Convert.ToHexString(Nonce);

    public string CiphertextHex => Convert.ToHexString(Ciphertext);

    public string TagHex => Convert.ToHexString(Tag);
}


namespace TerraScale.Dashboard.Platform.Identity;

public sealed record SessionRow
{
    public string? OriginalVersion { get; set; }

    public const string Browser = "browser";
    public const string Credential = "credential";

    public required string Id { get; init; }

    public required string Kind { get; init; }

    public string? TokenHash { get; init; }

    public required string UserId { get; init; }

    public required string Device { get; set; }

    public required DateTime ExpiresAt { get; set; }

    public required string Version { get; set; }

    public required byte[] Nonce { get; set; }

    public required byte[] Ciphertext { get; set; }

    public required byte[] Tag { get; set; }

    public string NonceHex => Convert.ToHexString(Nonce);

    public string CiphertextHex => Convert.ToHexString(Ciphertext);

    public string TagHex => Convert.ToHexString(Tag);
}

using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Contracts;

// Every member is required: a stored document missing one is malformed and must fail closed,
// never read as zero or empty.

/// <summary>
/// One ES256 signing key of an instance (collection <c>signing_keys</c>, addressed by instance and key id). Keys are only
/// created and retired, never changed.
/// </summary>
/// <param name="InstanceId">The instance owning the key.</param>
/// <param name="MasterKeyId">Identifies, without revealing it, the master key that sealed the key.</param>
/// <param name="Id">The JWT <c>kid</c>, a ULID.</param>
/// <param name="Algorithm">Always <c>ES256</c>.</param>
/// <param name="SecretBox">The private key d ‖ X ‖ Y sealed under the master key.</param>
/// <param name="CreatedAt">Creation time in Unix seconds.</param>
/// <param name="ActivatesAt">When the key may start signing, in Unix seconds; it is published from creation.</param>
/// <param name="X">The public P-256 X coordinate, canonical base64url.</param>
/// <param name="Y">The public P-256 Y coordinate, canonical base64url.</param>
/// <param name="PublicMetadataMac">Authenticates the public metadata under a master-derived key never stored with it.</param>
public sealed record SigningKey(
    [property: JsonPropertyName("instanceId"), JsonRequired] string InstanceId,
    [property: JsonPropertyName("masterKeyId"), JsonRequired] string MasterKeyId,
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("algorithm"), JsonRequired] string Algorithm,
    [property: JsonPropertyName("secretBox"), JsonRequired] string SecretBox,
    [property: JsonPropertyName("createdAt"), JsonRequired] long CreatedAt,
    [property: JsonPropertyName("activatesAt"), JsonRequired] long ActivatesAt,
    [property: JsonPropertyName("x"), JsonRequired] string X,
    [property: JsonPropertyName("y"), JsonRequired] string Y,
    [property: JsonPropertyName("publicMetadataMac"), JsonRequired] string PublicMetadataMac);

/// <summary>An attempt counter, cooldown or lockout notice claim shared by every replica (collection <c>rate_limits</c>).</summary>
/// <param name="Window">Fixed window number (Unix seconds divided by length), last admission's Unix milliseconds for a cooldown purpose, the latest claimed lockout deadline's Unix seconds for a notice purpose, or the writer's clock in Unix milliseconds for a token bucket.</param>
/// <param name="Used">Attempts admitted in that window; one for a notice claim; the outstanding debt in milliseconds at that write for a token bucket.</param>
public sealed record AttemptBucket(
    [property: JsonPropertyName("window"), JsonRequired] long Window,
    [property: JsonPropertyName("used"), JsonRequired] int Used);

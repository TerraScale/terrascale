using System.Text.Json.Serialization;

namespace TerraScale.Auth.Contracts;

// Every member is required: a stored document missing one is malformed and must fail closed,
// never read as zero or empty.

/// <summary>
/// An instance's ES256 signing keys, oldest first; the last one signs new tokens. One document
/// per instance (collection <c>signing_key_rings</c>, id = instance id), changed only by
/// conditional writes.
/// </summary>
/// <param name="Keys">Every key that still validates tokens.</param>
public sealed record SigningKeyRing([property: JsonPropertyName("keys"), JsonRequired] IReadOnlyList<SigningKeyRecord> Keys);

/// <summary>One persisted signing key.</summary>
/// <param name="Id">The JWT <c>kid</c>.</param>
/// <param name="Algorithm">Always <c>ES256</c>.</param>
/// <param name="SecretBox">The private key d ‖ X ‖ Y sealed under the master key.</param>
/// <param name="CreatedAt">Creation time in Unix seconds.</param>
public sealed record SigningKeyRecord(
    [property: JsonPropertyName("id"), JsonRequired] string Id,
    [property: JsonPropertyName("algorithm"), JsonRequired] string Algorithm,
    [property: JsonPropertyName("secretBox"), JsonRequired] string SecretBox,
    [property: JsonPropertyName("createdAt"), JsonRequired] long CreatedAt);

/// <summary>An attempt counter, cooldown or lockout notice claim shared by every replica (collection <c>rate_limits</c>).</summary>
/// <param name="Window">Fixed window number (Unix seconds divided by length), last admission's Unix milliseconds for a cooldown purpose, or the latest claimed lockout deadline's Unix seconds for a notice purpose.</param>
/// <param name="Used">Attempts admitted in that window; one for a notice claim.</param>
public sealed record AttemptBucket(
    [property: JsonPropertyName("window"), JsonRequired] long Window,
    [property: JsonPropertyName("used"), JsonRequired] int Used);

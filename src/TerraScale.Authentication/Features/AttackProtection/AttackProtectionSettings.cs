using System.Text.Json.Serialization;

namespace TerraScale.Authentication.Features.AttackProtection;

public sealed record AttackProtectionSettings(bool BotChallenge = false, bool BreachedPasswords = true, bool AccountLockout = true, bool NewDeviceEmail = true);

public sealed record AttackProtectionStatus(bool BotChallenge, bool BreachedPasswords, bool AccountLockout, bool NewDeviceEmail, bool ChallengeConfigured);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(AttackProtectionSettings))]
[JsonSerializable(typeof(TurnstileResponse))]
internal sealed partial class AttackProtectionJson : JsonSerializerContext;

internal sealed record TurnstileResponse(bool Success);

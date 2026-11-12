using System.Text.Json.Serialization;

namespace TerraScale.Dashboard.Platform.TsAuth;

public sealed record TsAuthAttackProtection(
    [property: JsonPropertyName("bot_challenge")] bool BotChallenge = false,
    [property: JsonPropertyName("breached_passwords")] bool BreachedPasswords = true,
    [property: JsonPropertyName("account_lockout")] bool AccountLockout = true,
    [property: JsonPropertyName("new_device_email")] bool NewDeviceEmail = true,
    [property: JsonPropertyName("challenge_configured"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ChallengeConfigured = false);

namespace TerraScale.Dashboard.Features.Profile.Models;

/// <summary>An in-progress TOTP enrollment: the secret is shown once until
/// the verification code completes (or abandons) the flow.</summary>
public sealed record TotpEnrollment(string Token, string Secret);

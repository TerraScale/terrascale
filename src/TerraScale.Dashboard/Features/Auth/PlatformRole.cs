namespace TerraScale.Dashboard.Features.Auth
{
    public interface IPlatformRoleGateway
    {
        // The caller must obtain this grant through the persisted forced rotation;
        // a cached access token does not prove the user's current platform role.
        Task<string?> PlatformRoleAsync(SessionRecord freshlyRotatedSession, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : IPlatformRoleGateway
    {
        public async Task<string?> PlatformRoleAsync(SessionRecord freshlyRotatedSession, CancellationToken cancellationToken)
        {
            var client = Client();
            var discovery = await client.Oidc.GetDiscoveryDocumentAsync(cancellationToken);
            try
            {
                var issuer = TrustedIssuer(discovery.Issuer);
                var claims = AuthJwtClaims.Read(freshlyRotatedSession.AccessToken, issuer, issuer,
                    await client.Oidc.GetJwksAsync(cancellationToken), time.GetUtcNow(), "at+jwt", 8192);
                if (claims.GetProperty("sub").GetString() != freshlyRotatedSession.UserId ||
                    claims.GetProperty("email_verified").ValueKind != System.Text.Json.JsonValueKind.True ||
                    claims.GetProperty("email").GetString()?.Trim().ToLowerInvariant() != freshlyRotatedSession.Email.Trim().ToLowerInvariant())
                {
                    return null;
                }

                return claims.TryGetProperty("platform_role", out var role) && role.ValueKind == System.Text.Json.JsonValueKind.String && role.GetString() == "admin"
                    ? "admin" : null;
            }
            catch (Exception error) when (error is SignInRejectedException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            { return null; }
        }
    }
}

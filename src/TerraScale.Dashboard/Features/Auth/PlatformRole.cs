namespace TerraScale.Dashboard.Features.Auth
{
    public interface IPlatformRoleGateway
    {
        Task<string?> PlatformRoleAsync(SessionRecord freshlyRotatedSession, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : IPlatformRoleGateway
    {
        public async Task<string?> PlatformRoleAsync(SessionRecord freshlyRotatedSession, CancellationToken cancellationToken)
        {
            var client = Client();
            var discovery = await MetadataAsync(client, cancellationToken);
            try
            {
                var issuer = TrustedIssuer(discovery.Issuer);
                var claims = AuthJwtClaims.Read(freshlyRotatedSession.AccessToken, issuer, issuer,
                    await SigningKeysAsync(client, freshlyRotatedSession.AccessToken, cancellationToken), time.GetUtcNow(), "at+jwt", 8192);
                if (claims.GetProperty("sub").GetString() != freshlyRotatedSession.UserId ||
                    claims.GetProperty("email_verified").ValueKind != System.Text.Json.JsonValueKind.True ||
                    !string.Equals(claims.GetProperty("email").GetString()?.Trim(), freshlyRotatedSession.Email.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (!claims.TryGetProperty("platform_role", out var role) || role.ValueKind != System.Text.Json.JsonValueKind.String || role.GetString() != "admin")
                {
                    return null;
                }
            }
            catch (Exception error) when (error is SignInRejectedException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
            { return null; }

            return await IsActiveAsync(freshlyRotatedSession, cancellationToken) ? "admin" : null;
        }
    }
}

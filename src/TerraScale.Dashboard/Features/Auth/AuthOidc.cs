namespace TerraScale.Dashboard.Features.Auth
{
    // The dashboard is a plain OIDC client of ts-auth: sign-in, sign-up, password
    // reset and MFA happen on ts-auth's hosted pages, never in the dashboard.
    public interface IAuthOidcGateway
    {
        Task<string> AuthorizationUrlAsync(OidcFlow flow, CancellationToken cancellationToken);
        Task<SessionRecord> ExchangeAsync(string code, OidcFlow flow, CancellationToken cancellationToken);
    }

    public interface IAuthLogoutGateway
    {
        Task<string> EndSessionUrlAsync(string dashboardOrigin, string? idToken, CancellationToken cancellationToken);
    }

    // TokenName is set when the flow mints a personal access token from a fresh,
    // independent ts-auth grant instead of starting a browser session; Mcp when the
    // grant answers an MCP client's OAuth authorization request.
    public sealed record OidcFlow(string State, string Nonce, string Verifier, string ReturnTo, string RedirectUri, DateTimeOffset ExpiresAt,
        string TokenName = "", int TokenDays = 0, TerraScale.Dashboard.Platform.Mcp.McpAuthorization? Mcp = null)
    {
        public StepUpIntent? StepUp { get; init; }
        public override string ToString()
        {
            return "Dashboard OIDC flow [REDACTED]";
        }
    }
}

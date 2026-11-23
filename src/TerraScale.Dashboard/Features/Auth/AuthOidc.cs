namespace TerraScale.Dashboard.Features.Auth
{
    public interface IAuthOidcGateway
    {
        Task<string> AuthorizationUrlAsync(OidcFlow flow, CancellationToken cancellationToken);
        Task<ExchangeResult> ExchangeAsync(string code, OidcFlow flow, DateTimeOffset? credentialExpiresAt, CancellationToken cancellationToken);
    }

    public interface IAuthLogoutGateway
    {
        Task<string> EndSessionUrlAsync(string dashboardOrigin, string? idToken, CancellationToken cancellationToken);
    }

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

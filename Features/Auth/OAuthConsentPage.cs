using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

public sealed record OAuthConsentPageModel(
    bool Authenticated,
    bool ValidRequest,
    string ClientId,
    string ClientName,
    string[] Scopes,
    string? Claims,
    string? Error,
    string? Outcome) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

public sealed class OAuthConsentPageEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Get("/oauth/consent");
        Summary("Renders the OAuth authorization consent screen");
        Tags("Pages");
        AllowAnonymous();
    }

    public override Task<IResult> HandleAsync(CancellationToken ct)
    {
        var query = HttpContext.Request.Query;
        var authenticated = DashboardAuthMiddleware.IsAuthenticated(HttpContext);
        var clientId = query["client_id"].ToString();
        var valid = clientId.Length > 0;
        var client = store.DemoOAuthClient;
        var model = new OAuthConsentPageModel(
            Authenticated: authenticated,
            ValidRequest: valid,
            ClientId: valid ? clientId : client.ClientId,
            ClientName: client.ClientName,
            Scopes: valid ? [.. store.DemoOAuthScopes] : [],
            Claims: query["claims"].ToString() is { Length: > 0 } claims ? claims : null,
            Error: query["error"].FirstOrDefault(),
            Outcome: query["outcome"].FirstOrDefault());
        return Task.FromResult<IResult>(OAuthConsentSlice.Create(model));
    }
}

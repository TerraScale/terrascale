using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.Auth;

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
        var clientId = query["client_id"].ToString();
        var valid = clientId.Length > 0;

        // Unknown clients fall back to the generic label, and scopes come
        // from the query string, exactly like the parsed authorize request.
        var known = store.DemoOAuthClient;
        var model = new OAuthConsentPageModel(
            ValidRequest: valid,
            ClientId: valid ? clientId : known.ClientId,
            ClientName: clientId == known.ClientId ? known.ClientName : "External application",
            Scopes: query["scope"].ToString() is { Length: > 0 } scope
                ? [.. scope.Split(' ', StringSplitOptions.RemoveEmptyEntries)]
                : [.. store.DemoOAuthScopes],
            Claims: query["claims"].ToString() is { Length: > 0 } claims ? claims : null,
            Error: query["error"].FirstOrDefault(),
            Outcome: query["outcome"].FirstOrDefault());
        return Task.FromResult<IResult>(OAuthConsentSlice.Create(model));
    }
}

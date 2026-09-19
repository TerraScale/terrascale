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
        // v1 sends anonymous visitors to login with a next return path.
        if (!DashboardAuthMiddleware.IsAuthenticated(HttpContext))
        {
            var target = HttpContext.Request.Path.Value + (HttpContext.Request.QueryString.Value ?? "");
            return Task.FromResult<IResult>(Results.Redirect("/login?next=" + Uri.EscapeDataString(target)));
        }

        var query = HttpContext.Request.Query;
        var rawQuery = HttpContext.Request.QueryString.Value ?? "";
        var clientId = query["client_id"].ToString();
        var valid = clientId.Length > 0;
        var error = !valid
            ? rawQuery.Length == 0
                ? "The OAuth authorization request is missing."
                : "The OAuth authorization request has no valid client."
            : null;

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
            Error: error,
            Outcome: query["outcome"].FirstOrDefault());
        return Task.FromResult<IResult>(OAuthConsentSlice.Create(model));
    }
}

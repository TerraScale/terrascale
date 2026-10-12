using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Shared.UI;
namespace TerraScale.Dashboard.Features.Auth;

public sealed record ConsentPage(DocumentModel Document, bool Agent = false, bool Submitted = false)
{
    public string Title => Agent ? "Approve agent access" : "Authorize application";
    public string Description => Agent ? "The dashboard cannot verify agent approval requests yet." : "The dashboard cannot verify OAuth authorization requests yet.";
    public string Message => Agent ? (Submitted ? "The approval was rejected because no verifiable agent request is available. No access was granted." : "The approval request is missing or cannot be verified. No access was granted.") :
        Submitted ? "The authorization request was rejected. No access was granted." : "The authorization request is missing or cannot be verified. No access was granted.";
}

public static class ConsentEndpoints
{
    public static void MapConsent(this WebApplication app)
    {
        app.MapGet("/oauth/consent", async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
        {
            var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
            if (context is null) return (IResult)HttpRedirects.SeeOther("/login?returnTo=" + Uri.EscapeDataString(Csrf.LocalRedirect(http.Request.Path + http.Request.QueryString)));
            // Neither SDK exposes an owning, verifiable consent-request proof.
            // Query parameters can never authorize an OAuth client themselves.
            return ConsentView.Create(new(new("Authorize application", DocumentModel.NonceFor(http))));
        });
        app.MapPost("/oauth/consent/submit", (Delegate)(Func<HttpContext, Task<IResult>>)(http => SubmitAsync(http, false)));
        app.MapGet("/agent/approve", (HttpContext http) => ConsentView.Create(new(new("Approve agent access", DocumentModel.NonceFor(http)), Agent: true)));
        app.MapPost("/agent/approve/submit", (Delegate)(Func<HttpContext, Task<IResult>>)(http => SubmitAsync(http, true)));
    }

    private static async Task<IResult> SubmitAsync(HttpContext http, bool agent)
    {
        if (!http.Request.HasFormContentType) return Results.BadRequest("Invalid authorization form.");
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
        return ConsentView.Create(new(new(agent ? "Approve agent access" : "Authorize application", DocumentModel.NonceFor(http)), Agent: agent, Submitted: true));
    }
}

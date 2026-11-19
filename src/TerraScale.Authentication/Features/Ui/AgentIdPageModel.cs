namespace TerraScale.Authentication.Features.Ui;

public enum AgentIdStep
{
    Waiting,
    Expired,
}

public sealed record AgentIdPageModel(AgentIdStep Step, string Client, string? ProofUrl = null)
{
    public const string Title = "Sign in as an agent";

    public string Nonce { get; init; } = string.Empty;

    public static IResult Render(HttpContext context, AgentIdPageModel model, int status = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.CacheControl = "no-store";
        var page = AgentIdPage.Create(model with { Nonce = nonce });
        page.StatusCode = status;
        return page;
    }
}

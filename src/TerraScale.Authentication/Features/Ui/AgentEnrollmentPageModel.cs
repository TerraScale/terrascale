using TerraScale.Authentication.Features.ProjectTenants;

namespace TerraScale.Authentication.Features.Ui;

public enum AgentEnrollmentStep
{
    Enter,
    Confirm,
    Approved,
    Denied,
}

public sealed record AgentEnrollmentPageModel(string Tenant, AgentEnrollmentStep Step, string? UserCode = null, string? Error = null)
{
    public const string Title = "Approve an AI agent";

    public string Nonce { get; init; } = string.Empty;

    public HostedTheme Theme { get; init; } = HostedTheme.Platform;

    public string? Account { get; init; }

    public string? AgentName { get; init; }

    public IReadOnlyList<string> Scopes { get; init; } = [];

    public bool SelfSubject { get; init; }

    public string? Fingerprint { get; init; }

    public string ProjectName => this.Theme.Name ?? this.Tenant;

    public string Action => "/ui/" + this.Tenant + "/agent-enrollments";

    public static IResult Render(HttpContext context, AgentEnrollmentPageModel model, int status = StatusCodes.Status200OK)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(model);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        context.Response.Headers["Referrer-Policy"] = "strict-origin";
        context.Response.Headers.CacheControl = "no-store";
        var page = AgentEnrollmentPage.Create(model with { Nonce = nonce, Theme = HostedTheme.Of(context) });
        page.StatusCode = status;
        return page;
    }
}

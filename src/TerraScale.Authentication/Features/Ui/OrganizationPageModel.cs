using TerraScale.Authentication.Features.ProjectOrganizations;

namespace TerraScale.Authentication.Features.Ui;

public sealed record OrganizationPageModel(string Tenant, string Nonce, string Title, string? Email = null, string? Invitation = null,
    string? Next = null, IReadOnlyList<OrganizationChoice>? Choices = null, IReadOnlyList<OrganizationClaims>? Suggestions = null, string? Done = null)
{
    public static IResult Render(HttpContext context, OrganizationPageModel model)
    {
        ArgumentNullException.ThrowIfNull(context);
        var nonce = BrowserSecurity.NewNonce();
        BrowserSecurity.ApplyPageHeaders(context.Response, nonce);
        context.Response.Headers["Referrer-Policy"] = "strict-origin";
        return OrganizationPage.Create(model with { Nonce = nonce });
    }
}

public sealed record OrganizationChoice(string Slug, string Role, string Url);

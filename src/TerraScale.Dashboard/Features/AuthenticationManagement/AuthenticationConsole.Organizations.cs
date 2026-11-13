using System.Globalization;
using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Common.Accounts;
using TerraScale.Dashboard.Common.UI;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

public static partial class AuthenticationConsole
{
    private static async Task<AuthenticationPage> OrganizationsAsync(HttpContext http, AuthenticationPage page, ITsAuthProjects projects, CancellationToken cancellationToken)
    {
        var project = page.Context.Project!.Id;
        var session = page.Context.Session;
        var id = http.Request.Query["org"].ToString();
        if (id.Length == 0)
        {
            var list = await projects.ListOrganizationsAsync(session, project, 25, Value(page.After), cancellationToken);
            return page with { Organizations = list.Items, Next = list.Next };
        }

        var org = await projects.GetOrganizationAsync(session, project, id, cancellationToken);
        var view = http.Request.Query["view"].ToString();
        page = page with { SelectedOrganization = org, OrganizationView = view };
        if (view == "members")
        {
            var members = await projects.ListOrganizationMembersAsync(session, project, id, 25, Value(page.After), cancellationToken);
            return page with { OrganizationMembers = members.Items, Next = members.Next };
        }

        if (view == "invitations")
        {
            var invitations = await projects.ListOrganizationInvitationsAsync(session, project, id, 25, Value(page.After), cancellationToken);
            return page with { OrganizationInvitations = invitations.Items, Next = invitations.Next };
        }

        return view == "domains" ? page with { OrganizationDomains = (await projects.ListOrganizationDomainsAsync(session, project, id, cancellationToken)).Items } : page;
    }

    private static async Task<IResult> OrganizationPostAsync(HttpContext http, AccountContext context, ITsAuthProjects projects, IFormCollection form, string[] parts, CancellationToken cancellationToken)
    {
        var project = context.Project!.Id;
        var session = context.Session;
        var org = parts.Length > 1 ? parts[1] : "";
        var view = "";
        switch (parts)
        {
            case ["organizations"]:
                org = (await projects.CreateOrganizationAsync(session, project, OrganizationInput(form), cancellationToken)).Id;
                break;
            case ["organizations", _, "update"]:
                await projects.UpdateOrganizationAsync(session, project, org, OrganizationInput(form), cancellationToken);
                break;
            case ["organizations", _, "delete"]:
                await projects.DeleteOrganizationAsync(session, project, org, cancellationToken);
                return HttpRedirects.SeeOther("/auth/organizations");
            case ["organizations", _, "members"]:
                await projects.AddOrganizationMemberAsync(session, project, org, new(form["user_id"], form["role"].ToString()), cancellationToken);
                view = "members";
                break;
            case ["organizations", _, "members", var user, "update"]:
                await projects.UpdateOrganizationMemberAsync(session, project, org, user, new(null, form["role"].ToString()), cancellationToken);
                view = "members";
                break;
            case ["organizations", _, "members", var user, "delete"]:
                await projects.DeleteOrganizationMemberAsync(session, project, org, user, cancellationToken);
                view = "members";
                break;
            case ["organizations", _, "invitations"]:
                await projects.CreateOrganizationInvitationAsync(session, project, org, new(form["email"].ToString(), form["role"].ToString()), cancellationToken);
                view = "invitations";
                break;
            case ["organizations", _, "invitations", var invitation, "revoke"]:
                await projects.RevokeOrganizationInvitationAsync(session, project, org, invitation, cancellationToken);
                view = "invitations";
                break;
            case ["organizations", _, "domains"]:
                await projects.AddOrganizationDomainAsync(session, project, org, new(form["domain"].ToString()), cancellationToken);
                view = "domains";
                break;
            case ["organizations", _, "domains", var domain, "verify"]:
                await projects.VerifyOrganizationDomainAsync(session, project, org, domain, cancellationToken);
                view = "domains";
                break;
            case ["organizations", _, "domains", var domain, "delete"]:
                await projects.DeleteOrganizationDomainAsync(session, project, org, domain, cancellationToken);
                view = "domains";
                break;
            default:
                return Results.NotFound();
        }

        return HttpRedirects.SeeOther("/auth/organizations?org=" + Uri.EscapeDataString(org) + "&view=" + view);
    }

    private static TsAuthOrganizationRequest OrganizationInput(IFormCollection form)
    {
        int? maximum = null;
        if (form["max_members"].ToString() is { Length: > 0 } text)
        {
            maximum = int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
                ? number : throw new AccountAccessException("Member limit must be a positive integer.", 422);
        }

        return new(form["name"].ToString(), form["slug"].ToString(), Value(form["admin_user_id"].ToString()), Value(form["image_url"].ToString()),
            OrganizationMetadata(form["public_metadata"].ToString()), OrganizationMetadata(form["private_metadata"].ToString()), maximum, form["domain_join"].ToString());
    }

    private static JsonElement OrganizationMetadata(string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value.Length == 0 ? "{}" : value);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new AccountAccessException("Metadata must be a JSON object.", 422);
        }
    }
}

public sealed partial record AuthenticationPage
{
    public IReadOnlyList<TsAuthOrganization> Organizations { get; init; } = [];
    public TsAuthOrganization? SelectedOrganization { get; init; }
    public string OrganizationView { get; init; } = "";
    public IReadOnlyList<TsAuthOrganizationMembership> OrganizationMembers { get; init; } = [];
    public IReadOnlyList<TsAuthOrganizationInvitation> OrganizationInvitations { get; init; } = [];
    public IReadOnlyList<TsAuthOrganizationDomain> OrganizationDomains { get; init; } = [];
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Metadata;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Mfa;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Hosting;
using TerraScale.Endpoints;

namespace TerraScale.Authentication.Features.Platform;

/// <summary>A body that names something.</summary>
/// <param name="Name">1 to 128 characters after trimming, with a letter or digit and no control characters.</param>
public sealed record NameRequest([property: JsonPropertyName("name")] string? Name);

/// <summary>A body that sets a role.</summary>
/// <param name="Role">The role.</param>
public sealed record RoleRequest([property: JsonPropertyName("role")] string? Role);

/// <summary>An organization.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Role">The caller's organization role; null for a collaborator.</param>
/// <param name="RequireMfa">Whether members need an active second factor to reach it.</param>
public sealed record OrganizationBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("require_mfa")] bool RequireMfa);

/// <summary>A body that sets an organization's second-factor policy.</summary>
/// <param name="RequireMfa">Whether members need an active second factor.</param>
public sealed record MfaPolicyRequest([property: JsonPropertyName("require_mfa")] bool? RequireMfa);

/// <summary>The caller's second-factor state.</summary>
/// <param name="Enabled">Whether an authenticator app (TOTP) is active.</param>
/// <param name="RecoveryCodesRemaining">Unused, unexpired recovery codes.</param>
/// <param name="ManageUrl">The hosted page that enrols and removes the factor.</param>
public sealed record AccountMfaBody(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("recovery_codes_remaining")] int RecoveryCodesRemaining,
    [property: JsonPropertyName("manage_url")] string ManageUrl);

/// <summary>A page of organizations.</summary>
/// <param name="Items">The organizations.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record OrganizationPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<OrganizationBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>An organization member.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Email">Its email.</param>
/// <param name="Role">Its organization role.</param>
/// <param name="JoinedAt">When it joined.</param>
public sealed record MemberBody(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("joined_at")] DateTimeOffset JoinedAt);

/// <summary>A page of members.</summary>
/// <param name="Items">The members.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record MemberPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<MemberBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>A project.</summary>
/// <param name="Id">Its id, also its tenant.</param>
/// <param name="OrganizationId">Its organization.</param>
/// <param name="Name">Its name.</param>
/// <param name="Issuer">Its end users' issuer, <c>{public origin}/{id}</c>.</param>
/// <param name="CreatedBy">The creating user.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="Role">The caller's effective project role.</param>
/// <param name="AllowedCidrs">The canonical CIDRs every API key token of the project must be used from; empty allows any address.</param>
public sealed record ProjectBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("created_by")] string CreatedBy,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("allowed_cidrs")] IReadOnlyList<string> AllowedCidrs);

/// <summary>A page of projects.</summary>
/// <param name="Items">The projects.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record ProjectPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<ProjectBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary>An explicit project role.</summary>
/// <param name="UserId">The user.</param>
/// <param name="Email">Its email.</param>
/// <param name="Role">Its project role.</param>
/// <param name="GrantedAt">When it was granted.</param>
public sealed record ProjectMemberBody(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("granted_at")] DateTimeOffset GrantedAt);

/// <summary>A page of project members.</summary>
/// <param name="Items">The project members.</param>
/// <param name="Next">The next page's <c>after</c>, or null.</param>
public sealed record ProjectMemberPageBody(
    [property: JsonPropertyName("items")] IReadOnlyList<ProjectMemberBody> Items,
    [property: JsonPropertyName("next")] string? Next);

/// <summary><c>POST /v1/terrascale/organizations</c>.</summary>
public sealed class CreateOrganizationEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(Organizations);
        this.Document("create_organization", "Create an organization", "The caller becomes its first member, with the owner role.", "Organizations", typeof(NameRequest), StatusCodes.Status201Created, typeof(OrganizationBody), roleChecked: false, scoped: false);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var name = PlatformDirectory.Name((await this.BodyAsync(PlatformApiJson.Default.NameRequest, cancellationToken)).Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        var organization = await platform.CreateOrganizationAsync(caller, name, cancellationToken);
        return Results.Json(Map(organization, Roles.Owner), PlatformApiJson.Default.OrganizationBody, statusCode: StatusCodes.Status201Created)
            .WithLocation($"{Organizations}/{organization.Id}");
    }
}

/// <summary><c>GET /v1/terrascale/organizations</c>.</summary>
public sealed class ListOrganizationsEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Organizations);
        this.Document("list_organizations", "List the caller's organizations", "Organizations the caller is a member of, and those where it is a collaborator on a project (role null), in id order.", "Organizations", null, StatusCodes.Status200OK, typeof(OrganizationPageBody), paged: true, roleChecked: false, scoped: false);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.OrganizationsAsync(caller.Id, limit, after, cancellationToken);
        return Results.Json(new OrganizationPageBody([.. page.Items.Select(item => Map(item.Organization, item.Role))], page.Next), PlatformApiJson.Default.OrganizationPageBody);
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}</c>.</summary>
public sealed class GetOrganizationEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Organization);
        this.Document("get_organization", "Get an organization", "Members and collaborators. A caller who is neither gets 404, as for an unknown id.", "Organizations", null, StatusCodes.Status200OK, typeof(OrganizationBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var access = await platform.OrganizationAccessAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return Results.Json(Map(access.Organization.Value, access.Role), PlatformApiJson.Default.OrganizationBody);
    }
}

/// <summary><c>PATCH /v1/terrascale/organizations/{organization}</c>.</summary>
public sealed class RenameOrganizationEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(Organization);
        this.Document("rename_organization", "Rename an organization", "Needs the admin organization role.", "Organizations", typeof(NameRequest), StatusCodes.Status200OK, typeof(OrganizationBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var name = PlatformDirectory.Name((await this.BodyAsync(PlatformApiJson.Default.NameRequest, cancellationToken)).Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        var organization = await platform.RenameOrganizationAsync(caller.Id, this.RouteId("organization"), name, cancellationToken);
        var access = await platform.OrganizationAccessAsync(caller.Id, organization.Id, cancellationToken);
        return Results.Json(Map(organization, access.Role), PlatformApiJson.Default.OrganizationBody);
    }
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/mfa-policy</c>.</summary>
public sealed class SetMfaPolicyEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Organization}/mfa-policy");
        this.Document("set_organization_mfa_policy", "Require two-factor authentication",
            "Needs the owner role. With require_mfa true, members without an active second factor get 403 mfa_required from the organization's endpoints and token exchange until they enrol; turning it on needs the caller's own factor (409 mfa_not_enrolled). Recorded in the audit log.",
            "Organizations", typeof(MfaPolicyRequest), StatusCodes.Status200OK, typeof(OrganizationBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var require = (await this.BodyAsync(PlatformApiJson.Default.MfaPolicyRequest, cancellationToken)).RequireMfa ?? throw Invalid("require_mfa must be true or false.");
        var organization = await platform.SetMfaPolicyAsync(caller.Id, this.RouteId("organization"), require, cancellationToken);
        return Results.Json(Map(organization, Roles.Owner), PlatformApiJson.Default.OrganizationBody);
    }
}

/// <summary><c>GET /v1/terrascale/account/mfa</c>.</summary>
public sealed class AccountMfaEndpoint(TotpCredentials mfa, AuthOptions options, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/v1/terrascale/account/mfa");
        this.Document("get_account_mfa", "Get two-factor status", "The caller's own second-factor state and the hosted page that manages it.", "Members", null, StatusCodes.Status200OK, typeof(AccountMfaBody), roleChecked: false, scoped: false);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (enabled, remaining) = await mfa.StatusAsync(caller.InstanceId, caller.Id, cancellationToken);
        return Results.Json(new AccountMfaBody(enabled, remaining, new Uri(options.PublicOrigin, $"/ui/{OidcIssuer.Slug}/security").AbsoluteUri), PlatformApiJson.Default.AccountMfaBody);
    }
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}</c>.</summary>
public sealed class DeleteOrganizationEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Organization);
        this.Document("delete_organization", "Delete an organization", "Needs the owner role. 409 not_empty while it has projects. Logically removes the auth organization and durably retries its remaining auth records. This response does not certify physical erasure or other services' data.", "Organizations", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.DeleteOrganizationAsync(caller.Id, this.RouteId("organization"), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/members</c>.</summary>
public sealed class ListMembersEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/members");
        this.Document("list_members", "List organization members", "Needs any organization role. In user id order.", "Members", null, StatusCodes.Status200OK, typeof(MemberPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.MembersAsync(caller.Id, this.RouteId("organization"), limit, after, cancellationToken);
        return Results.Json(new MemberPageBody([.. page.Items.Select(item => Map(item.Member, item.Email))], page.Next), PlatformApiJson.Default.MemberPageBody);
    }
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/members/{user}/role</c>.</summary>
public sealed class ChangeMemberRoleEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Organization}/members/{{user}}/role");
        this.Document("change_member_role", "Change a member's organization role", "Needs admin, and owner when the member is or becomes an owner. 409 last_owner when it would demote the last owner.", "Members", typeof(RoleRequest), StatusCodes.Status200OK, typeof(MemberBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var role = (await this.BodyAsync(PlatformApiJson.Default.RoleRequest, cancellationToken)).Role;
        if (role is null || !Roles.Organization.Contains(role))
        {
            throw Invalid("role must be viewer, member, admin or owner.");
        }

        var (member, email) = await platform.ChangeRoleAsync(caller.Id, this.RouteId("organization"), this.RouteId("user"), role, cancellationToken);
        return Results.Json(Map(member, email), PlatformApiJson.Default.MemberBody);
    }
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/members/{user}</c>.</summary>
public sealed class RemoveMemberEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Organization}/members/{{user}}");
        this.Document("remove_member", "Remove a member", "Needs admin, and owner to remove an owner. 409 last_owner for the last owner. Also removes the user's project roles in the organization.", "Members", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.RemoveAsync(caller.Id, this.RouteId("organization"), this.RouteId("user"), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/leave</c>.</summary>
public sealed class LeaveOrganizationEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/leave");
        this.Document("leave_organization", "Leave an organization", "Members and collaborators. Removes the caller's organization and project roles. 409 last_owner for the last owner.", "Members", null, StatusCodes.Status204NoContent, null, conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.RemoveAsync(caller.Id, this.RouteId("organization"), caller.Id, cancellationToken);
        return Results.NoContent();
    }
}

/// <summary><c>DELETE /v1/terrascale/account</c>.</summary>
public sealed class DeleteAccountEndpoint(AccountDeletion accounts, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete("/v1/terrascale/account");
        this.Document("delete_account", "Delete your account", "Once logical deletion completes (204), revokes auth access and frees the caller's email and durably retries remaining auth cleanup. This response does not certify physical erasure, backups or deletion of other services' data. Organizations where the caller is the only member are deleted with their projects and databases, including replicas in every region. TerraBase permanently fences each project and purges its data before accepting active-copy erasure; the account waits for every catalog holder. Backup erasure remains durable background work under the database deletion deadlines. Shared organizations are left. 202 while a project closure is pending: the account stays until it finishes in the background, and new sign-ins are temporarily refused until completion or cancellation; cancellation restores sign-in without changing credentials. The background worker resumes without another submit. Optional X-TerraScale-Deletion-Receipt: a canonical 32-byte random base64url capability, retained for 15 days to read status after tokens stop working. Reuse it only for this closure generation; 409 deletion_refused otherwise. A 409 (last_owner, not_empty, closure_conflict, binding_conflict) before anything was removed ends the request: the account, its memberships and organizations stay, but project closures it already started are not rolled back (TerraBase fences are permanent) and finish on their own. Once removals began, a 409 only pauses the deletion until its cause is resolved.", "Members", null, StatusCodes.Status204NoContent, null, conflict: true, roleChecked: false, scoped: false);
        Produces(StatusCodes.Status202Accepted);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await accounts.DeleteAsync(caller, cancellationToken, DeletionReceiptHeader.Read(this.HttpContext.Request)) ? Results.NoContent() : Results.Accepted();
}

/// <summary><c>POST /v1/terrascale/organizations/{organization}/projects</c>.</summary>
public sealed class CreateProjectEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post($"{Organization}/projects");
        this.Document("create_project", "Create a project", "Needs the member organization role. The caller becomes the project's explicit admin. 409 name_taken.", "Projects", typeof(NameRequest), StatusCodes.Status201Created, typeof(ProjectBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var name = PlatformDirectory.Name((await this.BodyAsync(PlatformApiJson.Default.NameRequest, cancellationToken)).Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        var project = await platform.CreateProjectAsync(caller.Id, this.RouteId("organization"), name, cancellationToken);
        return Results.Json(Map(project, Roles.Admin), PlatformApiJson.Default.ProjectBody, statusCode: StatusCodes.Status201Created)
            .WithLocation($"{Organizations}/{project.OrganizationId}/projects/{project.Id}");
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/projects</c>.</summary>
public sealed class ListProjectsEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Organization}/projects");
        this.Document("list_projects", "List projects", "Every project for organization members, the granted ones for collaborators; in id order.", "Projects", null, StatusCodes.Status200OK, typeof(ProjectPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.ProjectsAsync(caller.Id, this.RouteId("organization"), limit, after, cancellationToken);
        return Results.Json(new ProjectPageBody([.. page.Items.Select(item => Map(item.Project, item.Role))], page.Next), PlatformApiJson.Default.ProjectPageBody);
    }
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/projects/{project}</c>.</summary>
public sealed class GetProjectEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(Project);
        this.Document("get_project", "Get a project", "Needs an effective project role. Others get 404.", "Projects", null, StatusCodes.Status200OK, typeof(ProjectBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var access = await platform.ProjectAccessAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), cancellationToken);
        return Results.Json(Map(access.Project.Value, access.Role), PlatformApiJson.Default.ProjectBody);
    }
}

/// <summary><c>PATCH /v1/terrascale/organizations/{organization}/projects/{project}</c>.</summary>
public sealed class RenameProjectEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Patch(Project);
        this.Document("rename_project", "Rename a project", "Needs the project's admin role. 409 name_taken.", "Projects", typeof(NameRequest), StatusCodes.Status200OK, typeof(ProjectBody), conflict: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var name = PlatformDirectory.Name((await this.BodyAsync(PlatformApiJson.Default.NameRequest, cancellationToken)).Name) ?? throw Invalid("name must be 1 to 128 characters with a letter or digit.");
        var access = await platform.RenameProjectAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), name, cancellationToken);
        return Results.Json(Map(access.Project.Value, access.Role), PlatformApiJson.Default.ProjectBody);
    }
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/projects/{project}/allowed-cidrs</c>.</summary>
public sealed class SetProjectAllowedCidrsEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Project}/allowed-cidrs");
        this.Document("set_project_allowed_cidrs", "Set a project's IP allowlist", "Needs the project's admin role. Replaces the allowlist every API key token of the project is held to, on top of the key's own: up to 32 IPv4 or IPv6 CIDRs or addresses, empty allows any address. TerraBase answers 403 ip_not_allowed otherwise; a change applies to tokens issued afterwards, within 60 seconds.", "Projects", typeof(AllowedCidrsRequest), StatusCodes.Status200OK, typeof(ProjectBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var body = await this.BodyAsync(PlatformApiJson.Default.AllowedCidrsRequest, cancellationToken);
        var cidrs = (body.AllowedCidrs is null ? null : ApiKeys.NormalizeCidrs(body.AllowedCidrs))
            ?? throw Invalid("allowed_cidrs must be up to 32 IPv4 or IPv6 CIDRs or addresses.");
        var access = await platform.SetProjectAllowedCidrsAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), cidrs, cancellationToken);
        return Results.Json(Map(access.Project.Value, access.Role), PlatformApiJson.Default.ProjectBody);
    }
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/projects/{project}</c>.</summary>
public sealed class DeleteProjectEndpoint(Closures closures, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(Project);
        this.Document("delete_project", "Delete a project", "Needs the project's admin role. Commits a closure intent, then asks TerraBase to fence the project permanently; the auth project is removed (204) only after every TerraBase region confirmed it empty, then its project roles, API keys and all invitation statuses are durably retried. 202 while TerraBase has not confirmed: the project refuses new roles, API keys and invitations and the closure finishes in the background; repeating the request resumes it. 409 not_empty, closure_conflict or binding_conflict, changing nothing, when TerraBase refuses; once TerraBase showed the fence, a later refusal keeps the closure pending instead. This does not certify physical erasure.", "Projects", null, StatusCodes.Status204NoContent, null, conflict: true);
        Produces(StatusCodes.Status202Accepted);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken) =>
        await closures.DeleteProjectAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), cancellationToken) ? Results.NoContent() : Results.Accepted();
}

/// <summary><c>GET /v1/terrascale/organizations/{organization}/projects/{project}/members</c>.</summary>
public sealed class ListProjectMembersEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get($"{Project}/members");
        this.Document("list_project_members", "List explicit project roles", "Needs an effective project role. Lists explicit grants only; organization members' implied roles are not repeated. In user id order.", "Project members", null, StatusCodes.Status200OK, typeof(ProjectMemberPageBody), paged: true);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var (limit, after) = this.Paging();
        var page = await platform.ProjectMembersAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), limit, after, cancellationToken);
        return Results.Json(new ProjectMemberPageBody([.. page.Items.Select(item => Map(item.Member, item.Email))], page.Next), PlatformApiJson.Default.ProjectMemberPageBody);
    }
}

/// <summary><c>PUT /v1/terrascale/organizations/{organization}/projects/{project}/members/{user}</c>.</summary>
public sealed class SetProjectMemberRoleEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put($"{Project}/members/{{user}}");
        this.Document("set_project_member_role", "Grant or change a project role", "Needs the project's admin role. A new grant needs a member of the organization (404 otherwise; invite anyone else).", "Project members", typeof(RoleRequest), StatusCodes.Status200OK, typeof(ProjectMemberBody));
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        var role = (await this.BodyAsync(PlatformApiJson.Default.RoleRequest, cancellationToken)).Role;
        if (role is null || !Roles.Project.Contains(role))
        {
            throw Invalid("role must be viewer, member or admin.");
        }

        var (member, email) = await platform.SetProjectRoleAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), this.RouteId("user"), role, cancellationToken);
        return Results.Json(Map(member, email), PlatformApiJson.Default.ProjectMemberBody);
    }
}

/// <summary><c>DELETE /v1/terrascale/organizations/{organization}/projects/{project}/members/{user}</c>.</summary>
public sealed class RemoveProjectMemberEndpoint(PlatformDirectory platform, PlatformAuthentication authentication) : PlatformEndpoint(authentication)
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete($"{Project}/members/{{user}}");
        this.Document("remove_project_member", "Remove a project role", "Needs the project's admin role. Organization members keep the role their organization role implies.", "Project members", null, StatusCodes.Status204NoContent, null);
    }

    /// <inheritdoc />
    protected override async Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken)
    {
        await platform.RemoveProjectRoleAsync(caller.Id, this.RouteId("organization"), this.RouteId("project"), this.RouteId("user"), cancellationToken);
        return Results.NoContent();
    }
}

/// <summary>
/// A platform management endpoint (<c>/v1/terrascale/…</c>, TS-598): authenticates the caller's sign-in access token
/// before anything else, reads a small JSON body that names no unknown member, answers <c>no-store</c>, and turns a
/// storage failure into 503.
/// </summary>
public abstract class PlatformEndpoint(PlatformAuthentication authentication) : EndpointWithoutRequest<IResult>
{
    /// <summary>The organizations collection route.</summary>
    protected const string Organizations = "/v1/terrascale/organizations";

    /// <summary>One organization's route.</summary>
    protected const string Organization = Organizations + "/{organization}";

    /// <summary>One project's route.</summary>
    protected const string Project = Organization + "/projects/{project}";

    private const int MaxBody = 16 * 1024;

    /// <inheritdoc />
    public sealed override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        this.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            if (await authentication.AuthenticateAsync(this.HttpContext.Request, cancellationToken) is not { } caller)
            {
                this.HttpContext.Response.Headers.WWWAuthenticate = "Bearer realm=\"ts-auth\"";
                return Results.Json(new ErrorBody(new ErrorDetail("unauthorized", "Present a sign-in access token of this tenant.")), PlatformApiJson.Default.ErrorBody, statusCode: StatusCodes.Status401Unauthorized);
            }

            return await this.HandleAsync(caller, cancellationToken);
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException)
        {
            // TerraBase failures (TerraBaseException is an HttpRequestException): fail closed, details stay in the logs.
            throw ApiException.Unavailable(error);
        }
    }

    /// <summary>Handles a call of the signed-in <paramref name="caller"/>.</summary>
    protected abstract Task<IResult> HandleAsync(UserRecord caller, CancellationToken cancellationToken);

    /// <summary>A 400 <c>invalid_request</c>.</summary>
    protected static ApiException Invalid(string message) => new(StatusCodes.Status400BadRequest, "invalid_request", message);

    /// <summary>A route id; anything but a canonical uppercase ULID is 404, as for an unknown one.</summary>
    protected string RouteId(string name) =>
        this.HttpContext.GetRouteValue(name) is string value && PlatformDirectory.IsId(value) ? value : throw PlatformDirectory.NotFound();

    /// <summary><c>limit</c> (1 to 100, default <paramref name="fallback"/>) and <c>after</c>.</summary>
    protected (int Limit, string? After) Paging(int fallback = 20)
    {
        var query = this.HttpContext.Request.Query;
        var limit = fallback;
        if (query.TryGetValue("limit", out var text) && (text.Count != 1 || !int.TryParse(text[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit is < 1 or > 100))
        {
            throw Invalid("limit must be 1 to 100.");
        }

        return (limit, query.TryGetValue("after", out var after) && after.Count == 1 && !string.IsNullOrEmpty(after[0]) ? after[0] : null);
    }

    /// <summary>Reads a JSON object of at most 16 KiB.</summary>
    protected async Task<T> BodyAsync<T>(JsonTypeInfo<T> type, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            // Read here up to the bound, whatever the server's own limit, chunked or not.
            if (IsUtf8Json(this.HttpContext.Request))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[4096];
                int read;
                while ((read = await this.HttpContext.Request.Body.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length + read <= MaxBody)
                {
                    buffer.Write(chunk, 0, read);
                }

                if (read == 0 && JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type) is { } body)
                {
                    return body;
                }
            }
        }
        catch (Exception error) when (error is JsonException or BadHttpRequestException)
        {
        }

        throw Invalid($"The body must be a JSON object of at most {MaxBody} bytes with only this operation's members.");
    }

    // JSON between systems is UTF-8 (RFC 8259 §8.1): another declared charset is refused, never resolved.
    private static bool IsUtf8Json(HttpRequest request) =>
        request.HasJsonContentType()
        && Microsoft.Net.Http.Headers.MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
        && (media.Charset.Length == 0 || media.Charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Declares the operation in OpenAPI with the errors it can answer: 403 when it checks a role, 404 when it addresses
    /// an organization, project or other resource the caller may not reach.
    /// </summary>
    protected void Document(string operationId, string summary, string description, string tag, Type? request, int success, Type? response, bool paged = false, bool conflict = false, bool roleChecked = true, bool scoped = true)
    {
        Name(operationId);
        Summary(summary);
        Description(description);
        Tags(tag);
        if (request is not null)
        {
            // Accepts<T> would document EmptyRequest for a body this endpoint reads itself; the last metadata wins.
            Metadata(new AcceptsMetadata(["application/json"], request));
        }

        this.Definition.Produces(response ?? typeof(void), success);
        if (request is not null || paged)
        {
            Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        }

        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        if (roleChecked)
        {
            Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        }

        if (scoped)
        {
            Produces<ErrorBody>(StatusCodes.Status404NotFound);
        }

        if (conflict)
        {
            Produces<ErrorBody>(StatusCodes.Status409Conflict);
        }

        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        Metadata(new RequiresSecurity(SecuritySchemes.PlatformUser));
    }

    /// <summary>The organization as the caller sees it.</summary>
    protected static OrganizationBody Map(OrganizationRecord organization, string? role) =>
        new(organization.Id, organization.Name, organization.CreatedBy, organization.CreatedAt, role, organization.RequireMfa);

    /// <summary>The project as the caller sees it.</summary>
    protected ProjectBody Map(ProjectRecord project, string role)
    {
        ArgumentNullException.ThrowIfNull(project);
        var issuer = OidcIssuer.Issuer(this.HttpContext.RequestServices.GetRequiredService<AuthOptions>(), project.Id);
        return new(project.Id, project.OrganizationId, project.Name, issuer.AbsoluteUri, project.CreatedBy, project.CreatedAt, role, project.AllowedCidrs ?? []);
    }

    /// <summary>A member (never a collaborator, whose record has no role).</summary>
    protected static MemberBody Map(MembershipRecord member, string email) => new(member.UserId, email, member.Role!, member.JoinedAt);

    /// <summary>An invitation, with the status a caller sees.</summary>
    protected InvitationBody Map(Invitations invitations, InvitationRecord invitation)
    {
        ArgumentNullException.ThrowIfNull(invitations);
        ArgumentNullException.ThrowIfNull(invitation);
        return new(invitation.Id, invitation.Email, invitation.Role, invitation.ProjectId, invitation.InvitedBy, invitation.CreatedAt, invitation.ExpiresAt, invitations.Status(invitation));
    }

    /// <summary>A project member.</summary>
    protected static ProjectMemberBody Map(ProjectMemberRecord member, string email) => new(member.UserId, email, member.Role, member.GrantedAt);
}

/// <summary>Sets <c>Location</c> on a result.</summary>
internal static class LocationResults
{
    public static IResult WithLocation(this IResult result, string location) => new Located(result, location);

    private sealed class Located(IResult inner, string location) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.Headers.Location = location;
            return inner.ExecuteAsync(httpContext);
        }
    }
}

[JsonSourceGenerationOptions(UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(NameRequest))]
[JsonSerializable(typeof(RoleRequest))]
[JsonSerializable(typeof(MfaPolicyRequest))]
[JsonSerializable(typeof(AccountMfaBody))]
[JsonSerializable(typeof(OrganizationBody))]
[JsonSerializable(typeof(OrganizationPageBody))]
[JsonSerializable(typeof(MemberBody))]
[JsonSerializable(typeof(MemberPageBody))]
[JsonSerializable(typeof(ProjectBody))]
[JsonSerializable(typeof(ProjectPageBody))]
[JsonSerializable(typeof(ProjectMemberBody))]
[JsonSerializable(typeof(ProjectMemberPageBody))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(CreateApiKeyRequest))]
[JsonSerializable(typeof(AllowedCidrsRequest))]
[JsonSerializable(typeof(RotateApiKeyRequest))]
[JsonSerializable(typeof(ApiKeyBody))]
[JsonSerializable(typeof(ApiKeySecretBody))]
[JsonSerializable(typeof(ApiKeyPageBody))]
[JsonSerializable(typeof(CreateInvitationRequest))]
[JsonSerializable(typeof(InvitationTokenRequest))]
[JsonSerializable(typeof(StartTransferRequest))]
[JsonSerializable(typeof(InvitationBody))]
[JsonSerializable(typeof(InvitationCreatedBody))]
[JsonSerializable(typeof(InvitationPageBody))]
[JsonSerializable(typeof(InvitationLookupBody))]
[JsonSerializable(typeof(AcceptedInvitationBody))]
[JsonSerializable(typeof(OwnershipTransferBody))]
[JsonSerializable(typeof(AuditEventPageBody))]
internal sealed partial class PlatformApiJson : JsonSerializerContext;

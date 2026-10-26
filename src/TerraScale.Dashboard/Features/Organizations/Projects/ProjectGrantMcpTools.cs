using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Organizations.Projects;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Organizations;

// Extend the registered organization tool type without exposing organization directory tools to project grants.
public sealed partial class OrganizationMcpTools
{
    [McpServerTool(Name = "list_project_members", Title = "List explicit project grants", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true),
        Description("List one page of explicit grants for the exact project. Any current project viewer, member or admin may read. Organization-implied access is omitted; this is not a complete effective member roster.")]
    public Task<ProjectGrantMcpPage> ListProjectMembers(
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null,
        [Description("Opaque next cursor from the previous page. Omit to start at the first page.")] string? after = null) => Invoke(async () =>
    {
        var context = Scope(organizationId, projectId);
        var page = await ProjectGrants.ReadAsync(context, after, Cancellation);
        return new ProjectGrantMcpPage(context.Organization!.Id, context.Project!.Id, page.Items.Select(GrantView).ToArray(), page.Next);
    });

    [McpServerTool(Name = "set_project_member_role", Title = "Set explicit project grant", Idempotent = true, Destructive = true, OpenWorld = true, UseStructuredContent = true),
        Description("Grant or change explicit viewer/member/admin access to the exact project. Requires current effective project admin. New grants require an existing organization member; existing collaborator grants may be changed. Does not invite users or change organization membership.")]
    public Task<ProjectGrantMcpResult> SetProjectMemberRole([Description("Canonical user ULID, not an email address.")] string userId,
        [Description("Explicit project role: viewer, member or admin.")] string role,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => GrantChange(async () =>
    {
        var context = Scope(organizationId, projectId);
        var grant = await ProjectGrants.SetAsync(context, userId, role, Cancellation);
        return new ProjectGrantMcpResult(context.Organization!.Id, context.Project!.Id, GrantView(grant));
    });

    [McpServerTool(Name = "remove_project_member", Title = "Remove explicit project grant", Idempotent = false, Destructive = true, OpenWorld = true, UseStructuredContent = true),
        Description("Remove only this user's explicit grant to the exact project. Requires current effective project admin. Organization-implied access and independent service keys remain; removal does not imply total session or token revocation.")]
    public Task<ProjectGrantMcpRemoved> RemoveProjectMember([Description("Canonical user ULID whose explicit grant will be removed.")] string userId,
        [Description(OrganizationDescription)] string? organizationId = null, [Description(ProjectDescription)] string? projectId = null) => GrantChange(async () =>
    {
        var context = Scope(organizationId, projectId);
        await ProjectGrants.RemoveAsync(context, userId, Cancellation);
        return new ProjectGrantMcpRemoved(context.Organization!.Id, context.Project!.Id, userId, true,
            "Only the explicit grant was removed. Organization-implied access and independent service keys remain.");
    });

    private async Task<T> GrantChange<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (AccountAccessException error) when (error.StatusCode == 404)
        { throw new AccountAccessException("The project or target user is unavailable. New grants require an existing organization member; otherwise ask an organization admin about a project invitation.", 404); }
        catch (Exception error) when (error is IOException or HttpRequestException || error is OperationCanceledException && !Cancellation.IsCancellationRequested)
        { throw new AccountAccessException("The grant change could not be confirmed. Reload project grants before trying again.", 502); }
    }

    private ProjectGrantService ProjectGrants => http.HttpContext!.RequestServices.GetRequiredService<ProjectGrantService>();
    private static ProjectGrantMcpMember GrantView(TerraScaleAuthPlatformProjectMember grant) =>
        new(grant.UserId, grant.Email, grant.Role, grant.GrantedAt);
}

public sealed record ProjectGrantMcpMember(string UserId, string Email, string Role, DateTimeOffset GrantedAt);
public sealed record ProjectGrantMcpResult(string OrganizationId, string ProjectId, ProjectGrantMcpMember Grant);
public sealed record ProjectGrantMcpRemoved(string OrganizationId, string ProjectId, string UserId, bool Removed, string Effect);
public sealed record ProjectGrantMcpPage(string OrganizationId, string ProjectId, IReadOnlyList<ProjectGrantMcpMember> Grants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Next);

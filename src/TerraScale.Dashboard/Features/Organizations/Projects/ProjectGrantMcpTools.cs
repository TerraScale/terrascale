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

    private ProjectGrantService ProjectGrants => http.HttpContext!.RequestServices.GetRequiredService<ProjectGrantService>();
    private static ProjectGrantMcpMember GrantView(TerraScaleAuthPlatformProjectMember grant) =>
        new(grant.UserId, grant.Email, grant.Role, grant.GrantedAt);
}

public sealed record ProjectGrantMcpMember(string UserId, string Email, string Role, DateTimeOffset GrantedAt);
public sealed record ProjectGrantMcpPage(string OrganizationId, string ProjectId, IReadOnlyList<ProjectGrantMcpMember> Grants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Next);

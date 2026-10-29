using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Settings
{
    [McpServerToolType]
    public sealed class SettingsMcpTools(IHttpContextAccessor http, SettingsService settings)
    {
        [McpServerTool(Name = "get_project_settings", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read provider audit retention and the selected project's deletion eligibility. Requires organization management.")]
        public Task<ProjectSettings> Read(string? organizationId = null, string? projectId = null)
        {
            return settings.ReadAsync(Scope(organizationId, projectId), Cancellation);
        }

        [McpServerTool(Name = "set_audit_retention", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false), Description("Set the provider's organization audit retention from 1 to 90 days. Requires an owner or admin role.")]
        public async Task<SettingsSaved> SetRetention(int days, string? organizationId = null, string? projectId = null)
        {
            await settings.SetAuditRetentionAsync(Scope(organizationId, projectId), days, Cancellation);
            return new(true);
        }

        [McpServerTool(Name = "delete_project", UseStructuredContent = true, Destructive = true, OpenWorld = false), Description("Delete an empty project after confirming its exact name. The default or last project cannot be deleted. Requires an owner or admin role.")]
        public async Task<ProjectDeleted> Delete(string confirmName, string? organizationId = null, string? projectId = null)
        {
            var scope = Scope(organizationId, projectId);
            await settings.DeleteProjectAsync(scope, scope.Project?.Id ?? "", confirmName, Cancellation);
            return new(true);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record SettingsSaved(bool Saved);
    public sealed record ProjectDeleted(bool Deleted);
}

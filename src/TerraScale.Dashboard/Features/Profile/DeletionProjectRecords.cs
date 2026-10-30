using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Logs;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Documents;

namespace TerraScale.Dashboard.Features.Profile
{
    // The permanent cleanup journal replays project records after conflicts or late writes.
    public sealed class DeletionProjectRecords(IDashboardState state)
    {
        public async Task CleanAsync(string organizationId, string projectId, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId))
            {
                throw new IOException("Project cleanup scope is invalid.");
            }

            try
            {
                await SqlWorkspace.EraseAsync(state, new Dictionary<string, string> { ["OrganizationId"] = organizationId, ["ProjectId"] = projectId }, cancellationToken);
                await DatabaseWorkspaceCleanup.EraseMarkersAsync(state, organizationId, projectId, cancellationToken);
                var queryKey = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(organizationId + "/" + projectId)));
                await DropAsync<SavedQueryRecord>("dashboard_saved_queries", queryKey,
                    record => record.OrganizationId == organizationId && record.ProjectId == projectId, cancellationToken);
                await DropAsync<AlertRuleSet>(AlertRules.Collection, projectId,
                    record => record.OrganizationId == organizationId && record.ProjectId == projectId, cancellationToken);
                await DropAsync<LogDrain>(LogDrains.Collection, projectId,
                    record => record.OrganizationId == organizationId && record.ProjectId == projectId, cancellationToken);
                await RemoveIndexEntryAsync<AlertProjectIndex>(AlertRules.IndexCollection, organizationId, projectId,
                    index => index.Projects, cancellationToken);
                await RemoveIndexEntryAsync<LogDrainIndex>(LogDrains.IndexCollection, organizationId, projectId,
                    index => index.Projects, cancellationToken);
            }
            catch (Exception error) when (error is JsonException or TerraBaseException)
            {
                throw new IOException("Project record cleanup could not complete.", error);
            }
        }

        private async Task DropAsync<T>(string collection, string key, Func<T, bool> owns, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(collection, key, cancellationToken) is not { } document)
            {
                return;
            }

            if (document.Id != key || !owns(AccountService.Read<T>(document)))
            {
                throw new IOException("Project record ownership does not match.");
            }

            await state.DeleteAsync(collection, key, document.Version, cancellationToken);
        }

        private async Task RemoveIndexEntryAsync<T>(string collection, string organizationId, string projectId,
            Func<T, Dictionary<string, string>> projects, CancellationToken cancellationToken)
        {
            if (await state.GetAsync(collection, "all", cancellationToken) is not { } document)
            {
                return;
            }

            if (document.Id != "all")
            {
                throw new IOException("Project cleanup index identity does not match.");
            }

            var index = AccountService.Read<T>(document);
            var entries = projects(index) ?? throw new IOException("Project cleanup index is invalid.");
            if (!entries.TryGetValue(projectId, out var owner))
            {
                return;
            }

            if (owner != organizationId)
            {
                throw new IOException("Project cleanup index ownership does not match.");
            }

            entries.Remove(projectId);
            await state.PutAsync(collection, "all", JsonSerializer.SerializeToElement(index),
                TerraBasePrecondition.Matches(document.Version), cancellationToken);
        }
    }
}

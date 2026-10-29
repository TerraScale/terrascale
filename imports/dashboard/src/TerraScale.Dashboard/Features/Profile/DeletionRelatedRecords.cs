using System.Text.Json;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Features.Billing;
using TerraScale.Dashboard.Features.Billing.Internal;
using TerraScale.Dashboard.Features.Notifications;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.Dashboard.Platform.Persistence;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Profile;

public sealed class DeletionRelatedRecords(IDashboardState state, DashboardSessions sessions)
{
    public async Task CleanAccountAsync(string subject, CancellationToken cancellationToken)
    {
        await Features.Data.SqlWorkspace.EraseAsync(state, new Dictionary<string, string> { ["UserId"] = subject }, cancellationToken);
        foreach (var document in await state.ListAsync(McpOAuth.CodeCollection, cancellationToken))
        {
            if (document.State != "value") throw new IOException("Pending MCP authorization has a causal conflict.");
            if (!document.Document.TryGetProperty("UserId", out var owner) || owner.GetString() != subject) continue;
            if (!document.Document.TryGetProperty("CredentialId", out var credential) || credential.GetString() is not { } id)
                throw new IOException("Pending MCP credential reference is invalid.");
            await sessions.DeleteCredentialAsync(id, subject, cancellationToken);
            await state.DeleteListedAsync(McpOAuth.CodeCollection, document, cancellationToken);
        }
    }

    public async Task CleanOrganizationAsync(string organizationId, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(InternalBillingService.Collection, InternalBillingService.SingletonId, cancellationToken) is { } singleton)
        {
            if (singleton.Id != InternalBillingService.SingletonId) throw new IOException("Internal billing singleton identity changed.");
            if (AccountService.Read<InternalBillingSingleton>(singleton).OrganizationId == organizationId)
                await state.DeleteAsync(InternalBillingService.Collection, InternalBillingService.SingletonId, singleton.Version, cancellationToken);
        }
        await DropAsync<BillingChangeReceipt>(StripeBilling.ChangesCollection, organizationId, row => row.OrganizationId == organizationId, cancellationToken);
        await DropAsync<AllowanceNotificationState>(AllowanceNotifications.Collection, organizationId, row => row.OrganizationId == organizationId, cancellationToken);
        await Features.Data.DatabaseWorkspaceCleanup.EraseMarkersAsync(state, organizationId, null, cancellationToken);
        await CleanNotificationsAsync(organizationId, null, cancellationToken);
    }

    public async Task CleanProjectAsync(string organizationId, string projectId, CancellationToken cancellationToken) =>
        await CleanNotificationsAsync(organizationId, projectId, cancellationToken);

    private async Task CleanNotificationsAsync(string organizationId, string? projectId, CancellationToken cancellationToken)
    {
        foreach (var listed in await state.ListAsync(AccountService.AccountsCollection, cancellationToken))
        {
            if (AccountService.IsDeletionFence(listed)) continue;
            var known = AccountService.Read<AccountRecord>(listed);
            if (string.IsNullOrWhiteSpace(known.UserId)) throw new IOException("Notification owner is invalid.");
            var key = AccountService.AccountStorageKey(known.UserId);
            for (var attempt = 0; ; attempt++)
            {
                var document = await state.GetAsync(AccountService.AccountsCollection, key, cancellationToken);
                if (document is null || AccountService.IsDeletionFence(document)) break;
                var account = AccountService.Read<AccountRecord>(document);
                if (document.Id != key || account.UserId != known.UserId) throw new IOException("Notification owner changed.");
                if (account.Notifications.RemoveAll(note => note.OrganizationId == organizationId && (projectId is null || note.ProjectId == projectId)) == 0) break;
                try
                {
                    await state.PutAsync(AccountService.AccountsCollection, key, JsonSerializer.SerializeToElement(account), TerraBasePrecondition.Matches(document.Version), cancellationToken);
                    break;
                }
                catch (TerraBasePreconditionFailedException) when (attempt < 3) { }
            }
        }
    }

    private async Task DropAsync<T>(string collection, string key, Func<T, bool> owns, CancellationToken cancellationToken)
    {
        if (await state.GetAsync(collection, key, cancellationToken) is not { } document) return;
        if (document.Id != key || !owns(AccountService.Read<T>(document))) throw new IOException("Related deletion record ownership changed.");
        await state.DeleteAsync(collection, key, document.Version, cancellationToken);
    }
}

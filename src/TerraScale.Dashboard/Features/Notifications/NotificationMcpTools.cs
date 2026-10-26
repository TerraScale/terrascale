using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.ApiKeys;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Notifications;

[McpServerToolType]
public sealed class NotificationMcpTools(IHttpContextAccessor http, AccountService accounts)
{
    [McpServerTool(Name = "list_notifications", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read notifications visible in your selected organization and project.")]
    public async Task<NotificationToolList> Notifications(
        [Description("Use unread (case-insensitive) for unread notifications; empty, all and other values select all visible notifications.")] string status = "",
        [Description("One-based page of 10 notifications, clamped to the available page range. Read page/pageCount from the response.")] int page = 1,
        string? organizationId = null, string? projectId = null)
    {
        var context = await AccountScope.RefreshAsync(accounts, Scope(organizationId, projectId), Cancellation);
        if (http.HttpContext is { } request) await ApiKeyExpiryNotifications.RefreshAsync(request, context);
        var inbox = NotificationCommands.Inbox(context, status, page);
        return new(inbox.Items.Select(item => new NotificationToolItem(item.Id, item.Type, item.Title, item.Body,
            item.Unread ? "Unread" : "Read", item.Severity, item.CreatedAt)).ToArray(), inbox.Status, inbox.Unread, inbox.Critical,
            inbox.Total, inbox.Matching, inbox.Page, inbox.PageCount);
    }

    [McpServerTool(Name = "mark_notification_read", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Mark one visible notification as read.")]
    public async Task<NotificationsMarked> ReadNotification(string notificationId, string? organizationId = null, string? projectId = null)
        => new(await NotificationCommands.MarkAsync(accounts, Scope(organizationId, projectId), notificationId, Cancellation));

    [McpServerTool(Name = "mark_all_notifications_read", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Mark all notifications in the current scope as read.")]
    public async Task<NotificationsMarked> ReadAllNotifications(string? organizationId = null, string? projectId = null)
        => new(await NotificationCommands.MarkAsync(accounts, Scope(organizationId, projectId), null, Cancellation));

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

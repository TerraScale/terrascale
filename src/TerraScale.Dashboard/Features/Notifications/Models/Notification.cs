namespace TerraScale.Dashboard.Features.Notifications.Models;

public sealed record Notification(
    string Id,
    string Type,            // api-key-expiry | invitation | backup | quota | incident
    string Title,
    string Body,
    string Status,          // Unread | Read
    string Severity,        // info | warning | danger | success
    string CreatedAt);

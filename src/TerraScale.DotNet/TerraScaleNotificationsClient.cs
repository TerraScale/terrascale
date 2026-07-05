namespace TerraScale.DotNet;

/// <summary>
/// Client for signed-in user dashboard notification endpoints.
/// </summary>
public sealed class TerraScaleNotificationsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleNotificationsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists dashboard notifications for the signed-in user.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleNotification>> ListAsync(
        int skip = 0,
        int take = 50,
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleNotification>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.NotificationsList(skip, take, unreadOnly),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Marks one dashboard notification read.
    /// </summary>
    public Task<TerraScaleNotification> MarkReadAsync(
        string notificationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notificationId);
        return client.SendAsync<TerraScaleNotification>(
            HttpMethod.Post,
            TerraScaleApiRoutes.NotificationMarkRead(notificationId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Marks all dashboard notifications read for the signed-in user.
    /// </summary>
    public Task<TerraScaleMarkedNotificationsRead> MarkAllReadAsync(
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleMarkedNotificationsRead>(
            HttpMethod.Post,
            TerraScaleApiRoutes.NotificationsMarkAllRead(),
            body: null,
            cancellationToken);
}

/// <summary>
/// Dashboard notification returned by TerraScale.
/// </summary>
public sealed record TerraScaleNotification(
    string NotificationId,
    string Title,
    string? Body,
    string Severity,
    DateTimeOffset? ReadAt,
    string CreatedAt);

/// <summary>
/// Result of marking all notifications read.
/// </summary>
public sealed record TerraScaleMarkedNotificationsRead(int MarkedRead);

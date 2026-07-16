namespace TerraScale.DotNet;

/// <summary>
/// Client for signed-in user notification endpoints.
/// </summary>
public sealed class TerraScaleNotificationsClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleNotificationsClient(TerraScaleClient client)
    {
        this.client = client;
    }

    /// <summary>
    /// Lists notifications linked to one organization, including its project alerts.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleNotification>> ListForOrganizationAsync(
        string organizationId,
        int skip = 0,
        int take = 50,
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleNotification>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.NotificationsList(
                skip,
                take,
                unreadOnly,
                organizationId: organizationId),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Lists alerts linked to one project and optionally organization-wide notices.
    /// </summary>
    public Task<IReadOnlyList<TerraScaleNotification>> ListForProjectAsync(
        string projectId,
        bool includeOrganizationNotifications = false,
        int skip = 0,
        int take = 50,
        bool unreadOnly = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<IReadOnlyList<TerraScaleNotification>>(
            HttpMethod.Get,
            TerraScaleApiRoutes.NotificationsList(
                skip,
                take,
                unreadOnly,
                projectId: projectId,
                includeOrganizationNotifications: includeOrganizationNotifications),
            body: null,
            cancellationToken);
    }

    /// <summary>
    /// Lists notifications for the signed-in user.
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
    /// Marks one user notification read.
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
    /// Marks all notifications read for the signed-in user.
    /// </summary>
    public Task<TerraScaleMarkedNotificationsRead> MarkAllReadAsync(
        CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleMarkedNotificationsRead>(
            HttpMethod.Post,
            TerraScaleApiRoutes.NotificationsMarkAllRead(),
            body: null,
            cancellationToken);

    /// <summary>
    /// Marks all notifications linked to one organization read for the signed-in user.
    /// </summary>
    public Task<TerraScaleMarkedNotificationsRead> MarkAllReadForOrganizationAsync(
        string organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        return client.SendAsync<TerraScaleMarkedNotificationsRead>(
            HttpMethod.Post,
            TerraScaleApiRoutes.NotificationsMarkAllRead(organizationId),
            body: null,
            cancellationToken);
    }
}

/// <summary>
/// User notification returned by TerraScale.
/// </summary>
public sealed record TerraScaleNotification(
    string NotificationId,
    string Title,
    string? Body,
    string Severity,
    DateTimeOffset? ReadAt,
    string CreatedAt,
    string? OrganizationId = null,
    string? ProjectId = null);

/// <summary>
/// Result of marking all notifications read.
/// </summary>
public sealed record TerraScaleMarkedNotificationsRead(int MarkedRead);

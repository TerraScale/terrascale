using System.Text.Json;

namespace TerraScale.DotNet;

/// <summary>
/// Client for role-protected public platform administration endpoints.
/// </summary>
public sealed class TerraScaleAdministrationClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleAdministrationClient(TerraScaleClient client) => this.client = client;

    /// <summary>Gets the platform overview.</summary>
    public Task<TerraScaleAdminPlatformUsage> GetOverviewAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleAdminPlatformUsage>(HttpMethod.Get, TerraScaleApiRoutes.AdminOverviewGet(), null, cancellationToken);

    /// <summary>Lists platform users.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminUser>> ListUsersAsync(int skip = 0, int take = 100, string? search = null, CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<TerraScaleAdminPage<TerraScaleAdminUser>>(HttpMethod.Get, TerraScaleApiRoutes.AdminUsersList(skip, take, search), null, cancellationToken);
    }

    /// <summary>Gets a platform user.</summary>
    public Task<TerraScaleAdminUser> GetUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminUser>(HttpMethod.Get, TerraScaleApiRoutes.AdminUserGet, userId, null, cancellationToken);

    /// <summary>Enables a platform user.</summary>
    public Task<TerraScaleAdminUser> EnableUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminUser>(HttpMethod.Post, TerraScaleApiRoutes.AdminUserEnable, userId, null, cancellationToken);

    /// <summary>Disables a platform user and optionally records a reason.</summary>
    public Task<TerraScaleAdminUser> DisableUserAsync(string userId, string? reason = null, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminUser>(HttpMethod.Post, TerraScaleApiRoutes.AdminUserDisable, userId, new TerraScaleAdminDisableUserRequest(reason), cancellationToken);

    /// <summary>Permanently deletes a platform user.</summary>
    public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<bool>(HttpMethod.Delete, TerraScaleApiRoutes.AdminUserGet, userId, null, cancellationToken);

    /// <summary>Creates a support note attached to a user, organization, or project.</summary>
    public Task<TerraScaleAdminSupportNote> CreateSupportNoteAsync(TerraScaleAdminCreateSupportNoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Note);
        return client.SendAsync<TerraScaleAdminSupportNote>(HttpMethod.Post, TerraScaleApiRoutes.AdminSupportNotesCreate(), request, cancellationToken);
    }

    /// <summary>Lists all organizations.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminOrganization>> ListOrganizationsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminOrganization>(TerraScaleApiRoutes.AdminOrganizationsList, skip, take, cancellationToken);

    /// <summary>Lists all projects.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminProject>> ListProjectsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminProject>(TerraScaleApiRoutes.AdminProjectsList, skip, take, cancellationToken);

    /// <summary>Lists all project databases.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminDatabase>> ListDatabasesAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminDatabase>(TerraScaleApiRoutes.AdminDatabasesList, skip, take, cancellationToken);

    /// <summary>Gets platform usage.</summary>
    public Task<TerraScaleAdminPlatformUsage> GetUsageAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<TerraScaleAdminPlatformUsage>(HttpMethod.Get, TerraScaleApiRoutes.AdminUsageGet(), null, cancellationToken);

    /// <summary>Lists pricing plans, including non-public plans.</summary>
    public Task<IReadOnlyList<TerraScalePricingPlan>> ListPricingPlansAsync(CancellationToken cancellationToken = default) =>
        client.SendAsync<IReadOnlyList<TerraScalePricingPlan>>(HttpMethod.Get, TerraScaleApiRoutes.AdminPlansList(), null, cancellationToken);

    /// <summary>Creates a pricing plan.</summary>
    public Task<TerraScalePricingPlan> CreatePricingPlanAsync(TerraScaleAdminPricingPlanRequest request, CancellationToken cancellationToken = default) =>
        SendPlanAsync(HttpMethod.Post, TerraScaleApiRoutes.AdminPlansList(), request, cancellationToken);

    /// <summary>Updates a pricing plan.</summary>
    public Task<TerraScalePricingPlan> UpdatePricingPlanAsync(string planId, TerraScaleAdminPricingPlanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planId);
        return SendPlanAsync(HttpMethod.Patch, TerraScaleApiRoutes.AdminPlanGet(planId), request, cancellationToken);
    }

    /// <summary>Deletes a pricing plan.</summary>
    public Task<bool> DeletePricingPlanAsync(string planId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<bool>(HttpMethod.Delete, TerraScaleApiRoutes.AdminPlanGet, planId, null, cancellationToken);

    /// <summary>Lists platform audit events.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminAuditEvent>> ListAuditAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminAuditEvent>(TerraScaleApiRoutes.AdminAuditList, skip, take, cancellationToken);

    /// <summary>Lists platform notifications.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminNotification>> ListNotificationsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminNotification>(TerraScaleApiRoutes.AdminNotificationsList, skip, take, cancellationToken);

    private Task<T> SendForIdAsync<T>(HttpMethod method, Func<string, string> route, string id, object? body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return client.SendAsync<T>(method, route(id), body, cancellationToken);
    }

    private Task<TerraScalePricingPlan> SendPlanAsync(HttpMethod method, string route, TerraScaleAdminPricingPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return client.SendAsync<TerraScalePricingPlan>(method, route, request, cancellationToken);
    }

    private Task<TerraScaleAdminPage<T>> ListAsync<T>(Func<int, int, string> route, int skip, int take, CancellationToken cancellationToken)
    {
        TerraScalePagination.Validate(skip, take);
        return client.SendAsync<TerraScaleAdminPage<T>>(HttpMethod.Get, route(skip, take), null, cancellationToken);
    }
}

/// <summary>Page returned by a platform administration list operation.</summary>
public sealed record TerraScaleAdminPage<T>(IReadOnlyList<T> Items, long? TotalCount, string? NextCursor);
/// <summary>Platform-wide administration view of a user.</summary>
public sealed record TerraScaleAdminUser(string UserId, string Email, string? DisplayName, bool Enabled, bool Owner, IReadOnlyList<string> OrganizationIds, DateTimeOffset CreatedAt);
/// <summary>Support-note fields and optional resource attachment.</summary>
public sealed record TerraScaleAdminCreateSupportNoteRequest(string? UserId, string? OrganizationId, string? ProjectId, string Note);
/// <summary>Support note returned by platform administration.</summary>
public sealed record TerraScaleAdminSupportNote(string SupportNoteId, string? UserId, string? OrganizationId, string? ProjectId, string Note, DateTimeOffset CreatedAt);
/// <summary>Platform-wide administration view of an organization.</summary>
public sealed record TerraScaleAdminOrganization(string OrganizationId, string Name, string? Slug, DateTimeOffset CreatedAt);
/// <summary>Platform-wide administration view of a project.</summary>
public sealed record TerraScaleAdminProject(string ProjectId, string OrganizationId, string Name, string? Slug, DateTimeOffset CreatedAt);
/// <summary>Environment projection within an administration database result.</summary>
public sealed record TerraScaleAdminDatabaseEnvironment(string EnvironmentId, string DatabaseId, string Name, string Kind, string Status, bool Selected);
/// <summary>Platform-wide administration view of a project database.</summary>
public sealed record TerraScaleAdminDatabase(string DatabaseId, string ProjectId, string Name, string Status, IReadOnlyList<string> Regions, IReadOnlyList<TerraScaleAdminDatabaseEnvironment> Environments, DateTimeOffset CreatedAt);
/// <summary>Named platform usage quantity.</summary>
public sealed record TerraScaleAdminUsageMetric(string Name, long Quantity, string Unit);
/// <summary>Platform deployment region in an administration usage response.</summary>
public sealed record TerraScaleAdminRegion(string Code, string DisplayName, string Country, double Latitude, double Longitude, string OciRegionCode, string ClusterNodeTag, bool Available);
/// <summary>Platform-wide counts, deployment regions, and usage metrics.</summary>
public sealed record TerraScaleAdminPlatformUsage(long Organizations, long Projects, long Databases, IReadOnlyList<TerraScaleAdminRegion> Regions, IReadOnlyList<TerraScaleAdminUsageMetric> Usage);
/// <summary>Platform-wide audit event.</summary>
public sealed record TerraScaleAdminAuditEvent(string AuditEventId, string? ProjectId, string? ActorUserId, string Action, string TargetType, string? TargetId, DateTimeOffset OccurredAt, JsonElement? Metadata);
/// <summary>Platform-wide user notification.</summary>
public sealed record TerraScaleAdminNotification(string NotificationId, string? UserId, string Title, string? Body, string Severity, DateTimeOffset? ReadAt, DateTimeOffset CreatedAt);
/// <summary>Optional reason recorded when disabling a platform user.</summary>
public sealed record TerraScaleAdminDisableUserRequest(string? Reason);

/// <summary>Optional fields accepted when creating or updating an administration pricing plan.</summary>
public sealed record TerraScaleAdminPricingPlanRequest(
    string? Key = null,
    string? Name = null,
    string? Currency = null,
    int? MonthlyBaseCents = null,
    int? AnnualBaseCents = null,
    string? MonthlyPolarProductId = null,
    string? AnnualPolarProductId = null,
    string? MonthlyStripePriceId = null,
    string? AnnualStripePriceId = null,
    int? MonthlyTrialDays = null,
    int? AnnualTrialDays = null,
    bool? Visible = null,
    bool? DefaultCheckout = null,
    int? DisplayOrder = null,
    int? MaxDatabases = null,
    int? MaxRegions = null,
    int? MaxStorageGb = null,
    long? MaxRequestsPerMonth = null,
    int? MaxTeamMembers = null,
    int? MaxRepositories = null,
    int? MaxApiKeys = null,
    int? OperationOverageCentsPerMillion = null,
    int? StorageOverageCentsPerGbRegion = null,
    IReadOnlyList<TerraScalePricingUsageMetric>? IncludedUsage = null);

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

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
        GetAsync<TerraScaleAdminPlatformUsage>(HttpMethod.Get, TerraScaleApiRoutes.AdminOverviewGet(), cancellationToken);

    /// <summary>Lists platform users.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminUser>> ListUsersAsync(int skip = 0, int take = 100, string? search = null, CancellationToken cancellationToken = default)
    {
        TerraScalePagination.Validate(skip, take);
        return GetAsync<TerraScaleAdminPage<TerraScaleAdminUser>>(HttpMethod.Get, TerraScaleApiRoutes.AdminUsersList(skip, take, search), cancellationToken);
    }

    /// <summary>Gets a platform user.</summary>
    public Task<TerraScaleAdminUser> GetUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminUser>(HttpMethod.Get, TerraScaleApiRoutes.AdminUserGet, userId, cancellationToken);

    /// <summary>Enables a platform user.</summary>
    public Task<TerraScaleAdminUser> EnableUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminUser>(HttpMethod.Post, TerraScaleApiRoutes.AdminUserEnable, userId, cancellationToken);

    /// <summary>Disables a platform user and optionally records a reason.</summary>
    public Task<TerraScaleAdminUser> DisableUserAsync(string userId, string? reason = null, CancellationToken cancellationToken = default) =>
        SendForIdAsync<TerraScaleAdminDisableUserRequest, TerraScaleAdminUser>(HttpMethod.Post, TerraScaleApiRoutes.AdminUserDisable, userId, new TerraScaleAdminDisableUserRequest(reason), cancellationToken);

    /// <summary>Permanently deletes a platform user.</summary>
    public Task<bool> DeleteUserAsync(string userId, CancellationToken cancellationToken = default) =>
        SendForIdAsync<bool>(HttpMethod.Delete, TerraScaleApiRoutes.AdminUserGet, userId, cancellationToken);

    /// <summary>Creates a support note attached to a user, organization, or project.</summary>
    public Task<TerraScaleAdminSupportNote> CreateSupportNoteAsync(TerraScaleAdminCreateSupportNoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Note);
        return SendAsync<TerraScaleAdminCreateSupportNoteRequest, TerraScaleAdminSupportNote>(HttpMethod.Post, TerraScaleApiRoutes.AdminSupportNotesCreate(), request, cancellationToken);
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
        GetAsync<TerraScaleAdminPlatformUsage>(HttpMethod.Get, TerraScaleApiRoutes.AdminUsageGet(), cancellationToken);

    /// <summary>Lists pricing plans, including non-public plans.</summary>
    public Task<IReadOnlyList<TerraScalePricingPlan>> ListPricingPlansAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<TerraScalePricingPlan>>(HttpMethod.Get, TerraScaleApiRoutes.AdminPlansList(), cancellationToken);

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
        SendForIdAsync<bool>(HttpMethod.Delete, TerraScaleApiRoutes.AdminPlanGet, planId, cancellationToken);

    /// <summary>Lists platform audit events.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminAuditEvent>> ListAuditAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminAuditEvent>(TerraScaleApiRoutes.AdminAuditList, skip, take, cancellationToken);

    /// <summary>Lists platform notifications.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminNotification>> ListNotificationsAsync(int skip = 0, int take = 100, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminNotification>(TerraScaleApiRoutes.AdminNotificationsList, skip, take, cancellationToken);

    /// <summary>Gets a payment-provider webhook event.</summary>
    public Task<TerraScaleAdminPaymentWebhookEvent> GetPaymentWebhookAsync(string provider, string eventId, CancellationToken cancellationToken = default) =>
        GetForWebhookAsync<TerraScaleAdminPaymentWebhookEvent>(HttpMethod.Get, TerraScaleApiRoutes.AdminPaymentWebhookGet, provider, eventId, cancellationToken);

    /// <summary>Lists payment-provider webhook events awaiting replay.</summary>
    public Task<TerraScaleAdminPage<TerraScaleAdminPaymentWebhookEvent>> ListDeadLetteredPaymentWebhooksAsync(int skip = 0, int take = 50, CancellationToken cancellationToken = default) =>
        ListAsync<TerraScaleAdminPaymentWebhookEvent>(TerraScaleApiRoutes.AdminPaymentWebhookDeadLettersList, skip, take, cancellationToken);

    /// <summary>Replays a dedicated dead-lettered payment webhook event.</summary>
    public Task<TerraScaleAdminPaymentWebhookReplay> ReplayPaymentWebhookAsync(string provider, string eventId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (idempotencyKey.Trim().Length > 128)
        {
            throw new ArgumentException("Idempotency key must not exceed 128 characters.", nameof(idempotencyKey));
        }

        return client.SendJsonAsync(
            HttpMethod.Post,
            TerraScaleApiRoutes.AdminPaymentWebhookReplay(provider, eventId),
            TypeInfo<TerraScaleAdminPaymentWebhookReplay>(),
            cancellationToken,
            idempotencyKey.Trim());
    }

    private Task<T> SendForIdAsync<T>(HttpMethod method, Func<string, string> route, string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return GetAsync<T>(method, route(id), cancellationToken);
    }

    private Task<TResponse> SendForIdAsync<TRequest, TResponse>(HttpMethod method, Func<string, string> route, string id, TRequest body, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return SendAsync<TRequest, TResponse>(method, route(id), body, cancellationToken);
    }

    private Task<TerraScalePricingPlan> SendPlanAsync(HttpMethod method, string route, TerraScaleAdminPricingPlanRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync<TerraScaleAdminPricingPlanRequest, TerraScalePricingPlan>(method, route, request, cancellationToken);
    }

    private Task<TerraScaleAdminPage<T>> ListAsync<T>(Func<int, int, string> route, int skip, int take, CancellationToken cancellationToken)
    {
        TerraScalePagination.Validate(skip, take);
        return GetAsync<TerraScaleAdminPage<T>>(HttpMethod.Get, route(skip, take), cancellationToken);
    }

    private Task<T> GetAsync<T>(HttpMethod method, string route, CancellationToken cancellationToken) =>
        client.SendJsonAsync(method, route, TypeInfo<T>(), cancellationToken);

    private Task<T> GetForWebhookAsync<T>(HttpMethod method, Func<string, string, string> route, string provider, string eventId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        return GetAsync<T>(method, route(provider, eventId), cancellationToken);
    }

    private Task<TResponse> SendAsync<TRequest, TResponse>(HttpMethod method, string route, TRequest request, CancellationToken cancellationToken) =>
        client.SendJsonAsync(method, route, request, TypeInfo<TRequest>(), TypeInfo<TResponse>(), cancellationToken);

    private static JsonTypeInfo<T> TypeInfo<T>() =>
        (JsonTypeInfo<T>)TerraScaleAdminJsonContext.Default.GetTypeInfo(typeof(T))!;
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
/// <summary>Payment-provider webhook event available to a platform owner.</summary>
public sealed record TerraScaleAdminPaymentWebhookEvent(string Provider, string EventId, string EventType, string State, long DeliveryCount, long ProjectionAttemptCount, long ConflictCount, long ReplayRequestCount, DateTimeOffset FirstReceivedAt, DateTimeOffset LastReceivedAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? ProjectedAt, DateTimeOffset? DeadLetteredAt, DateTimeOffset? LastReplayRequestedAt, string? LastReplayRequestedByUserId, string? LastError, bool ReplayAvailable);
/// <summary>Result of replaying a dead-lettered payment-provider webhook event.</summary>
public sealed record TerraScaleAdminPaymentWebhookReplay(string Provider, string EventId, string EventType, string MessageId, DateTimeOffset RequestedAt, string RequestedByUserId);
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
    string? MonthlyPolarProductPriceId = null,
    string? AnnualPolarProductId = null,
    string? AnnualPolarProductPriceId = null,
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

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(TerraScaleAdminPlatformUsage))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminUser>))]
[JsonSerializable(typeof(TerraScaleAdminUser))]
[JsonSerializable(typeof(TerraScaleAdminCreateSupportNoteRequest))]
[JsonSerializable(typeof(TerraScaleAdminSupportNote))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminOrganization>))]
[JsonSerializable(typeof(TerraScaleAdminOrganization))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminProject>))]
[JsonSerializable(typeof(TerraScaleAdminProject))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminDatabase>))]
[JsonSerializable(typeof(TerraScaleAdminDatabase))]
[JsonSerializable(typeof(IReadOnlyList<TerraScalePricingPlan>))]
[JsonSerializable(typeof(TerraScalePricingPlan))]
[JsonSerializable(typeof(TerraScaleAdminPricingPlanRequest))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminAuditEvent>))]
[JsonSerializable(typeof(TerraScaleAdminAuditEvent))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminNotification>))]
[JsonSerializable(typeof(TerraScaleAdminNotification))]
[JsonSerializable(typeof(TerraScaleAdminPage<TerraScaleAdminPaymentWebhookEvent>))]
[JsonSerializable(typeof(TerraScaleAdminPaymentWebhookEvent))]
[JsonSerializable(typeof(TerraScaleAdminPaymentWebhookReplay))]
[JsonSerializable(typeof(TerraScaleAdminDisableUserRequest))]
internal partial class TerraScaleAdminJsonContext : JsonSerializerContext;

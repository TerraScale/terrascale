using System.Collections.Concurrent;
using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

internal static class TerraScaleApiE2E
{
    private const string MissingApiKeyConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_API_KEY, TERRASCALE_E2E_PROJECT_ID, and TERRASCALE_E2E_REPOSITORY to run TerraScale SDK E2E tests.";

    private const string MissingBearerConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_BEARER_TOKEN, and TERRASCALE_E2E_REPOSITORY to run TerraScale SDK bearer-token E2E tests.";

    private const string MissingBaseAddressConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL to run TerraScale SDK authentication-failure E2E tests.";

    private const string MissingRegionalRoutingConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_API_KEY, TERRASCALE_E2E_PROJECT_ID, TERRASCALE_E2E_REPOSITORY, and TERRASCALE_E2E_ROUTING_REGION to run regional routing metadata E2E tests.";

    private const string MissingRegionalFailureConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_API_KEY, TERRASCALE_E2E_PROJECT_ID, TERRASCALE_E2E_REPOSITORY, and TERRASCALE_E2E_UNAVAILABLE_REGION to run regional failure E2E tests.";

    private const string MissingBillingAccessLifecycleConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_BILLING_ORGANIZATION_ID, TERRASCALE_E2E_BILLING_ACCESS_TEST_USER_ID, TERRASCALE_E2E_BILLING_FORBIDDEN_ORGANIZATION_ID, and TERRASCALE_E2E_ENABLE_BILLING_ACCESS_LIFECYCLE=true to run the isolated billing-access lifecycle E2E test.";

    private const string MissingBillingCheckoutConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_BILLING_CHECKOUT_ORGANIZATION_ID, TERRASCALE_E2E_BILLING_CHECKOUT_PLAN_ID, TERRASCALE_E2E_BILLING_CHECKOUT_INVALID_PLAN_ID, TERRASCALE_E2E_BILLING_CHECKOUT_INTERVAL, TERRASCALE_E2E_BILLING_CHECKOUT_PROVIDER, and TERRASCALE_E2E_ENABLE_BILLING_CHECKOUT=true to run the dedicated checkout E2E test.";

    private const string MissingAdministrationAuthorizationConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, and TERRASCALE_E2E_BEARER_TOKEN to run the public administration authorization E2E test.";

    private const string MissingAdministrationWebhookReplayConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_SENSITIVE_BEARER_TOKEN, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_WEBHOOK_PROVIDER, TERRASCALE_E2E_ADMIN_WEBHOOK_EVENT_ID, and TERRASCALE_E2E_ENABLE_ADMIN_WEBHOOK_REPLAY=true to run the dedicated disposable webhook replay E2E test.";

    private const string MissingAdministrationMutationConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_MUTATION_USER_ID, TERRASCALE_E2E_ADMIN_DELETE_USER_ID (different from the mutation id), and TERRASCALE_E2E_ENABLE_ADMIN_MUTATION_LIFECYCLE=true to run the isolated administration user-state E2E test.";

    private const string MissingAdministrationDeleteUserConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_DELETE_USER_ID, TERRASCALE_E2E_ADMIN_MUTATION_USER_ID (different from the deletion id), TERRASCALE_E2E_ADMIN_DELETE_USER_CONFIRMED_DISPOSABLE=true, and TERRASCALE_E2E_ENABLE_ADMIN_DELETE_USER=true to run the one-time disposable user deletion E2E test.";

    private const string MissingAdministrationPolarPlanConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_POLAR_MONTHLY_PRODUCT_ID, TERRASCALE_E2E_ADMIN_POLAR_MONTHLY_PRICE_ID, TERRASCALE_E2E_ADMIN_POLAR_ANNUAL_PRODUCT_ID, TERRASCALE_E2E_ADMIN_POLAR_ANNUAL_PRICE_ID, and TERRASCALE_E2E_ENABLE_ADMIN_POLAR_PLAN_LIFECYCLE=true to run the disposable Polar plan E2E test.";

    private const string MissingAdministrationNonOwnerMutationConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_NONOWNER_PLAN_ID, TERRASCALE_E2E_ADMIN_WEBHOOK_PROVIDER, TERRASCALE_E2E_ADMIN_WEBHOOK_EVENT_ID, and TERRASCALE_E2E_ENABLE_ADMIN_NONOWNER_MUTATION_AUTHORIZATION=true to run the non-mutating administration authorization E2E test.";

    private const string MissingAdministrationPricingPlanUpdateConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_UPDATE_PLAN_ID, TERRASCALE_E2E_ADMIN_UPDATE_PLAN_CONFIRMED_DISPOSABLE=true, and TERRASCALE_E2E_ENABLE_ADMIN_PLAN_UPDATE_LIFECYCLE=true to run the isolated administration pricing-plan update E2E test.";

    private const string MissingAdministrationSupportNoteConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_PROJECT_ID, TERRASCALE_E2E_ENABLE_ADMIN_SUPPORT_NOTE_CREATE=true, and TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_CONFIRMED_ISOLATED_DISPOSABLE_ENVIRONMENT=true to run the irreversible support-note E2E test.";

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BillingAccessLifecycleLocks = new(StringComparer.Ordinal);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> BillingCheckoutLocks = new(StringComparer.Ordinal);

    public static bool IsConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ProjectId) &&
        !string.IsNullOrWhiteSpace(Repository);

    public static bool IsBearerConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(Repository);

    public static bool IsBaseAddressConfigured => BaseAddress is not null;

    public static bool IsRegionalRoutingConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(RegionalRoutingRegion);

    public static bool IsRegionalFailureConfigured =>
        IsConfigured &&
        !string.IsNullOrWhiteSpace(RegionalUnavailableRegion);

    public static bool IsBillingAccessLifecycleConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(BillingOrganizationId) &&
        !string.IsNullOrWhiteSpace(BillingAccessTestUserId) &&
        !string.IsNullOrWhiteSpace(BillingForbiddenOrganizationId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_BILLING_ACCESS_LIFECYCLE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsBillingCheckoutConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(BillingCheckoutOrganizationId) &&
        !string.IsNullOrWhiteSpace(BillingCheckoutPlanId) &&
        !string.IsNullOrWhiteSpace(BillingCheckoutInvalidPlanId) &&
        !string.IsNullOrWhiteSpace(BillingCheckoutInterval) &&
        !string.IsNullOrWhiteSpace(BillingCheckoutProvider) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_BILLING_CHECKOUT"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationAuthorizationConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(BearerToken);

    public static bool IsAdministrationWebhookReplayConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminSensitiveBearerToken) &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(AdminWebhookProvider) &&
        !string.IsNullOrWhiteSpace(AdminWebhookEventId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_WEBHOOK_REPLAY"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationMutationConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(AdminMutationUserId) &&
        !string.IsNullOrWhiteSpace(AdminDeleteUserId) &&
        !string.Equals(AdminMutationUserId, AdminDeleteUserId, StringComparison.Ordinal) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_MUTATION_LIFECYCLE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationDeleteUserConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(AdminDeleteUserId) &&
        !string.IsNullOrWhiteSpace(AdminMutationUserId) &&
        !string.Equals(AdminMutationUserId, AdminDeleteUserId, StringComparison.Ordinal) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ADMIN_DELETE_USER_CONFIRMED_DISPOSABLE"), "true", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_DELETE_USER"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationPolarPlanConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(AdminPolarMonthlyProductId) &&
        !string.IsNullOrWhiteSpace(AdminPolarMonthlyPriceId) &&
        !string.IsNullOrWhiteSpace(AdminPolarAnnualProductId) &&
        !string.IsNullOrWhiteSpace(AdminPolarAnnualPriceId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_POLAR_PLAN_LIFECYCLE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationNonOwnerMutationAuthorizationConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(BearerToken) &&
        !string.IsNullOrWhiteSpace(AdminNonOwnerPlanId) &&
        !string.IsNullOrWhiteSpace(AdminWebhookProvider) &&
        !string.IsNullOrWhiteSpace(AdminWebhookEventId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_NONOWNER_MUTATION_AUTHORIZATION"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationPricingPlanUpdateConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(AdminUpdatePlanId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ADMIN_UPDATE_PLAN_CONFIRMED_DISPOSABLE"), "true", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_PLAN_UPDATE_LIFECYCLE"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsAdministrationSupportNoteConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(AdminBearerToken) &&
        !string.IsNullOrWhiteSpace(AdminSupportNoteProjectId) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ENABLE_ADMIN_SUPPORT_NOTE_CREATE"), "true", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Environment.GetEnvironmentVariable("TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_CONFIRMED_ISOLATED_DISPOSABLE_ENVIRONMENT"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool HasCompleteAuthenticationConfiguration => IsConfigured || IsBearerConfigured;

    public static string SkipReason => MissingApiKeyConfigurationReason;

    public static string BearerSkipReason => MissingBearerConfigurationReason;

    public static string BaseAddressSkipReason => MissingBaseAddressConfigurationReason;

    public static string RegionalRoutingSkipReason => MissingRegionalRoutingConfigurationReason;

    public static string RegionalFailureSkipReason => MissingRegionalFailureConfigurationReason;

    public static string BillingAccessLifecycleSkipReason => MissingBillingAccessLifecycleConfigurationReason;

    public static string BillingCheckoutSkipReason => MissingBillingCheckoutConfigurationReason;

    public static string AdministrationAuthorizationSkipReason => MissingAdministrationAuthorizationConfigurationReason;

    public static string AdministrationWebhookReplaySkipReason => MissingAdministrationWebhookReplayConfigurationReason;

    public static string AdministrationMutationSkipReason => MissingAdministrationMutationConfigurationReason;

    public static string AdministrationDeleteUserSkipReason => MissingAdministrationDeleteUserConfigurationReason;

    public static string AdministrationPolarPlanSkipReason => MissingAdministrationPolarPlanConfigurationReason;

    public static string AdministrationNonOwnerMutationAuthorizationSkipReason => MissingAdministrationNonOwnerMutationConfigurationReason;

    public static string AdministrationPricingPlanUpdateSkipReason => MissingAdministrationPricingPlanUpdateConfigurationReason;

    public static string AdministrationSupportNoteSkipReason => MissingAdministrationSupportNoteConfigurationReason;

    public static Uri? BaseAddress =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("TERRASCALE_E2E_BASE_URL"), UriKind.Absolute, out var uri)
            ? uri
            : null;

    public static string ApiKey => Required("TERRASCALE_E2E_API_KEY");

    public static string BearerToken => Required("TERRASCALE_E2E_BEARER_TOKEN");

    public static string AdminBearerToken => Required("TERRASCALE_E2E_ADMIN_BEARER_TOKEN");

    public static string AdminSensitiveBearerToken => Required("TERRASCALE_E2E_ADMIN_SENSITIVE_BEARER_TOKEN");

    public static string AdminWebhookProvider => Required("TERRASCALE_E2E_ADMIN_WEBHOOK_PROVIDER");

    public static string AdminWebhookEventId => Required("TERRASCALE_E2E_ADMIN_WEBHOOK_EVENT_ID");

    public static string AdminMutationUserId => Required("TERRASCALE_E2E_ADMIN_MUTATION_USER_ID");

    public static string AdminDeleteUserId => Required("TERRASCALE_E2E_ADMIN_DELETE_USER_ID");

    public static string AdminPolarMonthlyProductId => Required("TERRASCALE_E2E_ADMIN_POLAR_MONTHLY_PRODUCT_ID");

    public static string AdminPolarMonthlyPriceId => Required("TERRASCALE_E2E_ADMIN_POLAR_MONTHLY_PRICE_ID");

    public static string AdminPolarAnnualProductId => Required("TERRASCALE_E2E_ADMIN_POLAR_ANNUAL_PRODUCT_ID");

    public static string AdminPolarAnnualPriceId => Required("TERRASCALE_E2E_ADMIN_POLAR_ANNUAL_PRICE_ID");

    public static string AdminNonOwnerPlanId => Required("TERRASCALE_E2E_ADMIN_NONOWNER_PLAN_ID");

    public static string AdminUpdatePlanId => Required("TERRASCALE_E2E_ADMIN_UPDATE_PLAN_ID");

    public static string AdminSupportNoteProjectId => Required("TERRASCALE_E2E_ADMIN_SUPPORT_NOTE_PROJECT_ID");

    public static string ProjectId => Required("TERRASCALE_E2E_PROJECT_ID");

    public static string Repository => Required("TERRASCALE_E2E_REPOSITORY");

    public static string RegionalRoutingRegion => Required("TERRASCALE_E2E_ROUTING_REGION");

    public static string RegionalUnavailableRegion => Required("TERRASCALE_E2E_UNAVAILABLE_REGION");

    public static string BillingOrganizationId => Required("TERRASCALE_E2E_BILLING_ORGANIZATION_ID");

    public static string BillingAccessTestUserId => Required("TERRASCALE_E2E_BILLING_ACCESS_TEST_USER_ID");

    public static string BillingForbiddenOrganizationId => Required("TERRASCALE_E2E_BILLING_FORBIDDEN_ORGANIZATION_ID");

    public static string BillingCheckoutOrganizationId => Required("TERRASCALE_E2E_BILLING_CHECKOUT_ORGANIZATION_ID");

    public static string BillingCheckoutPlanId => Required("TERRASCALE_E2E_BILLING_CHECKOUT_PLAN_ID");

    public static string BillingCheckoutInvalidPlanId => Required("TERRASCALE_E2E_BILLING_CHECKOUT_INVALID_PLAN_ID");

    public static string BillingCheckoutInterval => Required("TERRASCALE_E2E_BILLING_CHECKOUT_INTERVAL");

    public static string BillingCheckoutProvider => Required("TERRASCALE_E2E_BILLING_CHECKOUT_PROVIDER");

    public static async ValueTask<IDisposable> AcquireBillingAccessLifecycleLockAsync(
        string organizationId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var gate = BillingAccessLifecycleLocks.GetOrAdd(
            $"{organizationId.Trim()}:{userId.Trim()}",
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new BillingAccessLifecycleLease(gate);
    }

    public static async ValueTask<IDisposable> AcquireBillingCheckoutLockAsync(
        string organizationId,
        string planId,
        string provider,
        CancellationToken cancellationToken = default)
    {
        // CI must assign this dedicated organization/plan/provider fixture to one checkout lane at a time.
        var gate = BillingCheckoutLocks.GetOrAdd(
            $"{organizationId.Trim()}:{planId.Trim()}:{provider.Trim()}",
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new BillingCheckoutLease(gate);
    }

    public static IReadOnlyList<(string Name, string Value)> ConfigurationVariables =>
    [
        ("TERRASCALE_E2E_BASE_URL", BaseAddress?.ToString() ?? string.Empty),
        ("TERRASCALE_E2E_API_KEY", ApiKey),
        ("TERRASCALE_E2E_BEARER_TOKEN", BearerToken),
        ("TERRASCALE_E2E_PROJECT_ID", ProjectId),
        ("TERRASCALE_E2E_REPOSITORY", Repository)
    ];

    public static TerraScaleClient CreateClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingApiKeyConfigurationReason),
                ApiKey = ApiKey,
                ProjectId = ProjectId
            });

    public static TerraScaleClient CreateAnonymousClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingBaseAddressConfigurationReason)
            });

    public static TerraScaleClient CreateBearerProviderClient(
        Func<CancellationToken, ValueTask<string>> bearerTokenProvider) =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingBearerConfigurationReason),
                BearerTokenProvider = bearerTokenProvider
            });

    public static TerraScaleClient CreateInvalidBearerProviderClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingBaseAddressConfigurationReason),
                BearerTokenProvider = static _ => ValueTask.FromResult($"invalid-{Guid.NewGuid():N}")
            });

    public static TerraScaleClient CreateAdminClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingAdministrationAuthorizationConfigurationReason),
                BearerToken = AdminBearerToken
            });

    public static TerraScaleClient CreateSensitiveAdminClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingAdministrationWebhookReplayConfigurationReason),
                BearerToken = AdminSensitiveBearerToken
            });

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;

    private sealed class BillingAccessLifecycleLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private sealed class BillingCheckoutLease(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}

internal sealed class RequiresTerraScaleApiAttribute()
    : SkipAttribute(TerraScaleApiE2E.SkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsConfigured);
}

internal sealed class RequiresTerraScaleBearerTokenAttribute()
    : SkipAttribute(TerraScaleApiE2E.BearerSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsBearerConfigured);
}

internal sealed class RequiresTerraScaleBaseAddressAttribute()
    : SkipAttribute(TerraScaleApiE2E.BaseAddressSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsBaseAddressConfigured);
}

internal sealed class RequiresTerraScaleRegionalRoutingAttribute()
    : SkipAttribute(TerraScaleApiE2E.RegionalRoutingSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsRegionalRoutingConfigured);
}

internal sealed class RequiresTerraScaleRegionalFailureAttribute()
    : SkipAttribute(TerraScaleApiE2E.RegionalFailureSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsRegionalFailureConfigured);
}

internal sealed class RequiresTerraScaleBillingAccessLifecycleAttribute()
    : SkipAttribute(TerraScaleApiE2E.BillingAccessLifecycleSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsBillingAccessLifecycleConfigured);
}

internal sealed class RequiresTerraScaleBillingCheckoutAttribute()
    : SkipAttribute(TerraScaleApiE2E.BillingCheckoutSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsBillingCheckoutConfigured);
}

internal sealed class RequiresTerraScaleAdministrationAuthorizationAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationAuthorizationSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationAuthorizationConfigured);
}

internal sealed class RequiresTerraScaleAdministrationWebhookReplayAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationWebhookReplaySkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationWebhookReplayConfigured);
}

internal sealed class RequiresTerraScaleAdministrationMutationAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationMutationSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationMutationConfigured);
}

internal sealed class RequiresTerraScaleAdministrationDeleteUserAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationDeleteUserSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationDeleteUserConfigured);
}

internal sealed class RequiresTerraScaleAdministrationPolarPlanAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationPolarPlanSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationPolarPlanConfigured);
}

internal sealed class RequiresTerraScaleAdministrationNonOwnerMutationAuthorizationAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationNonOwnerMutationAuthorizationSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationNonOwnerMutationAuthorizationConfigured);
}

internal sealed class RequiresTerraScaleAdministrationPricingPlanUpdateAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationPricingPlanUpdateSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationPricingPlanUpdateConfigured);
}

internal sealed class RequiresTerraScaleAdministrationSupportNoteAttribute()
    : SkipAttribute(TerraScaleApiE2E.AdministrationSupportNoteSkipReason)
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(!TerraScaleApiE2E.IsAdministrationSupportNoteConfigured);
}

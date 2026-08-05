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

    private const string MissingBillingAccessLifecycleConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_BILLING_ORGANIZATION_ID, TERRASCALE_E2E_BILLING_ACCESS_TEST_USER_ID, TERRASCALE_E2E_BILLING_FORBIDDEN_ORGANIZATION_ID, and TERRASCALE_E2E_ENABLE_BILLING_ACCESS_LIFECYCLE=true to run the isolated billing-access lifecycle E2E test.";

    private const string MissingBillingCheckoutConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_BEARER_TOKEN, TERRASCALE_E2E_BILLING_CHECKOUT_ORGANIZATION_ID, TERRASCALE_E2E_BILLING_CHECKOUT_PLAN_ID, TERRASCALE_E2E_BILLING_CHECKOUT_INVALID_PLAN_ID, TERRASCALE_E2E_BILLING_CHECKOUT_INTERVAL, TERRASCALE_E2E_BILLING_CHECKOUT_PROVIDER, and TERRASCALE_E2E_ENABLE_BILLING_CHECKOUT=true to run the dedicated checkout E2E test.";

    private const string MissingAdministrationAuthorizationConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_ADMIN_BEARER_TOKEN, and TERRASCALE_E2E_BEARER_TOKEN to run the public administration authorization E2E test.";

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

    public static bool HasCompleteAuthenticationConfiguration => IsConfigured || IsBearerConfigured;

    public static string SkipReason => MissingApiKeyConfigurationReason;

    public static string BearerSkipReason => MissingBearerConfigurationReason;

    public static string BaseAddressSkipReason => MissingBaseAddressConfigurationReason;

    public static string BillingAccessLifecycleSkipReason => MissingBillingAccessLifecycleConfigurationReason;

    public static string BillingCheckoutSkipReason => MissingBillingCheckoutConfigurationReason;

    public static string AdministrationAuthorizationSkipReason => MissingAdministrationAuthorizationConfigurationReason;

    public static Uri? BaseAddress =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("TERRASCALE_E2E_BASE_URL"), UriKind.Absolute, out var uri)
            ? uri
            : null;

    public static string ApiKey => Required("TERRASCALE_E2E_API_KEY");

    public static string BearerToken => Required("TERRASCALE_E2E_BEARER_TOKEN");

    public static string AdminBearerToken => Required("TERRASCALE_E2E_ADMIN_BEARER_TOKEN");

    public static string ProjectId => Required("TERRASCALE_E2E_PROJECT_ID");

    public static string Repository => Required("TERRASCALE_E2E_REPOSITORY");

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

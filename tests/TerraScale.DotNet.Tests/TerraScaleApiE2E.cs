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

    public static bool HasCompleteAuthenticationConfiguration => IsConfigured || IsBearerConfigured;

    public static string SkipReason => MissingApiKeyConfigurationReason;

    public static string BearerSkipReason => MissingBearerConfigurationReason;

    public static string BaseAddressSkipReason => MissingBaseAddressConfigurationReason;

    public static Uri? BaseAddress =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("TERRASCALE_E2E_BASE_URL"), UriKind.Absolute, out var uri)
            ? uri
            : null;

    public static string ApiKey => Required("TERRASCALE_E2E_API_KEY");

    public static string BearerToken => Required("TERRASCALE_E2E_BEARER_TOKEN");

    public static string ProjectId => Required("TERRASCALE_E2E_PROJECT_ID");

    public static string Repository => Required("TERRASCALE_E2E_REPOSITORY");

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

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() ?? string.Empty;
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

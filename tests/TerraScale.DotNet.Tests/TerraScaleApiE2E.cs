using TerraScale.DotNet;

namespace TerraScale.DotNet.Tests;

internal static class TerraScaleApiE2E
{
    private const string MissingConfigurationReason =
        "Set TERRASCALE_E2E_BASE_URL, TERRASCALE_E2E_API_KEY, TERRASCALE_E2E_PROJECT_ID, and TERRASCALE_E2E_REPOSITORY to run TerraScale SDK E2E tests.";

    public static bool IsConfigured =>
        BaseAddress is not null &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(ProjectId) &&
        !string.IsNullOrWhiteSpace(Repository);

    public static string SkipReason => MissingConfigurationReason;

    public static Uri? BaseAddress =>
        Uri.TryCreate(Environment.GetEnvironmentVariable("TERRASCALE_E2E_BASE_URL"), UriKind.Absolute, out var uri)
            ? uri
            : null;

    public static string ApiKey => Required("TERRASCALE_E2E_API_KEY");

    public static string ProjectId => Required("TERRASCALE_E2E_PROJECT_ID");

    public static string Repository => Required("TERRASCALE_E2E_REPOSITORY");

    public static IReadOnlyList<(string Name, string Value)> ConfigurationVariables =>
    [
        ("TERRASCALE_E2E_BASE_URL", BaseAddress?.ToString() ?? string.Empty),
        ("TERRASCALE_E2E_API_KEY", ApiKey),
        ("TERRASCALE_E2E_PROJECT_ID", ProjectId),
        ("TERRASCALE_E2E_REPOSITORY", Repository)
    ];

    public static TerraScaleClient CreateClient() =>
        new(
            new HttpClient(),
            new TerraScaleClientOptions
            {
                BaseAddress = BaseAddress ?? throw new InvalidOperationException(MissingConfigurationReason),
                ApiKey = ApiKey,
                ProjectId = ProjectId
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

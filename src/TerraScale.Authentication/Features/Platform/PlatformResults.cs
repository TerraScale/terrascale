using TerraScale.Authentication.Errors;

namespace TerraScale.Authentication.Features.Platform;

internal static class PlatformFailures
{
    private static readonly IReadOnlyDictionary<string, string> RetryTwo = new Dictionary<string, string>
    {
        ["retry_after"] = "2",
    };

    internal static Unavailable Storage() => new("The storage engine is unavailable; retry shortly.", "temporarily_unavailable", RetryTwo);
}

using TerraScale.Authentication.Errors;

namespace TerraScale.Authentication.Features.Platform;

internal static class PlatformFailures
{
    private static readonly IReadOnlyDictionary<string, string> RetryTwo = new Dictionary<string, string>
    {
        ["retry_after"] = "2",
    };

    internal static readonly IReadOnlyDictionary<string, string> RetrySixty = new Dictionary<string, string>
    {
        ["retry_after"] = "60",
    };

    internal static Unavailable Storage() => new("The storage engine is unavailable; retry shortly.", "temporarily_unavailable", RetryTwo);

    internal static Forbidden StepUpRequired() => new("A fresh, approved, purpose-bound single-use proof is required.", "step_up_required");

    internal static Forbidden EmailChangeDenied() => new("No matching pending email change; request a new one.", "email_change_denied");

    internal static Conflict EmailTaken() => new("Another account already uses that email address.", "email_taken");

    internal static QuotaExceeded RateLimited(string message) => new(message, "rate_limited");

    internal static string Code(Failure failure) => failure switch
    {
        NotFound error => error.Code,
        Conflict error => error.Code,
        Validation error => error.Code,
        PreconditionFailed error => error.Code,
        Unauthorized error => error.Code,
        Forbidden error => error.Code,
        QuotaExceeded error => error.Code,
        Unavailable error => error.Code,
        PayloadTooLarge error => error.Code,
        UnsupportedMediaType error => error.Code,
        RequestTimeout error => error.Code,
        Gone error => error.Code,
        OutcomeUnknown error => error.Code,
    };
}

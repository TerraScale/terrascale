namespace TerraScale.Dashboard;

/// <summary>
/// Local-path validation for caller-supplied return targets (the local
/// checkout resumes whichever wizard started it). Only absolute paths on this
/// origin are honored; anything else falls back to the activation wizard, so
/// the redirect can never leave the dashboard.
/// </summary>
public static class SafeRedirect
{
    public const string DefaultCheckoutPath = "/onboarding";

    /// <summary>Resolves a return path, falling back when it is missing,
    /// off-origin (<c>//host</c>), or contains characters that could escape
    /// the path or split the response header.</summary>
    public static string LocalPath(string? returnTo) =>
        returnTo is { Length: > 1 } path
        && path[0] == '/'
        && path[1] is not ('/' or '\\')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Any(char.IsControl)
            ? path
            : DefaultCheckoutPath;

    /// <summary>Appends the payment outcome to a resolved local path.</summary>
    public static string WithPaymentResult(string? returnTo, string result)
    {
        var path = LocalPath(returnTo);
        var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{path}{separator}payment={result}";
    }
}

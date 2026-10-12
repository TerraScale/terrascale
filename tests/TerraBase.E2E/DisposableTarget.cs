namespace TerraBase.E2E;

/// <summary>
/// The disposable Fly app a machine-pinned run (<see cref="Cluster.FlyRevisionVariable"/>) targets
/// (docs/terrabase/e2e-disposable-app.md). It is named explicitly, it is never a production or preview app, and the run's
/// URL is that app's own <c>.fly.dev</c> host. Checked before any flyctl call or request, so a missing or mismatched
/// configuration cannot reach production.
/// </summary>
internal static class DisposableTarget
{
    public const string AppVariable = "TERRABASE_E2E_FLY_APP";

    // The production apps (ts-terrabase, ts-auth, ts-dashboard in org terrascale); previews are ts-pr-<n>.
    private static readonly HashSet<string> Production = new(["ts-terrabase", "ts-auth", "ts-dashboard"], StringComparer.OrdinalIgnoreCase);

    /// <summary>The app, or an <see cref="InvalidOperationException"/> naming what is wrong; reads only <paramref name="variable"/>.</summary>
    public static string Resolve(Func<string, string?> variable)
    {
        ArgumentNullException.ThrowIfNull(variable);
        if (variable(AppVariable) is not { Length: > 0 } app)
        {
            throw new InvalidOperationException($"a machine-pinned run needs {AppVariable} naming the disposable app; it has no default");
        }

        if (Production.Contains(app) || app.StartsWith("ts-pr-", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{app} is a production or preview app; a machine-pinned run targets only a disposable app");
        }

        if (!Uri.TryCreate(variable(Cluster.FlyUrlVariable), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps
            || !string.Equals(url.Host, $"{app}.fly.dev", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath != "/")
        {
            throw new InvalidOperationException($"{Cluster.FlyUrlVariable} must be https://{app}.fly.dev/, the disposable app's own host");
        }

        return app;
    }
}

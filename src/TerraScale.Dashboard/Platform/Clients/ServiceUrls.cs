namespace TerraScale.Dashboard.Platform.Clients;

public sealed record ServiceUrls(Uri TerraBase, Uri Platform, Uri? Control)
{
    public const string TerraBaseName = "TS_DASHBOARD_TERRABASE_URL";
    public const string PlatformName = "TS_DASHBOARD_PLATFORM_URL";
    public const string ControlName = "TS_CONTROL_URL";

    public static ServiceUrls From(IHostEnvironment environment, Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(read);
        return new(
            Origin(TerraBaseName, read(TerraBaseName), environment.IsDevelopment() ? new Uri("http://127.0.0.1:8081/") : null),
            Origin(PlatformName, read(PlatformName), environment.IsDevelopment() ? new Uri("http://127.0.0.1:8082/") : null),
            read(ControlName) is { Length: > 0 } control ? Origin(ControlName, control, null) : null);
    }

    private static Uri Origin(string name, string? configured, Uri? development)
    {
        if (configured is not { Length: > 0 })
        {
            return development ?? throw new InvalidOperationException(string.Concat("Set ", name, ": only Development defaults to a loopback origin."));
        }

        return Uri.TryCreate(configured, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri
            : throw new InvalidOperationException(string.Concat(name, " must be an origin such as http://127.0.0.1:8081."));
    }
}

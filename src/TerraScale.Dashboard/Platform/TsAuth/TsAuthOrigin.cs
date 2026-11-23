namespace TerraScale.Dashboard.Platform.TsAuth;

public static class TsAuthOrigin
{
    public const string Name = "TS_DASHBOARD_AUTH_ORIGIN";

    public static Uri Value { get; } = Parse(Environment.GetEnvironmentVariable(Name));

    public static string Text { get; } = Value.GetLeftPart(UriPartial.Authority);

    private static Uri Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return new Uri("https://auth.terrascale.tech/");
        }

        return Uri.TryCreate(value, UriKind.Absolute, out var origin) && origin.UserInfo.Length == 0 && origin.AbsolutePath == "/"
            && origin.Query.Length == 0 && origin.Fragment.Length == 0
            && (origin.Scheme == Uri.UriSchemeHttps && origin.IsDefaultPort || origin.Scheme == Uri.UriSchemeHttp && origin.IsLoopback)
            ? origin
            : throw new InvalidOperationException(string.Concat(Name,
                " must be an https origin without port, path, query or fragment, such as https://auth.terrascale.tech, or a plain-http loopback origin."));
    }
}

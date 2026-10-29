namespace TerraScale.DotNet.TerraBase;

/// <summary>Creates the transport configuration required by the TerraBase clients.</summary>
public static class TerraBaseHttp
{
    /// <summary>
    /// Creates a handler with redirects and cookies disabled. Redirects can disclose request bodies and acting-user
    /// headers to another origin. A connection attempt fails after 10 seconds with its own error instead of using the
    /// whole request deadline, and pooled connections are replaced every 2 minutes so a long-lived client follows DNS
    /// changes to TerraBase's regional endpoints. Configure TLS or connection settings before using it in a caller-owned
    /// HttpClient.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    };
}

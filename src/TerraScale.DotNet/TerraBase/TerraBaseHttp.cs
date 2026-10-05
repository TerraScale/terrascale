namespace TerraScale.DotNet.TerraBase;

/// <summary>Creates the transport configuration required by the TerraBase clients.</summary>
public static class TerraBaseHttp
{
    /// <summary>
    /// Creates a handler with redirects and cookies disabled. Redirects can disclose request bodies and acting-user
    /// headers to another origin. Configure TLS or connection settings before using it in a caller-owned HttpClient.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new() { AllowAutoRedirect = false, UseCookies = false };
}

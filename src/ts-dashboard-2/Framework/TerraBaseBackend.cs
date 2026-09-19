namespace TerraScale.Dashboard2;

#if TERRABASE_SDK
using TerraScale.DotNet;

/// <summary>
/// TerraBase live-observability access point. Reads TERRABASE_URL once and
/// builds caller-owned clients for that host; a loopback or custom origin
/// keeps the SDK in direct-routing mode (no Atlas discovery).
/// </summary>
public sealed class TerraBaseBackend
{
    public string RawUrl { get; } = Environment.GetEnvironmentVariable("TERRABASE_URL") ?? "";

    public bool Configured { get; }

    public Uri? Url { get; }

    public TerraBaseBackend()
    {
        if (Uri.TryCreate(RawUrl.Trim().TrimEnd('/'), UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https")
        {
            Configured = true;
            Url = parsed;
        }
    }

    public HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = Url,
            Timeout = TimeSpan.FromSeconds(10),
        };

    public TerraScaleClient CreateClient(HttpClient? http = null) =>
        new(http ?? CreateHttpClient(), new TerraScaleClientOptions { BaseAddress = Url!, ApiKey = "dashboard-local" });
}
#else
/// <summary>
/// Stub without the sibling ts-dotnet checkout: never configured, so the
/// Engine page renders its setup panel.
/// </summary>
public sealed class TerraBaseBackend
{
    public string RawUrl { get; } = "";

    public bool Configured => false;

    public Uri? Url => null;
}
#endif

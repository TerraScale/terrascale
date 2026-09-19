using TerraScale.DotNet;

namespace TerraScale.Dashboard2;

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

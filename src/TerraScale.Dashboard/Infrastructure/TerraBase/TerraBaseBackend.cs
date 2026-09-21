namespace TerraScale.Dashboard.Infrastructure.TerraBase;

#if TERRABASE_SDK
using TerraScale.DotNet;

/// <summary>
/// TerraBase live-observability access point. Reads TERRABASE_URL once and
/// builds caller-owned clients for that host; a loopback or custom origin
/// keeps the SDK in direct-routing mode (no Atlas discovery).
/// </summary>
public sealed class TerraBaseBackend
{
    public string RawUrl { get; }

    public string ApiKey { get; }

    public bool Configured { get; }

    public Uri? Url { get; }

    public TerraBaseBackend(IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().AddEnvironmentVariables().Build();
        RawUrl = (configuration["TERRABASE_URL"]
            ?? configuration["TERRASCALE_API_URL"]
            ?? Environment.GetEnvironmentVariable("TERRABASE_URL")
            ?? Environment.GetEnvironmentVariable("TERRASCALE_API_URL")
            ?? "").Trim();
        ApiKey = (configuration["TERRABASE_API_KEY"]
            ?? configuration["TERRASCALE_API_KEY"]
            ?? Environment.GetEnvironmentVariable("TERRABASE_API_KEY")
            ?? Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
            ?? "").Trim();

        if (Uri.TryCreate(RawUrl.TrimEnd('/'), UriKind.Absolute, out var parsed) &&
            parsed.Scheme is "http" or "https" &&
            parsed.AbsolutePath is "" or "/" &&
            parsed.UserInfo.Length == 0 && parsed.Query.Length == 0 && parsed.Fragment.Length == 0)
        {
            Configured = true;
            Url = new Uri(parsed.GetLeftPart(UriPartial.Authority));
        }
    }

    public HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = Url,
            Timeout = TimeSpan.FromSeconds(10),
        };

    public TerraScaleClient CreateClient(HttpClient? http = null) =>
        new(http ?? CreateHttpClient(), new TerraScaleClientOptions
        {
            BaseAddress = Url!,
            // Local TerraBase development historically accepted this key. A
            // configured key takes precedence for deployed members.
            ApiKey = ApiKey.Length > 0 ? ApiKey : "dashboard-local",
        });
}
#else
/// <summary>
/// Stub only for source consumers that intentionally omit the SDK. The
/// dashboard repository restores a vendored SDK package when no sibling
/// checkout is available.
/// </summary>
public sealed class TerraBaseBackend
{
    public string RawUrl { get; }

    public string ApiKey { get; }

    public bool Configured => false;

    public Uri? Url => null;

    public TerraBaseBackend(IConfiguration? configuration = null)
    {
        configuration ??= new ConfigurationBuilder().AddEnvironmentVariables().Build();
        RawUrl = (configuration["TERRABASE_URL"]
            ?? configuration["TERRASCALE_API_URL"]
            ?? Environment.GetEnvironmentVariable("TERRABASE_URL")
            ?? Environment.GetEnvironmentVariable("TERRASCALE_API_URL")
            ?? "").Trim();
        ApiKey = (configuration["TERRABASE_API_KEY"]
            ?? configuration["TERRASCALE_API_KEY"]
            ?? Environment.GetEnvironmentVariable("TERRABASE_API_KEY")
            ?? Environment.GetEnvironmentVariable("TERRASCALE_API_KEY")
            ?? "").Trim();
    }
}
#endif

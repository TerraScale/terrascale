using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using TerraScale.DotNet.TerraBase;

// Usage: <version> <source commit>. Fails unless the restored package is that build and its platform client works.
var expected = $"{args[0]}+{args[1]}";
var loaded = typeof(TerraBasePlatformClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
if (loaded != expected)
{
    throw new InvalidOperationException($"Loaded TerraScale.DotNet {loaded}, expected {expected}.");
}

using var http = new HttpClient(new PlatformStub());
var client = new TerraBasePlatformClient(http, new TerraBasePlatformClientOptions
{
    BaseAddress = new Uri("https://api.terrascale.tech/"),
    AccessTokenProvider = _ => ValueTask.FromResult("consumer-token"),
});
var databases = await client.ListDatabasesAsync();
if (databases.Items is not [{ Name: "platform-dashboard", HomeRegion: "gru", ReplicaRegions: ["iad"] }])
{
    throw new InvalidOperationException("The platform client returned an unexpected database list.");
}

// TerraBase's regional contract: the region's own host, the pin on the request, and a typed 421.
using var regionalHttp = new HttpClient(new WrongRegionStub());
var regional = new TerraBaseDocumentsClient(regionalHttp, new TerraBaseDocumentsClientOptions { Region = "sjc", ApiKey = "tb_consumer" }).Database("project", "app");
try
{
    _ = await regional.GetDocumentAsync("items", "a");
    throw new InvalidOperationException("A 421 did not throw.");
}
catch (TerraBaseWrongRegionException wrong) when (wrong is { Region: "sjc", HomeRegion: "gru", Regions: ["gru", "fra"] })
{
}

foreach (var invalid in new[] { "GRU", "Gru", "gru-" })
{
    try
    {
        _ = new TerraBaseDocumentsClient(regionalHttp, new TerraBaseDocumentsClientOptions { Region = invalid, ApiKey = "tb_consumer" });
        throw new InvalidOperationException($"Region {invalid} was accepted.");
    }
    catch (ArgumentException)
    {
    }
}

Console.WriteLine($"PASS TerraScale.DotNet {loaded} on {RuntimeInformation.FrameworkDescription}");

internal sealed class PlatformStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri?.AbsoluteUri != "https://api.terrascale.tech/v1/platform/databases"
            || request.Headers.Authorization is not { Scheme: "Bearer", Parameter: "consumer-token" })
        {
            throw new InvalidOperationException($"Unexpected request {request.Method} {request.RequestUri}.");
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"items":[{"name":"platform-dashboard","id":"db","homeRegion":"gru","replicaRegions":["iad"]}]}""", Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class WrongRegionStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "sjc.api.terrascale.tech" || !request.Headers.TryGetValues(TerraBaseRegion.PinHeader, out var pin) || pin.Single() != "sjc")
        {
            throw new InvalidOperationException($"Unexpected request {request.RequestUri} without the sjc pin.");
        }

        var response = new HttpResponseMessage(HttpStatusCode.MisdirectedRequest)
        {
            Content = new StringContent("""{"error":{"code":"wrong_region","message":"the database is served in gru"}}""", Encoding.UTF8, "application/json"),
        };
        response.Headers.Add("X-TerraBase-Region", "sjc");
        response.Headers.Add("X-TerraBase-Database-Home", "gru");
        response.Headers.Add("X-TerraBase-Database-Regions", "gru,fra");
        return Task.FromResult(response);
    }
}

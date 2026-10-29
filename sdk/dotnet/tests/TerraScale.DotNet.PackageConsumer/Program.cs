using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TerraScale.DotNet;
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
    _ = await regional.GetDocumentAsync("items", "01JA00000000000000000000D1");
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

// Read the exported contracts from the installed package, rather than a sibling source checkout.
var installed = Path.Combine(Environment.GetEnvironmentVariable("NUGET_PACKAGES")
    ?? throw new InvalidOperationException("Set the isolated consumer package cache."), "terrascale.dotnet", args[0], "contracts");
using var authFixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(installed, "auth", "fixtures", "platform-headless.json")));
const string organization = "00000000000000000000000001";
const string project = "00000000000000000000000002";
const string user = "00000000000000000000000003";
var projectList = authFixture.RootElement.GetProperty("list_projects").GetProperty("response").GetRawText()
    .Replace("{organization}", organization, StringComparison.Ordinal).Replace("{project}", project, StringComparison.Ordinal)
    .Replace("{user}", user, StringComparison.Ordinal);
using var authHttp = new HttpClient(new IntegrationStub(HttpMethod.Get,
    $"https://auth.terrascale.tech/v1/terrascale/organizations/{organization}/projects?limit=100", "original-user-token", projectList, "application/json"));
var auth = new TerraScaleAuthClient(authHttp, new TerraScaleAuthClientOptions
{ BaseAddress = new Uri("https://auth.terrascale.tech"), InstanceSlug = "terrascale" });
var projects = await auth.Platform.ListProjectsAsync("original-user-token", organization, limit: 100);
if (projects.Items is not [{ Id: project, OrganizationId: organization, Role: "member" }] || projects.Next != "opaque+/cursor=")
    throw new InvalidOperationException("Installed auth client did not preserve the exported project page.");

var transaction = File.ReadAllText(Path.Combine(installed, "terrabase", "fixtures", "transaction-changes.sse"));
transaction = transaction[transaction.IndexOf("event: change", StringComparison.Ordinal)..];
using var changesHttp = new HttpClient(new IntegrationStub(HttpMethod.Get,
    $"https://api.terrascale.tech/v1/projects/{project}/databases/app/changes?collections=items", "scoped-user-token", transaction, "text/event-stream"));
var changesDatabase = new TerraBaseDocumentsClient(changesHttp, new TerraBaseDocumentsClientOptions
{ BaseAddress = new Uri("https://api.terrascale.tech"), ApiKey = "scoped-user-token" }).Database(project, "app");
var changes = new List<TerraBaseChangesEvent>();
await foreach (var item in changesDatabase.StreamChangesAsync(collections: ["items"])) changes.Add(item);
if (changes is not [{ Cursor: null, Change.Id: "a" }, { Cursor: not null, Change.Id: "b" }])
    throw new InvalidOperationException("Installed changes client lost a checkpointless transaction event or its final checkpoint.");

Console.WriteLine($"PASS TerraScale.DotNet {loaded} on {RuntimeInformation.FrameworkDescription}: installed auth export and checkpointless transaction feed");

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
                """{"items":[{"name":"platform-dashboard","id":"db","homeRegion":"gru","replicaRegions":["iad"],"allRegions":false}]}""", Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class WrongRegionStub : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri?.Host != "api.terrascale.tech" || !request.Headers.TryGetValues(TerraBaseRegion.PinHeader, out var pin) || pin.Single() != "sjc")
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

internal sealed class IntegrationStub(HttpMethod method, string uri, string bearer, string body, string mediaType) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != method || request.RequestUri?.AbsoluteUri != uri ||
            request.Headers.Authorization is not { Scheme: "Bearer" } authorization || authorization.Parameter != bearer)
            throw new InvalidOperationException("Installed integration client changed its route or authority.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}

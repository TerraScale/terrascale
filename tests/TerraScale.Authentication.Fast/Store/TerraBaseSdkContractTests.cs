using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Store;

[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseSdkContractTests(TerraBaseFixture fixture)
{
    [Test]
    public async Task TerraBase_serves_retained_sdk_routes_and_rejects_removed_collection_hooks()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", "routes.json")));
        var operations = manifest.RootElement.GetProperty("operations").EnumerateArray().ToList();
        await Assert.That(operations.Count).IsGreaterThan(0);
        var missing = new List<string>();
        foreach (var operation in operations)
        {
            var client = operation.GetProperty("client").GetString()!;
            var method = new HttpMethod(operation.GetProperty("method").GetString()!);
            var template = operation.GetProperty("path").GetString()!;
            // Routing needs only a segment; non-ULID names also stop internal management mutations at validation.
            var path = Regex.Replace(template, @"\{([^{}]+)\}", match => match.Groups[1].Value);
            var internalRoute = client == "management" || path.StartsWith("/admin/", StringComparison.Ordinal);
            using var request = !internalRoute
                ? new HttpRequestMessage(method, new Uri(fixture.Server.ClientAddress, path))
                : new HttpRequestMessage(method, new Uri(fixture.Server.InternalAddress, path)) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            if (method != HttpMethod.Get && method != HttpMethod.Delete)
            {
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            }

            using var response = await (internalRoute ? fixture.Server.Http : fixture.Server.PublicHttp).SendAsync(request);
            if (template is "/v1/projects/{project}/databases/{database}/collections/{collection}/webhooks"
                or "/v1/projects/{project}/databases/{database}/collections/{collection}/webhooks/{webhook}"
                or "/v1/projects/{project}/databases/{database}/collections/{collection}/webhooks/{webhook}/deliveries"
                or "/v1/projects/{project}/databases/{database}/functions/{function}/deliveries")
            {
                await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
                continue;
            }

            if (response.StatusCode is HttpStatusCode.MethodNotAllowed
                || response.StatusCode is HttpStatusCode.NotFound
                && (await response.Content.ReadAsStringAsync()) is var body && (body.Length == 0 || body.Contains("unknown_route", StringComparison.Ordinal)))
            {
                missing.Add($"{operation.GetProperty("operation").GetString()}: {method} {template} answered {(int)response.StatusCode}");
            }
        }

        await Assert.That(missing).IsEmpty();
    }
}

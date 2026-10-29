using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Tests;

namespace TerraScale.Authentication.Fast.Store;

/// <summary>
/// The TerraBase built from this commit serves every route the pinned ts-dotnet clients call
/// (<c>$(TsDotnetRoot)/contracts/terrabase/routes.json</c>; ts-dotnet's own tests check its clients send exactly those).
/// An unknown route answers 404 and a known path with the wrong method 405, so an unauthenticated request that gets
/// neither reached an endpoint. Documents and platform routes are tried on the public listener, management routes on the internal
/// listener (HTTP/2), where the platform operator calls them.
/// </summary>
[ClassDataSource<TerraBaseFixture>(Shared = SharedType.PerTestSession)]
public sealed class TerraBaseSdkContractTests(TerraBaseFixture fixture)
{
    private static readonly HashSet<string> RetiredByPivotE2 = new(StringComparer.Ordinal)
    {
        "renameOrganization",
        "getCurrentOrganization",
        "setCurrentOrganization",
        "renameProject",
        "listMembers",
        "changeMemberRole",
        "removeMember",
        "addMember",
        "leaveOrganization",
        "createInvitation",
        "listInvitations",
        "revokeInvitation",
        "acceptInvitation",
        "getOwnershipTransfer",
        "startOwnershipTransfer",
        "confirmOwnershipTransfer",
        "cancelOwnershipTransfer",
        "getAuditRetention",
        "setAuditRetention",
        "listAuditEvents",
    };

    [Test]
    public async Task TerraBase_serves_every_route_the_sdk_calls()
    {
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "contracts", "terrabase", "routes.json")));
        var operations = manifest.RootElement.GetProperty("operations").EnumerateArray().ToList();
        await Assert.That(operations.Count).IsGreaterThan(0);
        var manifestNames = operations.Select(operation => operation.GetProperty("operation").GetString()!).ToHashSet(StringComparer.Ordinal);
        await Assert.That(RetiredByPivotE2.Except(manifestNames)).IsEmpty();
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
            if (RetiredByPivotE2.Contains(operation.GetProperty("operation").GetString()!))
            {
                await Assert.That(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed).IsTrue()
                    .Because(operation.GetProperty("operation").GetString()!);
            }
            else if (response.StatusCode is HttpStatusCode.MethodNotAllowed
                || response.StatusCode is HttpStatusCode.NotFound
                && (await response.Content.ReadAsStringAsync()) is var body && (body.Length == 0 || body.Contains("unknown_route", StringComparison.Ordinal)))
            {
                missing.Add($"{operation.GetProperty("operation").GetString()}: {method} {template} answered {(int)response.StatusCode}");
            }
        }

        await Assert.That(missing).IsEmpty();
    }
}

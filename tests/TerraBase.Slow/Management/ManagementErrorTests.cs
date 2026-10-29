using System.Net;
using System.Text;
using TerraBase.Testing;

namespace TerraBase.Slow.Management;

public sealed class ManagementErrorTests
{
    [Test]
    [Arguments("POST", "/v1/management/organizations", "{}", "name must be 1 to 128 characters, include a letter or digit and have no control characters")]
    [Arguments("PUT", "/v1/management/organizations/missing/plan", "{}", "plan must be hobby, starter, pro, team or enterprise")]
    [Arguments("POST", "/v1/management/organizations/missing/projects", "{}", "name must be 1 to 128 characters, include a letter or digit and have no control characters")]
    [Arguments("POST", "/v1/management/organizations/missing/projects/missing/databases", "{\"name\":\"Main\"}", "name must be a lowercase database name of at most 63 characters")]
    public async Task Invalid_input_returns_an_uncacheable_error_envelope(string method, string path, string json, string message)
    {
        await using var api = await ManagementApi.StartAsync();
        path = path.Replace("/missing", "/" + Ulid.NewUlid(), StringComparison.Ordinal);
        using var request = new HttpRequestMessage(new HttpMethod(method), Uri(path))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        await ErrorAssert.ErrorAsync(api.Client.SendAsync(request), HttpStatusCode.BadRequest, "invalid_request", message);
    }

    [Test]
    [Arguments("/v1/management/organizations?limit=0")]
    [Arguments("/v1/management/organizations/missing/projects?limit=101")]
    [Arguments("/v1/management/organizations/missing/projects/missing/databases?limit=invalid")]
    public async Task Invalid_pagination_returns_the_validation_case(string path)
    {
        await using var api = await ManagementApi.StartAsync();
        path = path.Replace("/missing", "/" + Ulid.NewUlid(), StringComparison.Ordinal);
        await ErrorAssert.ErrorAsync(api.Client.GetAsync(Uri(path)), HttpStatusCode.BadRequest, "invalid_request",
            "limit must be an integer between 1 and 100");
    }

    [Test]
    [Arguments("", "documents:read", "name must be 1 to 100 characters")]
    [Arguments("key", "unknown", "scopes must be a non-empty subset of: documents:read, documents:write")]
    public async Task Invalid_operator_key_input_returns_the_validation_envelope(string name, string scope, string message)
    {
        await using var api = await ManagementApi.StartAsync();
        await ErrorAssert.ErrorAsync(api.Server.Client.PostAsJsonAsync(Uri("/admin/api-keys"), new { name, scopes = new[] { scope } }),
            HttpStatusCode.BadRequest, "invalid_request", message);
    }

    private static Uri Uri(string path) => new(path, UriKind.Relative);
}
